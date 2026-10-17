using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Botja.Services;

/// <summary>
/// Detects and tracks nearby hostile NPCs, their positions, and aggro status.
/// </summary>
public sealed unsafe partial class HostileDetectionService(
    IObjectTable objects,
    HostileDetectionConfig config,
    IGameGui gameGui
)
{
    // GetNearbyHostiles can be called several times per Update() tick (avoidance + UI) at this
    // project's confirmed ~170-180Hz real tick rate — only rescan IObjectTable this often.
    private const long HostileScanIntervalMs = 150;
    // Beyond an obstacle's own extent (sight/sound range) times this multiplier — perpendicular to
    // the route — a hostile is too far off to the side to matter for the current leg.
    private const float ThreatFalloffMultiplier = 2f;
    // Beyond an obstacle's own extent times this multiplier (real distance from the traveller), a
    // hostile is too far along a long route to matter for the *current* planning decision yet.
    private const float NearTermRangeMultiplier = 4f;
    // Sight-cone shape shared by the real heuristic, the debug overlay, and the obstacle planner — a
    // mob's frontal aggro wedge, real Rotation data, never verified against actual game aggro logic
    // since no aggro type/range is ever exposed to the client.
    internal const float FacingConeHalfAngleRadians = MathF.PI / 4f; // 45 degrees each side = 90 total
    internal const float FacingConeRangeYalms = 9f;

    // Standard enemy detection radius (yalms) — NPCs beyond this are ignored.
    public float DetectionRadiusYalms => config.DetectionRadiusYalms;

    private long lastScanMs = -HostileScanIntervalMs; // never-scanned sentinel that can't overflow the subtraction below
    private List<HostileNpc> cachedHostiles = [];

    /// <summary>
    /// Gets all hostile NPCs currently detected within DetectionRadiusYalms.
    /// Returns empty list if no player or no enemies found.
    /// </summary>
    public IReadOnlyList<HostileNpc> GetNearbyHostiles()
    {
        long now = Environment.TickCount64;
        if (now - lastScanMs < HostileScanIntervalMs)
            return cachedHostiles;
        lastScanMs = now;

        var player = objects.LocalPlayer;
        if (player == null)
        {
            ResetDebugScanCounters();
            cachedHostiles = [];
            return cachedHostiles;
        }

        float detectionRadius = config.DetectionRadiusYalms > 0f
            ? config.DetectionRadiusYalms
            : 100f;
        var hostiles = new List<HostileNpc>();

        int totalObjectCount = 0;
        int battleNpcCount = 0;
        int hostileFlaggedCount = 0;
        var friendlyNameplateAddresses = GetFriendlyNameplateAddresses();

        foreach (var obj in objects)
        {
            totalObjectCount++;

            // Only consider battle NPCs.
            if (obj is not IBattleNpc battleNpc)
                continue;
            battleNpcCount++;

            // Only include NPCs whose object status flags explicitly mark them hostile.
            if (!battleNpc.StatusFlags.HasFlag(StatusFlags.Hostile))
                continue;

            if (friendlyNameplateAddresses.Contains(battleNpc.Address))
                continue;

            // Allied/friendly battle NPCs (e.g. Bozja Resistance troops that join your party for a
            // CE) are added as temporary party/alliance members and must never count as hostile,
            // even if StatusFlags.Hostile is set on them.
            if (battleNpc.StatusFlags.HasFlag(StatusFlags.PartyMember)
                || battleNpc.StatusFlags.HasFlag(StatusFlags.AllianceMember)
                || battleNpc.BattleNpcKind == BattleNpcSubKind.NpcPartyMember)
                continue;
            hostileFlaggedCount++;

            // Skip dead or untargetable enemies.
            if (battleNpc.IsDead || !battleNpc.IsTargetable)
                continue;

            // Calculate 2D distance (ignore Y).
            float dx = battleNpc.Position.X - player.Position.X;
            float dz = battleNpc.Position.Z - player.Position.Z;
            float distance = MathF.Sqrt(dx * dx + dz * dz);

            // Skip enemies beyond detection radius.
            if (distance > detectionRadius)
                continue;

            // Check if this NPC is currently targeting the player, or has some other live target.
            // Uses the resolved TargetObject (null when untargeted) rather than comparing
            // TargetObjectId against 0 — FFXIV uses a non-zero sentinel for "no target", so that
            // comparison treated every idle hostile as "already engaged elsewhere".
            var target = battleNpc.TargetObject;
            bool isTargetingPlayer = target != null && target.GameObjectId == player.GameObjectId;

            // Already targeting something/someone else entirely — heuristic proxy for "occupied
            // elsewhere", but based on its actual live target rather than just the InCombat flag.
            bool isEngagedElsewhere = target != null && !isTargetingPlayer;

            hostiles.Add(new HostileNpc(
                ObjectId: battleNpc.GameObjectId,
                DataId: battleNpc.BaseId,
                Name: battleNpc.Name.ToString(),
                Position: battleNpc.Position,
                DistanceYalms: distance,
                IsTargetingPlayer: isTargetingPlayer,
                IsEngagedElsewhere: isEngagedElsewhere,
                Facing: battleNpc.Rotation,
                CurrentHp: battleNpc.CurrentHp,
                MaxHp: battleNpc.MaxHp,
                Rank: GetForayRank(battleNpc),
                AggroType: MobAggroData.Classify(battleNpc.BaseId),
                FateId: GetFateId(battleNpc)
            ));
        }

        UpdateDebugScanCounters(totalObjectCount, battleNpcCount, hostileFlaggedCount, hostiles.Count);

        // Sort by distance (closest first).
        cachedHostiles = [.. hostiles.OrderBy(h => h.DistanceYalms)];
        return cachedHostiles;
    }

    private static HashSet<nint> GetFriendlyNameplateAddresses()
    {
        var addresses = new HashSet<nint>();
        var uiModule = UIModule.Instance();
        var ui3d = uiModule == null ? null : uiModule->GetUI3DModule();
        if (ui3d == null)
            return addresses;

        for (int index = 0; index < ui3d->ObjectInfos.Length; index++)
        {
            ref var info = ref ui3d->ObjectInfos[index];
            if (info.GameObject != null
                && (info.NamePlateObjectKind == UIObjectKind.BattleNpcFriendly
                    || info.NamePlateObjectKindAdjusted == UIObjectKind.BattleNpcFriendly))
                addresses.Add((nint)info.GameObject);
        }

        return addresses;
    }

    private static byte GetForayRank(IBattleNpc battleNpc)
    {
        try
        {
            return ((BattleChara*)battleNpc.Address)->ForayInfo.Level;
        }
        catch
        {
            return battleNpc.Level;
        }
    }

    // Native GameObject.FateId (inherited by BattleChara) — the game's own, precise record of which
    // FATE (if any) claimed this mob, confirmed present via FFXIVClientStructs' shipped XML docs.
    // Far more reliable than guessing by radius around a FATE's nominal center: a mob just outside
    // that circle is still correctly excluded, and a random unrelated hostile that happens to wander
    // into a FATE's radius is correctly NOT excluded. 0 means "not claimed by any FATE".
    private static uint GetFateId(IBattleNpc battleNpc)
    {
        try
        {
            return ((BattleChara*)battleNpc.Address)->FateId;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Gets every nearby hostile relevant to route planning between two points — real aggro-type
    /// obstacle shape (wedge/circle) is decided by the caller from each hostile's AggroType; this
    /// only handles the exclusions (already pulled us, already fighting something else, magic-type
    /// never avoided) and the corridor-proximity inclusion filter (extent = each hostile's own
    /// sight/sound range, so a sound-type hostile's much larger radius is still considered from
    /// further off to the side than a sight-type one's narrower cone).
    /// </summary>
    public IReadOnlyList<HostileNpc> GetRouteRelevantHostiles(Vector3 from, Vector3 to, float soundRangeYalms, float safetyMarginYalms) =>
        GetHostilesNearPath([from, to], soundRangeYalms, safetyMarginYalms);

    /// <summary>
    /// Same relevance test as <see cref="GetRouteRelevantHostiles"/>, generalized to every segment of
    /// an arbitrary polyline instead of just the straight start-to-destination line — lets a caller
    /// check hostiles against vnav's own REAL (terrain-following) route, which can curve away from
    /// the idealized straight line and pass closer to a hostile than the straight-line check alone
    /// would ever catch.
    /// </summary>
    public IReadOnlyList<HostileNpc> GetHostilesNearPath(IReadOnlyList<Vector3> path, float soundRangeYalms, float safetyMarginYalms)
    {
        var result = new List<HostileNpc>();
        if (path.Count < 2)
            return result;

        foreach (var hostile in GetNearbyHostiles())
        {
            // Already pulled us — avoidance can't undo that, so it's not a route obstacle anymore.
            if (hostile.IsTargetingPlayer)
                continue;

            // Already targeting something/someone else entirely — not a threat to steer around.
            if (hostile.IsEngagedElsewhere)
                continue;

            // Claimed by an active FATE — this is the intended combat target AT the destination,
            // not an obstacle to route around (the destination itself often sits right among these
            // mobs, e.g. a FATE's center). Uses the game's own native FateId rather than a
            // radius/position guess — confirmed via user report: navigation would treat a FATE's own
            // mobs as something to avoid and halt right as it got close enough for them to matter,
            // exactly where avoidance is no longer wanted at all.
            if (hostile.FateId != 0)
                continue;

            // Never aggroes from travel proximity — structurally excluded, not just deprioritized.
            if (hostile.AggroType == AggroType.Magic)
                continue;

            float extent = (hostile.AggroType == AggroType.Sound ? soundRangeYalms : FacingConeRangeYalms) + safetyMarginYalms;
            float nearTermRangeSquared = MathF.Pow(extent * NearTermRangeMultiplier, 2f);
            if (hostile.DistanceYalms * hostile.DistanceYalms > nearTermRangeSquared)
                continue;

            float threatRadiusSquared = MathF.Pow(extent * ThreatFalloffMultiplier, 2f);
            bool nearAnySegment = false;
            for (int i = 0; i < path.Count - 1; i++)
            {
                if (PerpDistSquaredToSegment(hostile.Position, path[i], path[i + 1]) <= threatRadiusSquared)
                {
                    nearAnySegment = true;
                    break;
                }
            }

            if (nearAnySegment)
                result.Add(hostile);
        }

        return result;
    }

    // Squared perpendicular distance from a point to the closest spot on a single segment (clamped to
    // the segment's own endpoints) — XZ only, matching every other route-proximity check here.
    private static float PerpDistSquaredToSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        float routeX = b.X - a.X;
        float routeZ = b.Z - a.Z;
        float lengthSquared = routeX * routeX + routeZ * routeZ;
        if (lengthSquared <= 0.01f)
        {
            float dx0 = point.X - a.X, dz0 = point.Z - a.Z;
            return dx0 * dx0 + dz0 * dz0;
        }

        float t = Math.Clamp(((point.X - a.X) * routeX + (point.Z - a.Z) * routeZ) / lengthSquared, 0f, 1f);
        float nearestX = a.X + routeX * t;
        float nearestZ = a.Z + routeZ * t;
        float dx = point.X - nearestX;
        float dz = point.Z - nearestZ;
        return dx * dx + dz * dz;
    }

    /// <summary>
    /// Gets all hostiles currently targeting the player.
    /// </summary>
    public IReadOnlyList<HostileNpc> GetHostilesTargetingPlayer()
    {
        return GetNearbyHostiles().Where(h => h.IsTargetingPlayer).ToList();
    }

    /// <summary>
    /// Checks if the player is currently being targeted by any hostile.
    /// </summary>
    public bool IsPlayerUnderAttack()
    {
        return GetHostilesTargetingPlayer().Count > 0;
    }
}
