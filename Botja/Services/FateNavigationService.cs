using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Ocelot.Actions;
using Ocelot.Graphics;
using Ocelot.Ipc.BossMod;
using Ocelot.Ipc.VNavmesh;
using Ocelot.Lifecycle;
using Ocelot.Services.OverlayRenderer;
using Ocelot.Services.PlayerState;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Botja.Services;

// Mirrors BOCCHI's AethernetData — PlaceName row ID + world position.
internal record struct AethernetShard(uint PlaceNameId, Vector3 Position);

public class FateNavigationService(
    IDalamudPluginInterface pluginInterface,
    IVNavmeshIpc vnav,
    ILifestreamIpc lifestream,
    IClientState clientState,
    IObjectTable objects,
    ITargetManager targets,
    IPlayer player,
    IOverlayRenderer overlay,
    NavigationConfig navigationConfig,
    IPluginLog log
) : IOnUpdate, IOnRender
{
    private readonly ICallGateSubscriber<bool> navIsReady = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");

    // Occult Crescent field aetheryte data sourced from BOCCHI zone definitions.
    private static readonly IReadOnlyDictionary<ushort, IReadOnlyList<AethernetShard>> ZoneAetherytes =
        new Dictionary<ushort, IReadOnlyList<AethernetShard>>
        {
            // Southern Front  (Utyais Aegis is the main aetheryte)
            [920] = new AethernetShard[]
            {
                new(3529, new Vector3( -201.953f, 5.020f,  846.952f)), // Utyais Aegis
                new(3530, new Vector3( 486.777f,  34.927f,  531.334f)), // Olana's Stand
                new(3531, new Vector3( -257.953f, 35.934f, 534.355f)), // Lunya's Stand
                new(3575, new Vector3( 169.787f,  2.945f,  192.278f)), // Camp Steva
            },
            // Zadnor  (Camp Vrdelnis is the main aetheryte)
            [975] = new AethernetShard[]
            {
                new(3664, new Vector3(679.682f, 297.291f, 660.028f)), // Camp Vrdelnis
                new(3665, new Vector3(-356.466f, 286.030f, 758.449f)), // Zuprtik Point
                new(3666, new Vector3(-689.387f, 276.539f, -292.164f)), // Ljeban Point
                new(3667, new Vector3( 106.370f, 300.953f,  -130.815f)), // Hrmovir Point
            },
        };


    // From BOCCHI NavigationConstants
    private const float MaxDirectWalkDistance = 80f;
    private const float EventArrivalRadius = 5f;
    private const float ShardInteractRadius = 3.5f;
    // Wider than ShardInteractRadius — matches BOCCHI's IsAlreadyAtAetheryte arrivedRadius
    // (post-TP landings and menu-open range are often several yards from the crystal).
    private const float ArrivedAtAetheryteRadius = 12f;
    // These are wall-clock durations (ms), NOT frame counts — Update() is not guaranteed to run at
    // 60fps (observed real rates are much higher), so a tick-count timeout would fire far too early.
    private const long WalkToShardTimeoutMs = 30_000; // ~30s safety timeout
    private const long TeleportStartTimeoutMs = 3_000; // matches BOCCHI's WaitUntilTeleportStarted
    private const long TeleportArriveTimeoutMs = 20_000; // matches BOCCHI's WaitUntilArrived
    // Lifestream's busy/jump/betweenAreas signals can all fail to ever flip when a hop silently
    // fails ("Destination could not be found") — don't wait out the full TeleportArriveTimeoutMs
    // in that case, retry the hop (it usually succeeds on a retry) after this much shorter grace period.
    private const long TeleportFailFastMs = 3_000;
    // A direct walk across a whole zone is a terrible fallback for a hop that just needs retrying —
    // only give up on aethernet and fall back to walking after this many silent-fail retries.
    private const int MaxTeleportFailFastRetries = 3;
    private const long NavmeshReadyTimeoutMs = 30_000; // matches BOCCHI's PathfindToChain NavmeshReadyTimeout
    private const long NavmeshReadyMinMs = 250; // give IsNavmeshReady a moment to drop after zone load
    // When a teleport gives up specifically because we're stuck InCombat, try to let auto-rotation
    // kill whatever's attacking first, then fall back to running away from it until it deaggros.
    private const long CombatFightGraceMs = 20_000;
    private const long CombatFleeTimeoutMs = 30_000;
    private const float CombatFleeDistance = 25f; // yalms to run away per flee attempt
    // Rough average movement speed for ETA estimates (accounts for mount ramp-up/dismount time).
    private const float EstimatedMoveSpeed = 10f; // yalms/sec
    private const float EstimatedTeleportOverheadSeconds = 5f; // menu + animation overhead for an aethernet hop
    // Busy/BetweenAreas flags can race with the actual warp (or never flip for an instant same-zone
    // hop) — a real position jump away from where we stood when we fired the teleport is the only
    // proof it actually happened. Aethernet shards in Bozja are always much further apart than this.
    private const float TeleportJumpThreshold = 15f;

    private enum TeleportPhase { None, WalkingToSourceShard, WaitingForStart, WaitingForEnd, Settling, WalkingToDestination, ResolvingCombat }

    private static readonly Color ShardPathColor = new(0.3f, 0.6f, 1f); // blue — walking to source shard
    private static readonly Color TeleportWaitColor = new(1f, 0.8f, 0.1f); // yellow — teleport in progress
    private static readonly Color FinalPathColor = new(0.2f, 1f, 0.3f); // green — final walk to goal

    private const long HeartbeatIntervalMs = 1_000; // log a state snapshot this often during waits
    private const long WalkToDestinationTimeoutMs = 60_000; // ~60s safety timeout
    // vnav.PathfindAndMoveCloseTo can silently no-op (e.g. called before IsNavmeshReady flips, or a
    // dropped IPC call), leaving Pathfinding/Running both false and the player frozen for the whole
    // phase timeout. Re-issue the move if vnav still looks idle this long after we last asked it to go.
    private const long MovementStallRetryMs = 3_000;
    // A genuinely unreachable point (e.g. stale/bad shard coords vnav can't find a polygon for) would
    // otherwise retry the identical failing request forever — give up on the aethernet/final-walk route
    // after this many stall retries, matching the bounded-retry pattern used for teleport fail-fast.
    private const int MaxMovementStallRetries = 3;
    // Snap-to-mesh search radius passed to vnav.FindPointOnFloor before every pathfind — matches
    // BOCCHI's PathfinderConfig.FloorSnapExtents default. Kept small since this only needs to find
    // the floor directly beneath the stored X/Z, not search sideways for a different spot.
    private const float MeshSnapHalfExtentXZ = 5f;

    private TeleportPhase phase;
    private Vector3 pendingDestination;
    private string pendingDestinationName = string.Empty;
    private Vector3 pendingSourceShardPos;
    private Vector3 pendingDestShardPos;
    private Vector3 pendingPreTeleportPos;
    private int phaseTicks; // diagnostic Update() call counter only — NOT a reliable time unit, see timeouts above
    private long phaseStartMs;
    private long lastHeartbeatMs;
    private long lastMoveIssuedMs;
    private int movementStallRetries;
    private int teleportFailFastRetries;
    private Vector3 combatResolveDestination;
    private bool combatResolveFleeing;
    // Bounds ResolvingCombat to a single attempt per navigation request — without this, a hop that
    // keeps failing while genuinely stuck in combat (no rotation plugin to kill it, can't outrun it)
    // would loop through fight/flee forever and never actually get us there.
    private bool combatResolveAttempted;
    private bool resumingAfterCombatResolve;

    private static long NowMs() => Environment.TickCount64;
    private long PhaseElapsedMs => NowMs() - phaseStartMs;

    // Debug state for the UI (FateListWindow) to display.
    public string DebugPhase => phase.ToString();
    public int DebugPhaseTicks => phaseTicks;
    public Vector3 DebugPendingDestination => pendingDestination;
    public string DebugPendingDestinationName => pendingDestinationName;
    public Vector3 DebugPendingSourceShardPos => pendingSourceShardPos;
    public Vector3 DebugPendingDestShardPos => pendingDestShardPos;
    public bool DebugVnavIsPathfinding => SafeIsPathfinding();
    public bool DebugVnavIsRunning => SafeIsRunning();
    public bool DebugVnavIsNavmeshReady => SafeIsNavmeshReady();
    public bool DebugLifestreamBusy => SafeIsBusy();

    // True while any navigation phase (walk/teleport hop/settle) is in progress — used by
    // CombatControlService to hold off auto-rotation while we're travelling, not fighting.
    public bool IsNavigating => phase != TeleportPhase.None;

    // True while ResolvingCombat is trying to fight off whatever blocked a teleport — CombatControlService
    // treats this the same as "arrived and fighting" so the rotation plugin is allowed to help clear it.
    public bool IsResolvingCombat => phase == TeleportPhase.ResolvingCombat;

    // Smart routing: walk if close, or if walking beats the aethernet route; otherwise walk to the nearest aetheryte, teleport to the shard nearest the destination, then walk in.
    public void AutoNavigate(Vector3 destination, string? destinationName = null)
    {
        if (destinationName is not null)
            pendingDestinationName = destinationName;

        Vector3? playerPos = objects.LocalPlayer?.Position;
        if (playerPos == null)
        {
            log.Debug("[FateNav] AutoNavigate({Destination}) aborted: local player is null", destination);
            return;
        }

        log.Debug("[FateNav] AutoNavigate({Destination}) from {PlayerPos}", destination, playerPos.Value);

        float directDist = Dist2D(playerPos.Value, destination);
        if (directDist <= MaxDirectWalkDistance)
        {
            log.Debug("[FateNav] AutoNavigate: within direct walk distance, walking");
            PathTo(destination);
            return;
        }

        AethernetShard? destShard = FindNearestShardStruct(destination);
        if (destShard == null)
        {
            log.Debug("[FateNav] AutoNavigate: no known shards in zone {Zone}, walking directly", clientState.TerritoryType);
            PathTo(destination);
            return;
        }

        // Must match Lifestream's own real interact/detection range (ShardInteractRadius), not the
        // looser ArrivedAtAetheryteRadius — firing the teleport from further out than Lifestream can
        // actually detect an active (custom) aetheryte from causes "Destination could not be found (3)".
        bool alreadyNearShard = FindNearestShardStruct(playerPos.Value) is { } nearShard && Dist2D(playerPos.Value, nearShard.Position) <= ShardInteractRadius;
        AethernetShard? sourceShard = alreadyNearShard ? null : FindNearestShardStruct(playerPos.Value);

        float walkToSourceDist = alreadyNearShard ? 0f : sourceShard?.Position is { } sourcePos ? Dist2D(playerPos.Value, sourcePos) : float.MaxValue;
        float walkFromDestShardDist = Dist2D(destShard.Value.Position, destination);
        float routeDist = walkToSourceDist + walkFromDestShardDist;

        if (directDist <= routeDist)
        {
            log.Debug("[FateNav] AutoNavigate: direct walk ({DirectDist}) is not longer than aethernet route ({RouteDist}), walking directly", directDist, routeDist);
            PathTo(destination);
            return;
        }

        if (alreadyNearShard)
        {
            log.Debug("[FateNav] AutoNavigate: already at nearest shard, routing via aethernet");
            PathViaAethernet(destination);
            return;
        }

        if (sourceShard != null)
        {
            log.Debug("[FateNav] AutoNavigate: walking to nearest shard {PlaceNameId} before aethernet teleport", sourceShard.Value.PlaceNameId);
            WalkToShardThenAethernet(sourceShard.Value.Position, destination);
            return;
        }

        PathTo(destination);
    }

    // Read-only estimate of AutoNavigate's travel time, for display (e.g. the FATE list ETA column).
    public float EstimateTravelSeconds(Vector3 destination)
    {
        if (objects.LocalPlayer is not { } player)
            return 0f;

        Vector3 playerPos = player.Position;
        float directDist = Dist2D(playerPos, destination);
        if (directDist <= MaxDirectWalkDistance)
            return directDist / EstimatedMoveSpeed;

        AethernetShard? destShard = FindNearestShardStruct(destination);
        if (destShard == null)
            return directDist / EstimatedMoveSpeed;

        bool alreadyNearShard = FindNearestShardStruct(playerPos) is { } nearShard && Dist2D(playerPos, nearShard.Position) <= ShardInteractRadius;
        AethernetShard? sourceShard = alreadyNearShard ? null : FindNearestShardStruct(playerPos);

        float walkToSourceDist = alreadyNearShard ? 0f : sourceShard?.Position is { } sourcePos ? Dist2D(playerPos, sourcePos) : float.MaxValue;
        float walkFromDestShardDist = Dist2D(destShard.Value.Position, destination);
        float routeDist = walkToSourceDist + walkFromDestShardDist;

        if (directDist <= routeDist || walkToSourceDist == float.MaxValue)
            return directDist / EstimatedMoveSpeed;

        return routeDist / EstimatedMoveSpeed + EstimatedTeleportOverheadSeconds;
    }

    // Walk to a source shard's position, then aethernet-teleport toward the final destination once in range.
    private void WalkToShardThenAethernet(Vector3 shardPos, Vector3 finalDestination)
    {
        CancelPending();
        // Store the snapped (on-mesh) position, not the raw dict value — a stale/bad stored Y
        // otherwise leaves both the arrival check and the overlay circle pointing underground.
        Vector3 snappedShardPos = SnapToMesh(shardPos);
        pendingDestination = finalDestination;
        pendingSourceShardPos = snappedShardPos;
        phase = TeleportPhase.WalkingToSourceShard;
        phaseTicks = 0;
        phaseStartMs = NowMs();
        movementStallRetries = 0;

        TryMount(snappedShardPos);
        log.Debug("[FateNav] WalkToShardThenAethernet: walking to shard {ShardPos} (raw {RawShardPos}), phase -> WalkingToSourceShard", snappedShardPos, shardPos);
        StartMove(snappedShardPos, ShardInteractRadius);
    }

    // Walk directly, ignoring aethernet (walk button).
    public void PathTo(Vector3 destination, string? destinationName = null)
    {
        if (destinationName is not null)
            pendingDestinationName = destinationName;

        CancelPending();
        TryMount(destination);
        log.Debug("[FateNav] PathTo({Destination}) via vnavmesh", destination);
        StartMove(destination, EventArrivalRadius);
        pendingDestination = destination;
        phase = TeleportPhase.WalkingToDestination;
        phaseTicks = 0;
        phaseStartMs = NowMs();
        movementStallRetries = 0;
    }

    // Teleport to nearest shard, then walk to destination (matches BOCCHI PathViaAethernet).
    // isRetry: re-firing the same hop after a silent-fail fail-fast — preserves the retry counter
    // instead of resetting it, so retries are actually bounded by MaxTeleportFailFastRetries.
    public void PathViaAethernet(Vector3 destination, bool isRetry = false, string? destinationName = null)
    {
        if (destinationName is not null)
            pendingDestinationName = destinationName;

        if (!isRetry)
            teleportFailFastRetries = 0;

        // A resumed attempt after ResolvingCombat is still part of the same journey — don't let it
        // reset the one-shot guard, or a persistently-uncleared combat state could loop forever.
        if (!isRetry && !resumingAfterCombatResolve)
            combatResolveAttempted = false;

        // Don't even interact with the aethernet crystal while InCombat — Lifestream refuses the hop
        // anyway, so check up front instead of reactively discovering that after a round of retries.
        if (player.Conditions[ConditionFlag.InCombat])
        {
            if (combatResolveAttempted)
            {
                log.Debug("[FateNav] PathViaAethernet({Destination}): still in combat after resolving once this journey, giving up on the teleporter and walking instead", destination);
                PathTo(destination);
                return;
            }

            log.Debug("[FateNav] PathViaAethernet({Destination}): in combat, resolving combat before interacting with the teleporter", destination);
            combatResolveAttempted = true;
            StartResolvingCombat(destination);
            return;
        }

        AethernetShard? shard = FindNearestShardStruct(destination);
        if (shard == null)
        {
            log.Debug("[FateNav] PathViaAethernet({Destination}): no shard found in zone {Zone}, falling back to walk", destination, clientState.TerritoryType);
            PathTo(destination);
            return;
        }

        CancelPending();
        pendingDestination = destination;
        pendingDestShardPos = shard.Value.Position;

        // Skip-if-already-there — matches BOCCHI's AethernetTeleport.SkipIfAlreadyThere, avoids a
        // pointless Lifestream call (and its busy-wait) when we're already standing on the shard.
        if (objects.LocalPlayer is { } player0 && Dist2D(player0.Position, pendingDestShardPos) <= ArrivedAtAetheryteRadius)
        {
            log.Debug("[FateNav] PathViaAethernet({Destination}): already at destination shard {PlaceNameId} — skipping teleport", destination, shard.Value.PlaceNameId);
            phase = TeleportPhase.Settling;
            phaseTicks = 0;
            phaseStartMs = NowMs();
            return;
        }

        phase = TeleportPhase.WaitingForStart;
        phaseTicks = 0;
        phaseStartMs = NowMs();
        pendingPreTeleportPos = objects.LocalPlayer?.Position ?? Vector3.NaN;

        log.Debug("[FateNav] PathViaAethernet({Destination}): lifestream aethernet teleport to place {PlaceNameId}, phase -> WaitingForStart", destination, shard.Value.PlaceNameId);
        SafeStopVnav();
        bool queued;
        try
        {
            Actions.TryUnmount(player);
            queued = lifestream.AethernetTeleportByPlaceNameId(shard.Value.PlaceNameId);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[FateNav] lifestream.AethernetTeleportByPlaceNameId({PlaceNameId}) failed", shard.Value.PlaceNameId);
            queued = false;
        }

        if (!queued)
        {
            // The IPC call itself reports rejection (e.g. shard not unlocked/reachable) — no point
            // waiting out the fail-fast grace period when we already know it didn't queue.
            RetryOrFallbackTeleport(destination, $"lifestream.AethernetTeleportByPlaceNameId({shard.Value.PlaceNameId}) returned false");
        }
    }

    // Teleport to nearest shard only, no walk after (matches BOCCHI TeleportToward).
    public void TeleportToward(Vector3 destination)
    {
        if (player.Conditions[ConditionFlag.InCombat])
        {
            log.Debug("[FateNav] TeleportToward({Destination}): in combat, not interacting with the teleporter", destination);
            return;
        }

        CancelPending();
        AethernetShard? shard = FindNearestShardStruct(destination);
        if (shard == null)
        {
            log.Debug("[FateNav] TeleportToward({Destination}): no shard found in zone {Zone}", destination, clientState.TerritoryType);
            return;
        }

        if (objects.LocalPlayer is { } player0 && Dist2D(player0.Position, shard.Value.Position) <= ArrivedAtAetheryteRadius)
        {
            log.Debug("[FateNav] TeleportToward({Destination}): already at destination shard {PlaceNameId} — skipping teleport", destination, shard.Value.PlaceNameId);
            return;
        }

        log.Debug("[FateNav] TeleportToward({Destination}): lifestream aethernet teleport to place {PlaceNameId}", destination, shard.Value.PlaceNameId);
        SafeStopVnav();
        try
        {
            Actions.TryUnmount(player);
            bool queued = lifestream.AethernetTeleportByPlaceNameId(shard.Value.PlaceNameId);
            if (!queued)
                log.Warning("[FateNav] TeleportToward({Destination}): lifestream.AethernetTeleportByPlaceNameId({PlaceNameId}) returned false", destination, shard.Value.PlaceNameId);
        }
        catch (Exception ex) { log.Warning(ex, "[FateNav] lifestream.AethernetTeleportByPlaceNameId({PlaceNameId}) failed", shard.Value.PlaceNameId); }
    }

    // Always stop prior movement before issuing a new one — matches BOCCHI's PathfindToChain
    // "Stop prior movement" step (Path.Stop alone does not cancel a pending SimpleMove task).
    private void StartMove(Vector3 destination, float range)
    {
        SafeStopVnav();
        lastMoveIssuedMs = NowMs();
        Vector3 target = SnapToMesh(destination);
        try
        {
            vnav.PathfindAndMoveCloseTo(target, false, range);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[FateNav] vnavmesh PathfindAndMoveCloseTo failed for {Destination}", target);
            CancelPending();
        }
    }

    // A stale/bad stored coordinate (e.g. a shard's Y off by 100+ yalms) makes vnav hard-fail the
    // whole pathfind ("failed to find polygon on a mesh") instead of just walking somewhere close —
    // snap to the nearest real mesh point first. FindPointOnFloor keeps X/Z fixed and only corrects
    // the vertical position via a floor raycast — FindPointOnMesh (nearest-point-on-mesh-surface)
    // was tried first but can also drift X/Z sideways to the nearest polygon edge, which visibly
    // shifted the shard/overlay circle left-right even though only Y was ever wrong here. Both IPCs
    // already return the original point unchanged if nothing is found nearby, so this is safe even
    // when the point genuinely is fine.
    private Vector3 SnapToMesh(Vector3 destination)
    {
        try
        {
            return vnav.FindPointOnFloor(destination, MeshSnapHalfExtentXZ);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[FateNav] vnav.FindPointOnMesh failed for {Destination}", destination);
            return destination;
        }
    }

    // True once vnav has had a fair chance to pick up the last move request but still reports idle —
    // the tell-tale sign of a silently-dropped PathfindAndMoveCloseTo call (see MovementStallRetryMs).
    private bool IsMovementStalled() =>
        NowMs() - lastMoveIssuedMs > MovementStallRetryMs && !SafeIsPathfinding() && !SafeIsRunning();

    public void Stop()
    {
        log.Debug("[FateNav] Stop() requested, current phase {Phase}", phase);
        CancelPending();
        SafeStopVnav();
    }

    public void Update()
    {
        if (phase == TeleportPhase.None) return;

        if (objects.LocalPlayer?.IsDead == true)
        {
            log.Debug("[FateNav] Player is dead; stopping navigation");
            Stop();
            return;
        }

        phaseTicks++;
        LogHeartbeat();

        if (phase == TeleportPhase.WalkingToSourceShard)
        {
            bool arrived = objects.LocalPlayer is { } player && Dist2D(player.Position, pendingSourceShardPos) <= ShardInteractRadius;
            if (arrived)
            {
                log.Debug("[FateNav] Update: WalkingToSourceShard done after {Ticks} ticks, continuing via aethernet", phaseTicks);
                PathViaAethernet(pendingDestination);
            }
            else if (PhaseElapsedMs > WalkToShardTimeoutMs)
            {
                // Never reached the shard — proceeding to PathViaAethernet would fire the hop from
                // wherever we stalled, not from the shard. Abort to a direct walk instead.
                log.Debug("[FateNav] Update: WalkingToSourceShard timed out after {Ticks} ticks ({ElapsedMs}ms) without reaching shard, aborting to direct walk", phaseTicks, PhaseElapsedMs);
                PathTo(pendingDestination);
            }
            else if (IsMovementStalled())
            {
                movementStallRetries++;
                if (movementStallRetries > MaxMovementStallRetries)
                {
                    log.Debug("[FateNav] Update: WalkingToSourceShard stalled {Retries} times (vnav can't reach {ShardPos}), aborting to direct walk", movementStallRetries, pendingSourceShardPos);
                    PathTo(pendingDestination);
                }
                else
                {
                    log.Debug("[FateNav] Update: WalkingToSourceShard stalled (vnav idle) after {Ticks} ticks, re-issuing move to {ShardPos} ({Retry}/{Max})", phaseTicks, pendingSourceShardPos, movementStallRetries, MaxMovementStallRetries);
                    StartMove(pendingSourceShardPos, ShardInteractRadius);
                }
            }
            return;
        }
        else if (phase == TeleportPhase.WaitingForStart)
        {
            // Do NOT call SafeStopVnav() here every tick — vnavmesh.Path.Stop is global, not scoped
            // to our own pathfind requests, and Lifestream itself may use vnavmesh to walk the player
            // to the shard before it can teleport. Stubbing it out continuously silently cancelled that
            // walk on every single hop (100% reproducible zero-movement failures). We already stop our
            // own leftover movement once, right before firing the teleport, in PathViaAethernet/TeleportToward.

            // Arrived-at-shard requires an actual position jump away from where we stood when we
            // fired the teleport — Lifestream's busy flag (and BetweenAreas) can race with or never
            // reflect a same-zone hop, so a raw distance check alone could false-positive on the
            // very first tick before anything has actually moved. Matches BOCCHI's WaitUntilTeleportStarted.
            bool betweenAreas = player.IsBetweenAreas();
            bool jumped = objects.LocalPlayer is { } jp && Dist2D(jp.Position, pendingPreTeleportPos) > TeleportJumpThreshold;
            bool arrived = !betweenAreas && jumped && objects.LocalPlayer is { } p && Dist2D(p.Position, pendingDestShardPos) <= ArrivedAtAetheryteRadius;
            bool busy = SafeIsBusy();
            if (arrived || busy || betweenAreas || PhaseElapsedMs > TeleportStartTimeoutMs)
            {
                log.Debug("[FateNav] Update: WaitingForStart -> WaitingForEnd after {Ticks} ticks (arrived={Arrived}, jumped={Jumped}, lifestream busy={Busy}, betweenAreas={BetweenAreas})", phaseTicks, arrived, jumped, busy, betweenAreas);
                phase = TeleportPhase.WaitingForEnd;
                phaseTicks = 0;
                phaseStartMs = NowMs();
            }
        }
        else if (phase == TeleportPhase.WaitingForEnd)
        {
            // Same reason as WaitingForStart — must not spam vnav.Path.Stop while Lifestream may be
            // using vnavmesh itself to walk the player to the shard.

            // Require both: not mid-warp, AND an actual position jump away from the pre-teleport spot.
            // This is the only reliable proof the hop executed — busy/BetweenAreas flags can race with
            // (or never reflect) an instant same-zone aethernet hop. Matches BOCCHI's WaitUntilArrived.
            bool betweenAreas = player.IsBetweenAreas();
            bool jumped = objects.LocalPlayer is { } jp && Dist2D(jp.Position, pendingPreTeleportPos) > TeleportJumpThreshold;
            bool arrived = !betweenAreas && jumped && objects.LocalPlayer is { } p && Dist2D(p.Position, pendingDestShardPos) <= ArrivedAtAetheryteRadius;
            if (arrived || (!betweenAreas && jumped && !SafeIsBusy()) || PhaseElapsedMs > TeleportArriveTimeoutMs)
            {
                log.Debug("[FateNav] Update: WaitingForEnd -> Settling after {Ticks} ticks (arrived={Arrived}, jumped={Jumped}, lifestream busy={Busy}, betweenAreas={BetweenAreas})", phaseTicks, arrived, jumped, SafeIsBusy(), betweenAreas);
                phase = TeleportPhase.Settling;
                phaseTicks = 0;
                phaseStartMs = NowMs();
            }
            else if (!betweenAreas && !jumped && !SafeIsBusy() && PhaseElapsedMs > TeleportFailFastMs)
            {
                // No busy flag, no BetweenAreas, no position jump this whole grace period — the hop
                // silently failed (e.g. Lifestream "Destination could not be found").
                RetryOrFallbackTeleport(pendingDestination, "WaitingForEnd fail-fast (no busy/jump/betweenAreas)");
            }
        }
        else if (phase == TeleportPhase.Settling)
        {
            // Still mid-warp — Position/navmesh state is meaningless until this clears.
            if (player.IsBetweenAreas())
            {
                return;
            }

            // Wait for vnav to report the (possibly reloaded post-TP) navmesh ready before pathing,
            // instead of guessing with a fixed delay — matches BOCCHI's PathfindToChain "Wait for navmesh".
            bool navReady = PhaseElapsedMs > NavmeshReadyMinMs && SafeIsNavmeshReady();

            // Also wait for any leftover pathfind task to actually clear — matches BOCCHI's
            // "Wait for pathfind slot" (AsyncMoveRequest rejects stacked SimpleMove calls).
            bool slotFree = !SafeIsPathfinding();
            if (!slotFree)
            {
                SafeStopVnav();
            }

            if ((navReady && slotFree) || PhaseElapsedMs > NavmeshReadyTimeoutMs)
            {
                log.Debug("[FateNav] Update: Settling done after {Ticks} ticks (navmesh ready={Ready}, slot free={SlotFree}), walking to {Destination}", phaseTicks, navReady, slotFree, pendingDestination);
                TryMount(pendingDestination);
                StartMove(pendingDestination, EventArrivalRadius);
                phase = TeleportPhase.WalkingToDestination;
                phaseTicks = 0;
                phaseStartMs = NowMs();
                movementStallRetries = 0;
            }
        }
        else if (phase == TeleportPhase.WalkingToDestination)
        {
            bool arrived = objects.LocalPlayer is { } p && Dist2D(p.Position, pendingDestination) <= EventArrivalRadius;
            if (arrived || PhaseElapsedMs > WalkToDestinationTimeoutMs)
            {
                log.Debug("[FateNav] Update: WalkingToDestination done after {Ticks} ticks (Arrived={Arrived})", phaseTicks, arrived);
                if (arrived)
                {
                    Actions.TryUnmount(player);
                }

                CancelPending();
            }
            else if (IsMovementStalled())
            {
                movementStallRetries++;
                if (movementStallRetries > MaxMovementStallRetries)
                {
                    log.Debug("[FateNav] Update: WalkingToDestination stalled {Retries} times (vnav can't reach {Destination}), giving up", movementStallRetries, pendingDestination);
                    CancelPending();
                }
                else
                {
                    log.Debug("[FateNav] Update: WalkingToDestination stalled (vnav idle) after {Ticks} ticks, re-issuing move to {Destination} ({Retry}/{Max})", phaseTicks, pendingDestination, movementStallRetries, MaxMovementStallRetries);
                    StartMove(pendingDestination, EventArrivalRadius);
                }
            }
        }
        else if (phase == TeleportPhase.ResolvingCombat)
        {
            if (!player.Conditions[ConditionFlag.InCombat])
            {
                log.Debug("[FateNav] Update: ResolvingCombat cleared after {Ticks} ticks ({ElapsedMs}ms, fleeing={Fleeing}), retrying teleport", phaseTicks, PhaseElapsedMs, combatResolveFleeing);
                resumingAfterCombatResolve = true;
                PathViaAethernet(combatResolveDestination);
                resumingAfterCombatResolve = false;
                return;
            }

            if (!combatResolveFleeing)
            {
                if (PhaseElapsedMs > CombatFightGraceMs)
                {
                    // Still in combat after letting auto-rotation try to kill it — put distance
                    // between us and whatever's attacking instead of fighting indefinitely.
                    combatResolveFleeing = true;
                    Vector3 fleeTarget = ComputeFleeTarget();
                    log.Debug("[FateNav] Update: ResolvingCombat still in combat after {ElapsedMs}ms, fleeing to {FleeTarget}", PhaseElapsedMs, fleeTarget);
                    StartMove(fleeTarget, EventArrivalRadius);
                    phaseStartMs = NowMs(); // restart the clock for the flee timeout below
                }

                return;
            }

            if (PhaseElapsedMs > CombatFleeTimeoutMs)
            {
                log.Debug("[FateNav] Update: ResolvingCombat gave up fleeing after {ElapsedMs}ms, still in combat, falling back to direct walk", PhaseElapsedMs);
                PathTo(combatResolveDestination);
            }
        }
    }

    private void CancelPending()
    {
        if (phase != TeleportPhase.None)
            log.Debug("[FateNav] CancelPending: clearing phase {Phase} after {Ticks} ticks", phase, phaseTicks);
        phase = TeleportPhase.None;
        phaseTicks = 0;
        phaseStartMs = NowMs();
    }

    // Shared decision point for a failed/rejected aethernet hop — retries a bounded number of times
    // (this usually succeeds on a retry) before giving up on a much slower direct walk across the zone.
    private void RetryOrFallbackTeleport(Vector3 destination, string reason)
    {
        teleportFailFastRetries++;
        if (teleportFailFastRetries <= MaxTeleportFailFastRetries)
        {
            log.Debug("[FateNav] {Reason}, retrying aethernet hop ({Retry}/{Max})", reason, teleportFailFastRetries, MaxTeleportFailFastRetries);
            PathViaAethernet(destination, isRetry: true);
        }
        else if (!combatResolveAttempted && player.Conditions[ConditionFlag.InCombat])
        {
            // Lifestream refuses to teleport while InCombat — that's very likely why every retry
            // above failed the same way. Walking off mid-fight is a bad fallback too; try to clear
            // the fight (or put distance between us and it) before attempting the hop again. Only
            // once per journey (see combatResolveAttempted) so a fight/flee that never resolves can't
            // loop forever — it still falls through to the direct-walk fallback below eventually.
            log.Debug("[FateNav] {Reason} after {Retries} retries and still in combat, resolving combat before retrying", reason, teleportFailFastRetries);
            combatResolveAttempted = true;
            StartResolvingCombat(destination);
        }
        else
        {
            log.Debug("[FateNav] {Reason} after {Retries} retries, falling back to direct walk", reason, teleportFailFastRetries);
            PathTo(destination);
        }
    }

    // Give auto-rotation a chance to kill whatever's attacking us; if we're still in combat after a
    // grace period, run away from it instead, then retry the original teleport once combat clears.
    private void StartResolvingCombat(Vector3 destination)
    {
        combatResolveDestination = destination;
        combatResolveFleeing = false;
        phase = TeleportPhase.ResolvingCombat;
        phaseTicks = 0;
        phaseStartMs = NowMs();
    }

    // Flees directly away from the current target (almost always whatever's attacking us in this
    // situation); falls back to retreating toward where we stood before the teleport attempt, then to
    // an arbitrary direction, if there's no target to read a threat position from.
    private Vector3 ComputeFleeTarget()
    {
        Vector3 playerPos = objects.LocalPlayer?.Position ?? pendingPreTeleportPos;
        Vector3 threatPos = targets.Target?.Position ?? pendingPreTeleportPos;

        Vector3 away = new(playerPos.X - threatPos.X, 0f, playerPos.Z - threatPos.Z);
        if (away.LengthSquared() < 1f)
            away = new Vector3(1f, 0f, 0f);

        return playerPos + Vector3.Normalize(away) * CombatFleeDistance;
    }

    // Periodic full-state snapshot so a stuck/looping phase is visible in the log without needing a breakpoint.
    private void LogHeartbeat()
    {
        long now = NowMs();
        if (now - lastHeartbeatMs < HeartbeatIntervalMs)
            return;
        lastHeartbeatMs = now;

        Vector3 pos = objects.LocalPlayer?.Position ?? Vector3.NaN;
        float jumpDist = Dist2D(pos, pendingPreTeleportPos);
        log.Debug(
            "[FateNav] Heartbeat: phase={Phase} ticks={Ticks} elapsedMs={ElapsedMs} pos={Pos:F1} pendingDest={Dest:F1} destShard={DestShard:F1} jumpDist={JumpDist:F1} betweenAreas={BetweenAreas} vnav(Pathfinding={Pathfinding} Running={Running} NavmeshReady={NavmeshReady}) lifestreamBusy={Busy}",
            phase, phaseTicks, PhaseElapsedMs, pos, pendingDestination, pendingDestShardPos, jumpDist, player.IsBetweenAreas(), SafeIsPathfinding(), SafeIsRunning(), SafeIsNavmeshReady(), SafeIsBusy());
    }

    private AethernetShard? FindNearestShardStruct(Vector3 point)
    {
        if (!ZoneAetherytes.TryGetValue((ushort)clientState.TerritoryType, out IReadOnlyList<AethernetShard>? shards))
        {
            log.Debug("[FateNav] FindNearestShardStruct: zone {Zone} has no known aethernet shards", clientState.TerritoryType);
            return null;
        }

        return shards
            .OrderBy(s => Dist2D(s.Position, point))
            .Select(s => (AethernetShard?)s)
            .FirstOrDefault();
    }

    private bool SafeIsBusy()
    {
        try { return lifestream.IsBusy(); }
        catch (Exception ex) { log.Warning(ex, "[FateNav] lifestream.IsBusy() failed"); return false; }
    }

    private bool SafeIsNavmeshReady()
    {
        try { return navIsReady.HasFunction && navIsReady.InvokeFunc(); }
        catch (Exception ex) { log.Warning(ex, "[FateNav] vnav.IsNavmeshReady() failed"); return true; }
    }

    private bool SafeIsPathfinding()
    {
        try { return vnav.IsPathfinding(); }
        catch (Exception ex) { log.Warning(ex, "[FateNav] vnav.IsPathfinding() failed"); return false; }
    }

    private bool SafeIsRunning()
    {
        try { return vnav.IsRunning(); }
        catch (Exception ex) { log.Warning(ex, "[FateNav] vnav.IsRunning() failed"); return false; }
    }

    private void SafeStopVnav()
    {
        try { vnav.Stop(); }
        catch (Exception ex) { log.Warning(ex, "[FateNav] vnav.Stop() failed"); }
    }

    private void TryMount(Vector3 destination)
    {
        if (player.IsMounted() || player.IsMounting()) return;
        if (Dist2D(player.Position, destination) < navigationConfig.MountRange) return;
        if (player.Conditions[ConditionFlag.InCombat] || player.IsCasting() || player.IsBetweenAreas() || player.IsInteracting()) return;
        if (!Actions.MountRoulette.CanCast()) return;

        log.Debug("[FateNav] TryMount: casting MountRoulette");
        try { Actions.MountRoulette.Cast(); }
        catch (Exception ex) { log.Warning(ex, "[FateNav] MountRoulette cast failed"); }
    }

    // World-space overlay so the current phase/target is visible in-game, not just in the log.
    public void Render()
    {
        if (phase == TeleportPhase.None || objects.LocalPlayer is not { } p)
            return;

        Vector3 from = p.Position;
        switch (phase)
        {
            case TeleportPhase.WalkingToSourceShard:
                overlay.StrokeLine(from, pendingSourceShardPos, ShardPathColor);
                overlay.StrokeCircle(pendingSourceShardPos, ShardInteractRadius, ShardPathColor);
                break;
            case TeleportPhase.WaitingForStart:
            case TeleportPhase.WaitingForEnd:
                overlay.StrokeCircle(pendingDestShardPos, ArrivedAtAetheryteRadius, TeleportWaitColor);
                break;
            case TeleportPhase.Settling:
                overlay.StrokeCircle(pendingDestShardPos, ArrivedAtAetheryteRadius, TeleportWaitColor);
                break;
            case TeleportPhase.WalkingToDestination:
                overlay.StrokeLine(from, pendingDestination, FinalPathColor);
                overlay.StrokeCircle(pendingDestination, EventArrivalRadius, FinalPathColor);
                break;
        }
    }

    private static float Dist2D(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }
}
