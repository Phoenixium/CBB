using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Botja.Services;

/// <summary>
/// Detects and tracks nearby hostile NPCs, their positions, and aggro status.
/// </summary>
public sealed unsafe class HostileDetectionService(
    IObjectTable objects,
    HostileDetectionConfig config
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

    // Snapshot of the last REAL scan (not a cache hit) — lets the debug UI show exactly which
    // filtering stage a hostile drops out at, without needing a log file.
    public int DebugTotalObjectCount { get; private set; }
    public int DebugBattleNpcCount { get; private set; }
    public int DebugHostileFlaggedCount { get; private set; }
    public int DebugWithinRadiusCount { get; private set; }

    /// <summary>
    /// Information about a detected hostile NPC.
    /// </summary>
    public record struct HostileNpc(
        ulong ObjectId,
        uint DataId,
        string Name,
        Vector3 Position,
        float DistanceYalms,
        bool IsTargetingPlayer,
        bool IsEngagedElsewhere,
        float Facing,
        uint CurrentHp,
        uint MaxHp,
        byte Rank,
        AggroType AggroType,
        uint FateId
    );

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
            DebugTotalObjectCount = 0;
            DebugBattleNpcCount = 0;
            DebugHostileFlaggedCount = 0;
            DebugWithinRadiusCount = 0;
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

        DebugTotalObjectCount = totalObjectCount;
        DebugBattleNpcCount = battleNpcCount;
        DebugHostileFlaggedCount = hostileFlaggedCount;
        DebugWithinRadiusCount = hostiles.Count;

        // Sort by distance (closest first).
        cachedHostiles = [.. hostiles.OrderBy(h => h.DistanceYalms)];
        return cachedHostiles;
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
    public IReadOnlyList<HostileNpc> GetRouteRelevantHostiles(Vector3 from, Vector3 to, float soundRangeYalms, float safetyMarginYalms)
    {
        var result = new List<HostileNpc>();
        float routeX = to.X - from.X;
        float routeZ = to.Z - from.Z;
        float routeLengthSquared = routeX * routeX + routeZ * routeZ;
        if (routeLengthSquared <= 0.01f)
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

            float hostileX = hostile.Position.X - from.X;
            float hostileZ = hostile.Position.Z - from.Z;
            float alongRoute = Math.Clamp((hostileX * routeX + hostileZ * routeZ) / routeLengthSquared, 0f, 1f);
            float nearestX = from.X + routeX * alongRoute;
            float nearestZ = from.Z + routeZ * alongRoute;
            float dx = hostile.Position.X - nearestX;
            float dz = hostile.Position.Z - nearestZ;
            float perpDistSquared = dx * dx + dz * dz;
            float threatRadius = extent * ThreatFalloffMultiplier;
            if (perpDistSquared > threatRadius * threatRadius)
                continue;

            result.Add(hostile);
        }

        return result;
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
