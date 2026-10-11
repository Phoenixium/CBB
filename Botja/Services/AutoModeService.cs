using Dalamud.Game.ClientState.Fates;
using Dalamud.Plugin.Services;
using Ocelot.Lifecycle;
using System;
using System.Linq;
using System.Numerics;

namespace Botja.Services;

// Fully autonomous "farm Bozja" loop: signs up for a recruiting CE in the background (via
// CeSignupService, which only interacts with the Resistance Recruitment UI — it works from
// anywhere, so it never needs to interrupt current FATE travel/combat) while continuing to
// auto-navigate to and fight the current top-priority FATE. Only once the CE actually commences
// (CombatControlService.ActiveCeEventId, the game auto-warps the player in) do we drop whatever
// FATE activity was in progress and let CombatControlService fight the CE instead.
// CombatControlService is what actually notices a FATE/CE has finished (it clears its active id) —
// once that happens this service immediately moves on to whatever is next.
public sealed class AutoModeService(
    FateNavigationService nav,
    FatePriorityService fatePriority,
    FatePriorityConfig fatePriorityConfig,
    IFateTable fateTable,
    IObjectTable objects,
    CeSignupService ceSignup,
    CombatControlService combatControl,
    AutoModeState autoModeState,
    FollowConfig follow,
    IPluginLog log
) : IOnUpdate
{
    // Set alongside combatControl.SetActiveFate whenever we pick a FATE flagged as a "follow" FATE
    // (see FollowConfig) — consumed once that FATE genuinely finishes (not merely abandoned) to arm
    // followUpWait below.
    private bool pendingIsFollowFate;
    private Vector3 pendingFollowPosition;
    private string pendingFollowName = "";

    // True while sitting at a just-finished follow FATE's position waiting for its chained follow-up
    // to appear nearby.
    private bool waitingForFollowUp;
    private long followUpDeadlineMs;

    // Extra slack added to a FATE's own radius when checking whether we actually arrived — nav's
    // arrival radii are much tighter than a FATE's fight area.
    private const float FateArrivalBufferYalms = 30f;

    public bool IsRunning { get; private set; }
    public string Status { get; private set; } = "Idle";

    public void Start()
    {
        IsRunning = true;
        autoModeState.IsRunning = true;
        pendingIsFollowFate = false;
        waitingForFollowUp = false;
        Status = "Looking for the next FATE or CE.";
    }

    public void Stop()
    {
        IsRunning = false;
        autoModeState.IsRunning = false;
        pendingIsFollowFate = false;
        waitingForFollowUp = false;
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
            pendingIsFollowFate = false;
            waitingForFollowUp = false;
            Status = "Paused: player is dead.";
            return;
        }

        // The CE has actually commenced (the game auto-warps the player in) — THIS is the only point
        // where CE involvement interrupts FATE travel/combat; queueing for one (below) never does.
        if (combatControl.ActiveCeEventId != 0)
        {
            if (nav.IsNavigating)
            {
                log.Debug("[AutoMode] CE commenced, aborting FATE travel to fight it");
                nav.Stop();
            }
            if (combatControl.ActiveFateId != 0)
                combatControl.SetActiveFate(0);
            pendingIsFollowFate = false;
            waitingForFollowUp = false;
            Status = "Engaged in a critical engagement; waiting for it to finish.";
            return;
        }

        // Queue/sign up for a recruiting CE in the background (CeSignupService runs its own Update
        // independently) — never stops current FATE travel/combat, only actually switching over once
        // the CE commences (handled above). ceSignup.Status is surfaced separately in the UI.
        if (!ceSignup.IsRunning && (ceSignup.TryFindRecruitingEvent(out _) || ceSignup.HasRecentCeAnnouncement))
        {
            log.Debug("[AutoMode] Recruiting CE found, queueing signup in the background");
            ceSignup.Start();
        }

        // Handled here, before the generic nav.IsNavigating block below, since that block would
        // otherwise mistake our "walk back to the wait spot" travel for a vanished-FATE reroute and
        // cancel it.
        if (waitingForFollowUp)
        {
            var nearbyFate = FindFateNear(pendingFollowPosition, follow.RadiusYalms);
            if (nearbyFate != null)
            {
                log.Debug("[AutoMode] Follow-up FATE {Name} appeared near the wait position", nearbyFate.Name);
                waitingForFollowUp = false; // fall through to the normal pick below
            }
            else if (NowMs() < followUpDeadlineMs)
            {
                double secondsLeft = (followUpDeadlineMs - NowMs()) / 1000.0;
                Status = $"Waiting near {pendingFollowName}'s spot for a follow-up FATE ({secondsLeft:F0}s left).";
                return;
            }
            else
            {
                log.Debug("[AutoMode] Follow-up wait for {Name} timed out, resuming normal picking", pendingFollowName);
                waitingForFollowUp = false;
            }
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
            // of walking all the way to a dead FATE's last known position. Never having reached it,
            // it doesn't count as "finished" for follow-up purposes.
            log.Debug("[AutoMode] Target FATE disappeared mid-travel, rerouting");
            pendingIsFollowFate = false;
            nav.Stop();
        }

        if (combatControl.ActiveFateId != 0 && !IsNearActiveFate())
        {
            // Not travelling, yet nowhere near the FATE either — AutoNavigate silently gave up
            // (timed out / couldn't path there) rather than the FATE actually finishing. Give up on
            // it too instead of sitting in "Engaged" forever with nothing actually happening.
            log.Debug("[AutoMode] Target FATE {FateId} not nearby despite no active nav, abandoning", combatControl.ActiveFateId);
            combatControl.SetActiveFate(0);
            pendingIsFollowFate = false;
        }

        if (combatControl.ActiveFateId != 0)
        {
            Status = "Engaged; waiting for it to finish.";
            return;
        }

        if (pendingIsFollowFate)
        {
            // The FATE we were actually fighting just genuinely ended — sit at its position instead
            // of immediately routing elsewhere, giving a chained follow-up FATE a chance to appear.
            pendingIsFollowFate = false;
            waitingForFollowUp = true;
            followUpDeadlineMs = NowMs() + (long)(follow.WaitSeconds * 1000);
            log.Debug("[AutoMode] {Name} was a marked follow FATE; waiting {Seconds}s at its position for a follow-up", pendingFollowName, follow.WaitSeconds);
            Status = $"Waiting near {pendingFollowName}'s spot for a follow-up FATE.";
            nav.AutoNavigate(pendingFollowPosition, $"{pendingFollowName} (follow-up wait spot)");
            return;
        }

        var top = fatePriority.GetAutoSelectableFates(fateTable).FirstOrDefault();
        if (top == null)
        {
            Status = "No FATEs or CEs available; waiting.";
            return;
        }

        log.Debug("[AutoMode] Navigating to top FATE {Name} (progress={Progress}%, maxProgressPercent={MaxProgressPercent})", top.Name, top.Progress, fatePriorityConfig.MaxProgressPercent);
        Status = $"Heading to {top.Name}.";
        combatControl.SetActiveFate(top.FateId);
        pendingIsFollowFate = fatePriority.IsFollowFate(top);
        pendingFollowPosition = top.Position;
        pendingFollowName = top.Name.ToString();
        nav.AutoNavigate(top.Position, top.Name.ToString());
    }

    private bool IsNearActiveFate()
    {
        if (objects.LocalPlayer is not { } player)
            return false;

        var fate = fateTable.FirstOrDefault(f => f.FateId == combatControl.ActiveFateId);
        if (fate == null)
            return false; // shouldn't happen — CombatControlService already clears once the FATE is gone

        return Distance2D(player.Position, fate.Position) <= fate.Radius + FateArrivalBufferYalms;
    }

    private IFate? FindFateNear(Vector3 position, float radius) =>
        fatePriority.GetAutoSelectableFates(fateTable).FirstOrDefault(fate => Distance2D(fate.Position, position) <= radius);

    private static float Distance2D(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static long NowMs() => Environment.TickCount64;
}
