using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Ocelot.Actions;
using Ocelot.Graphics;
using Ocelot.Ipc.BossMod;
using Ocelot.Ipc.Lifestream;
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

public partial class FateNavigationService(
    IDalamudPluginInterface pluginInterface,
    IVNavmeshIpc vnav,
    ILifestreamIpc lifestream,
    IClientState clientState,
    IObjectTable objects,
    IPlayer player,
    IOverlayRenderer overlay,
    NavigationConfig navigationConfig,
    HostileDetectionService hostileDetection,
    HostileDetectionConfig hostileDetectionConfig,
    IPluginLog log
) : IOnUpdate, IOnRender
{
    private readonly ICallGateSubscriber<bool> navIsReady = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
    // Not wrapped by Ocelot's IVNavmeshIpc — the real live polyline vnav is currently following,
    // distinct from any straight-line target we asked it to walk to (see SafeGetPathWaypoints).
    private readonly ICallGateSubscriber<List<Vector3>> pathListWaypoints = pluginInterface.GetIpcSubscriber<List<Vector3>>("vnavmesh.Path.ListWaypoints");

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
                new(3664, new Vector3(679.6825f, 297.2915f, 660.0288f)), // Camp Vrdelnis
                new(3665, new Vector3(-356.466f, 286.030f, 758.449f)), // Zuprtik Point
                new(3666, new Vector3(-689.387f, 276.539f, -292.164f)), // Ljeban Point
                new(3667, new Vector3( 106.370f, 300.953f,  -130.815f)), // Hrmovir Point
            },
        };


    // From BOCCHI NavigationConstants
    private const float MaxDirectWalkDistance = 80f;
    private const float EventArrivalRadius = 10f;
    private const float ShardInteractRadius = 2f;
    // Wider than ShardInteractRadius — matches BOCCHI's IsAlreadyAtAetheryte arrivedRadius
    // (post-TP landings and menu-open range are often several yards from the crystal).
    private const float ArrivedAtAetheryteRadius = 12f;
    // These are wall-clock durations (ms), NOT frame counts — Update() is not guaranteed to run at
    // 60fps (observed real rates are much higher), so a tick-count timeout would fire far too early.
    private const long WalkToShardTimeoutMs = 30_000; // floor for short hops
    // A flat timeout doesn't scale with actual distance — a shard genuinely far away (and/or walked
    // on foot rather than mounted) can still be making real progress well past 30s. Timeout instead
    // scales with the distance measured at phase entry, using a conservative unmounted-speed floor so
    // it can't be gamed into a huge cap by a bad reading, plus a flat overhead buffer for mount-cast/
    // pathing startup. Never smaller than WalkToShardTimeoutMs so short hops keep the original bound.
    private const float MinShardWalkSpeedYalms = 6f;
    private const long WalkToShardTimeoutBufferMs = 15_000;
    // How often a skipped/failed mount attempt gets retried during travel (see TryMount).
    private const long MountRetryIntervalMs = 3_000;
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
    // Rough average movement speed for ETA estimates (accounts for mount ramp-up/dismount time).
    private const float EstimatedMoveSpeed = 10f; // yalms/sec
    private const float EstimatedTeleportOverheadSeconds = 5f; // menu + animation overhead for an aethernet hop
    // Busy/BetweenAreas flags can race with the actual warp (or never flip for an instant same-zone
    // hop) — a real position jump away from where we stood when we fired the teleport is the only
    // proof it actually happened. Aethernet shards in Bozja are always much further apart than this.
    private const float TeleportJumpThreshold = 15f;

    private enum TeleportPhase { None, WalkingToSourceShard, WaitingForStart, WaitingForEnd, Settling, WalkingToDestination }

    private static readonly Color ShardPathColor = new(0.3f, 0.6f, 1f); // blue — walking to source shard
    private static readonly Color TeleportWaitColor = new(1f, 0.8f, 0.1f); // yellow — teleport in progress
    private static readonly Color FinalPathColor = new(0.2f, 1f, 0.3f); // green — final walk to goal
    private static readonly Color AvoidanceSteerColor = new(1f, 0.1f, 0.8f); // magenta — hostile-avoidance steering target
    private static readonly Color VnavRealPathColor = new(1f, 1f, 1f); // white — vnav's own live path, not Botja's plan

    private const long HeartbeatIntervalMs = 1_000; // log a state snapshot this often during waits
    private const long WalkToDestinationTimeoutMs = 60_000; // ~60s safety timeout
    // Below this, an "arrived" WalkingToDestination phase almost certainly means the player was
    // already standing at the destination when navigation started (e.g. Auto Mode starting right at
    // a FATE) rather than having actually travelled there — don't dismount for that trivial case.
    private const long ArrivalDismountMinPhaseElapsedMs = 500;
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
    // Wider snap radius specifically for avoidance waypoints — these are COMPUTED points (the
    // obstacle planner has zero navmesh/terrain awareness), not curated data like shard coordinates,
    // so they can legitimately land a few yalms off the nearest real walkable polygon.
    private const float AvoidanceMeshSnapHalfExtentXZ = 15f;
    // vnav's plain floor query only searches a narrow/implicit height range around the GIVEN Y — the
    // obstacle planner's linearly-interpolated waypoint Y can be meaningfully off the REAL ground
    // height across Bozja's varied terrain (confirmed directly: a waypoint was computed floating in
    // mid-air, so the floor query genuinely couldn't find the real ground because of the bad Y guess,
    // not because no ground existed nearby). Used as a wide vertical fallback search specifically for
    // avoidance waypoints, never for curated positions (shard/FATE coordinates), whose Y is already
    // trustworthy.
    private const float AvoidanceMeshSnapHalfExtentY = 50f;
    // Confirmed via a real repro log: with the wide 15y avoidance search radius, vnav.FindPointOnFloor
    // can return a "found" point whose X/Z sits ~15y away from the query — not just a corrected Y at
    // the same X/Z as the comment above assumes — silently relocating a carefully-planned detour
    // waypoint somewhere unrelated (in that log, right back toward the obstacles it was meant to
    // avoid). Any result drifting further than this is treated the same as "no polygon found".
    private const float MaxMeshSnapDriftYalms = 5f;
    private const float AvoidanceFinalApproachBuffer = 20f;
    // Upper bound on how long a plan is trusted without re-checking it against live hostile
    // data — only actually rebuilds if HostilesChangedSincePlan says something real moved/turned
    // (see below); AnyPlanSegmentBlocked (checked every tick) still forces an early replan
    // regardless of this timer if a mob wanders/turns into the currently-followed path.
    private const long AvoidanceReplanIntervalMs = 1_500;
    // Below this much movement/turning since the last plan, a hostile is treated as unchanged —
    // rebuilding the whole visibility graph for sub-yalm wander would just reshuffle an equally
    // valid route (tie-breaking noise) and restart movement for no real benefit.
    private const float AvoidanceReplanMoveThresholdYalms = 2f;
    private const float AvoidanceReplanFacingThresholdRadians = 0.26f; // ~15 degrees
    // Arrival radius for an intermediate avoidance waypoint — tighter than the real destination's
    // arrival range so the character actually rounds each corner instead of cutting through it.
    // Deliberately kept BELOW HostileDetectionConfig.AvoidanceSafetyMarginYalms's default (2.5y):
    // this value is also passed straight through as vnav's own arrival tolerance for that hop
    // (PathfindAndMoveCloseTo stops once within this distance of the target), so if it were EQUAL
    // to or larger than the hard clearance margin, vnav could legitimately stop short in a way that
    // eats into (or eliminates) the very clearance the waypoint was computed to guarantee — reported
    // by the user as still occasionally clipping a Sound mob's circle despite a seemingly-clear plan.
    private const float AvoidanceWaypointArrivalRadius = 2f;
    // Skip re-issuing StartMove unless the current waypoint moved meaningfully since it was last
    // issued — filters out tiny per-replan drift so movement stays smooth instead of restarting
    // vnav's pathfind for every negligible adjustment.
    private const float AvoidanceWaypointChangeThreshold = 3f;
    // Hard aggro limits mean "no safe route exists" is a real possible outcome, not just an edge
    // case — require this many consecutive ms of failed replans (not a single bad frame) before
    // surfacing IsAvoidanceStuck and stopping movement, so one flaky scan tick can't false-trigger it.
    private const long AvoidanceStuckConfirmMs = 1_000;
    // Same debounce idea as AvoidanceStuckConfirmMs, but for the opposite transition: a single tick
    // reporting zero route-relevant obstacles (e.g. a live-position jitter nudging a borderline
    // hostile across the inclusion threshold for one frame) must not tear down an already-good plan
    // and force a full replan+restart the very next tick it reappears — confirmed via a real repro
    // log showing "noPlanYet=True" on literally every replan for 1785 consecutive ticks with zero
    // player movement, i.e. the plan was being destroyed and rebuilt from scratch every single tick.
    private const long AvoidanceObstaclesGoneConfirmMs = 500;
    // Same idea again, for segmentBlocked specifically: AnyRealPathSegmentBlocked checks vnav's OWN
    // live path, which depends on whichever waypoint we last commanded it toward — so replanning to
    // a different waypoint changes that live path, which can flip segmentBlocked back on, which
    // replans again, forever. Confirmed via a real repro log: two different N/N+2-obstacle plans
    // alternating every single tick (each one "blocked" by the other's own live-path side effect)
    // with the player frozen in place the whole time. Requiring this much sustained agreement before
    // acting lets vnav's live path settle on the currently-committed target first.
    private const long AvoidanceSegmentBlockedConfirmMs = 300;
    // Caps how much of vnav's real path (the "white line") feeds into the obstacle planner's search
    // area — only the nearby curvature can matter for shaping a detour; without this, a long-distance
    // real route bowing out far from the straight line (e.g. a mountain pass midway through a
    // thousand-yalm journey) would balloon the search box far past where any obstacle could matter.
    private const float AvoidanceSpineLookaheadYalms = 100f;
    // Bounds retries of a single avoidance waypoint vnav genuinely can't reach (confirmed via real
    // repro logs of the removed mesh-snap pre-check) — matches MaxMovementStallRetries' role for the
    // non-avoidance phases, just scoped to one waypoint instead of a whole phase.
    private const int MaxAvoidanceStallRetries = 3;
    // Separate, waypoint-identity-independent safety net: AvoidanceStallRetries resets on any
    // cosmetically different waypoint (even a few cm apart from hostile jitter), so a terrain gap
    // the planner keeps routing through (confirmed via real repro logs: vnav's own pathfind failing
    // outright, "failed to find polygon on a mesh", for a whole corridor of nearly-identical dead
    // points) can retry forever without ever tripping that counter, since every attempt looks "new".
    // This instead watches real progress toward the actual destination regardless of which waypoint
    // produced it — matches the historical 10s precedent from this file's earlier avoidance designs.
    private const long AvoidanceProgressTimeoutMs = 10_000;
    // Ignore remaining-distance noise below this (route recompute jitter, sub-yalm position drift)
    // so a route that's merely holding steady isn't mistaken for "still improving".
    private const float AvoidanceProgressEpsilonYalms = 1f;

    private TeleportPhase phase;
    private Vector3 pendingDestination;
    private string pendingDestinationName = string.Empty;
    private Vector3 pendingSourceShardPos;
    private Vector3 pendingDestShardPos;
    private Vector3 pendingPreTeleportPos;
    private int phaseTicks; // diagnostic Update() call counter only — NOT a reliable time unit, see timeouts above
    private long phaseStartMs;
    private long shardWalkTimeoutMs; // distance-scaled timeout for the current WalkingToSourceShard phase
    private long nextMountAttemptMs; // throttles repeated TryMount attempts during travel
    private bool wasInCombat; // detects the InCombat falling edge so mounting resumes immediately, not after the throttle
    private long lastHeartbeatMs;
    private long lastMoveIssuedMs;
    private int movementStallRetries;
    private int teleportFailFastRetries;
    // Current detour route (real world XZ waypoints, last one always the real destination) — null
    // means "no obstacle currently requires a detour". Driven waypoint-by-waypoint via
    // vnav.PathfindAndMoveCloseTo — vnav.FollowPath ("Path.MoveTo", whole polyline in one call) was
    // tried twice this session and confirmed not to actually move the character either time; don't
    // reintroduce it without new evidence it's been fixed on vnavmesh's side.
    private List<Vector3>? plannedAvoidancePath;
    private int avoidanceWaypointIndex;
    private Vector3 lastIssuedAvoidanceWaypoint = new(float.MaxValue, 0f, float.MaxValue);
    private int lastIssuedAvoidanceWaypointIndex = -1;
    private long lastAvoidancePlanMs;
    // 0 while replanning is succeeding; set to the first failure's timestamp once it starts failing,
    // so IsAvoidanceStuck only fires after AvoidanceStuckConfirmMs of sustained failure, not one blip.
    private long avoidanceNoPathSinceMs;
    // Mirrors avoidanceNoPathSinceMs for the opposite case — 0 while obstacles are present, set to
    // the first all-clear tick's timestamp once obstacles.Count hits 0, so a real plan isn't dropped
    // over a single blip tick (see AvoidanceObstaclesGoneConfirmMs).
    private long obstaclesGoneSinceMs;
    // 0 while the current plan looks clear; set to the first tick it looks blocked, so a genuinely
    // transient/self-inflicted blip (see AvoidanceSegmentBlockedConfirmMs) can't force a replan alone.
    private long segmentBlockedSinceMs;
    // Best (smallest) remaining distance to the real destination seen so far this avoidance episode,
    // and when it was last meaningfully improved — see AvoidanceProgressTimeoutMs.
    private float avoidanceBestRemainingDist = float.MaxValue;
    private long avoidanceLastProgressMs;
    // Logs the no-progress warning once per stuck episode instead of spamming every tick.
    private bool avoidanceNoProgressWarned;
    // Consecutive stalled retries of the CURRENT avoidance waypoint — reset whenever the waypoint
    // itself changes (a new waypoint deserves a fresh chance), not on every replan.
    private int avoidanceStallRetries;
    // Hostile position/facing snapshot from the last successful plan, keyed by ObjectId — lets the
    // timer-based replan check skip rebuilding when nothing actually moved/turned meaningfully.
    private Dictionary<ulong, (Vector3 Position, float Facing)> lastPlanHostileSnapshot = [];
    // ObjectIds the player is currently standing inside the REAL (unpadded) aggro shape of — used
    // only to log once on entry instead of spamming every tick while still inside.
    private HashSet<ulong> hostilesInsideZone = [];

    private static long NowMs() => Environment.TickCount64;
    private long PhaseElapsedMs => NowMs() - phaseStartMs;

    // True while any navigation phase (walk/teleport hop/settle) is in progress — used by
    // CombatControlService to hold off auto-rotation while we're travelling, not fighting.
    public bool IsNavigating => phase != TeleportPhase.None;

    // True when no route exists that avoids every nearby sight/sound aggro radius — those are hard
    // limits, so movement is stopped instead of falling back to an unsafe direct walk.
    public bool IsAvoidanceStuck { get; private set; }

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

        float shardDist = objects.LocalPlayer is { } p ? Dist2D(p.Position, snappedShardPos) : 0f;
        shardWalkTimeoutMs = Math.Max(WalkToShardTimeoutMs, (long)(shardDist / MinShardWalkSpeedYalms * 1000) + WalkToShardTimeoutBufferMs);

        TryMount(snappedShardPos);
        log.Debug("[FateNav] WalkToShardThenAethernet: walking to shard {ShardPos} (raw {RawShardPos}, dist {Dist:F1}, timeout {Timeout}ms), phase -> WalkingToSourceShard", snappedShardPos, shardPos, shardDist, shardWalkTimeoutMs);
        // Goes through avoidance first — a direct unconditional StartMove here would aim the very
        // first movement command of the journey straight at the raw shard position, bypassing
        // obstacle-awareness for that one command (avoidance would only take over on the NEXT tick).
        if (!UpdateHostileAvoidance(snappedShardPos, ShardInteractRadius))
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
        // Goes through avoidance first — see WalkToShardThenAethernet for why the very first movement
        // command of a journey shouldn't bypass it with a direct unconditional StartMove.
        if (!UpdateHostileAvoidance(destination, EventArrivalRadius))
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

        // Lifestream refuses aethernet teleports while InCombat — walking is unaffected by combat, so
        // just keep moving toward the destination directly instead of pausing to wait it out.
        if (player.Conditions[ConditionFlag.InCombat])
        {
            log.Debug("[FateNav] PathViaAethernet({Destination}): in combat, teleporter unusable — walking directly instead", destination);
            PathTo(destination);
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
            // No manual dismount here — aethernet teleport works fine while mounted, and forcing one
            // dismounted the player the instant Auto Mode started if already standing at a shard.
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
            // No manual dismount here — see the matching comment in PathViaAethernet.
            bool queued = lifestream.AethernetTeleportByPlaceNameId(shard.Value.PlaceNameId);
            if (!queued)
                log.Warning("[FateNav] TeleportToward({Destination}): lifestream.AethernetTeleportByPlaceNameId({PlaceNameId}) returned false", destination, shard.Value.PlaceNameId);
        }
        catch (Exception ex) { log.Warning(ex, "[FateNav] lifestream.AethernetTeleportByPlaceNameId({PlaceNameId}) failed", shard.Value.PlaceNameId); }
    }

    // Always stop prior movement before issuing a new one — matches BOCCHI's PathfindToChain
    // "Stop prior movement" step (Path.Stop alone does not cancel a pending SimpleMove task).
    private void StartMove(Vector3 destination, float range, float snapHalfExtentXZ = MeshSnapHalfExtentXZ, float snapHalfExtentY = 0f)
    {
        SafeStopVnav();
        lastMoveIssuedMs = NowMs();
        Vector3 target = TrySnapToMesh(destination, snapHalfExtentXZ, snapHalfExtentY, out Vector3 snapped) ? snapped : destination;
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

    // Drives the visibility-graph obstacle planner and follows the resulting detour, one waypoint at
    // a time via vnav.PathfindAndMoveCloseTo (vnav.FollowPath was tried twice this session as a way
    // to submit the whole polyline in one call and avoid per-corner restarts, but confirmed both
    // times to never actually move the character — don't retry it without new evidence it's fixed).
    // Sight/sound aggro radii are hard limits — if no route avoiding every one of them exists, this
    // stops movement (IsAvoidanceStuck) rather than falling back to an unguarded walk.
    private bool UpdateHostileAvoidance(Vector3 destination, float arrivalRange)
    {
        if (objects.LocalPlayer is not { } currentPlayer)
            return false;

        // Ground-truth check, independent of whether avoidance is even enabled or currently
        // succeeding — the plain fact a regression here is supposed to prevent.
        LogHostileZoneEntry(currentPlayer.Position);

        if (!hostileDetectionConfig.EnableNavmeshAvoidance)
        {
            ClearAvoidancePlan();
            return false;
        }

        Vector3 currentPos = currentPlayer.Position;
        float remainingDist = Dist2D(currentPos, destination);
        if (remainingDist <= arrivalRange + AvoidanceFinalApproachBuffer)
        {
            ClearAvoidancePlan();
            return false;
        }

        var relevantHostiles = hostileDetection.GetRouteRelevantHostiles(
            currentPos, destination, hostileDetectionConfig.SoundAggroRangeYalms, hostileDetectionConfig.AvoidanceSafetyMarginYalms);

        // vnav's own live path (the "white line") can curve away from the straight line above due to
        // real terrain — check it too, in parallel, so a hostile near the ACTUAL route is never
        // missed just because it sits off the idealized straight line to the destination. Also kept
        // around below to shape the obstacle planner's search area around the real route's curvature
        // instead of an idealized straight line (never as a literal move target — see ObstaclePathPlanner.FindPath).
        List<Vector3> realPath = SafeGetPathWaypoints();
        if (realPath.Count > 0)
        {
            List<Vector3> polyline = [currentPos, .. realPath];
            var realPathHostiles = hostileDetection.GetHostilesNearPath(
                polyline, hostileDetectionConfig.SoundAggroRangeYalms, hostileDetectionConfig.AvoidanceSafetyMarginYalms);
            if (realPathHostiles.Count > 0)
            {
                var seenIds = relevantHostiles.Select(h => h.ObjectId).ToHashSet();
                relevantHostiles = relevantHostiles.Concat(realPathHostiles.Where(h => !seenIds.Contains(h.ObjectId))).ToList();
            }
        }

        // Trimmed to the nearby lookahead only (see AvoidanceSpineLookaheadYalms) — passed to
        // FindPath purely to shape its search area, never as a move target.
        List<Vector3> realPathSpine = [];
        {
            float traveled = 0f;
            Vector3 prev = currentPos;
            foreach (var point in realPath)
            {
                traveled += Dist2D(prev, point);
                realPathSpine.Add(point);
                prev = point;
                if (traveled >= AvoidanceSpineLookaheadYalms)
                    break;
            }
        }

        var obstacles = BuildObstacles(relevantHostiles);

        if (obstacles.Count == 0)
        {
            // Don't tear down an existing, already-good plan the instant a single tick reports zero
            // obstacles — only hand control back to the caller's own direct StartMove once that's
            // held for a sustained window (see AvoidanceObstaclesGoneConfirmMs for why).
            if (plannedAvoidancePath == null)
                return false;

            if (obstaclesGoneSinceMs == 0)
                obstaclesGoneSinceMs = NowMs();

            if (NowMs() - obstaclesGoneSinceMs <= AvoidanceObstaclesGoneConfirmMs)
            {
                // Still within the grace window — fall through below and keep following the existing
                // plan rather than reacting to one single all-clear tick.
            }
            else
            {
                ClearAvoidancePlan();
                return false;
            }
        }
        else
        {
            obstaclesGoneSinceMs = 0;
        }

        // Diagnostic-only, doesn't alter movement: avoidanceStallRetries resets on any cosmetically
        // different waypoint (even sub-yalm hostile jitter), so a genuine terrain gap the planner
        // keeps routing through regardless of the exact waypoint (confirmed via real repro logs:
        // vnav's own pathfind failing outright, "failed to find polygon on a mesh", for a whole
        // corridor of nearly-identical dead points) can retry forever without that counter ever
        // tripping, since every attempt looks "new". This instead watches real progress toward the
        // actual destination regardless of which waypoint produced it.
        if (remainingDist < avoidanceBestRemainingDist - AvoidanceProgressEpsilonYalms)
        {
            avoidanceBestRemainingDist = remainingDist;
            avoidanceLastProgressMs = NowMs();
            avoidanceNoProgressWarned = false;
        }
        else if (avoidanceLastProgressMs == 0)
        {
            avoidanceLastProgressMs = NowMs();
        }
        else if (!avoidanceNoProgressWarned && NowMs() - avoidanceLastProgressMs > AvoidanceProgressTimeoutMs)
        {
            avoidanceNoProgressWarned = true;
            log.Warning("[FateNav] Hostile avoidance: no real progress toward {Destination} for over {TimeoutMs}ms despite repeated replans — likely routing through terrain vnav can't actually reach",
                destination, AvoidanceProgressTimeoutMs);
        }

        bool noPlanYet = plannedAvoidancePath == null;
        // Checks the real vnav route too, not just the flat plan's idealized straight segments — a
        // flat segment can look clear while the real terrain-following detour to walk it swings
        // closer to a hostile than the straight-line check ever accounted for.
        bool rawSegmentBlocked = !noPlanYet && (AnyPlanSegmentBlocked(obstacles) || AnyRealPathSegmentBlocked(currentPos, obstacles));
        if (!rawSegmentBlocked)
            segmentBlockedSinceMs = 0;
        else if (segmentBlockedSinceMs == 0)
            segmentBlockedSinceMs = NowMs();
        bool segmentBlocked = rawSegmentBlocked && NowMs() - segmentBlockedSinceMs > AvoidanceSegmentBlockedConfirmMs;
        bool timerDue = !noPlanYet && !segmentBlocked && NowMs() - lastAvoidancePlanMs > AvoidanceReplanIntervalMs;
        bool hostilesMoved = timerDue && HostilesChangedSincePlan(relevantHostiles);
        bool needsReplan = noPlanYet || segmentBlocked || (timerDue && hostilesMoved);

        if (needsReplan)
        {
            // Tags WHY a replan is happening — distinguishes "just building the first plan" from
            // "an existing route got invalidated" or "periodic recheck found hostiles moved", so a
            // suspiciously frequent replan cadence (a likely cause of jittery/regressed steering) is
            // visible in the log instead of just inferred from timestamps.
            log.Debug("[FateNav] Hostile avoidance: replanning (noPlanYet={NoPlanYet}, segmentBlocked={SegmentBlocked}, hostilesMoved={HostilesMoved})",
                noPlanYet, segmentBlocked, hostilesMoved);
            lastAvoidancePlanMs = NowMs();
            List<Vector3>? path = ObstaclePathPlanner.FindPath(currentPos, destination, obstacles, hostileDetectionConfig.AvoidanceComfortDistanceYalms, realPathSpine);
            if (path == null)
            {
                if (avoidanceNoPathSinceMs == 0)
                    avoidanceNoPathSinceMs = NowMs();

                if (NowMs() - avoidanceNoPathSinceMs > AvoidanceStuckConfirmMs)
                {
                    if (!IsAvoidanceStuck)
                        log.Warning("[FateNav] Hostile avoidance: no safe route to {Destination} clears every nearby aggro radius — stopping instead of risking a pull", destination);
                    IsAvoidanceStuck = true;
                    SafeStopVnav();
                    return true;
                }

                if (plannedAvoidancePath == null)
                {
                    // Never had a safe plan yet — do NOT hand control back to the caller's own
                    // unguarded StartMove while we can't confirm ANY route clears every nearby
                    // aggro radius. Reported by the user directly: this let the very first move
                    // after starting navigation walk straight into a cone, right when avoidance had
                    // JUST determined the direct path wasn't safe. Hold position and keep retrying
                    // until either a plan succeeds or the stuck-confirm timeout above fires.
                    SafeStopVnav();
                    return true;
                }

                // Otherwise: a brief blip against an EXISTING, already-good plan (mob mid-turn,
                // transient scan gap) — fall through and keep following it below rather than
                // reacting to one single failed replan.
            }
            else
            {
                avoidanceNoPathSinceMs = 0;
                // A fresh plan deserves a fresh debounce window too — without this, once the first
                // blip crossed AvoidanceSegmentBlockedConfirmMs a single time, segmentBlockedSinceMs
                // was never reset, so every tick after kept reading as "already over the threshold"
                // forever, bypassing the debounce entirely (confirmed via a real repro log: a replan
                // storm every ~15ms, literally every tick, for over half a second straight).
                segmentBlockedSinceMs = 0;
                IsAvoidanceStuck = false;
                // A genuinely new plan deserves a fresh chance — without this, a waypoint that kept
                // recomputing to the same dead value across several replans (each one resetting
                // IsAvoidanceStuck without resetting this) let the retry count climb indefinitely
                // across separate stuck-cycles instead of stopping cleanly after MaxAvoidanceStallRetries
                // each time (confirmed via a real repro log: "stalled 167 times in a row").
                avoidanceStallRetries = 0;
                plannedAvoidancePath = path;
                avoidanceWaypointIndex = 0;
                SnapshotPlanHostiles(relevantHostiles);
                // Full route dump — lets an in-game overlay oddity (e.g. an unexpectedly large
                // number of waypoints) be correlated directly against what was actually computed.
                log.Debug("[FateNav] Hostile avoidance: (re)planned {Count}-waypoint route around {ObstacleCount} obstacle(s): {Waypoints}",
                    path.Count, obstacles.Count, string.Join(" -> ", path));
            }
        }

        if (plannedAvoidancePath is not { Count: > 0 } plan)
            return false;

        if (avoidanceWaypointIndex >= plan.Count)
            avoidanceWaypointIndex = plan.Count - 1;

        if (avoidanceWaypointIndex < plan.Count - 1
            && Dist2D(currentPos, plan[avoidanceWaypointIndex]) <= AvoidanceWaypointArrivalRadius)
        {
            avoidanceWaypointIndex++;
        }

        Vector3 waypoint = plan[avoidanceWaypointIndex];
        // Deliberately always the flat plan node, NOT a vnav real-path sub-node — tried that
        // (steering to vnav's next live-path waypoint instead) and confirmed it causes big loops:
        // once vnav reaches a close sub-node it goes idle, Path.ListWaypoints empties, and the code
        // falls back to re-commanding this SAME far node from the new position — recomputing a fresh
        // real route every few yalms that can diverge enough to send the character backward. A single
        // long-range PathfindAndMoveCloseTo call lets vnav walk its own real route autonomously
        // instead of being interrupted and re-pathed constantly. The real path is still consulted
        // for safety (AnyRealPathSegmentBlocked above), just never as the literal move target.

        // Advancing to a genuinely new INDEX in the plan must always count as a change, even if that
        // waypoint happens to sit close to the previous one (common near an obstacle, where the
        // planner can't always take a big shortcut) — confirmed as a real repro'd stuck cause: with
        // only a distance check, a waypoint closer than AvoidanceWaypointChangeThreshold to the last
        // ISSUED one never got a fresh StartMove at all, so the character just sat idle (vnav already
        // arrived at the old point) until the full 3s stall timer forced a retry, over and over for
        // every tightly-packed cluster of waypoints along the route.
        bool waypointChanged = avoidanceWaypointIndex != lastIssuedAvoidanceWaypointIndex
            || Dist2D(waypoint, lastIssuedAvoidanceWaypoint) > AvoidanceWaypointChangeThreshold;
        bool stalled = NowMs() - lastMoveIssuedMs > MovementStallRetryMs && !SafeIsPathfinding() && !SafeIsRunning();

        // Deliberately NOT pre-validating this waypoint against vnav.TryFindPointOnFloor anymore.
        // Four rounds of real repro logs proved that check was the wrong tool here: vnav's own
        // PathfindAndMoveCloseTo repeatedly succeeded (full polygon chain, milliseconds) toward
        // points that TryFindPointOnFloor insisted had "no nearby mesh polygon" — including the
        // literal FATE/shard destination itself. Every fix built on top of that check (skip dead
        // waypoints, substitute nearby points, reject far snaps, exclude only the final waypoint)
        // just chased new self-inflicted failure modes, culminating in the player being completely
        // frozen in place for 9+ seconds while the same "waypoint has no nearby mesh polygon" spam
        // repeated every ~15-30ms — even though vnav could clearly path there the whole time.
        // Trusting vnav's real pathfind (like every non-avoidance move already does) and relying on
        // the same stall-retry counter used elsewhere in this file is simpler and matches reality.
        if (waypointChanged)
            avoidanceStallRetries = 0;

        if (waypointChanged || stalled)
        {
            if (stalled && !waypointChanged)
            {
                avoidanceStallRetries++;
                if (avoidanceStallRetries > MaxAvoidanceStallRetries)
                {
                    if (!IsAvoidanceStuck)
                        log.Warning("[FateNav] Hostile avoidance: waypoint {Waypoint} ({Index}/{Count}) stalled {Retries} times in a row — vnav can't reach it, stopping instead of retrying forever",
                            waypoint, avoidanceWaypointIndex + 1, plan.Count, avoidanceStallRetries);
                    IsAvoidanceStuck = true;
                    SafeStopVnav();
                    return true;
                }
            }

            lastIssuedAvoidanceWaypoint = waypoint;
            lastIssuedAvoidanceWaypointIndex = avoidanceWaypointIndex;
            log.Debug("[FateNav] Hostile avoidance: steering to {Waypoint} ({Index}/{Count}) to clear nearby aggro radii", waypoint, avoidanceWaypointIndex + 1, plan.Count);
            StartMove(waypoint, AvoidanceWaypointArrivalRadius, AvoidanceMeshSnapHalfExtentXZ, AvoidanceMeshSnapHalfExtentY);
        }

        return true;
    }

    private void ClearAvoidancePlan()
    {
        plannedAvoidancePath = null;
        avoidanceWaypointIndex = 0;
        lastIssuedAvoidanceWaypoint = new Vector3(float.MaxValue, 0f, float.MaxValue);
        lastIssuedAvoidanceWaypointIndex = -1;
        avoidanceNoPathSinceMs = 0;
        obstaclesGoneSinceMs = 0;
        segmentBlockedSinceMs = 0;
        avoidanceStallRetries = 0;
        avoidanceBestRemainingDist = float.MaxValue;
        avoidanceLastProgressMs = 0;
        avoidanceNoProgressWarned = false;
        IsAvoidanceStuck = false;
        lastPlanHostileSnapshot = [];
    }

    // True if any hostile the current plan was built around has since moved or turned enough to
    // matter, or the relevant set itself changed (new one in range, one dropped out) — gates the
    // periodic timer replan so a static cluster of hostiles doesn't get needlessly rebuilt (and the
    // followed path restarted) every AvoidanceReplanIntervalMs for no real reason.
    private bool HostilesChangedSincePlan(IReadOnlyList<HostileDetectionService.HostileNpc> hostiles)
    {
        if (hostiles.Count != lastPlanHostileSnapshot.Count)
            return true;

        foreach (var hostile in hostiles)
        {
            if (!lastPlanHostileSnapshot.TryGetValue(hostile.ObjectId, out var prev))
                return true;

            if (Dist2D(hostile.Position, prev.Position) > AvoidanceReplanMoveThresholdYalms)
                return true;

            float facingDelta = MathF.Abs(NormalizeAngle(hostile.Facing - prev.Facing));
            if (facingDelta > AvoidanceReplanFacingThresholdRadians)
                return true;
        }

        return false;
    }

    private void SnapshotPlanHostiles(IReadOnlyList<HostileDetectionService.HostileNpc> hostiles)
    {
        lastPlanHostileSnapshot = hostiles.ToDictionary(h => h.ObjectId, h => (h.Position, h.Facing));
    }

    private static float NormalizeAngle(float radians)
    {
        while (radians > MathF.PI) radians -= 2f * MathF.PI;
        while (radians < -MathF.PI) radians += 2f * MathF.PI;
        return radians;
    }

    // Checks the WHOLE stored plan (not just the immediate next leg) against live obstacle data —
    // a mob wandering/turning could invalidate a segment further along the route, not only the one
    // currently being walked.
    private bool AnyPlanSegmentBlocked(IReadOnlyList<PathObstacle> obstacles)
    {
        if (obstacles.Count == 0 || plannedAvoidancePath is not { Count: > 1 } plan)
            return false;

        for (int i = 0; i < plan.Count - 1; i++)
        {
            if (ObstaclePathPlanner.IsSegmentBlocked(plan[i], plan[i + 1], obstacles))
                return true;
        }

        return false;
    }

    // Same obstacle test as AnyPlanSegmentBlocked, but against vnav's REAL route (what's actually
    // about to be walked) instead of the flat planner's idealized straight segments.
    private bool AnyRealPathSegmentBlocked(Vector3 currentPos, IReadOnlyList<PathObstacle> obstacles)
    {
        if (obstacles.Count == 0)
            return false;

        var realPath = SafeGetPathWaypoints();
        if (realPath.Count == 0)
            return false;

        Vector3 prev = currentPos;
        foreach (var point in realPath)
        {
            if (ObstaclePathPlanner.IsSegmentBlocked(prev, point, obstacles))
                return true;
            prev = point;
        }

        return false;
    }

    // Turns each route-relevant hostile into the real obstacle shape(s) for its aggro type — a wedge
    // for Sight (frontal cone only), a full circle for Sound (omnidirectional) — PLUS a small
    // always-on personal-space circle for every hostile regardless of type/facing, so a Sight mob's
    // side/back (outside its cone, otherwise unconstrained) still keeps a comfortable pass-by
    // distance instead of the path brushing right past its actual position. Magic-type hostiles
    // never reach this point (already excluded by GetRouteRelevantHostiles).
    private List<PathObstacle> BuildObstacles(IReadOnlyList<HostileDetectionService.HostileNpc> hostiles)
    {
        float margin = hostileDetectionConfig.AvoidanceSafetyMarginYalms;
        var obstacles = new List<PathObstacle>(hostiles.Count * 2);
        foreach (var hostile in hostiles)
        {
            Vector2 center = new(hostile.Position.X, hostile.Position.Z);
            switch (hostile.AggroType)
            {
                case AggroType.Sound:
                    obstacles.Add(PathObstacle.Circle(center, hostileDetectionConfig.SoundAggroRangeYalms + margin));
                    break;
                case AggroType.Sight:
                    obstacles.Add(PathObstacle.Wedge(
                        center, hostile.Facing, HostileDetectionService.FacingConeHalfAngleRadians, HostileDetectionService.FacingConeRangeYalms + margin));
                    break;
            }

            if (hostileDetectionConfig.AvoidancePersonalSpaceYalms > 0.01f)
                obstacles.Add(PathObstacle.Circle(center, hostileDetectionConfig.AvoidancePersonalSpaceYalms));
        }

        return obstacles;
    }

    // Logs the moment the player's ACTUAL position enters a hostile's real, UNPADDED aggro trigger
    // shape (no safety margin/comfort distance applied) — the plain fact hostile avoidance exists to
    // prevent, checked against every currently-scanned hostile (not just the ones the current plan
    // considered "route relevant"), so a regression anywhere (bad plan, stalled movement, avoidance
    // disabled, wrong aggro-type classification, etc.) becomes an unmissable warning in the log
    // instead of only being visible as an actual in-game pull. Only fires once per entry, not every
    // tick spent inside, via hostilesInsideZone tracking the entry/exit edge.
    //
    // Applies the SAME relevance exclusions as GetRouteRelevantHostiles (already pulled us/engaged
    // elsewhere/claimed by a FATE) — confirmed via a real repro log full of these warnings that
    // turned out to be entirely false positives: mobs already fighting something else, mobs that had
    // already pulled the player long before this navigation even started, and FATE mobs at the
    // literal destination (which avoidance correctly stopped steering around). None of those are
    // travel-safety failures, so logging them as "avoidance may have failed" was just noise hiding
    // the real signal.
    private void LogHostileZoneEntry(Vector3 currentPos)
    {
        var stillInside = new HashSet<ulong>();
        foreach (var hostile in hostileDetection.GetNearbyHostiles())
        {
            if (hostile.IsTargetingPlayer || hostile.IsEngagedElsewhere || hostile.FateId != 0)
                continue;

            bool inside = hostile.AggroType switch
            {
                AggroType.Sound => Dist2D(currentPos, hostile.Position) <= hostileDetectionConfig.SoundAggroRangeYalms,
                AggroType.Sight => IsInsideFacingCone(currentPos, hostile.Position, hostile.Facing,
                    HostileDetectionService.FacingConeHalfAngleRadians, HostileDetectionService.FacingConeRangeYalms),
                _ => false,
            };

            if (!inside)
                continue;

            stillInside.Add(hostile.ObjectId);
            if (!hostilesInsideZone.Contains(hostile.ObjectId))
            {
                log.Warning("[FateNav] Entered {AggroType} aggro zone of {Name} (id {DataId}) at {Distance:F1}y — avoidance may have failed",
                    hostile.AggroType, hostile.Name, hostile.DataId, Dist2D(currentPos, hostile.Position));
            }
        }

        hostilesInsideZone = stillInside;
    }

    private static bool IsInsideFacingCone(Vector3 point, Vector3 apex, float facing, float halfAngle, float range)
    {
        float dx = point.X - apex.X;
        float dz = point.Z - apex.Z;
        float dist = MathF.Sqrt(dx * dx + dz * dz);
        if (dist > range)
            return false;
        if (dist <= 0.01f)
            return true; // standing on top of the mob's own position is unsafe from every direction

        float toX = dx / dist, toZ = dz / dist;
        float forwardX = MathF.Sin(facing), forwardZ = MathF.Cos(facing);
        float cosAngle = forwardX * toX + forwardZ * toZ;
        return cosAngle >= MathF.Cos(halfAngle);
    }

    // A stale/bad stored coordinate (e.g. a shard's Y off by 100+ yalms) makes vnav hard-fail the
    // whole pathfind ("failed to find polygon on a mesh") instead of just walking somewhere close —
    // snap to the nearest real mesh point first. FindPointOnFloor is USUALLY just a floor raycast
    // that keeps X/Z fixed and only corrects Y — but confirmed via a real repro log, with a wide
    // enough halfExtentXZ it can also return a polygon whose X/Z is relocated far sideways from the
    // query (see MaxMeshSnapDriftYalms below, which guards against trusting that blindly).
    private Vector3 SnapToMesh(Vector3 destination) =>
        TrySnapToMesh(destination, MeshSnapHalfExtentXZ, 0f, out Vector3 snapped) ? snapped : destination;

    // Uses the Try* form (vnav does report this — its return value is just false/no-op-ish rather
    // than a thrown error) so a genuinely unreachable point gets logged/detected instead of silently
    // pathfinding to a spot vnav already told us has no nearby polygon. Confirmed via a real repro
    // log: an avoidance waypoint (a COMPUTED point, not curated data — the obstacle planner has zero
    // navmesh/terrain awareness) landed somewhere with no nearby polygon at all, and the old
    // always-fall-back-to-the-raw-point behavior retried that exact same dead point every ~3s
    // indefinitely ("failed to find polygon on a mesh", identical coordinates, repeated in the log).
    private bool TrySnapToMesh(Vector3 destination, float halfExtentXZ, float halfExtentY, out Vector3 snapped)
    {
        try
        {
            // halfExtentY > 0 means the caller's own Y guess is unreliable (avoidance waypoints) —
            // search a generous vertical range around it instead of vnav's narrow implicit floor-query
            // range, so a badly-guessed height doesn't hide real, nearby ground.
            bool found = halfExtentY > 0f
                ? vnav.TryFindPointOnMesh(destination, halfExtentXZ, halfExtentY, out snapped)
                : vnav.TryFindPointOnFloor(destination, halfExtentXZ, out snapped);

            if (found)
            {
                float drift = Dist2D(destination, snapped);
                if (drift > MaxMeshSnapDriftYalms)
                {
                    log.Warning("[FateNav] SnapToMesh: vnav relocated {Destination} to {Snapped} ({Drift:F1}y away, over the {Max}y cap) — rejecting, treating as unfound",
                        destination, snapped, drift, MaxMeshSnapDriftYalms);
                    snapped = destination;
                    return false;
                }

                return true;
            }

            log.Debug("[FateNav] SnapToMesh: vnav found no mesh polygon within {HalfExtent}y of {Destination}", halfExtentXZ, destination);
            snapped = destination;
            return false;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[FateNav] vnav.FindPointOnFloor failed for {Destination}", destination);
            snapped = destination;
            return false;
        }
    }

    // True once vnav has had a fair chance to pick up the last move request but still reports idle —
    // the tell-tale sign of a silently-dropped PathfindAndMoveCloseTo call (see MovementStallRetryMs).
    // Suppressed while under attack — combat interrupting movement (facing lock, hitstop, etc.) looks
    // identical to a genuinely dropped vnav request, but shouldn't burn through the stall-retry budget
    // and cancel the ENTIRE navigation session just because one hostile is currently hitting us.
    private bool IsMovementStalled() =>
        !hostileDetection.IsPlayerUnderAttack()
        && NowMs() - lastMoveIssuedMs > MovementStallRetryMs && !SafeIsPathfinding() && !SafeIsRunning();

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
            TryMount(pendingSourceShardPos);

            if (UpdateHostileAvoidance(pendingSourceShardPos, ShardInteractRadius))
                return;

            bool arrived = objects.LocalPlayer is { } player && Dist2D(player.Position, pendingSourceShardPos) <= ShardInteractRadius;
            if (arrived)
            {
                log.Debug("[FateNav] Update: WalkingToSourceShard done after {Ticks} ticks, continuing via aethernet", phaseTicks);
                PathViaAethernet(pendingDestination);
            }
            else if (PhaseElapsedMs > shardWalkTimeoutMs)
            {
                // Never reached the shard — proceeding to PathViaAethernet would fire the hop from
                // wherever we stalled, not from the shard. Abort to a direct walk instead.
                log.Debug("[FateNav] Update: WalkingToSourceShard timed out after {Ticks} ticks ({ElapsedMs}ms, timeout {Timeout}ms) without reaching shard, aborting to direct walk", phaseTicks, PhaseElapsedMs, shardWalkTimeoutMs);
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
                // Goes through avoidance first, same reasoning as PathTo/WalkToShardThenAethernet.
                if (!UpdateHostileAvoidance(pendingDestination, EventArrivalRadius))
                    StartMove(pendingDestination, EventArrivalRadius);
                phase = TeleportPhase.WalkingToDestination;
                phaseTicks = 0;
                phaseStartMs = NowMs();
                movementStallRetries = 0;
            }
        }
        else if (phase == TeleportPhase.WalkingToDestination)
        {
            TryMount(pendingDestination);

            if (UpdateHostileAvoidance(pendingDestination, EventArrivalRadius))
                return;

            bool arrived = objects.LocalPlayer is { } p && Dist2D(p.Position, pendingDestination) <= EventArrivalRadius;
            if (arrived || PhaseElapsedMs > WalkToDestinationTimeoutMs)
            {
                log.Debug("[FateNav] Update: WalkingToDestination done after {Ticks} ticks (Arrived={Arrived})", phaseTicks, arrived);
                if (arrived && PhaseElapsedMs > ArrivalDismountMinPhaseElapsedMs)
                {
                    Actions.Unmount.Cast();
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
    }

    private void CancelPending()
    {
        if (phase != TeleportPhase.None)
            log.Debug("[FateNav] CancelPending: clearing phase {Phase} after {Ticks} ticks", phase, phaseTicks);
        phase = TeleportPhase.None;
        phaseTicks = 0;
        phaseStartMs = NowMs();
        nextMountAttemptMs = 0;
        ClearAvoidancePlan();
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
        else
        {
            log.Debug("[FateNav] {Reason} after {Retries} retries, falling back to direct walk", reason, teleportFailFastRetries);
            PathTo(destination);
        }
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

    private List<Vector3> SafeGetPathWaypoints()
    {
        try { return pathListWaypoints.HasFunction ? pathListWaypoints.InvokeFunc() : []; }
        catch (Exception ex) { log.Warning(ex, "[FateNav] vnav.Path.ListWaypoints() failed"); return []; }
    }

    private void SafeStopVnav()
    {
        try { vnav.Stop(); }
        catch (Exception ex) { log.Warning(ex, "[FateNav] vnav.Stop() failed"); }
    }

    // Throttled rather than one-shot — a skipped attempt (still casting, mid-combat, CanCast false
    // for a transient reason) would otherwise never be retried for the rest of the journey, leaving
    // the player walking the whole way. Called every tick while actively traveling; the throttle
    // keeps that cheap instead of hammering CanCast()/Cast() at the project's ~170Hz tick rate.
    // Combat is an exception to the throttle: the moment InCombat clears, retry immediately instead
    // of potentially waiting out the rest of the interval before mounting back up.
    private void TryMount(Vector3 destination)
    {
        bool inCombat = player.Conditions[ConditionFlag.InCombat];
        if (wasInCombat && !inCombat)
            nextMountAttemptMs = 0;
        wasInCombat = inCombat;

        if (NowMs() < nextMountAttemptMs) return;
        nextMountAttemptMs = NowMs() + MountRetryIntervalMs;

        if (player.IsMounted() || player.IsMounting()) return;
        if (Dist2D(player.Position, destination) < navigationConfig.MountRange) return;
        if (player.IsCasting() || player.IsBetweenAreas() || player.IsInteracting()) return;

        // Mounting is blocked outright while fighting — sprint instead so combat doesn't reduce
        // travel to base run speed for however long it takes to clear.
        if (inCombat)
        {
            if (Actions.Sprint.CanCast())
            {
                try { Actions.Sprint.Cast(); }
                catch (Exception ex) { log.Warning(ex, "[FateNav] Sprint cast failed"); }
            }
            return;
        }

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

        // Shows the current avoidance detour route, separate from the underlying phase's own
        // destination line, so the two are never visually confused.
        if (plannedAvoidancePath is { Count: > 0 } avoidancePlan)
        {
            Vector3 prev = from;
            foreach (var point in avoidancePlan)
            {
                overlay.StrokeLine(prev, point, AvoidanceSteerColor);
                overlay.StrokeCircle(point, 1.5f, AvoidanceSteerColor);
                prev = point;
            }
        }

        // vnav's own real route to whatever target we last gave it — can diverge from the lines
        // above around terrain our own plan has no awareness of.
        if (SafeGetPathWaypoints() is { Count: > 0 } realPath)
        {
            Vector3 prev = from;
            foreach (var point in realPath)
            {
                overlay.StrokeLine(prev, point, VnavRealPathColor);
                prev = point;
            }
        }
    }

    private static float Dist2D(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }
}
