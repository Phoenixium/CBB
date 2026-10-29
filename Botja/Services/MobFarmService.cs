using Botja.Windows;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Ocelot.Lifecycle;
using Ocelot.Services.PlayerState;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Botja.Services;

public enum MobFarmPhase
{
    Waiting,
    Gathering,
    Stacking,
    Fighting,
}

public sealed class MobFarmService(
    IClientState clientState,
    IObjectTable objects,
    ITargetManager targets,
    IPlayer player,
    CeSignupService ceSignup,
    HostileDetectionService hostileDetection,
    FateNavigationService nav,
    CombatControlService combatControl,
    MobFarmConfig config,
    MobFarmSpotSession spots,
    MobFarmPullAssist pullAssist,
    IPluginLog log
) : IOnUpdate, IOnTerritoryChanged, IOnStop
{
    private static readonly HashSet<uint> SupportedTerritories = [920, 975];
    private const float SpotArrivalYalms = 10f;
    private const float StackArrivalYalms = 10f;
    private const long MoveRetryMs = 3_000;
    private const string NavigationPrefix = "Mob Farm: ";

    private long phaseStartedMs;
    private long lastMoveRequestMs;
    private Vector3 lastMoveDestination;
    private string? moveKey;
    private bool hasMoveRequest;
    private bool stackingMoveIssued;
    private bool returningHome;
    private bool fateMobTargetingPlayer;
    private Vector3 startPosition;
    private List<HostileDetectionService.HostileNpc> selfDefense = [];
    private List<HostileDetectionService.HostileNpc> freeMobs = [];
    private List<HostileDetectionService.HostileNpc> contestedMobs = [];

    public bool IsRunning { get; private set; }
    public bool IsPausedForCe { get; private set; }
    public MobFarmPhase Phase { get; private set; } = MobFarmPhase.Waiting;
    public string Status { get; private set; } = "Stopped";
    public string CurrentSpotName => spots.Name ?? "Current position";
    public string? CurrentTargetName { get; private set; }
    public int FreeMobCount => freeMobs.Count;
    public int EngagedMobCount => selfDefense.Count;
    public int ContestedMobCount => contestedMobs.Count;
    public int SelectedMobCount => config.MobBaseIds.Count;

    public void Start()
    {
        if (!SupportedTerritories.Contains(clientState.TerritoryType))
        {
            Status = "Mob Farm is available only in Bozja and Zadnor.";
            return;
        }

        StopOwnedNavigation();
        IsRunning = true;
        Phase = MobFarmPhase.Waiting;
        phaseStartedMs = NowMs();
        CurrentTargetName = null;
        ClearMoveRequest();
        stackingMoveIssued = false;
        returningHome = false;
        spots.Begin();
        startPosition = spots.Origin;
        Status = "Waiting for selected mobs.";
        combatControl.SetMobFarmEngaged(false);
        log.Information("[MobFarm] Started in territory {Territory}; spot={Spot}", clientState.TerritoryType, CurrentSpotName);
    }

    public void Stop()
    {
        if (!IsRunning)
            return;

        IsRunning = false;
        combatControl.SetMobFarmEngaged(false);
        StopOwnedNavigation();
        spots.Reset();
        Phase = MobFarmPhase.Waiting;
        CurrentTargetName = null;
        ClearMoveRequest();
        stackingMoveIssued = false;
        returningHome = false;
        IsPausedForCe = false;
        selfDefense = [];
        freeMobs = [];
        contestedMobs = [];
        Status = "Stopped";
        log.Information("[MobFarm] Stopped");
    }

    public void OnTerritoryChanged(uint territory)
    {
        if (IsRunning && !SupportedTerritories.Contains(territory))
            Stop();
    }

    public void OnStop() => Stop();

    public void Update()
    {
        if (!IsRunning)
            return;

        if (!SupportedTerritories.Contains(clientState.TerritoryType))
        {
            Stop();
            return;
        }

        if (objects.LocalPlayer == null)
        {
            combatControl.SetMobFarmEngaged(false);
            Status = "Waiting for the local player.";
            return;
        }

        if (ceSignup.TryGetCurrentEventId(out var activeCeId))
        {
            IsPausedForCe = true;
            combatControl.SetMobFarmEngaged(false);
            StopOwnedNavigation();
            Status = $"Paused during Critical Engagement {activeCeId}.";
            return;
        }

        IsPausedForCe = false;

        RefreshMobs();
        if (Phase == MobFarmPhase.Waiting && spots.TickClaimed(contestedMobs, freeMobs))
        {
            StopOwnedNavigation();
            ClearMoveRequest();
            Status = $"Moving to {CurrentSpotName}.";
        }

        bool playerInCombat = player.Conditions[ConditionFlag.InCombat] && !fateMobTargetingPlayer;
        if (Phase == MobFarmPhase.Waiting && (selfDefense.Count > 0 || playerInCombat))
            SetPhase(MobFarmPhase.Fighting);

        combatControl.SetMobFarmEngaged(
            Phase == MobFarmPhase.Fighting
            || ((Phase is MobFarmPhase.Gathering or MobFarmPhase.Stacking) && (selfDefense.Count > 0 || playerInCombat)));

        switch (Phase)
        {
            case MobFarmPhase.Waiting:
                UpdateWaiting();
                break;
            case MobFarmPhase.Gathering:
                UpdateGathering(playerInCombat);
                break;
            case MobFarmPhase.Stacking:
                UpdateStacking(playerInCombat);
                break;
            case MobFarmPhase.Fighting:
                UpdateFighting(playerInCombat);
                break;
        }
    }

    private void RefreshMobs()
    {
        var hostiles = hostileDetection.GetNearbyHostiles();
        fateMobTargetingPlayer = hostiles.Any(hostile => hostile.FateId != 0 && hostile.IsTargetingPlayer);
        selfDefense = hostiles
            .Where(hostile => hostile.FateId == 0 && hostile.IsTargetingPlayer)
            .ToList();

        var selected = hostiles.Where(hostile =>
                hostile.FateId == 0
            && config.MobBaseIds.Contains(hostile.DataId))
            .Where(hostile => !hostile.IsTargetingPlayer)
            .ToList();

        freeMobs = selected.Where(hostile => !hostile.IsEngagedElsewhere).ToList();
        contestedMobs = selected.Where(hostile => hostile.IsEngagedElsewhere).ToList();
    }

    private void UpdateWaiting()
    {
        if (spots.NeedsApproach)
        {
            float distance = MobFarmSpotSession.Distance2D(player.Position, spots.Origin);
            if (distance <= SpotArrivalYalms)
            {
                spots.MarkArrived();
                ClearMoveRequest();
            }
            else
            {
                RequestWalk("spot", spots.Origin, $"{CurrentSpotName} farm spot");
                Status = $"Moving to {CurrentSpotName} ({distance:F0} yalms).";
                return;
            }
        }

        if (config.MobBaseIds.Count == 0)
        {
            Status = "Select mob templates in settings to start farming.";
            return;
        }

        if (freeMobs.Count < config.MinimumMobsToStartLoop || freeMobs.Count == 0)
        {
            Status = $"Waiting at {CurrentSpotName}: {freeMobs.Count} free selected mobs.";
            return;
        }

        SetPhase(MobFarmPhase.Gathering);
    }

    private void UpdateGathering(bool playerInCombat)
    {
        int minimum = spots.EffectiveMinimumMobsToStartFight;
        if (selfDefense.Count >= minimum)
        {
            SetPhase(MobFarmPhase.Stacking);
            return;
        }

        if (config.StackingTimeoutSeconds > 0f
            && NowMs() - phaseStartedMs >= config.StackingTimeoutSeconds * 1000f
            && selfDefense.Count > 0)
        {
            SetPhase(MobFarmPhase.Stacking);
            return;
        }

        if (freeMobs.Count == 0)
        {
            if (selfDefense.Count == 0 && !playerInCombat)
                StopOwnedNavigation();
            SetPhase(selfDefense.Count > 0 || playerInCombat ? MobFarmPhase.Stacking : MobFarmPhase.Waiting);
            return;
        }

        HostileDetectionService.HostileNpc current = freeMobs.OrderBy(hostile => hostile.DistanceYalms).First();
        CurrentTargetName = current.Name;
        IBattleNpc? mob = ResolveBattleNpc(current.ObjectId);
        if (mob == null)
        {
            Status = $"Reacquiring {current.Name}.";
            return;
        }

        if (config.ShouldHandleTargeting && targets.Target?.GameObjectId != mob.GameObjectId)
            targets.Target = mob;

        float range = player.GetAttackRange();
        if (current.DistanceYalms <= MobFarmPullAssist.PullRangeYalms)
        {
            try
            {
                pullAssist.TryPull(mob, current.DistanceYalms);
            }
            catch (Exception ex)
            {
                log.Debug(ex, "[MobFarm] Pull action failed for {Mob}", current.Name);
            }
        }

        bool targetPulled = mob.TargetObject?.GameObjectId == objects.LocalPlayer?.GameObjectId;
        if (targetPulled && freeMobs.Count > 1)
        {
            var next = freeMobs.Where(hostile => hostile.ObjectId != current.ObjectId)
                .OrderBy(hostile => hostile.DistanceYalms)
                .First();
            RequestWalk($"mob_{next.ObjectId}", next.Position, $"gather {next.Name}");
            Status = $"Gathering {selfDefense.Count} / {minimum} mobs; pulling {current.Name}.";
            return;
        }

        if (current.DistanceYalms > range)
            RequestWalk($"mob_{current.ObjectId}", current.Position, $"gather {current.Name}");
        else
            ClearMoveRequest();

        Status = $"Gathering mobs: {selfDefense.Count} / {minimum} engaged; {freeMobs.Count} free.";
    }

    private void UpdateStacking(bool playerInCombat)
    {
        combatControl.SetMobFarmEngaged(selfDefense.Count > 0 || playerInCombat);
        if (selfDefense.Count == 0)
        {
            StopOwnedNavigation();
            SetPhase(playerInCombat ? MobFarmPhase.Fighting : MobFarmPhase.Waiting);
            return;
        }

        if (!stackingMoveIssued)
        {
            Vector3? destination = spots.StackPoint;
            if (destination == null)
            {
                ulong currentTargetId = targets.Target?.GameObjectId ?? 0;
                destination = selfDefense
                    .Where(hostile => hostile.ObjectId != currentTargetId)
                    .OrderByDescending(hostile => hostile.DistanceYalms)
                    .Select(hostile => (Vector3?)hostile.Position)
                    .FirstOrDefault();
            }

            if (destination is { } stackPosition
                && MobFarmSpotSession.Distance2D(player.Position, stackPosition) > StackArrivalYalms)
            {
                RequestWalk("stack", stackPosition, "stack point");
                stackingMoveIssued = true;
                Status = $"Stacking {selfDefense.Count} engaged mobs.";
                return;
            }

            SetPhase(MobFarmPhase.Fighting);
            return;
        }

        if (!nav.IsNavigating || NowMs() - phaseStartedMs >= config.StackingTimeoutSeconds * 1000f)
        {
            StopOwnedNavigation();
            SetPhase(MobFarmPhase.Fighting);
            return;
        }

        Status = $"Stacking {selfDefense.Count} engaged mobs.";
    }

    private void UpdateFighting(bool playerInCombat)
    {
        combatControl.SetMobFarmEngaged(true);
        if (selfDefense.Count > 0)
        {
            returningHome = false;
            StopOwnedNavigation();
            if (config.ShouldHandleTargeting)
                SelectCombatTarget();
            CurrentTargetName = (targets.Target as IBattleNpc)?.Name.ToString();
            Status = $"Fighting {selfDefense.Count} mobs at {CurrentSpotName}.";
            return;
        }

        if (playerInCombat)
        {
            returningHome = false;
            StopOwnedNavigation();
            Status = "In combat; waiting for nearby mobs to be reacquired.";
            return;
        }

        combatControl.SetMobFarmEngaged(false);
        float distanceHome = MobFarmSpotSession.Distance2D(player.Position, startPosition);
        if (!returningHome && config.ReturnToStartInWaitingPhase
            && distanceHome >= config.MinDistanceToReturnHomeYalms)
            returningHome = true;

        if (returningHome)
        {
            if (distanceHome <= StackArrivalYalms)
            {
                returningHome = false;
                StopOwnedNavigation();
                SetPhase(MobFarmPhase.Waiting);
                return;
            }

            RequestWalk("home", startPosition, "starting spot");
            Status = "Returning to the starting farm spot.";
            return;
        }

        StopOwnedNavigation();
        ClearMoveRequest();
        SetPhase(MobFarmPhase.Waiting);
    }

    private void SelectCombatTarget()
    {
        if (selfDefense.Count == 0)
            return;

        HostileDetectionService.HostileNpc selected;
        if (!config.ForceTargetCentralEnemy || selfDefense.Count == 1)
        {
            selected = selfDefense.OrderBy(hostile => hostile.DistanceYalms).First();
        }
        else
        {
            Vector3 center = selfDefense.Aggregate(Vector3.Zero, (sum, hostile) => sum + hostile.Position) / selfDefense.Count;
            selected = selfDefense.OrderBy(hostile => MobFarmSpotSession.Distance2D(hostile.Position, center)).First();
        }

        var mob = ResolveBattleNpc(selected.ObjectId);
        if (mob != null && targets.Target?.GameObjectId != mob.GameObjectId)
            targets.Target = mob;
    }

    private IBattleNpc? ResolveBattleNpc(ulong objectId) =>
        objects.OfType<IBattleNpc>().FirstOrDefault(mob => mob.GameObjectId == objectId);

    private void RequestWalk(string key, Vector3 destination, string description)
    {
        long now = NowMs();
        bool changed = moveKey != key
            || !hasMoveRequest
            || MobFarmSpotSession.Distance2D(lastMoveDestination, destination) > 3f;
        bool retry = !nav.IsNavigating && now - lastMoveRequestMs >= MoveRetryMs;
        if (!changed && !retry)
            return;

        moveKey = key;
        lastMoveDestination = destination;
        lastMoveRequestMs = now;
        hasMoveRequest = true;
        nav.PathTo(destination, NavigationPrefix + description);
    }

    private void StopOwnedNavigation()
    {
        if (nav.IsNavigating
            && nav.DebugPendingDestinationName.StartsWith(NavigationPrefix, StringComparison.Ordinal))
            nav.Stop();
        ClearMoveRequest();
    }

    private void ClearMoveRequest()
    {
        moveKey = null;
        hasMoveRequest = false;
        lastMoveRequestMs = 0;
    }

    private void SetPhase(MobFarmPhase phase)
    {
        if (Phase == phase)
            return;

        Phase = phase;
        phaseStartedMs = NowMs();
        stackingMoveIssued = false;
        ClearMoveRequest();
    }

    private static long NowMs() => Environment.TickCount64;
}