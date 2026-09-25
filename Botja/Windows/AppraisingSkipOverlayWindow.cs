using Botja.Services;
using Dalamud.Bindings.ImGui;
using Ocelot.Windows;
using System.Numerics;

namespace Botja.Windows;

// Draws one "skip this item" checkbox per visible ItemInspectionList row, in the empty gap column
// between the item name and the "Required" count. Checked items are excluded from appraising.
public class AppraisingSkipOverlayWindow(GuiInteractionService guiInteract)
    : OcelotWindow("Appraising Skip Overlay##Botja")
{
    private const float ColumnWidth = 96f;
    private const float CheckboxHorizontalOffset = -4f;
    private const float CheckboxVerticalOffset = 0f;

    public override void PreOpenCheck()
    {
        var rows = guiInteract.GetItemInspectionRowPositions();
        if (rows.Count == 0)
        {
            IsOpen = false;
            return;
        }

        IsOpen = true;

        float minX = float.MaxValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (var row in rows)
        {
            minX = System.Math.Min(minX, row.CheckboxScreenX + CheckboxHorizontalOffset);
            minY = System.Math.Min(minY, row.CheckboxScreenY + CheckboxVerticalOffset);
            maxY = System.Math.Max(maxY, row.CheckboxScreenY + CheckboxVerticalOffset + row.RowHeight);
        }

        Position = new Vector2(minX, minY);
        PositionCondition = ImGuiCond.Always;
        Size = new Vector2(ColumnWidth, maxY - minY);
        SizeCondition = ImGuiCond.Always;
        Flags = ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoBackground
            // NoInputs stops this window from ever being the "hovered window", so the game's own
            // item-hover tooltip underneath still receives hover — hit-testing below is done manually
            // against the raw mouse position instead of relying on ImGui's per-window input routing.
            | ImGuiWindowFlags.NoInputs;
    }

    protected override void Render()
    {
        // Skip drawing entirely while any native tooltip is up — our draws always composite on top of
        // the game's UI layer regardless of NoInputs, so this is the only way to not visually cover it.
        if (guiInteract.IsAnyTooltipVisible())
            return;

        var drawList = ImGui.GetWindowDrawList();
        var mouse = ImGui.GetIO().MousePos;
        bool clicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        float checkboxSize = ImGui.GetFrameHeight();
        uint frameColor = ImGui.GetColorU32(ImGuiCol.FrameBg);
        uint frameHoveredColor = ImGui.GetColorU32(ImGuiCol.FrameBgHovered);
        uint checkColor = ImGui.GetColorU32(ImGuiCol.CheckMark);
        uint textColor = ImGui.GetColorU32(ImGuiCol.Text);

        foreach (var row in guiInteract.GetItemInspectionRowPositions())
        {
            float y = row.CheckboxScreenY + CheckboxVerticalOffset + ((row.RowHeight - checkboxSize) / 2f);
            var min = new Vector2(row.CheckboxScreenX + CheckboxHorizontalOffset, y);
            var max = min + new Vector2(checkboxSize, checkboxSize);

            bool hovered = mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y;
            bool skipped = guiInteract.IsItemInspectionItemSkipped(row.TrueItemId);
            if (hovered && clicked)
            {
                skipped = !skipped;
                guiInteract.SetItemInspectionItemSkipped(row.TrueItemId, skipped);
            }

            drawList.AddRectFilled(min, max, hovered ? frameHoveredColor : frameColor, ImGui.GetStyle().FrameRounding);
            if (skipped)
            {
                var inset = checkboxSize * 0.2f;
                drawList.AddLine(min + new Vector2(inset, checkboxSize / 2f), min + new Vector2(checkboxSize / 2f, checkboxSize - inset), checkColor, 2f);
                drawList.AddLine(min + new Vector2(checkboxSize / 2f, checkboxSize - inset), max - new Vector2(inset, inset), checkColor, 2f);
            }

            // Verification aid — remove once row/item alignment is confirmed correct in-game.
            drawList.AddText(new Vector2(max.X + 4f, min.Y), textColor, row.TrueItemId.ToString());
        }
    }
}
