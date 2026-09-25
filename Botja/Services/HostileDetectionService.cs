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
    // Standard enemy detection radius (yalms) — NPCs beyond this are ignored.
    public float DetectionRadiusYalms => config.DetectionRadiusYalms;

    /// <summary>
    /// Information about a detected hostile NPC.
    /// </summary>
    public record struct HostileNpc(
        ulong ObjectId,
        string Name,
        Vector3 Position,
        float DistanceYalms,
        bool IsTargetingPlayer,
        uint CurrentHp,
        uint MaxHp,
        byte Rank
    );

    /// <summary>
    /// Gets all hostile NPCs currently detected within DetectionRadiusYalms.
    /// Returns empty list if no player or no enemies found.
    /// </summary>
    public IReadOnlyList<HostileNpc> GetNearbyHostiles()
    {
        var player = objects.LocalPlayer;
        if (player == null)
            return Array.Empty<HostileNpc>();

        float detectionRadius = config.DetectionRadiusYalms > 0f
            ? config.DetectionRadiusYalms
            : 100f;
        var hostiles = new List<HostileNpc>();

        foreach (var obj in objects)
        {
            // Only consider battle NPCs.
            if (obj is not IBattleNpc battleNpc)
                continue;

            // Only include NPCs whose object status flags explicitly mark them hostile.
            if (!battleNpc.StatusFlags.ToString().Contains("Hostile", StringComparison.Ordinal))
                continue;

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

            // Check if this NPC is currently targeting the player.
            bool isTargetingPlayer = battleNpc is ICharacter character
                && character.TargetObjectId == player.GameObjectId;

            hostiles.Add(new HostileNpc(
                ObjectId: battleNpc.GameObjectId,
                Name: battleNpc.Name.ToString(),
                Position: battleNpc.Position,
                DistanceYalms: distance,
                IsTargetingPlayer: isTargetingPlayer,
                CurrentHp: battleNpc.CurrentHp,
                MaxHp: battleNpc.MaxHp,
                Rank: GetForayRank(battleNpc)
            ));
        }

        // Sort by distance (closest first).
        return hostiles.OrderBy(h => h.DistanceYalms).ToList();
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

    public bool TryGetBlockingHostile(Vector3 from, Vector3 to, float clearance, out HostileNpc blockingHostile)
    {
        blockingHostile = default;
        float routeX = to.X - from.X;
        float routeZ = to.Z - from.Z;
        float routeLengthSquared = routeX * routeX + routeZ * routeZ;
        if (routeLengthSquared <= 0.01f)
            return false;

        HostileNpc? closest = null;
        float closestAlongRoute = float.MaxValue;
        float clearanceSquared = clearance * clearance;

        foreach (var hostile in GetNearbyHostiles())
        {
            float hostileX = hostile.Position.X - from.X;
            float hostileZ = hostile.Position.Z - from.Z;
            float alongRoute = Math.Clamp((hostileX * routeX + hostileZ * routeZ) / routeLengthSquared, 0f, 1f);
            float nearestX = from.X + routeX * alongRoute;
            float nearestZ = from.Z + routeZ * alongRoute;
            float dx = hostile.Position.X - nearestX;
            float dz = hostile.Position.Z - nearestZ;

            if (dx * dx + dz * dz <= clearanceSquared && alongRoute < closestAlongRoute)
            {
                closest = hostile;
                closestAlongRoute = alongRoute;
            }
        }

        if (closest is not { } result)
            return false;

        blockingHostile = result;
        return true;
    }

    /// <summary>
    /// Checks whether a given position is within danger range of any detected hostile.
    /// </summary>
    public bool IsPositionInDanger(Vector3 position, float? dangerRadiusYalms = null)
    {
        float dangerRadius = dangerRadiusYalms ?? config.DangerRadiusYalms;
        foreach (var hostile in GetNearbyHostiles())
        {
            float dx = position.X - hostile.Position.X;
            float dz = position.Z - hostile.Position.Z;
            float distance = MathF.Sqrt(dx * dx + dz * dz);

            if (distance <= dangerRadius)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the closest hostile NPC within detection radius, or null if none found.
    /// </summary>
    public HostileNpc? GetClosestHostile()
    {
        var hostiles = GetNearbyHostiles();
        return hostiles.Count > 0 ? hostiles[0] : null;
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

    /// <summary>
    /// Gets the number of hostiles within the detection radius.
    /// </summary>
    public int GetHostileCount()
    {
        return GetNearbyHostiles().Count;
    }
}
