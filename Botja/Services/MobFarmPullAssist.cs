using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Ocelot.Services.PlayerState;
using System;
using Action = Ocelot.Actions.Action;

namespace Botja.Services;

public sealed class MobFarmPullAssist(MobFarmConfig config, IPlayer player, ITargetManager targets)
{
    public const float PullRangeYalms = 20f;
    private const ulong InvalidTargetId = 0xE0000000;
    private const long PullThrottleMs = 400;
    private static readonly Action Provoke = new(ActionType.Action, 7533);
    private static readonly Action ShieldLob = new(ActionType.Action, 24);
    private static readonly Action Tomahawk = new(ActionType.Action, 46);
    private static readonly Action Unmend = new(ActionType.Action, 3624);
    private static readonly Action LightningShot = new(ActionType.Action, 16143);
    private static readonly Action Intervene = new(ActionType.Action, 16461);
    private static readonly Action Onslaught = new(ActionType.Action, 7386);
    private static readonly Action Shadowstride = new(ActionType.Action, 36926);
    private static readonly Action Trajectory = new(ActionType.Action, 36934);
    private long nextAttemptMs;

    public bool TryPull(IBattleNpc target, float distanceYalms)
    {
        if (!player.IsTank() || distanceYalms > PullRangeYalms
            || target.TargetObject?.GameObjectId == player.PlayerCharacter?.GameObjectId)
            return false;

        long now = Environment.TickCount64;
        if (now < nextAttemptMs)
            return false;
        nextAttemptMs = now + PullThrottleMs;

        if (config.ShouldHandleTargeting)
            targets.Target = target;

        ulong targetId = target.GameObjectId;
        if (targetId == 0 || targetId == InvalidTargetId)
            return false;

        Action? ranged = config.UseRangedPull ? GetRangedPull() : null;
        if (ranged is { } rangedAction && rangedAction.CanCast())
            return rangedAction.Cast(targetId);

        if (config.UseProvoke && Provoke.CanCast())
            return Provoke.Cast(targetId);

        Action? gapCloser = config.UseGapCloser ? GetGapCloser() : null;
        return gapCloser is { } gapAction && gapAction.CanCast() && gapAction.Cast(targetId);
    }

    private Action? GetRangedPull() => player.GetClassJob()?.RowId switch
    {
        19 or 1 => ShieldLob,
        21 or 3 => Tomahawk,
        32 => Unmend,
        37 => LightningShot,
        _ => null,
    };

    private Action? GetGapCloser() => player.GetClassJob()?.RowId switch
    {
        19 => Intervene,
        21 => Onslaught,
        32 => Shadowstride,
        37 => Trajectory,
        _ => null,
    };
}