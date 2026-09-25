using Dalamud.Plugin.Services;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Ocelot.Config;
using Ocelot.Lifecycle;
using System;
using System.Collections.Generic;
using System.Text;

namespace Botja.Services;

public unsafe partial class GuiInteractionService(IGameGui gameGui, IAddonLifecycle addonLifecycle, ItemInspectionConfig itemInspectionConfig, IConfigSaver configSaver, IPluginLog log) : IOnUpdate
{
    private static readonly TimeSpan AutomationActionDelay = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan AutomationPhaseTimeout = TimeSpan.FromMinutes(10);

    private enum ItemInspectionAutomationPhase
    {
        Idle,
        SelectRow,
        ClickRow,
        WaitForYesNo,
        WaitForResult,
        AdvanceResult,
        WaitForList,
    }

    private ItemInspectionAutomationPhase automationPhase;
    private int automationRowIndex;
    private int automationListLength;
    private int automationProcessedItems;
    private int automationItemQuantity;
    private int automationItemNextClicksRemaining;
    private int automationTicks;
    private DateTime automationNextActionAt;
    private DateTime automationPhaseStartedAt;
    private string automationStatus = "Idle";

    public string ItemInspectionAutomationStatus => automationStatus;

    public void StartItemInspectionAutomation()
    {
        var list = GetItemInspectionList();
        if (list == null)
        {
            automationStatus = "Cannot start: ItemInspectionList is not open.";
            log.Debug("[GuiInteract] {Status}", automationStatus);
            return;
        }

        automationRowIndex = 0;
        automationListLength = list->ListLength;
    automationProcessedItems = 0;
        automationItemQuantity = 0;
        automationItemNextClicksRemaining = 0;
        automationTicks = 0;
        automationNextActionAt = DateTime.MinValue;
        automationPhaseStartedAt = DateTime.UtcNow;
        automationPhase = ItemInspectionAutomationPhase.SelectRow;
        automationStatus = $"Running: 0/{automationListLength}";
        log.Debug("[GuiInteract] ItemInspection automation started, listLength={ListLength}", automationListLength);
    }

    public void StopItemInspectionAutomation()
    {
        if (automationPhase != ItemInspectionAutomationPhase.Idle)
            log.Debug("[GuiInteract] ItemInspection automation stopped at phase={Phase} row={Row}/{Length}", automationPhase, automationRowIndex, automationListLength);

        automationPhase = ItemInspectionAutomationPhase.Idle;
    automationProcessedItems = 0;
        automationItemQuantity = 0;
        automationItemNextClicksRemaining = 0;
        automationTicks = 0;
        automationNextActionAt = DateTime.MinValue;
        automationPhaseStartedAt = DateTime.MinValue;
        automationStatus = "Stopped";
    }

    public void Update()
    {
        UpdateItemInspectionAutomation();
        UpdateGenericListAutomation();
    }

    private void UpdateItemInspectionAutomation()
    {
        if (automationPhase == ItemInspectionAutomationPhase.Idle)
            return;

        automationTicks++;
        if (DateTime.UtcNow < automationNextActionAt)
            return;

        var phaseElapsed = DateTime.UtcNow - automationPhaseStartedAt;
        if (phaseElapsed > AutomationPhaseTimeout)
        {
            automationStatus = $"Stopped: timeout in {automationPhase} at row {automationRowIndex} after {phaseElapsed.TotalSeconds:F1}s ({automationTicks} ticks)";
            log.Debug("[GuiInteract] {Status}", automationStatus);
            StopItemInspectionAutomation();
            return;
        }

        switch (automationPhase)
        {
            case ItemInspectionAutomationPhase.SelectRow:
                var list = GetItemInspectionList();
                if (list == null)
                {
                    automationStatus = "Stopped: ItemInspectionList is not open.";
                    StopItemInspectionAutomation();
                    return;
                }

                automationListLength = list->ListLength;
                if (automationListLength <= 0)
                {
                    automationStatus = $"Done: processed {automationProcessedItems} items";
                    automationPhase = ItemInspectionAutomationPhase.Idle;
                    log.Debug("[GuiInteract] {Status}", automationStatus);
                    return;
                }

                automationRowIndex = FindFirstProcessableItemInspectionRow(list);
                if (automationRowIndex < 0)
                {
                    automationStatus = $"Done: processed {automationProcessedItems} items; {automationListLength} skipped";
                    automationPhase = ItemInspectionAutomationPhase.Idle;
                    log.Debug("[GuiInteract] {Status}", automationStatus);
                    return;
                }

                automationItemQuantity = GetItemInspectionListRowQuantity(automationRowIndex) ?? 1;
                automationItemNextClicksRemaining = automationItemQuantity;
                if (!SelectItemInspectionListRow(automationRowIndex, true))
                {
                    automationStatus = $"Stopped: cannot select row {automationRowIndex}";
                    StopItemInspectionAutomation();
                    return;
                }

                SetAutomationPhase(ItemInspectionAutomationPhase.ClickRow);
                return;

            case ItemInspectionAutomationPhase.ClickRow:
                if (!ClickItemInspectionListRow(automationRowIndex))
                {
                    automationStatus = $"Stopped: cannot click row {automationRowIndex}";
                    StopItemInspectionAutomation();
                    return;
                }

                SetAutomationPhase(ItemInspectionAutomationPhase.WaitForYesNo);
                return;

            case ItemInspectionAutomationPhase.WaitForYesNo:
                if (IsAddonVisible("SelectYesno"))
                {
                    ClickSelectYesnoYes();
                    SetAutomationPhase(ItemInspectionAutomationPhase.WaitForResult);
                }
                return;

            case ItemInspectionAutomationPhase.WaitForResult:
                if (IsAddonVisible("ItemInspectionResult"))
                    SetAutomationPhase(ItemInspectionAutomationPhase.AdvanceResult);
                return;

            case ItemInspectionAutomationPhase.AdvanceResult:
                if (!IsAddonVisible("ItemInspectionResult"))
                {
                    if (automationItemNextClicksRemaining > 0)
                    {
                        automationStatus = $"Running: waiting for result, {automationItemNextClicksRemaining} item clicks remaining";
                        SetAutomationPhase(ItemInspectionAutomationPhase.WaitForResult);
                        return;
                    }

                    automationProcessedItems++;
                    SetAutomationPhase(ItemInspectionAutomationPhase.WaitForList);
                    return;
                }

                if (automationItemNextClicksRemaining <= 0)
                {
                    ClickItemInspectionResultClose();
                    automationProcessedItems++;
                    SetAutomationPhase(ItemInspectionAutomationPhase.WaitForList);
                    return;
                }

                if (!ClickItemInspectionResultNext())
                {
                    automationStatus = $"Stopped: cannot click Next for row {automationRowIndex}";
                    StopItemInspectionAutomation();
                    return;
                }

                automationItemNextClicksRemaining--;
                automationStatus = $"Running: processed {automationProcessedItems}, remaining {automationListLength}, item click {automationItemQuantity - automationItemNextClicksRemaining}/{automationItemQuantity}";
                automationNextActionAt = DateTime.UtcNow + AutomationActionDelay;
                return;

            case ItemInspectionAutomationPhase.WaitForList:
                if (IsAddonVisible("ItemInspectionList"))
                    SetAutomationPhase(ItemInspectionAutomationPhase.SelectRow);
                return;
        }
    }

    private void SetAutomationPhase(ItemInspectionAutomationPhase nextPhase)
    {
        automationPhase = nextPhase;
        automationTicks = 0;
        automationNextActionAt = DateTime.UtcNow + AutomationActionDelay;
        automationPhaseStartedAt = DateTime.UtcNow;
        automationStatus = $"Running: processed {automationProcessedItems}, remaining {automationListLength}, phase={automationPhase}, item={automationItemQuantity - automationItemNextClicksRemaining}/{automationItemQuantity}";
    }

    // Generic "click row 0, then confirm SelectYesno" loop — for addons like SkyIslandExchange2 where
    // each accepted row just needs a Yes confirmation, unlike ItemInspectionList's quantity/Next flow.
    private enum GenericListAutomationPhase
    {
        Idle,
        ClickRow,
        WaitForYesNo,
        WaitForList,
    }

    private GenericListAutomationPhase genericListPhase;
    private string genericListAddonName = "";
    private uint genericListNodeId;
    private int genericListProcessedRows;
    private int genericListTicks;
    private DateTime genericListNextActionAt;
    private DateTime genericListPhaseStartedAt;
    private string genericListStatus = "Idle";

    public string GenericListAutomationStatus => genericListStatus;

    public void StartGenericListAutomation(string addonName, uint listNodeId)
    {
        genericListAddonName = addonName;
        genericListNodeId = listNodeId;
        genericListProcessedRows = 0;
        genericListTicks = 0;
        genericListNextActionAt = DateTime.MinValue;
        genericListPhaseStartedAt = DateTime.UtcNow;
        genericListPhase = GenericListAutomationPhase.ClickRow;
        genericListStatus = $"Running: {addonName} list {listNodeId}";
        log.Debug("[GuiInteract] Generic list automation started for {AddonName} node {ListNodeId}", addonName, listNodeId);
    }

    public void StopGenericListAutomation()
    {
        if (genericListPhase != GenericListAutomationPhase.Idle)
            log.Debug("[GuiInteract] Generic list automation stopped at phase={Phase} processed={Processed}", genericListPhase, genericListProcessedRows);

        genericListPhase = GenericListAutomationPhase.Idle;
        genericListTicks = 0;
        genericListNextActionAt = DateTime.MinValue;
        genericListStatus = "Stopped";
    }

    private void UpdateGenericListAutomation()
    {
        if (genericListPhase == GenericListAutomationPhase.Idle)
            return;

        genericListTicks++;
        if (DateTime.UtcNow < genericListNextActionAt)
            return;

        var phaseElapsed = DateTime.UtcNow - genericListPhaseStartedAt;
        if (phaseElapsed > AutomationPhaseTimeout)
        {
            genericListStatus = $"Stopped: timeout in {genericListPhase} after {phaseElapsed.TotalSeconds:F1}s ({genericListTicks} ticks)";
            log.Debug("[GuiInteract] {Status}", genericListStatus);
            StopGenericListAutomation();
            return;
        }

        switch (genericListPhase)
        {
            case GenericListAutomationPhase.ClickRow:
                if (!IsAddonVisible(genericListAddonName))
                {
                    genericListStatus = $"Done: {genericListAddonName} closed, processed {genericListProcessedRows} rows";
                    genericListPhase = GenericListAutomationPhase.Idle;
                    log.Debug("[GuiInteract] {Status}", genericListStatus);
                    return;
                }

                int? remainingRows = GetAddonListLength(genericListAddonName, genericListNodeId);
                if (remainingRows is null or <= 0)
                {
                    genericListStatus = $"Done: {genericListAddonName} has no more rows, processed {genericListProcessedRows}";
                    genericListPhase = GenericListAutomationPhase.Idle;
                    log.Debug("[GuiInteract] {Status}", genericListStatus);
                    return;
                }

                if (!SelectAddonListRow(genericListAddonName, genericListNodeId, 0, true))
                {
                    genericListStatus = $"Stopped: cannot select row 0 in {genericListAddonName}";
                    StopGenericListAutomation();
                    return;
                }

                if (!ClickAddonListRow(genericListAddonName, genericListNodeId, 0))
                {
                    genericListStatus = $"Stopped: cannot click row 0 in {genericListAddonName}";
                    StopGenericListAutomation();
                    return;
                }

                SetGenericListPhase(GenericListAutomationPhase.WaitForYesNo);
                return;

            case GenericListAutomationPhase.WaitForYesNo:
                if (IsAddonVisible("SelectYesno"))
                {
                    ClickSelectYesnoYes();
                    genericListProcessedRows++;
                    SetGenericListPhase(GenericListAutomationPhase.WaitForList);
                }
                return;

            case GenericListAutomationPhase.WaitForList:
                if (IsAddonVisible(genericListAddonName))
                    SetGenericListPhase(GenericListAutomationPhase.ClickRow);
                return;
        }
    }

    private void SetGenericListPhase(GenericListAutomationPhase nextPhase)
    {
        genericListPhase = nextPhase;
        genericListTicks = 0;
        genericListNextActionAt = DateTime.UtcNow + AutomationActionDelay;
        genericListPhaseStartedAt = DateTime.UtcNow;
        genericListStatus = $"Running: {genericListAddonName}, processed {genericListProcessedRows}, phase={genericListPhase}";
    }

    // Clicks "Yes" (button index 0) on the SelectYesno addon if it is currently open.
    public bool ClickSelectYesnoYes()
    {
        var addon = (AddonSelectYesno*)gameGui.GetAddonByName("SelectYesno").Address;
        if (addon == null || !addon->AtkUnitBase.IsVisible)
        {
            log.Debug("[GuiInteract] SelectYesno not open, nothing to click");
            return false;
        }

        log.Debug("[GuiInteract] Clicking Yes on SelectYesno at 0x{Address:X}", (nint)addon);
        Callback.Fire(&addon->AtkUnitBase, true, 0);
        return true;
    }


    // Generic version for addons without a dedicated FFXIVClientStructs struct handy (e.g.
    // ItemInspectionList/ItemInspectionResult) — lets us try different callback values live instead
    // of guessing blind. updateState/values meaning is addon-specific; 0 is the common "confirm" index.
    public bool FireAddonCallback(string addonName, bool updateState, params object[] values)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(addonName).Address;
        if (addon == null || !addon->IsVisible)
        {
            log.Debug("[GuiInteract] {AddonName} not open, nothing to fire", addonName);
            return false;
        }

        log.Debug("[GuiInteract] Firing callback on {AddonName} at 0x{Address:X} with values [{Values}]", addonName, (nint)addon, string.Join(", ", values));
        Callback.Fire(addon, updateState, values);
        return true;
    }

    // Advances to the next item in the ItemInspectionResult popup (left "Next" button, index 0).
    public bool ClickItemInspectionResultNext() => FireAddonCallback("ItemInspectionResult", true, 0);

    // Closes the ItemInspectionResult popup (right "Close" button, index 1).
    public bool ClickItemInspectionResultClose() => FireAddonCallback("ItemInspectionResult", true, 1);

    public bool SelectItemInspectionListRow(int rowIndex, bool dispatchEvent)
    {
        var addon = (AddonItemInspectionList*)gameGui.GetAddonByName("ItemInspectionList").Address;
        if (addon == null || !addon->AtkUnitBase.IsVisible)
        {
            log.Debug("[GuiInteract] ItemInspectionList not open, cannot select row {RowIndex}", rowIndex);
            return false;
        }

        var list = addon->GetComponentListById(7);
        if (list == null)
        {
            log.Debug("[GuiInteract] ItemInspectionList list component 7 not found");
            return false;
        }

        log.Debug("[GuiInteract] ItemInspectionList SelectItem({RowIndex}, {DispatchEvent}) listLength={ListLength} firstVisible={FirstVisible} selected={Selected}",
            rowIndex, dispatchEvent, list->ListLength, list->FirstVisibleItemIndex, list->SelectedItemIndex);
        list->SelectItem(rowIndex, dispatchEvent);
        return true;
    }

    public int? GetItemInspectionListRowQuantity(int rowIndex)
    {
        var list = GetItemInspectionList();
        if (list == null)
            return null;

        var renderer = FindItemInspectionListRenderer(list, rowIndex);
        var textNode = renderer != null ? renderer->GetTextNodeById(5) : null;
        if (textNode == null)
            return null;

        return ParseFirstInteger(textNode->NodeText.ToString());
    }

    // Confirmed via a full AtkValues dump: rows start at index 6 with a fixed 6-value stride
    // [itemId, iconId, name, requiredCount, ownedQuantity, flag] — 24 real rows total.
    private const int ItemInspectionAtkValueFirstRowOffset = 6;
    private const int ItemInspectionAtkValueStride = 6;

    // Unused/recycled renderer-pool slots can still report IsVisible=true but carry no real item
    // text — used purely to reject those, never for row identity (identity comes from ListItemIndex).
    private static bool HasItemInspectionRowContent(AtkComponentListItemRenderer* renderer)
    {
        for (uint textNodeId = 1; textNodeId <= 12; textNodeId++)
        {
            var textNode = renderer->GetTextNodeById(textNodeId);
            if (textNode == null)
                continue;

            string value = textNode->NodeText.ToString().Trim();
            if (value.Length > 3 && !int.TryParse(value, out _))
                return true;
        }

        return false;
    }

    private readonly record struct ItemInspectionAtkValueRow(int BlockIndex, int ItemId, int? OwnedQuantity);

    // Parses every real row out of the raw AtkValues array (bounded by AtkValuesCount, NOT
    // list->ListLength — confirmed that field only counts currently-visible rows, see below).
    private List<ItemInspectionAtkValueRow> ParseItemInspectionAtkValueRows(AddonItemInspectionList* addon)
    {
        var rows = new List<ItemInspectionAtkValueRow>();
        for (int blockIndex = 0; ; blockIndex++)
        {
            int valueIndex = ItemInspectionAtkValueFirstRowOffset + (blockIndex * ItemInspectionAtkValueStride);
            if (valueIndex + 4 >= addon->AtkValuesCount)
                break;

            var itemIdValue = addon->AtkValues[valueIndex];
            if (itemIdValue.Type != AtkValueType.Int)
                break;

            var ownedValue = addon->AtkValues[valueIndex + 4];
            int? ownedQuantity = ownedValue.Type == AtkValueType.Int ? ownedValue.Int : null;

            rows.Add(new ItemInspectionAtkValueRow(blockIndex, itemIdValue.Int, ownedQuantity));
        }

        return rows;
    }

    // Confirmed via a full AtkValues dump cross-referenced against the in-game list: the game hides
    // any row with ownedQuantity==0 from the visible turn-in list entirely, and
    // renderer->ListItemIndex is numbered densely over ONLY the still-visible rows — NOT the raw
    // AtkValues block index. E.g. block 19 "Clarity" had ListItemIndex 15 because 4 earlier
    // zero-quantity rows (blocks 6, 7, 15, 17) are skipped before it. Reconstruct that same dense
    // list here and index into it directly instead of the raw block-index formula.
    private List<ItemInspectionAtkValueRow> GetVisibleItemInspectionAtkValueRows(AddonItemInspectionList* addon)
    {
        var visible = new List<ItemInspectionAtkValueRow>();
        foreach (var row in ParseItemInspectionAtkValueRows(addon))
        {
            if ((row.OwnedQuantity ?? 1) > 0)
                visible.Add(row);
        }

        return visible;
    }

    public int? GetItemInspectionTrueItemId(int rowIndex)
    {
        var addon = (AddonItemInspectionList*)gameGui.GetAddonByName("ItemInspectionList").Address;
        if (addon == null || !addon->AtkUnitBase.IsVisible)
            return null;

        var visible = GetVisibleItemInspectionAtkValueRows(addon);
        if (rowIndex < 0 || rowIndex >= visible.Count)
            return null;

        return visible[rowIndex].ItemId;
    }

    private static int? ParseFirstInteger(string value)
    {
        int result = 0;
        bool found = false;
        foreach (char character in value)
        {
            if (!char.IsDigit(character))
            {
                if (found)
                    break;
                continue;
            }

            found = true;
            result = (result * 10) + (character - '0');
        }

        return found ? result : null;
    }

    private int FindFirstProcessableItemInspectionRow(AtkComponentList* list)
    {
        for (int rowIndex = list->ListLength - 1; rowIndex >= 0; rowIndex--)
        {
            int? trueItemId = GetItemInspectionTrueItemId(rowIndex);
            if (trueItemId is null || !ShouldSkipItemInspectionItem(trueItemId.Value))
                return rowIndex;
        }

        return -1;
    }

    private bool ShouldSkipItemInspectionItem(int itemId) => itemInspectionConfig.SkipItemIds.Contains(itemId);

    // Public wrappers for the overlay checkboxes — same SkipItemIds set the automation loop reads.
    public bool IsItemInspectionItemSkipped(int itemId) => itemInspectionConfig.SkipItemIds.Contains(itemId);

    public void SetItemInspectionItemSkipped(int itemId, bool skipped)
    {
        if (skipped)
        {
            if (itemInspectionConfig.SkipItemIds.Add(itemId))
                configSaver.Save();

            return;
        }

        if (itemInspectionConfig.SkipItemIds.Remove(itemId))
            configSaver.Save();
    }

    public readonly record struct ItemInspectionRowPosition(int RowIndex, int TrueItemId, float CheckboxScreenX, float CheckboxScreenY, float RowHeight);

    // Screen position for a small checkbox column in the gap between the item name and the
    // "Required" count, for each currently visible row — anchored off the Required text node's own
    // ScreenX/ScreenY, which already includes the full cumulative scale chain.
    public IReadOnlyList<ItemInspectionRowPosition> GetItemInspectionRowPositions(float columnGap = 24f)
    {
        var addon = (AddonItemInspectionList*)gameGui.GetAddonByName("ItemInspectionList").Address;
        if (addon == null || !addon->AtkUnitBase.IsVisible)
            return [];

        var list = GetItemInspectionList();
        if (list == null)
            return [];

        // The renderer POOL includes unused/recycled slots that still report IsVisible=true,
        // sometimes with a real leftover item name, and can share a stale (not yet repositioned)
        // ScreenY with a genuinely visible row. Restrict to the known-visible index window and
        // require real row content instead of trusting IsVisible()/ScreenY alone.
        int firstVisible = list->FirstVisibleItemIndex;
        int visibleCount = list->NumVisibleItems;
        int lastVisible = visibleCount > 0 ? firstVisible + visibleCount - 1 : int.MaxValue;

        var raw = new List<(int ListItemIndex, float X, float Y)>();
        for (int i = 0; i < list->UldManager.NodeListCount; i++)
        {
            var componentNode = list->UldManager.NodeList[i]->GetAsAtkComponentNode();
            var renderer = componentNode != null ? componentNode->GetAsAtkComponentListItemRenderer() : null;
            if (renderer == null || !componentNode->AtkResNode.IsVisible())
                continue;

            if (renderer->ListItemIndex < firstVisible || renderer->ListItemIndex > lastVisible)
                continue;

            var requiredNode = renderer->GetTextNodeById(4);
            if (requiredNode == null)
                continue;

            if (!HasItemInspectionRowContent(renderer))
                continue;

            raw.Add((renderer->ListItemIndex, requiredNode->AtkResNode.ScreenX - columnGap, requiredNode->AtkResNode.ScreenY));
        }

        raw.Sort((a, b) => a.ListItemIndex.CompareTo(b.ListItemIndex));
        for (int i = raw.Count - 1; i > 0; i--)
        {
            if (raw[i].ListItemIndex == raw[i - 1].ListItemIndex)
                raw.RemoveAt(i);
        }

        // Height*ScaleY only reflects the node's own local scale, not the full cumulative parent
        // scale chain, and undershot the real on-screen row spacing — derive it from the actual
        // gap between consecutive rows instead, which is already correct since ScreenY is absolute.
        var visibleAtkRows = GetVisibleItemInspectionAtkValueRows(addon);
        var result = new List<ItemInspectionRowPosition>();
        for (int i = 0; i < raw.Count; i++)
        {
            float rowHeight = i + 1 < raw.Count
                ? raw[i + 1].Y - raw[i].Y
                : i > 0 ? raw[i].Y - raw[i - 1].Y : 25f;

            int listItemIndex = raw[i].ListItemIndex;
            int trueItemId = listItemIndex >= 0 && listItemIndex < visibleAtkRows.Count
                ? visibleAtkRows[listItemIndex].ItemId
                : listItemIndex;
            result.Add(new ItemInspectionRowPosition(listItemIndex, trueItemId, raw[i].X, raw[i].Y, rowHeight));
        }

        return result;
    }

    public bool ClickItemInspectionListRow(int rowIndex)
    {
        var addon = (AddonItemInspectionList*)gameGui.GetAddonByName("ItemInspectionList").Address;
        if (addon == null || !addon->AtkUnitBase.IsVisible)
        {
            log.Debug("[GuiInteract] ItemInspectionList not open, cannot click row {RowIndex}", rowIndex);
            return false;
        }

        var list = addon->GetComponentListById(7);
        if (list == null)
        {
            log.Debug("[GuiInteract] ItemInspectionList list component 7 not found");
            return false;
        }

        var renderer = FindItemInspectionListRenderer(list, rowIndex);
        if (renderer == null)
        {
            log.Debug("[GuiInteract] ItemInspectionList renderer for row {RowIndex} not found", rowIndex);
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

        log.Debug("[GuiInteract] ItemInspectionList ReceiveEvent(ListItemClick, row={RowIndex}, node={NodeId})", rowIndex, renderer->AtkResNode->NodeId);
        addon->ReceiveEvent(AtkEventType.ListItemClick, 0, &atkEvent, &eventData);
        return true;
    }

    private static AtkComponentListItemRenderer* FindItemInspectionListRenderer(AtkComponentList* list, int rowIndex)
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

    private bool IsAddonVisible(string addonName)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(addonName).Address;
        return addon != null && addon->IsVisible;
    }

    // "ItemDetail" and "Tooltip" are the native hover-tooltip addons (item and action/status/generic
    // respectively); plugin ImGui draws always composite on top of the game's own UI layer, so
    // overlays must self-hide while any of these is up rather than rely on z-order.
    public bool IsAnyTooltipVisible() => IsAddonVisible("ItemDetail") || IsAddonVisible("Tooltip");

    // Screen-space position/size of a native addon window, for positioning our own ImGui overlays
    // next to it (e.g. a quick-action panel beside the appraising window).
    public (float X, float Y, float Width, float Height)? GetAddonScreenRect(string addonName)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(addonName).Address;
        if (addon == null || !addon->IsVisible)
            return null;

        return (addon->X, addon->Y, addon->GetScaledWidth(true), addon->GetScaledHeight(true));
    }

    private AtkComponentList* GetItemInspectionList()
    {
        var addon = (AddonItemInspectionList*)gameGui.GetAddonByName("ItemInspectionList").Address;
        if (addon == null || !addon->AtkUnitBase.IsVisible)
            return null;

        return addon->GetComponentListById(7);
    }
}
