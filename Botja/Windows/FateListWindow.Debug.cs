using Dalamud.Bindings.ImGui;
using System;
using System.Linq;

namespace Botja.Windows;

public partial class FateListWindow
{
    private string inspectAddonName = "Talk";
    private int callbackValue;
    private string uiClickFilter = "ItemInspection";
    private string uiClickName = "";
    private string hoveredNodeInspection = "";
    private bool liveInspectHoveredNodes;
    private DateTime? delayedHoverCaptureAt;
    private int itemInspectionListRowIndex;
    private bool itemInspectionListDispatchEvent = true;
    private string ceRecruitmentButton = "Register";

    private void RenderDebugInfo()
    {
        if (!ImGui.CollapsingHeader("Debug info"))
            return;

        if (targets.Target is { } target)
        {
            ImGui.TextUnformatted($"Target: {target.Name} — BaseId: {target.BaseId}  Pos: {target.Position:f1}");
        }
        else
        {
            ImGui.TextUnformatted("Target an aetheryte to see its BaseId.");
        }

        var zoneShards = aetheryteList
            .Where(a => a.TerritoryId == clientState.TerritoryType)
            .ToList();
        if (zoneShards.Count > 0)
        {
            ImGui.TextUnformatted("Registered aetherytes in zone:");
            foreach (var shard in zoneShards)
            {
                uint pnId = shard.AetheryteData.Value.PlaceName.RowId;
                string pnName = shard.AetheryteData.Value.PlaceName.Value.Name.ToString();
                ImGui.BulletText($"{pnName} — PlaceName ID: {pnId}  AetheryteId: {shard.AetheryteId}");
            }
        }

        ImGui.Separator();

        ImGui.TextUnformatted("Navigation debug:");
        ImGui.TextUnformatted($"Phase: {nav.DebugPhase}  (ticks: {nav.DebugPhaseTicks})");
        ImGui.TextUnformatted($"vnav: Pathfinding={nav.DebugVnavIsPathfinding}  Running={nav.DebugVnavIsRunning}  NavmeshReady={nav.DebugVnavIsNavmeshReady}");
        ImGui.TextUnformatted($"lifestream busy: {nav.DebugLifestreamBusy}");
        ImGui.TextUnformatted($"pendingDestination: {nav.DebugPendingDestination:F1}");
        ImGui.TextUnformatted($"pendingSourceShardPos: {nav.DebugPendingSourceShardPos:F1}");
        ImGui.TextUnformatted($"pendingDestShardPos: {nav.DebugPendingDestShardPos:F1}");

        ImGui.Separator();

        ImGui.TextUnformatted("GUI interaction test:");
        if (ImGui.Button("Click Yes (SelectYesno)"))
            guiInteract.ClickSelectYesnoYes();
        ImGui.TextUnformatted(guiInteract.DebugAddonInfo("SelectYesno"));

        ImGui.Separator();

        ImGui.TextUnformatted("CE recruitment debug:");
        if (ImGui.Button("Open Resistance Recruitment"))
            guiInteract.OpenCeRecruitmentWindow();
        ImGui.SameLine();
        if (ImGui.Button("Copy CE buttons"))
            ImGui.SetClipboardText(string.Join("\n", guiInteract.GetCeRecruitmentButtons()));
        ImGui.TextUnformatted(guiInteract.DebugCeRecruitmentWindow());
        ImGui.InputText("CE button label", ref ceRecruitmentButton, 64);
        ImGui.SameLine();
        if (ImGui.Button("Click CE button"))
            guiInteract.ClickCeRecruitmentButton(ceRecruitmentButton);

        ImGui.Separator();

        if (ImGui.Button("List loaded addons"))
            ImGui.SetClipboardText(string.Join("\n", guiInteract.ListLoadedAddons()));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copies every currently loaded addon name to the clipboard");

        foreach (var addonInfo in guiInteract.ListLoadedAddons())
        {
            if (!addonInfo.StartsWith("Talk") && !addonInfo.StartsWith("SelectYesno") && !addonInfo.Contains("Visible=True"))
                continue;
            ImGui.BulletText(addonInfo);
        }

        ImGui.InputText("Addon name to dump", ref inspectAddonName, 64);
        ImGui.SameLine();
        if (ImGui.Button("Dump nodes"))
            ImGui.SetClipboardText(guiInteract.DumpAddonNodes(inspectAddonName));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copies the node dump to the clipboard (id/type/position/text per node)");

        ImGui.InputInt("Callback value", ref callbackValue);
        ImGui.SameLine();
        if (ImGui.Button("Fire callback##inspectAddon"))
            guiInteract.FireAddonCallback(inspectAddonName, true, callbackValue);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Callback.Fire(\"{inspectAddonName}\", true, {callbackValue})");

        if (ImGui.Button("ItemInspectionResult: Next"))
            guiInteract.ClickItemInspectionResultNext();
        ImGui.SameLine();
        if (ImGui.Button("Close"))
            guiInteract.ClickItemInspectionResultClose();

        if (ImGui.Button("Dump ItemInspectionList rows"))
            ImGui.SetClipboardText(guiInteract.DumpItemInspectionListRows());
        if (ImGui.BeginTable("ItemInspectionDebugRows", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Item ID");
            ImGui.TableSetupColumn("Quantity");
            ImGui.TableSetupColumn("Skipped");
            ImGui.TableHeadersRow();

            foreach (var row in guiInteract.GetItemInspectionDebugRows())
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(row.ItemId.ToString());
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(row.Quantity.ToString());
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(row.Skipped ? "yes" : "no");
            }

            ImGui.EndTable();
        }
        ImGui.InputInt("ItemInspectionList row", ref itemInspectionListRowIndex);
        ImGui.SameLine();
        ImGui.Checkbox("Dispatch event", ref itemInspectionListDispatchEvent);
        if (ImGui.Button("Select ItemInspectionList row"))
            guiInteract.SelectItemInspectionListRow(itemInspectionListRowIndex, itemInspectionListDispatchEvent);
        ImGui.SameLine();
        if (ImGui.Button("Click row"))
            guiInteract.ClickItemInspectionListRow(itemInspectionListRowIndex);
        ImGui.InputText("UI click filter", ref uiClickFilter, 64);
        ImGui.SameLine();
        if (ImGui.Button("Copy available UI clicks"))
            ImGui.SetClipboardText(string.Join("\n", guiInteract.ListAvailableUiClicks(uiClickFilter)));
        ImGui.InputText("UI click name", ref uiClickName, 128);
        ImGui.SameLine();
        if (ImGui.Button("Send UI click##inspectAddon"))
            guiInteract.SendUiClick(inspectAddonName, uiClickName);

        var mousePos = ImGui.GetMousePos();
        ImGui.TextUnformatted($"Mouse: {mousePos.X:F0}, {mousePos.Y:F0}");
        ImGui.Checkbox("Live inspect hovered game node", ref liveInspectHoveredNodes);
        if (liveInspectHoveredNodes)
            hoveredNodeInspection = guiInteract.InspectNodesAt(mousePos.X, mousePos.Y);
        if (ImGui.Button("Inspect hovered game node"))
            hoveredNodeInspection = guiInteract.InspectNodesAt(mousePos.X, mousePos.Y);
        ImGui.SameLine();
        if (ImGui.Button("Capture in 3s"))
            delayedHoverCaptureAt = DateTime.UtcNow.AddSeconds(3);
        if (delayedHoverCaptureAt is { } captureAt)
        {
            double secondsLeft = (captureAt - DateTime.UtcNow).TotalSeconds;
            if (secondsLeft <= 0)
            {
                var capturePos = ImGui.GetMousePos();
                hoveredNodeInspection = guiInteract.InspectNodesAt(capturePos.X, capturePos.Y);
                delayedHoverCaptureAt = null;
            }
            else
            {
                ImGui.SameLine();
                ImGui.TextUnformatted($"capturing in {secondsLeft:F1}s");
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Copy hovered node report"))
            ImGui.SetClipboardText(hoveredNodeInspection);
        if (!string.IsNullOrWhiteSpace(hoveredNodeInspection))
            ImGui.TextUnformatted(hoveredNodeInspection.Split('\n')[0]);

        if (ImGui.Button("Toggle ItemInspectionList event log"))
            guiInteract.ToggleItemInspectionEventLogging();
        ImGui.SameLine();
        if (ImGui.Button("Copy last item event"))
            ImGui.SetClipboardText(guiInteract.RecentItemInspectionEvents);
        ImGui.TextUnformatted(guiInteract.LastItemInspectionEvent);
    }
}
