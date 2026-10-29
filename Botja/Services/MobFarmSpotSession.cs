using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using Ocelot.Services.PlayerState;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Botja.Services;

public sealed class MobFarmSpotSession(MobFarmConfig config, IPlayer player, IObjectTable objects)
{
    private readonly HashSet<int> claimedIndices = [];
    private long? contestedSinceMs;

    public MobFarmSpot? Current { get; private set; }
    public Vector3 Origin { get; private set; }
    public Vector3? StackPoint => Current?.StackPoint;
    public string? Name => Current?.Name;
    public bool NeedsApproach { get; private set; }
    public int EffectiveMinimumMobsToStartFight =>
        Current is { MinimumMobsToStartFight: > 0 } spot
            ? spot.MinimumMobsToStartFight
            : config.MinimumMobsToStartFight;

    public void Begin()
    {
        claimedIndices.Clear();
        contestedSinceMs = null;
        Current = SelectBest(player.Position);
        Origin = Current?.Origin ?? player.Position;
        NeedsApproach = Current != null && Distance2D(player.Position, Origin) > 8f;
    }

    public void Reset()
    {
        claimedIndices.Clear();
        contestedSinceMs = null;
        Current = null;
        Origin = Vector3.Zero;
        NeedsApproach = false;
    }

    public void MarkArrived() => NeedsApproach = false;

    public bool TickClaimed(
        IReadOnlyCollection<HostileDetectionService.HostileNpc> contested,
        IReadOnlyCollection<HostileDetectionService.HostileNpc> free)
    {
        if (Current == null || config.Spots.Count == 0)
        {
            contestedSinceMs = null;
            return false;
        }

        Vector3 watch = StackPoint ?? Origin;
        ulong localId = objects.LocalPlayer?.GameObjectId ?? ulong.MaxValue;
        bool claimed = (contested.Count > 0 && free.Count == 0)
            || objects.OfType<IPlayerCharacter>()
                .Any(other => other.GameObjectId != localId
                    && Distance2D(other.Position, watch) <= config.ClaimedPlayerRadiusYalms);

        if (!claimed)
        {
            contestedSinceMs = null;
            return false;
        }

        long now = Environment.TickCount64;
        contestedSinceMs ??= now;
        if (now - contestedSinceMs.Value < config.ClaimedSpotSeconds * 1000f)
            return false;

        int index = config.Spots.IndexOf(Current);
        if (index >= 0)
            claimedIndices.Add(index);

        contestedSinceMs = null;
        MobFarmSpot? next = SelectBest(player.Position);
        if (next == null || ReferenceEquals(next, Current))
            return false;

        Current = next;
        Origin = next.Origin;
        NeedsApproach = true;
        return true;
    }

    private MobFarmSpot? SelectBest(Vector3 from) => config.Spots
        .Select((spot, index) => (Spot: spot, Index: index))
        .Where(entry => entry.Spot.Enabled && !claimedIndices.Contains(entry.Index))
        .OrderByDescending(entry => entry.Spot.Priority)
        .ThenBy(entry => Distance2D(from, entry.Spot.Origin))
        .Select(entry => entry.Spot)
        .FirstOrDefault();

    internal static float Distance2D(Vector3 first, Vector3 second)
    {
        float dx = first.X - second.X;
        float dz = first.Z - second.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}