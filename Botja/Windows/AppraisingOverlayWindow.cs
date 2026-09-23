using Botja.Services;
using Dalamud.Bindings.ImGui;
using Ocelot.Windows;
using System;
using System.Numerics;

namespace Botja.Windows;

// Floats a small control panel directly beside the native "ItemInspectionList" (appraising) window,
// repositioned every frame to track it. Hidden whenever that addon isn't open.
public class AppraisingOverlayWindow(GuiInteractionService guiInteract)
    : OcelotWindow("Appraising Overlay##Botja")
{
    private const string TargetAddonName = "ItemInspectionList";
    private const float HorizontalGap = 8f;
    private static readonly Vector2 OverlaySize = new(220, 110);

    // WindowHost.DrawInternal calls PreOpenCheck() unconditionally every frame, BEFORE checking
    // IsOpen — unlike Update(), which is only reached once IsOpen is already true. Since this window
    // has to open/close itself based on the native addon's visibility, this is the correct hook;
    // using Update() here would mean the window can never open itself in the first place.
    public override void PreOpenCheck()
    {
        var rect = guiInteract.GetAddonScreenRect(TargetAddonName);
        if (rect is not { } r)
        {
            IsOpen = false;
            return;
        }

        IsOpen = true;

        // Prefer placing it to the right of the native window, but clamp to the visible viewport —
        // otherwise it silently renders off-screen when the addon sits near the right/bottom edge.
        var displaySize = ImGui.GetIO().DisplaySize;
        float x = r.X + r.Width + HorizontalGap;
        if (x + OverlaySize.X > displaySize.X)
            x = MathF.Max(0, r.X - OverlaySize.X - HorizontalGap);
        float y = MathF.Min(r.Y, MathF.Max(0, displaySize.Y - OverlaySize.Y));

        Position = new Vector2(x, y);
        PositionCondition = ImGuiCond.Always;
        Size = OverlaySize;
        SizeCondition = ImGuiCond.Always;
        Flags = ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav;
    }

    protected override void Render()
    {
        ImGui.TextUnformatted("Appraising");
        ImGui.Separator();

        if (ImGui.Button("Start"))
            guiInteract.StartItemInspectionAutomation();
        ImGui.SameLine();
        if (ImGui.Button("Stop"))
            guiInteract.StopItemInspectionAutomation();

        ImGui.TextWrapped(guiInteract.ItemInspectionAutomationStatus);
    }
}

