using Dalamud.Plugin.Services;
using Ocelot.Lifecycle;
using System;
using System.Linq;

namespace Botja.Services;

// Fully autonomous "farm Bozja" loop: joins a recruiting CE if one is available (CEs take priority
// over FATEs), otherwise auto-navigates to the current top-priority FATE. CombatControlService is
// what actually notices a FATE/CE has finished (it clears its active id) — once that happens this
// service immediately moves on to whatever is next.
public sealed class AutoModeService(
    FateNavigationService nav,
    FatePriorityService fatePriority,
    IFateTable fateTable,
    IObjectTable objects,
    CeSignupService ceSignup,
    CombatControlService combatControl,
    IPluginLog log
) : IOnUpdate
{
    // True right after we've kicked off a CE signup, until CombatControlService has had a chance to
    // notice the commenced CE (or the signup gave up) — avoids racing a FATE pick into that gap.
    private bool awaitingCePickup;

    // Extra slack added to a FATE's own radius when checking whether we actually arrived — nav's
    // arrival radii are much tighter than a FATE's fight area.
    private const float FateArrivalBufferYalms = 30f;

    public bool IsRunning { get; private set; }
    public string Status { get; private set; } = "Idle";

    public void Start()
    {
        IsRunning = true;
        awaitingCePickup = false;
        Status = "Looking for the next FATE or CE.";
    }

    public void Stop()
    {
        IsRunning = false;
        awaitingCePickup = false;
        nav.Stop();
        if (ceSignup.IsRunning)
            ceSignup.Stop();
        Status = "Stopped";
    }

    public void Update()
    {
        if (!IsRunning)
            return;

        if (objects.LocalPlayer?.IsDead == true)
        {
            if (nav.IsNavigating)
                nav.Stop();
            if (ceSignup.IsRunning)
                ceSignup.Stop();
            Status = "Paused: player is dead.";
            return;
        }

        if (ceSignup.IsRunning)
        {
            awaitingCePickup = true;
            Status = $"Joining CE: {ceSignup.Status}";
            return;
        }

        if (awaitingCePickup)
        {
            if (combatControl.ActiveCeEventId != 0)
                awaitingCePickup = false; // picked up by CombatControlService, now fighting it
            else if (ceSignup.LastCommencedEventId == 0)
                awaitingCePickup = false; // signup never commenced (failed/timed out/stopped) — move on
            else
                return; // brief gap while CombatControlService notices the newly-commenced CE
        }

        // CEs always take priority — abandon any FATE travel/engagement the moment one is available
        // (either already recruiting, or just announced in chat ahead of showing up as recruiting).
        if (combatControl.ActiveCeEventId == 0 && (ceSignup.TryFindRecruitingEvent(out _) || ceSignup.HasRecentCeAnnouncement))
        {
            if (nav.IsNavigating)
            {
                log.Debug("[AutoMode] CE available, aborting current FATE travel to join it");
                nav.Stop();
            }
            combatControl.SetActiveFate(0);
            log.Debug("[AutoMode] Recruiting CE found, joining");
            Status = "Joining recruiting CE.";
            ceSignup.Start();
            awaitingCePickup = true;
            return;
        }

        if (nav.IsNavigating)
        {
            if (combatControl.ActiveFateId != 0)
            {
                Status = nav.IsResolvingCombat
                    ? "Teleport blocked by combat; fighting/fleeing before retrying."
                    : $"Travelling to {nav.DebugPendingDestinationName}.";
                return;
            }

            if (nav.IsResolvingCombat)
            {
                // No FATE armed (e.g. a manual walk) but still let FateNavigationService finish
                // fighting/fleeing off combat on its own — don't yank the nav out from under it.
                Status = "Teleport blocked by combat; fighting/fleeing before retrying.";
                return;
            }

            // Target FATE vanished (ended/failed) mid-travel — stop and pick a new one below instead
            // of walking all the way to a dead FATE's last known position.
            log.Debug("[AutoMode] Target FATE disappeared mid-travel, rerouting");
            nav.Stop();
        }

        if (combatControl.ActiveFateId != 0 && !IsNearActiveFate())
        {
            // Not travelling, yet nowhere near the FATE either — AutoNavigate silently gave up
            // (timed out / couldn't path there) rather than the FATE actually finishing. Give up on
            // it too instead of sitting in "Engaged" forever with nothing actually happening.
            log.Debug("[AutoMode] Target FATE {FateId} not nearby despite no active nav, abandoning", combatControl.ActiveFateId);
            combatControl.SetActiveFate(0);
        }

        if (combatControl.ActiveFateId != 0 || combatControl.ActiveCeEventId != 0)
        {
            Status = "Engaged; waiting for it to finish.";
            return;
        }

        var top = fatePriority.GetSortedFates(fateTable).FirstOrDefault();
        if (top == null)
        {
            Status = "No FATEs or CEs available; waiting.";
            return;
        }

        log.Debug("[AutoMode] Navigating to top FATE {Name}", top.Name);
        Status = $"Heading to {top.Name}.";
        combatControl.SetActiveFate(top.FateId);
        nav.AutoNavigate(top.Position, top.Name.ToString());
    }

    private bool IsNearActiveFate()
    {
        if (objects.LocalPlayer is not { } player)
            return false;

        var fate = fateTable.FirstOrDefault(f => f.FateId == combatControl.ActiveFateId);
        if (fate == null)
            return false; // shouldn't happen — CombatControlService already clears once the FATE is gone

        float dx = player.Position.X - fate.Position.X;
        float dz = player.Position.Z - fate.Position.Z;
        return MathF.Sqrt(dx * dx + dz * dz) <= fate.Radius + FateArrivalBufferYalms;
    }
}
