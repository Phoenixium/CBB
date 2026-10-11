using Botja.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Ocelot.Windows;
using System;
using System.Numerics;

namespace Botja.Windows;

// Floats a small control panel directly beside the native "SkyIslandExchange2" (field note
// exchange) window, repositioned every frame to track it. Hidden whenever that addon isn't open,
// analogous to AppraisingOverlayWindow tracking the appraising addon. Additionally only shown in
// Bozjan zones (Southern Front/Zadnor), since the addon name/list layout is Bozja-specific.
public class FieldNoteExchangeOverlayWindow(GuiInteractionService guiInteract, IClientState clientState)
    : OcelotWindow("Field Note Exchange##Botja")
{
    private const string TargetAddonName = "SkyIslandExchange2";
    private const float HorizontalGap = 8f;
    private static readonly Vector2 OverlaySize = new(220, 90);

    // WindowHost.DrawInternal calls PreOpenCheck() unconditionally every frame, BEFORE checking
    // IsOpen — same reasoning as AppraisingOverlayWindow's use of this hook.
    public override void PreOpenCheck()
    {
        if (!FateListWindow.BozjaZones.Contains(clientState.TerritoryType))
        {
            IsOpen = false;
            return;
        }

        var rect = guiInteract.GetAddonScreenRect(TargetAddonName);
        if (rect is not { } r)
        {
            IsOpen = false;
            return;
        }

        IsOpen = true;

        // Prefer placing it to the right of the native window, but clamp to the visible viewport.
        var displaySize = ImGui.GetIO().DisplaySize;
        float x = r.X + r.Width + HorizontalGap;
        if (x + OverlaySize.X > displaySize.X)
            x = MathF.Max(0, r.X - OverlaySize.X - HorizontalGap);
        float y = MathF.Min(r.Y, MathF.Max(0, displaySize.Y - OverlaySize.Y));

        Position = new Vector2(x, y);
        PositionCondition = ImGuiCond.Always;
        Size = OverlaySize;
        SizeCondition = ImGuiCond.Always;
        Flags = ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav;
    }

    protected override void Render()
    {
        if (ImGui.Button("Start field note exchange"))
            guiInteract.StartGenericListAutomation("SkyIslandExchange2", 13);
        ImGui.SameLine();
        if (ImGui.Button("Stop field note exchange"))
            guiInteract.StopGenericListAutomation();
        ImGui.TextWrapped(guiInteract.GenericListAutomationStatus);
    }
}
