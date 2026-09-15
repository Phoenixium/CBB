using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Ocelot.Lifecycle;
using Ocelot.Rotation.Services;
using System;

namespace Botja.Services;

// Gates the (Wrath/BossMod/RSR) auto-rotation plugin on and off so the character only ever attacks
// while actively engaged with the FATE/CE Botja was sent to help with:
//   - off while FateNavigationService is travelling (walking/teleporting)
//   - on once arrived, as long as the targeted FATE/CE is still active
//   - off again the moment that FATE/CE ends (fate leaves the fate table / CE stops being current)
public sealed unsafe class CombatControlService(
    IRotationService rotation,
    FateNavigationService nav,
    CeSignupService ceSignup,
    IFateTable fateTable,
    CombatConfig config,
    IPluginLog log
) : IOnUpdate
{
    private bool loaded;
    private bool loadGaveUp;
    private int loadAttempts;
    private long nextLoadAttemptMs;
    private bool combatEnabled;
    private uint activeFateId;
    private ushort activeCeEventId;

    // Retry with backoff (5s, 10s, 20s... capped at 60s) instead of hammering the IPC every tick;
    // give up after this many failures so a genuinely-missing rotation plugin doesn't retry forever.
    private const int MaxLoadAttempts = 8;
    private const long LoadRetryBaseMs = 5_000;
    private const long LoadRetryMaxMs = 60_000;

    public bool CombatEnabled => combatEnabled;
    public uint ActiveFateId => activeFateId;
    public ushort ActiveCeEventId => activeCeEventId;

    // Arms combat for a specific FATE — call this whenever navigation is sent toward a FATE.
    public void SetActiveFate(uint fateId) => activeFateId = fateId;

    public void Update()
    {
        if (!loaded && !loadGaveUp && Environment.TickCount64 >= nextLoadAttemptMs)
        {
            try
            {
                // Lazy: DynamicRotationService only resolves its underlying provider on its own
                // IOnPreUpdate tick, which runs before ours, so `current` is already set by now.
                rotation.Load();
                loaded = true;
            }
            catch (Exception ex)
            {
                loadAttempts++;
                if (loadAttempts >= MaxLoadAttempts)
                {
                    loadGaveUp = true;
                    log.Warning(ex, "[CombatControl] rotation.Load() failed {Attempts} times, giving up — is a supported rotation plugin (Wrath/BossMod/RSR) installed and enabled?", loadAttempts);
                }
                else
                {
                    // The rotation plugin's IPC (e.g. BossMod.Presets.Create) can still be unregistered
                    // for a few ticks right after it loads — back off and retry instead of throwing every tick.
                    long backoffMs = Math.Min(LoadRetryBaseMs << (loadAttempts - 1), LoadRetryMaxMs);
                    log.Debug(ex, "[CombatControl] rotation.Load() failed (attempt {Attempts}/{Max}), retrying in {BackoffMs}ms", loadAttempts, MaxLoadAttempts, backoffMs);
                    nextLoadAttemptMs = Environment.TickCount64 + backoffMs;
                }
            }
        }


        // Pick up a freshly-commenced CE without any extra UI wiring.
        if (ceSignup.LastCommencedEventId != 0)
            activeCeEventId = ceSignup.LastCommencedEventId;

        // Also detect a CE we're already inside of (e.g. Botja/Auto Mode started mid-CE, or the
        // player joined one manually) that never went through CeSignupService's own signup flow.
        if (activeCeEventId == 0 && ceSignup.TryGetCurrentEventId(out var currentCeEventId))
            activeCeEventId = currentCeEventId;

        if (!config.AutoControlEnabled)
            return;

        // ResolvingCombat is FateNavigationService actively trying to fight/flee off whatever blocked
        // a teleport — treat that like "arrived", not "travelling", so the rotation can help clear it.
        bool traveling = nav.IsNavigating && !nav.IsResolvingCombat;
        bool fateActive = activeFateId != 0 && IsFateStillActive(activeFateId);
        bool ceActive = activeCeEventId != 0 && IsCeStillActive(activeCeEventId);

        // Clear as soon as the target disappears, even mid-travel — AutoModeService watches for this
        // to reroute to a new target instead of walking all the way to a FATE that's already gone.
        if (activeFateId != 0 && !fateActive)
            activeFateId = 0;
        if (activeCeEventId != 0 && !ceActive)
            activeCeEventId = 0;

        bool shouldFight = !traveling && (fateActive || ceActive);
        if (shouldFight == combatEnabled)
            return;

        combatEnabled = shouldFight;
        try
        {
            if (combatEnabled)
            {
                log.Debug("[CombatControl] Enabling auto-rotation (fate {FateId}, ce {CeId})", activeFateId, activeCeEventId);
                rotation.EnableAutoRotation();
            }
            else
            {
                log.Debug("[CombatControl] Disabling auto-rotation (traveling={Traveling})", traveling);
                rotation.DisableAutoRotation();
            }
        }
        catch (Exception ex)
        {
            // Same IPC-not-ready race as rotation.Load() — the rotation plugin can vanish/reload mid-session.
            log.Debug(ex, "[CombatControl] rotation.{Method}() failed", combatEnabled ? "EnableAutoRotation" : "DisableAutoRotation");
        }
    }

    private bool IsFateStillActive(uint fateId)
    {
        foreach (var fate in fateTable)
        {
            if (fate.FateId == fateId)
                return true;
        }
        return false;
    }

    private static bool IsCeStillActive(ushort eventId)
    {
        DynamicEventContainer* container;
        try
        {
            container = DynamicEventContainer.GetInstance();
        }
        catch
        {
            return false;
        }

        if (container == null)
            return false;

        foreach (ref var dynamicEvent in container->Events)
        {
            // DynamicEventState: Inactive=0, Register=1, Warmup=2, Battle=3 — a finished/failed CE's
            // slot reverts to Inactive rather than disappearing, so matching on id alone never clears
            // (that's why this got permanently stuck showing "Engaged" after the CE ended).
            if (dynamicEvent.DynamicEventId == eventId && dynamicEvent.State != DynamicEventState.Inactive)
                return true;
        }

        return false;
    }
}
