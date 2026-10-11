using Ocelot.Graphics;
using Ocelot.Lifecycle;
using Ocelot.Services.OverlayRenderer;
using Ocelot.Windows;
using System;
using System.Numerics;

namespace Botja.Services;

public sealed class HostileOverlayService(
    HostileDetectionService hostileDetection,
    HostileDetectionConfig hostileDetectionConfig,
    CombatControlService combatControl,
    IOverlayRenderer overlay,
    IMainWindow mainWindow
) : IOnRender
{
    private const int ConeArcSegments = 8;
    private const uint CircleSegments = 24;
    private static readonly Color ConeColor = new(1f, 1f, 0f, 0.6f); // yellow — Sight aggro wedge
    private static readonly Color SoundCircleColor = new(1f, 0.5f, 0f, 0.6f); // orange — Sound aggro radius
    private static readonly Color PersonalSpaceColor = new(1f, 1f, 1f, 0.35f); // faint white — always-on body clearance

    public void Render()
    {
        if (!mainWindow.IsOpen)
            return;

        foreach (var hostile in hostileDetection.GetNearbyHostiles())
        {
            overlay.StrokeCircle(hostile.Position, 2f, new Color(1f, 0f, 0f, 0.8f));
            if (IsFateOrCeMob(hostile.FateId))
                continue;

            // Draws the REAL obstacle shape(s) avoidance actually uses (same constants, same
            // inflated margin) so this doubles as a genuine diagnostic, not just decoration.
            // Magic-type gets no extra shape — it's never treated as an obstacle at all.
            switch (hostile.AggroType)
            {
                case AggroType.Sight:
                    DrawFacingCone(hostile.Position, hostile.Facing);
                    break;
                case AggroType.Sound:
                    overlay.StrokeCircle(hostile.Position, hostileDetectionConfig.SoundAggroRangeYalms + hostileDetectionConfig.AvoidanceSafetyMarginYalms, SoundCircleColor, CircleSegments);
                    break;
            }

            if (hostileDetectionConfig.AvoidancePersonalSpaceYalms > 0.01f)
                overlay.StrokeCircle(hostile.Position, hostileDetectionConfig.AvoidancePersonalSpaceYalms, PersonalSpaceColor, CircleSegments);
        }
    }

    // FATE/CE mobs are meant to be engaged, not steered around, so the aggro-shape debug overlay is
    // just clutter for them. Uses the mob's own native FateId (same precise check the real avoidance
    // logic uses, HostileDetectionService.GetRouteRelevantHostiles) rather than a position/radius
    // guess — a non-FATE enemy that merely happens to be standing inside a FATE's zone must NOT get
    // an exception here either, since avoidance still genuinely treats it as an obstacle; a radius
    // check would incorrectly suppress its shape and make this overlay lie about what's really being
    // avoided. A CE has no such per-mob signal readily available, so an active CE still just
    // suppresses shapes entirely while it's ongoing.
    private bool IsFateOrCeMob(uint fateId) =>
        fateId != 0 || combatControl.ActiveCeEventId != 0;

    // Draws the real Sight-type wedge — same range/half-angle FateNavigationService's obstacle
    // planner treats as a hard no-go zone (HostileDetectionService.FacingConeHalfAngleRadians/Range).
    private void DrawFacingCone(Vector3 position, float facing)
    {
        float range = HostileDetectionService.FacingConeRangeYalms + hostileDetectionConfig.AvoidanceSafetyMarginYalms;
        float halfAngle = HostileDetectionService.FacingConeHalfAngleRadians;
        Vector2 forward = new(MathF.Sin(facing), MathF.Cos(facing));

        Vector3 leftEdge = ToWorld(position, Rotate(forward, -halfAngle), range);
        Vector3 rightEdge = ToWorld(position, Rotate(forward, halfAngle), range);
        overlay.StrokeLine(position, leftEdge, ConeColor);
        overlay.StrokeLine(position, rightEdge, ConeColor);

        Vector3 prev = leftEdge;
        for (int i = 1; i <= ConeArcSegments; i++)
        {
            float angle = -halfAngle + (2f * halfAngle * i / ConeArcSegments);
            Vector3 point = ToWorld(position, Rotate(forward, angle), range);
            overlay.StrokeLine(prev, point, ConeColor);
            prev = point;
        }
    }

    private static Vector3 ToWorld(Vector3 origin, Vector2 direction, float range) =>
        origin + new Vector3(direction.X, 0f, direction.Y) * range;

    private static Vector2 Rotate(Vector2 v, float angle)
    {
        float cos = MathF.Cos(angle);
        float sin = MathF.Sin(angle);
        return new Vector2(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
    }
}
