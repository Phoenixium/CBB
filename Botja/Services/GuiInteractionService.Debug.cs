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

namespace Botja.Services;

public unsafe partial class GuiInteractionService
{
    public readonly record struct ItemInspectionDebugRow(int ItemId, int Quantity, bool Skipped);

    private bool itemInspectionEventLogging;
    private string lastItemInspectionEvent = "ItemInspectionList event logging is off.";
    private readonly List<string> itemInspectionEvents = [];

    public string LastItemInspectionEvent => lastItemInspectionEvent;
    public string RecentItemInspectionEvents => itemInspectionEvents.Count == 0
        ? lastItemInspectionEvent
        : string.Join("\n", itemInspectionEvents);

    public IReadOnlyList<ItemInspectionDebugRow> GetItemInspectionDebugRows()
    {
        var list = GetItemInspectionList();
        if (list == null)
            return [];

        var result = new List<ItemInspectionDebugRow>();
        for (int itemId = 0; itemId < list->ListLength; itemId++)
            result.Add(new ItemInspectionDebugRow(itemId, GetItemInspectionListRowQuantity(itemId) ?? 0, ShouldSkipItemInspectionItem(itemId)));

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

    public string DumpItemInspectionListRows()
    {
        var addon = (AddonItemInspectionList*)gameGui.GetAddonByName("ItemInspectionList").Address;
        if (addon == null)
            return "ItemInspectionList: not found (not currently open)";

        var list = addon->GetComponentListById(7);
        var sb = new StringBuilder();
        sb.AppendLine($"ItemInspectionList  ID={addon->AtkUnitBase.Id}  Visible={addon->AtkUnitBase.IsVisible}  NodeListCount={addon->AtkUnitBase.UldManager.NodeListCount}");
        if (list != null)
            sb.AppendLine($"List node 7: Length={list->ListLength} FirstVisible={list->FirstVisibleItemIndex} Selected={list->SelectedItemIndex} Hovered={list->HoveredItemIndex} VisibleRows={list->VisibleRowCount} NumVisibleItems={list->NumVisibleItems}");

        for (int i = 0; i < addon->AtkUnitBase.UldManager.NodeListCount; i++)
        {
            var componentNode = addon->AtkUnitBase.UldManager.NodeList[i]->GetAsAtkComponentNode();
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
