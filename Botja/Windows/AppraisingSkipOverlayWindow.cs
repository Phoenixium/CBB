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
            | ImGuiWindowFlags.NoBackground;
    }

    protected override void Render()
    {
        float checkboxSize = ImGui.GetFrameHeight();
        foreach (var row in guiInteract.GetItemInspectionRowPositions())
        {
            float y = row.CheckboxScreenY + CheckboxVerticalOffset + ((row.RowHeight - checkboxSize) / 2f);
            ImGui.SetCursorScreenPos(new Vector2(row.CheckboxScreenX + CheckboxHorizontalOffset, y));

            bool skipped = guiInteract.IsItemInspectionItemSkipped(row.TrueItemId);
            if (ImGui.Checkbox($"##skip{row.RowIndex}", ref skipped))
                guiInteract.SetItemInspectionItemSkipped(row.TrueItemId, skipped);

            // Verification aid — remove once row/item alignment is confirmed correct in-game.
            ImGui.SameLine();
            ImGui.TextUnformatted(row.TrueItemId.ToString());
        }
    }
}
