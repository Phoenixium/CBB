using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;

namespace Botja.Services;

public unsafe partial class GuiInteractionService
{
    public readonly record struct ItemInspectionDebugRow(int RowIndex, int TrueItemId, int Quantity, bool Skipped);

    private bool itemInspectionEventLogging;
    private string lastItemInspectionEvent = "ItemInspectionList event logging is off.";
    private readonly List<string> itemInspectionEvents = [];

    public string LastItemInspectionEvent => lastItemInspectionEvent;
    public string RecentItemInspectionEvents => itemInspectionEvents.Count == 0
        ? lastItemInspectionEvent
        : string.Join("\n", itemInspectionEvents);

    // Raw numbers behind the skip-checkbox overlay, to diagnose misalignment against a real
    // screenshot instead of guessing offsets blindly.
    public string DumpItemInspectionRowPositions()
    {
        var sb = new StringBuilder();
        float checkboxSize = 20f; // matches ImGui.GetFrameHeight() closely enough for comparison
        foreach (var row in GetItemInspectionRowPositions())
        {
            float centeredY = row.CheckboxScreenY + ((row.RowHeight - checkboxSize) / 2f);
            sb.AppendLine($"Row={row.RowIndex} ItemId={row.TrueItemId} NodeTopY={row.CheckboxScreenY:F1} RowHeight={row.RowHeight:F1} CheckboxX={row.CheckboxScreenX:F1} CenteredCheckboxY={centeredY:F1}");
        }

        return sb.ToString();
    }

    public IReadOnlyList<ItemInspectionDebugRow> GetItemInspectionDebugRows()
    {
        var list = GetItemInspectionList();
        if (list == null)
            return [];

        var result = new List<ItemInspectionDebugRow>();
        for (int rowIndex = 0; rowIndex < list->ListLength; rowIndex++)
        {
            int trueItemId = GetItemInspectionTrueItemId(rowIndex) ?? rowIndex;
            result.Add(new ItemInspectionDebugRow(rowIndex, trueItemId, GetItemInspectionListRowQuantity(rowIndex) ?? 0, ShouldSkipItemInspectionItem(trueItemId)));
        }

        return result;
    }

    public IReadOnlyList<string> ListAvailableUiClicks(string filter)
    {
        var result = new List<string>();
        foreach (var click in ClickHelper.GetAvailableClicks())
        {
            if (string.IsNullOrWhiteSpace(filter) || click.Contains(filter, StringComparison.OrdinalIgnoreCase))
                result.Add(click);
        }

        return result;
    }

    public bool SendUiClick(string addonName, string clickName)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(addonName).Address;
        if (addon == null || !addon->IsVisible)
        {
            log.Debug("[GuiInteract] {AddonName} not open, cannot send UI click {ClickName}", addonName, clickName);
            return false;
        }

        log.Debug("[GuiInteract] Sending UI click {ClickName} to {AddonName} at 0x{Address:X}", clickName, addonName, (nint)addon);
        ClickHelper.SendClick(clickName, (nint)addon);
        return true;
    }

    public void ToggleItemInspectionEventLogging()
    {
        if (itemInspectionEventLogging)
        {
            addonLifecycle.UnregisterListener(OnItemInspectionListReceiveEvent);
            addonLifecycle.UnregisterListener(OnItemInspectionResultSetup);
            itemInspectionEventLogging = false;
            lastItemInspectionEvent = "ItemInspectionList event logging is off.";
            itemInspectionEvents.Clear();
            return;
        }

        addonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, "ItemInspectionList", OnItemInspectionListReceiveEvent);
        addonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "ItemInspectionList", OnItemInspectionListReceiveEvent);
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, "ItemInspectionResult", OnItemInspectionResultSetup);
        itemInspectionEventLogging = true;
        lastItemInspectionEvent = "ItemInspectionList event logging is on. Manually click a row.";
        itemInspectionEvents.Clear();
        itemInspectionEvents.Add(lastItemInspectionEvent);
    }

    private void OnItemInspectionListReceiveEvent(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonReceiveEventArgs receive)
            return;

        string eventLine = $"ItemInspectionList {type}: AtkEventType={receive.AtkEventType} EventParam={receive.EventParam} " +
                           $"AtkEvent=0x{receive.AtkEvent:X} AtkEventData=0x{receive.AtkEventData:X}";
        RecordItemInspectionEvent(eventLine);

        string eventTypeName = receive.AtkEventType.ToString();
        if (!eventTypeName.Contains("RollOut") && !eventTypeName.Contains("RollOver"))
            lastItemInspectionEvent = eventLine;

        log.Debug("[GuiInteract] {Event}", eventLine);
    }

    private void OnItemInspectionResultSetup(AddonEvent type, AddonArgs args)
    {
        string eventLine = $"ItemInspectionResult {type}: Addon=0x{args.Addon:X}";
        lastItemInspectionEvent = eventLine;
        RecordItemInspectionEvent(eventLine);
        log.Debug("[GuiInteract] {Event}", eventLine);
    }

    private void RecordItemInspectionEvent(string eventLine)
    {
        itemInspectionEvents.Add(eventLine);
        if (itemInspectionEvents.Count > 30)
            itemInspectionEvents.RemoveAt(0);
    }

    // Generic version of the ItemInspectionList row dump/select/click helpers — works on any addon
    // with an AtkComponentList by node ID (e.g. SkyIslandExchange2's list is node 13, not 7).
    public string DumpAddonListRows(string addonName, uint listNodeId)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(addonName).Address;
        if (addon == null)
            return $"{addonName}: not found (not currently open)";

        var list = FindComponentList(addon, listNodeId);
        if (list == null)
            return $"{addonName}: list node {listNodeId} not found (or not a List component)";

        var sb = new StringBuilder();
        sb.AppendLine($"{addonName}  ID={addon->Id}  Visible={addon->IsVisible}  ListNode={listNodeId}");
        sb.AppendLine($"List: Length={list->ListLength} FirstVisible={list->FirstVisibleItemIndex} Selected={list->SelectedItemIndex} Hovered={list->HoveredItemIndex} VisibleRows={list->VisibleRowCount} NumVisibleItems={list->NumVisibleItems}");

        for (int i = 0; i < list->UldManager.NodeListCount; i++)
        {
            var componentNode = list->UldManager.NodeList[i]->GetAsAtkComponentNode();
            var renderer = componentNode != null ? componentNode->GetAsAtkComponentListItemRenderer() : null;
            if (renderer == null || !componentNode->AtkResNode.IsVisible())
                continue;

            sb.AppendLine($"RowComponent NodeId={componentNode->AtkResNode.NodeId} ListItemIndex={renderer->ListItemIndex} Pos=({componentNode->AtkResNode.X:F0},{componentNode->AtkResNode.Y:F0}) Size=({componentNode->AtkResNode.Width}x{componentNode->AtkResNode.Height})");
            for (int textNodeId = 1; textNodeId <= 12; textNodeId++)
            {
                var textNode = renderer->GetTextNodeById((uint)textNodeId);
                if (textNode == null || !textNode->AtkResNode.IsVisible())
                    continue;

                sb.AppendLine($"  TextNode {textNodeId}: \"{textNode->NodeText}\"");
            }
        }

        return sb.ToString();
    }

    // Reads the current row count without selecting/clicking anything — used to detect "no rows
    // left" before an automation loop tries to select/click a row that no longer exists.
    public int? GetAddonListLength(string addonName, uint listNodeId)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(addonName).Address;
        if (addon == null || !addon->IsVisible)
            return null;

        var list = FindComponentList(addon, listNodeId);
        return list == null ? null : list->ListLength;
    }

    public bool SelectAddonListRow(string addonName, uint listNodeId, int rowIndex, bool dispatchEvent)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(addonName).Address;
        if (addon == null || !addon->IsVisible)
        {
            log.Debug("[GuiInteract] {AddonName} not open, cannot select row {RowIndex}", addonName, rowIndex);
            return false;
        }

        var list = FindComponentList(addon, listNodeId);
        if (list == null)
        {
            log.Debug("[GuiInteract] {AddonName} list node {ListNodeId} not found", addonName, listNodeId);
            return false;
        }

        log.Debug("[GuiInteract] {AddonName} SelectItem({RowIndex}, {DispatchEvent}) listLength={ListLength} selected={Selected}",
            addonName, rowIndex, dispatchEvent, list->ListLength, list->SelectedItemIndex);
        list->SelectItem(rowIndex, dispatchEvent);
        return true;
    }

    public bool ClickAddonListRow(string addonName, uint listNodeId, int rowIndex)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(addonName).Address;
        if (addon == null || !addon->IsVisible)
        {
            log.Debug("[GuiInteract] {AddonName} not open, cannot click row {RowIndex}", addonName, rowIndex);
            return false;
        }

        var list = FindComponentList(addon, listNodeId);
        if (list == null)
        {
            log.Debug("[GuiInteract] {AddonName} list node {ListNodeId} not found", addonName, listNodeId);
            return false;
        }

        var renderer = FindListItemRenderer(list, rowIndex);
        if (renderer == null)
        {
            log.Debug("[GuiInteract] {AddonName} renderer for row {RowIndex} not found", addonName, rowIndex);
            return false;
        }

        var eventData = new AtkEventData();
        eventData.ListItemData.ListItemRenderer = renderer;
        eventData.ListItemData.SelectedIndex = rowIndex;
        eventData.ListItemData.MouseButtonId = 0;

        var atkEvent = new AtkEvent();
        atkEvent.Node = renderer->AtkResNode;
        atkEvent.Listener = (AtkEventListener*)addon;
        atkEvent.Param = 0;

        log.Debug("[GuiInteract] {AddonName} ReceiveEvent(ListItemClick, row={RowIndex}, node={NodeId})", addonName, rowIndex, renderer->AtkResNode->NodeId);
        addon->ReceiveEvent(AtkEventType.ListItemClick, 0, &atkEvent, &eventData);
        return true;
    }

    private static AtkComponentList* FindComponentList(AtkUnitBase* addon, uint listNodeId)
    {
        for (int i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || node->NodeId != listNodeId)
                continue;

            var componentNode = node->GetAsAtkComponentNode();
            var component = componentNode != null ? componentNode->GetComponent() : null;
            if (component != null && component->GetComponentType() == ComponentType.List)
                return (AtkComponentList*)component;
        }

        return null;
    }

    private static AtkComponentListItemRenderer* FindListItemRenderer(AtkComponentList* list, int rowIndex)
    {
        for (int i = 0; i < list->UldManager.NodeListCount; i++)
        {
            var componentNode = list->UldManager.NodeList[i]->GetAsAtkComponentNode();
            var renderer = componentNode != null ? componentNode->GetAsAtkComponentListItemRenderer() : null;
            if (renderer != null && renderer->ListItemIndex == rowIndex)
                return renderer;
        }

        return null;
    }

    public string DebugAddonInfo(string addonName)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(addonName).Address;
        if (addon == null)
            return $"{addonName}: not found (not currently open)";

        return $"{addonName}  Address=0x{(nint)addon:X}  ID={addon->Id}  Visible={addon->IsVisible}  " +
               $"UldObjectCount={addon->UldManager.ObjectCount}  NodeListCount={addon->UldManager.NodeListCount}";
    }

    public IReadOnlyList<string> ListLoadedAddons()
    {
        var result = new List<string>();
        var unitManager = &AtkStage.Instance()->RaptureAtkUnitManager->AtkUnitManager;
        var list = &unitManager->AllLoadedUnitsList;
        for (int i = 0; i < list->Count; i++)
        {
            var unit = list->Entries[i].Value;
            if (unit == null)
                continue;

            string name = unit->NameString;
            result.Add($"{name}  ID={unit->Id}  Visible={unit->IsVisible}  Addr=0x{(nint)unit:X}");
        }

        return result;
    }

    public string DebugCeRecruitmentWindow()
    {
        var agent = GetCeRecruitmentAgent();
        if (agent == null)
            return "Resistance Recruitment agent: unavailable";

        var agentInterface = (AgentInterface*)agent;
        uint addonId = agentInterface->GetAddonId();
        var manager = RaptureAtkUnitManager.Instance();
        var addon = addonId == 0 || manager == null ? null : manager->GetAddonById(checked((ushort)addonId));
        return $"Resistance Recruitment agent: Active={agentInterface->IsAgentActive()} AddonId={addonId} " +
               (addon == null ? "Addon=unavailable" : $"AddonVisible={addon->IsVisible} Loaded={addon->UldManager.LoadedState}");
    }

    public bool OpenCeRecruitmentWindow()
    {
        var agent = GetCeRecruitmentAgent();
        if (agent == null)
            return false;

        ((AgentInterface*)agent)->Show();
        return true;
    }

    public IReadOnlyList<string> GetCeRecruitmentButtons()
    {
        var addon = GetCeRecruitmentAddon();
        return addon == null ? [] : CollectCeRecruitmentButtons(addon).Select(button => button.Text).ToList();
    }

    public bool IsCeRecruitmentWindowOpen() => GetCeRecruitmentAddon() != null;

    public bool ClickCeRecruitmentButton(string label)
        => ClickCeRecruitmentButton(label, null);

    public bool ClickCeRecruitmentButton(string label, string? eventName)
    {
        var addon = GetCeRecruitmentAddon();
        if (addon == null || string.IsNullOrWhiteSpace(label))
            return false;

        var candidate = CollectCeRecruitmentButtons(addon).FirstOrDefault(button =>
            button.Text.Equals(label, StringComparison.OrdinalIgnoreCase) &&
            (eventName == null || ButtonBelongsToEvent(button, eventName)));
        if (candidate.Button == 0)
            return false;

        var button = (AtkComponentButton*)candidate.Button;
        var ownerNode = button->AtkComponentBase.OwnerNode;
        var eventNode = ownerNode == null ? null : ownerNode->AtkResNode.AtkEventManager.Event;
        if (eventNode == null)
            return false;

        for (int index = 0; index < 16; index++)
        {
            if (eventNode->State.EventType is AtkEventType.MouseClick or AtkEventType.ButtonClick || eventNode->NextEvent == null)
                break;

            eventNode = eventNode->NextEvent;
        }

        var atkEvent = new AtkEvent
        {
            Node = &ownerNode->AtkResNode,
            Listener = (AtkEventListener*)addon,
            Param = eventNode->Param,
        };
        var eventData = new AtkEventData();
        addon->ReceiveEvent(eventNode->State.EventType, unchecked((int)eventNode->Param), &atkEvent, &eventData);
        return true;
    }

    private static bool ButtonBelongsToEvent(CeRecruitmentButton button, string eventName)
    {
        var component = (AtkComponentButton*)button.Button;
        var ownerNode = component == null ? null : component->AtkComponentBase.OwnerNode;
        var node = ownerNode == null ? null : &ownerNode->AtkResNode;
        for (int depth = 0; node != null && depth < 8; depth++, node = node->ParentNode)
            if (NodeTreeContainsText(node, eventName, 0))
                return true;

        return false;
    }

    private static bool NodeTreeContainsText(AtkResNode* node, string eventName, int depth)
    {
        if (node == null || depth > 8)
            return false;

        if (node->Type == NodeType.Text &&
            ((AtkTextNode*)node)->NodeText.ToString().Contains(eventName, StringComparison.OrdinalIgnoreCase))
            return true;

        for (var child = node->ChildNode; child != null; child = child->NextSiblingNode)
            if (NodeTreeContainsText(child, eventName, depth + 1))
                return true;

        var componentNode = node->GetAsAtkComponentNode();
        var component = componentNode == null ? null : componentNode->GetComponent();
        if (component == null || component->UldManager.LoadedState != AtkLoadState.Loaded || component->UldManager.NodeList == null)
            return false;

        for (int index = 0; index < component->UldManager.NodeListCount; index++)
            if (NodeTreeContainsText(component->UldManager.NodeList[index], eventName, depth + 1))
                return true;

        return false;
    }

    private static AgentMycBattleAreaInfo* GetCeRecruitmentAgent()
    {
        var agentModule = AgentModule.Instance();
        return agentModule == null
            ? null
            : (AgentMycBattleAreaInfo*)agentModule->GetAgentByInternalId(AgentId.MycBattleAreaInfo);
    }

    private static AtkUnitBase* GetCeRecruitmentAddon()
    {
        var agent = GetCeRecruitmentAgent();
        if (agent == null || !((AgentInterface*)agent)->IsAgentActive())
            return null;

        uint addonId = ((AgentInterface*)agent)->GetAddonId();
        var manager = RaptureAtkUnitManager.Instance();
        var addon = addonId == 0 || manager == null ? null : manager->GetAddonById(checked((ushort)addonId));
        return addon != null && addon->IsVisible && addon->UldManager.LoadedState == AtkLoadState.Loaded ? addon : null;
    }

    private readonly record struct CeRecruitmentButton(nint Button, string Text);

    private static List<CeRecruitmentButton> CollectCeRecruitmentButtons(AtkUnitBase* addon)
    {
        var buttons = new List<CeRecruitmentButton>();
        Walk(&addon->UldManager, buttons, 0);
        return buttons;

        static void Walk(AtkUldManager* manager, List<CeRecruitmentButton> buttons, int depth)
        {
            if (manager == null || manager->LoadedState != AtkLoadState.Loaded || manager->NodeList == null || depth > 6 || buttons.Count > 64)
                return;

            for (int index = 0; index < manager->NodeListCount; index++)
            {
                var node = manager->NodeList[index];
                if (node == null || !node->IsVisible() || (ushort)node->Type < 1000)
                    continue;

                var component = ((AtkComponentNode*)node)->Component;
                if (component == null)
                    continue;

                if (component->GetComponentType() == ComponentType.Button)
                {
                    var button = (AtkComponentButton*)component;
                    var textNode = button->ButtonTextNode;
                    if (button->IsEnabled && textNode != null)
                        buttons.Add(new CeRecruitmentButton((nint)button, textNode->NodeText.ToString()));
                }

                Walk(&component->UldManager, buttons, depth + 1);
            }
        }
    }

    public string DumpAddonNodes(string addonName, int maxNodes = 200)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(addonName).Address;
        if (addon == null)
            return $"{addonName}: not found (not currently open)";

        var sb = new StringBuilder();
        sb.AppendLine($"{addonName}  ID={addon->Id}  Visible={addon->IsVisible}  NodeListCount={addon->UldManager.NodeListCount}");

        int count = Math.Min(addon->UldManager.NodeListCount, maxNodes);
        for (int i = 0; i < count; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null)
                continue;

            string text = "";
            if (node->Type == NodeType.Text)
                text = $" Text=\"{((AtkTextNode*)node)->NodeText}\"";
            else if (node->Type == NodeType.Counter)
                text = $" Text=\"{((AtkCounterNode*)node)->NodeText}\"";

            string parent = node->ParentNode != null ? node->ParentNode->NodeId.ToString() : "-";
            sb.AppendLine($"  [{i}] NodeId={node->NodeId} ParentId={parent} Type={node->Type} Visible={node->IsVisible()} " +
                          $"Pos=({node->X:F0},{node->Y:F0}) Size=({node->Width}x{node->Height}){text}");
        }

        return sb.ToString();
    }

    public string InspectNodesAt(float mouseX, float mouseY, int maxMatches = 30)
    {
        var matches = new List<(float Area, string Description)>();
        var visited = new HashSet<nint>();
        var unitManager = &AtkStage.Instance()->RaptureAtkUnitManager->AtkUnitManager;
        var list = &unitManager->AllLoadedUnitsList;

        for (int addonIndex = 0; addonIndex < list->Count; addonIndex++)
        {
            var addon = list->Entries[addonIndex].Value;
            if (addon == null || !addon->IsVisible)
                continue;

            string addonName = addon->NameString;
            for (int nodeIndex = 0; nodeIndex < addon->UldManager.NodeListCount; nodeIndex++)
            {
                var node = addon->UldManager.NodeList[nodeIndex];
                CollectNodeHits(addonName, addon, $"AddonNode[{nodeIndex}]", node, mouseX, mouseY, matches, visited);
            }
        }

        return BuildInspectResult(mouseX, mouseY, matches.OrderBy(match => match.Area).Take(maxMatches).Select(match => match.Description).ToList());
    }

    private static void CollectNodeHits(
        string addonName,
        AtkUnitBase* addon,
        string source,
        AtkResNode* node,
        float mouseX,
        float mouseY,
        List<(float Area, string Description)> matches,
        HashSet<nint> visited)
    {
        if (node == null || !visited.Add((nint)node))
            return;

        if (node->IsVisible() && ContainsScreenPoint(node, mouseX, mouseY))
            matches.Add((GetNodeArea(node), DescribeNodeHit(addonName, addon, source, node)));

        for (var child = node->ChildNode; child != null; child = child->NextSiblingNode)
            CollectNodeHits(addonName, addon, $"ChildOf({node->NodeId})", child, mouseX, mouseY, matches, visited);

        var componentNode = node->GetAsAtkComponentNode();
        var component = componentNode != null ? componentNode->GetComponent() : null;
        if (component == null)
            return;

        for (int i = 0; i < component->UldManager.NodeListCount; i++)
            CollectNodeHits(addonName, addon, $"Component({node->NodeId})[{i}]", component->UldManager.NodeList[i], mouseX, mouseY, matches, visited);
    }

    private static string BuildInspectResult(float mouseX, float mouseY, IReadOnlyList<string> matches)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Mouse=({mouseX:F0},{mouseY:F0})  Matches={matches.Count}");
        foreach (var match in matches)
            sb.AppendLine(match);
        return sb.ToString();
    }

    private static bool ContainsScreenPoint(AtkResNode* node, float mouseX, float mouseY)
    {
        float width = node->Width * (node->ScaleX == 0 ? 1f : node->ScaleX);
        float height = node->Height * (node->ScaleY == 0 ? 1f : node->ScaleY);
        return mouseX >= node->ScreenX && mouseX <= node->ScreenX + width &&
               mouseY >= node->ScreenY && mouseY <= node->ScreenY + height;
    }

    private static float GetNodeArea(AtkResNode* node)
    {
        float width = node->Width * (node->ScaleX == 0 ? 1f : node->ScaleX);
        float height = node->Height * (node->ScaleY == 0 ? 1f : node->ScaleY);
        return width * height;
    }

    private static string DescribeNodeHit(string addonName, AtkUnitBase* addon, string source, AtkResNode* node)
    {
        string text = "";
        if (node->Type == NodeType.Text)
            text = $" Text=\"{((AtkTextNode*)node)->NodeText}\"";
        else if (node->Type == NodeType.Counter)
            text = $" Text=\"{((AtkCounterNode*)node)->NodeText}\"";

        string component = DescribeComponent(node);
        string rowText = DescribeListRendererText(node);
        return $"{addonName} AddonId={addon->Id} Source={source} Path={BuildNodePath(node)} Type={node->Type}{component} " +
               $"Screen=({node->ScreenX},{node->ScreenY}) Size=({node->Width}x{node->Height}){text}{rowText}";
    }

    private static string DescribeComponent(AtkResNode* node)
    {
        var componentNode = node->GetAsAtkComponentNode();
        if (componentNode == null)
            return "";

        var component = componentNode->GetComponent();
        return component == null ? " Component=null" : $" Component={component->GetComponentType()}";
    }

    private static string DescribeListRendererText(AtkResNode* node)
    {
        var componentNode = node->GetAsAtkComponentNode();
        var renderer = componentNode != null ? componentNode->GetAsAtkComponentListItemRenderer() : null;
        if (renderer == null)
            return "";

        var texts = new List<string>();
        for (int textNodeId = 1; textNodeId <= 12; textNodeId++)
        {
            var textNode = renderer->GetTextNodeById((uint)textNodeId);
            if (textNode == null || !textNode->AtkResNode.IsVisible())
                continue;

            string value = textNode->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                texts.Add($"{textNodeId}=\"{value}\"");
        }

        return texts.Count == 0 ? "" : $" RendererText=[{string.Join(", ", texts)}]";
    }

    private static string BuildNodePath(AtkResNode* node)
    {
        var ids = new List<uint>();
        for (var current = node; current != null && ids.Count < 32; current = current->ParentNode)
            ids.Add(current->NodeId);

        ids.Reverse();
        return string.Join("/", ids.Select(id => id.ToString()));
    }
}

public sealed unsafe partial class HostileDetectionService
{
    // Snapshot of the last REAL scan (not a cache hit) — lets the debug UI show exactly which
    // filtering stage a hostile drops out at, without needing a log file.
    public int DebugTotalObjectCount { get; private set; }
    public int DebugBattleNpcCount { get; private set; }
    public int DebugHostileFlaggedCount { get; private set; }
    public int DebugWithinRadiusCount { get; private set; }

    private void ResetDebugScanCounters() => UpdateDebugScanCounters(0, 0, 0, 0);

    private void UpdateDebugScanCounters(int totalObjects, int battleNpcs, int hostileFlagged, int withinRadius)
    {
        DebugTotalObjectCount = totalObjects;
        DebugBattleNpcCount = battleNpcs;
        DebugHostileFlaggedCount = hostileFlagged;
        DebugWithinRadiusCount = withinRadius;
    }

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
        AggroType AggroType,
        uint FateId
    );

    public string DumpNpcDebugData(IGameObject? selectedTarget, uint territoryId)
    {
        var report = new StringBuilder();
        var player = objects.LocalPlayer;
        var detectedIds = GetNearbyHostiles().Select(hostile => hostile.ObjectId).ToHashSet();
        var friendlyNameplateAddresses = GetFriendlyNameplateAddresses();
        float radius = config.DetectionRadiusYalms > 0f ? config.DetectionRadiusYalms : 100f;
        var npcs = objects.OfType<IBattleNpc>()
            .OrderByDescending(npc => npc.GameObjectId == selectedTarget?.GameObjectId)
            .ThenBy(npc => player == null ? 0f : Vector3.DistanceSquared(player.Position, npc.Position))
            .ToList();

        report.AppendLine("BOTJA NPC DIAGNOSTIC EXPORT");
        report.AppendLine($"Captured UTC: {DateTime.UtcNow:O}");
        report.AppendLine($"Territory ID: {territoryId}");
        report.AppendLine($"Configured radius: {config.DetectionRadiusYalms.ToString(CultureInfo.InvariantCulture)}y; effective radius: {radius.ToString(CultureInfo.InvariantCulture)}y (XZ)");
        report.AppendLine($"Loaded battle NPCs: {npcs.Count}; cached detected hostiles: {detectedIds.Count}");
        report.AppendLine($"Scan counters: Objects={DebugTotalObjectCount}; BattleNpc={DebugBattleNpcCount}; HostileFlaggedAfterAllyExclusion={DebugHostileFlaggedCount}; WithinRadius={DebugWithinRadiusCount}");
        report.AppendLine("Scope: all client-loaded battle NPCs, including friendly, dead, untargetable, and out-of-radius NPCs. Not a full-zone scan.");
        report.AppendLine("Live filter results below are separate from the detection cache (up to 150ms old).");
        report.AppendLine("StatusFlags.Hostile is the current filter input, not proof of hostility toward the player.");
        report.AppendLine("AggroType is a Botja lookup, not a native faction or allegiance field.");
        report.AppendLine("Nameplate UI kind and color category are independent diagnostic signals; colors can be customized and are not proof of allegiance.");
        report.AppendLine("Property reads are bounded and report errors individually; pointers are addresses only, not memory dumps.");
        report.AppendLine();
        report.AppendLine("LOCAL PLAYER");
        AppendObjectProperties(report, player);
        report.AppendLine();
        report.AppendLine("SELECTED TARGET");
        AppendObjectProperties(report, selectedTarget);
        if (selectedTarget != null && selectedTarget is not IBattleNpc)
            AppendNameplateDebugData(report, selectedTarget);

        foreach (var npc in npcs)
        {
            report.AppendLine();
            report.AppendLine(new string('=', 80));
            report.AppendLine($"NPC: {npc.Name}; SelectedTarget={npc.GameObjectId == selectedTarget?.GameObjectId}");
            report.AppendLine($"ObjectId: {npc.GameObjectId} (0x{npc.GameObjectId:X16}); BaseId: {npc.BaseId}; Address: 0x{npc.Address:X}");
            report.AppendLine($"CachedDetectionIncludesNpc: {detectedIds.Contains(npc.GameObjectId)}");
            try
            {
                var flags = npc.StatusFlags;
                var target = npc.TargetObject;
                float distance = player == null ? float.NaN : Vector2.Distance(
                    new Vector2(player.Position.X, player.Position.Z), new Vector2(npc.Position.X, npc.Position.Z));
                bool hostile = flags.HasFlag(StatusFlags.Hostile);
                bool party = flags.HasFlag(StatusFlags.PartyMember);
                bool alliance = flags.HasFlag(StatusFlags.AllianceMember);
                bool partyNpc = npc.BattleNpcKind == BattleNpcSubKind.NpcPartyMember;
                bool friendlyNameplate = friendlyNameplateAddresses.Contains(npc.Address);
                string outcome = player == null ? "Excluded: no local player"
                    : !hostile ? "Excluded: Hostile flag not set"
                    : friendlyNameplate ? "Excluded: UI3D nameplate classified as BattleNpcFriendly"
                    : party || alliance || partyNpc ? "Excluded: party/alliance/NpcPartyMember"
                    : npc.IsDead || !npc.IsTargetable ? "Excluded: dead or untargetable"
                    : distance > radius ? "Excluded: outside detection radius"
                    : "INCLUDED AS HOSTILE";
                report.AppendLine($"Live detection outcome: {outcome}");
                report.AppendLine($"Filter inputs: Hostile={hostile}; FriendlyNameplate={friendlyNameplate}; PartyMember={party}; AllianceMember={alliance}; NpcPartyMember={partyNpc}; Dead={npc.IsDead}; Targetable={npc.IsTargetable}; DistanceXZ={distance.ToString("F3", CultureInfo.InvariantCulture)}y");
                report.AppendLine($"StatusFlags: {FormatDebugValue(flags)}; BattleNpcKind: {FormatDebugValue(npc.BattleNpcKind)}");
                report.AppendLine($"TargetingPlayer={target != null && player != null && target.GameObjectId == player.GameObjectId}; EngagedElsewhere={target != null && player != null && target.GameObjectId != player.GameObjectId}");
                report.AppendLine($"Resolved target: {(target == null ? "<null>" : $"{target.Name}; ObjectId={target.GameObjectId} (0x{target.GameObjectId:X16}); BaseId={target.BaseId}; Kind={target.ObjectKind}; Position={FormatDebugValue(target.Position)}")}");
                report.AppendLine($"Botja AggroType: {MobAggroData.Classify(npc.BaseId)}");
                if (npc.Address != 0)
                {
                    var native = (BattleChara*)npc.Address;
                    report.AppendLine("Native BattleChara fields:");
                    report.AppendLine($"  FateId={native->FateId}; ForayInfo.Level={native->ForayInfo.Level}");
                    report.AppendLine($"  NamePlateIconId={native->NamePlateIconId}; TargetableStatus={native->TargetableStatus}");
                    report.AppendLine($"  CurrentTargetStatus={native->CurrentTargetStatus}; NextTargetStatus={native->NextTargetStatus}; RenderFlags={native->RenderFlags}");
                }
            }
            catch (Exception exception)
            {
                report.AppendLine($"Live/native diagnostic error: {exception.GetType().Name}: {exception.Message}");
            }

            report.AppendLine("All readable managed NPC interface properties:");
            AppendObjectProperties(report, npc);
            AppendNameplateDebugData(report, npc);
        }

        report.AppendLine();
        report.AppendLine("END OF NPC DIAGNOSTIC EXPORT");
        return report.ToString();
    }

    private void AppendNameplateDebugData(StringBuilder report, IGameObject gameObject)
    {
        report.AppendLine("NAMEPLATE DIAGNOSTICS");
        if (gameObject.Address == 0)
        {
            report.AppendLine("  Native object unavailable.");
            return;
        }

        try
        {
            var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)gameObject.Address;
            report.AppendLine($"  Native EntityId=0x{native->EntityId:X8}; ObjectIndex={native->ObjectIndex}; Visibility={native->Visibility}; UpdateFlags={FormatDebugValue(native->UpdateFlags)}");
            report.AppendLine($"  NamePlateIconId={native->NamePlateIconId}; RenderFlags={FormatDebugValue(native->RenderFlags)}; NameplateRenderBitSet={((ulong)native->RenderFlags & (1UL << 11)) != 0}");
            report.AppendLine($"  NameplateOffset={native->NameplateOffset}; OffsetTarget={native->NameplateOffsetTarget}; OffsetScaleMultiplier={native->NameplateOffsetScaleMultiplier.ToString(CultureInfo.InvariantCulture)}");
            try
            {
                byte colorType = native->GetNamePlateColorType();
                var colors = native->GetNamePlateColors();
                report.AppendLine($"  Native color category={colorType} ({NameplateColorCategory(colorType)}); PackedColors=0x{colors.Data:X16}");
                report.AppendLine($"  Native text color=0x{colors.Data >> 32:X8}; Edge color=0x{(uint)colors.Data:X8}; TargetType={FormatDebugValue(native->GetTargetType())}");
            }
            catch (Exception exception)
            {
                report.AppendLine($"  Native nameplate getter error: {exception.GetBaseException().Message}");
            }

            var uiModule = UIModule.Instance();
            if (uiModule == null)
            {
                report.AppendLine("  UIModule unavailable.");
                return;
            }

            var ui3d = uiModule->GetUI3DModule();
            var addon = (AddonNamePlate*)gameGui.GetAddonByName("NamePlate").Address;
            report.AppendLine(addon == null ? "  NamePlate addon unavailable."
                : $"  Addon=0x{(nint)addon:X}; Visible={addon->IsVisible}; ObjectArray=0x{(nint)addon->NamePlateObjectArray:X}");
            if (ui3d != null)
            {
                report.AppendLine($"  UI3D nameplate object count={ui3d->NamePlateObjectInfoCount}");
                bool infoFound = false;
                for (int index = 0; index < ui3d->ObjectInfos.Length; index++)
                {
                    ref var info = ref ui3d->ObjectInfos[index];
                    if (info.GameObject != native)
                        continue;
                    infoFound = true;
                    report.AppendLine($"  UI3D ObjectInfo[{index}]: Kind={FormatDebugValue(info.NamePlateObjectKind)}; AdjustedKind={FormatDebugValue(info.NamePlateObjectKindAdjusted)}; PlateIndex={info.NamePlateIndex}; Scale={info.NamePlateScale}; SortPriority={info.SortPriority}");
                    report.AppendLine($"    PlatePosition={info.NamePlatePos}; ProjectedPosition={info.ObjectPosProjectedScreenSpace}; CameraDistance={info.DistanceFromCamera}; PlayerDistance={info.DistanceFromPlayer}");
                }
                if (!infoFound)
                    report.AppendLine("  No UI3D ObjectInfo matched this native object address.");

                bool slotFound = false;
                for (int slot = 0; slot < ui3d->NamePlateObjectIds.Length; slot++)
                {
                    if (ui3d->NamePlateObjectIds[slot].Id != gameObject.GameObjectId)
                        continue;
                    slotFound = true;
                    report.AppendLine($"  UI3D plate slot[{slot}]: ObjectId=0x{ui3d->NamePlateObjectIds[slot].Id:X16}; WorkingObjectId=0x{ui3d->NamePlateObjectIds_2[slot].Id:X16}");
                    if (addon == null || addon->NamePlateObjectArray == null || slot >= AddonNamePlate.NumNamePlateObjects)
                        continue;
                    var plate = &addon->NamePlateObjectArray[slot];
                    report.AppendLine($"    Rendered plate: Kind={FormatDebugValue(plate->NamePlateKind)}; HPLabelState={plate->HPLabelState}; ClickThrough={plate->ClickThrough}; IsPvpEnemy={plate->IsPvpEnemy}; UseAttackCursor={plate->UseAttackCursor}");
                    report.AppendLine($"    Priority={plate->Priority}; TextSize={plate->TextW}x{plate->TextH}; IconAdjust={plate->IconXAdjust},{plate->IconYAdjust}; NeedsToBeBaked={plate->NeedsToBeBaked}; DrawOriginalText={plate->DrawOriginalText}");
                    report.AppendLine($"    Bake: IsBaked={plate->BakeData.IsBaked}; Alpha={plate->BakeData.Alpha}; Size={plate->BakeData.Width}x{plate->BakeData.Height}; UV={plate->BakeData.U},{plate->BakeData.V}; TextYOffset={plate->BakeData.TextYOffset}");
                    report.AppendLine($"    Root=0x{(nint)plate->RootComponentNode:X}; RootVisible={(plate->RootComponentNode != null && plate->RootComponentNode->IsVisible())}; NameContainerVisible={(plate->NameContainer != null && plate->NameContainer->IsVisible())}; GaugeVisible={(plate->GaugeContainer != null && plate->GaugeContainer->IsVisible())}");
                    if (plate->NameText != null)
                    {
                        var text = plate->NameText;
                        report.AppendLine($"    NameText=\"{text->NodeText}\"; Visible={text->IsVisible()}; TextColor=0x{*(uint*)&text->TextColor:X8}; EdgeColor=0x{*(uint*)&text->EdgeColor:X8}");
                    }
                }
                if (!slotFound)
                    report.AppendLine("  No current UI3D plate slot matched this GameObjectId (hidden/off-screen/unallocated possible).");
            }
            else
            {
                report.AppendLine("  UI3DModule unavailable.");
            }

            var rapture = uiModule->GetRaptureAtkModule();
            if (rapture != null)
            {
                bool found = false;
                report.AppendLine($"  RaptureAtkModule NameplateInfoCount={rapture->NameplateInfoCount}");
                for (int index = 0; index < rapture->NamePlateInfoEntries.Length; index++)
                {
                    ref var entry = ref rapture->NamePlateInfoEntries[index];
                    if (entry.ObjectId.Id != gameObject.GameObjectId)
                        continue;
                    found = true;
                    report.AppendLine($"  NamePlateInfo[{index}]: ObjectId=0x{entry.ObjectId.Id:X16}; Level={entry.Level}; ClassJobId={entry.ClassJobId}; Icon={entry.Icon}; Flags=0x{entry.Flags:X8}; IsPrefix={entry.IsPrefix}; IsDirty={entry.IsDirty}");
                    report.AppendLine($"    Name=\"{entry.Name}\"; NameOverridePointer=0x{(nint)entry.NameOverride:X}; FcName=\"{entry.FcName}\"; Title=\"{entry.Title}\"; DisplayTitle=\"{entry.DisplayTitle}\"; LevelText=\"{entry.LevelText}\"");
                }
                if (!found)
                    report.AppendLine("  No NamePlateInfo matched this GameObjectId.");
            }

            var stage = AtkStage.Instance();
            var numberArray = stage == null ? null : stage->GetNumberArrayData(NumberArrayType.NamePlate);
            var stringArray = stage == null ? null : stage->GetStringArrayData(StringArrayType.NamePlate);
            if (numberArray == null || numberArray->IntArray == null || numberArray->Size < 1056)
            {
                report.AppendLine("  NamePlate number array unavailable or smaller than the expected layout.");
                return;
            }
            var numbers = (NamePlateNumberArray*)numberArray->IntArray;
            var strings = stringArray != null && stringArray->StringArray != null && stringArray->Size >= 300
                ? (NamePlateStringArray*)stringArray->StringArray : null;
            report.AppendLine($"  Number array: ActiveCount={numbers->ActiveNamePlateCount}; NamePlateSize={numbers->NamePlateSize}; StringArrayAvailable={strings != null}");
            bool rowFound = false;
            for (int row = 0; row < Math.Clamp(numbers->ActiveNamePlateCount, 0, numbers->ObjectData.Length); row++)
            {
                ref var data = ref numbers->ObjectData[row];
                bool entityMatches = native->EntityId != 0 && native->EntityId != 0xE0000000 && data.EntityId == native->EntityId;
                bool slotMatches = ui3d != null && data.NamePlateObjectIndex >= 0 && data.NamePlateObjectIndex < ui3d->NamePlateObjectIds.Length
                    && ui3d->NamePlateObjectIds[data.NamePlateObjectIndex].Id == gameObject.GameObjectId;
                if (!entityMatches && !slotMatches)
                    continue;
                rowFound = true;
                report.AppendLine($"  Number row[{row}]: EntityId=0x{data.EntityId:X8}; PlateSlot={data.NamePlateObjectIndex}; EntityMatch={entityMatches}; SlotMatch={slotMatches}; Kind={FormatDebugValue(data.NamePlateKind)}; HPLabelState={data.HPLabelState}");
                report.AppendLine($"    TextColor=0x{data.NameTextColor:X8}; EdgeColor=0x{data.NameEdgeColor:X8}; GaugeFillColor=0x{data.GaugeFillColor:X8}; GaugeContainerColor=0x{data.GaugeContainerColor:X8}; GaugeFillPercent={data.GaugeFillPercentage}");
                report.AppendLine($"    UpdateFlags=0x{data.UpdateFlags:X8}; DrawFlags=0x{data.DrawFlags:X8}; VisibilityFlags=0x{data.VisibilityFlags:X8}");
                report.AppendLine($"    DrawName={(data.VisibilityFlags & 1) != 0}; DrawGauge={(data.VisibilityFlags & 2) != 0}; UnchangedTimeline={(data.VisibilityFlags & 4) != 0}; GaugeEnabled={(data.DrawFlags & 0x20) != 0}; AttackCursor={(data.DrawFlags & 0x40) != 0}; PvpEnemy={(data.DrawFlags & 4) != 0}; HideTitle={(data.DrawFlags & 0x80) != 0}; PrefixTitle={(data.DrawFlags & 1) != 0}");
                report.AppendLine($"    ScreenPosition={data.X},{data.Y}; Depth={data.Depth}; Scale={data.Scale}; NameIconId={data.NameIconId}; MarkerIconId={data.MarkerIconId}");
                if (strings != null)
                    report.AppendLine($"    String row[{row}]: Name=\"{strings->Name[row]}\"; Title=\"{strings->Title[row]}\"; Fc=\"{strings->Fc[row]}\"; Letter=\"{strings->Letter[row]}\"; Target=\"{strings->Target[row]}\"; Level=\"{strings->Level[row]}\"");
            }
            if (!rowFound)
                report.AppendLine("  No active number/string row matched this NPC.");
        }
        catch (Exception exception)
        {
            report.AppendLine($"  Nameplate diagnostic error: {exception.GetType().Name}: {exception.GetBaseException().Message}");
        }
    }

    private static string NameplateColorCategory(byte category) => category switch
    {
        0 => "Object", 1 => "Self", 2 => "Party", 3 => "Other player",
        4 => "Limsa", 5 => "Gridania", 6 => "Ul'dah", 7 => "Unengaged enemy",
        8 => "Dead", 9 => "Engaged enemy", 10 => "Claimed enemy", 11 => "Unclaimed enemy",
        12 or 13 => "NPC", 14 => "Minion", 15 => "Self buddy", 16 => "Self pet",
        17 => "Other buddy", 18 => "Other pet", 20 => "Alliance",
        27 => "Tank", 28 => "Healer", 29 => "DPS", 30 => "Other class",
        _ => "Unknown/hardcoded category"
    };

    private static void AppendObjectProperties(StringBuilder report, IGameObject? gameObject)
    {
        if (gameObject == null)
        {
            report.AppendLine("  <null>");
            return;
        }

        var properties = gameObject.GetType().GetInterfaces()
            .Where(contract => typeof(IGameObject).IsAssignableFrom(contract))
            .SelectMany(contract => contract.GetProperties())
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .DistinctBy(property => property.Name)
            .OrderBy(property => property.Name);
        foreach (var property in properties)
        {
            try
            {
                var value = property.GetValue(gameObject);
                report.AppendLine($"  {property.Name}: {FormatDebugValue(value)}");
                if (value is not IEnumerable entries || value is string)
                    continue;

                int index = 0;
                foreach (var entry in entries)
                {
                    if (index >= 128)
                    {
                        report.AppendLine("    <remaining entries omitted after 128>");
                        break;
                    }
                    report.AppendLine($"    [{index++}]: {FormatDebugValue(entry)}");
                    if (entry == null)
                        continue;
                    foreach (var entryProperty in entry.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(candidate => candidate.CanRead && candidate.GetIndexParameters().Length == 0)
                        .OrderBy(candidate => candidate.Name))
                    {
                        try
                        {
                            report.AppendLine($"      {entryProperty.Name}: {FormatDebugValue(entryProperty.GetValue(entry))}");
                        }
                        catch (Exception exception)
                        {
                            report.AppendLine($"      {entryProperty.Name}: <error: {exception.GetBaseException().Message}>");
                        }
                    }
                }
                report.AppendLine($"    Entries exported: {index}");
            }
            catch (Exception exception)
            {
                report.AppendLine($"  {property.Name}: <error: {exception.GetBaseException().Message}>");
            }
        }
    }

    private static string FormatDebugValue(object? value) => value switch
    {
        null => "<null>",
        Enum enumValue => $"{enumValue} (decimal {enumValue.ToString("D")}, hex 0x{enumValue.ToString("X")})",
        IGameObject gameObject => $"{gameObject.Name}; ObjectId={gameObject.GameObjectId} (0x{gameObject.GameObjectId:X16}); BaseId={gameObject.BaseId}; Kind={gameObject.ObjectKind}",
        nint address => $"0x{address:X}",
        Vector3 position => FormattableString.Invariant($"({position.X:F6}, {position.Y:F6}, {position.Z:F6})"),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "<null>",
        _ => value.ToString() ?? "<null>"
    };

}

public partial class FateNavigationService
{
    // Debug state exposed to FateListWindow.
    public string DebugPhase => phase.ToString();
    public int DebugPhaseTicks => phaseTicks;
    public Vector3 DebugPendingDestination => pendingDestination;
    public string DebugPendingDestinationName => pendingDestinationName;
    public Vector3 DebugPendingSourceShardPos => pendingSourceShardPos;
    public Vector3 DebugPendingDestShardPos => pendingDestShardPos;
    public bool DebugVnavIsPathfinding => SafeIsPathfinding();
    public bool DebugVnavIsRunning => SafeIsRunning();
    public bool DebugVnavIsNavmeshReady => SafeIsNavmeshReady();
    public int DebugVnavPathWaypointCount => SafeGetPathWaypoints().Count;
    public bool DebugLifestreamBusy => SafeIsBusy();
}
