using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Nodes;
using Ocelot.Lifecycle;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Botja.Services;

public sealed class AppraisingNativeOverlayService(
    IDalamudPluginInterface pluginInterface,
    IFramework framework,
    IGameGui gameGui,
    IAddonLifecycle addonLifecycle,
    GuiInteractionService guiInteract,
    IPluginLog log
) : IOnStart, IOnStop, IOnUpdate
{
    private const float CheckboxHorizontalOffset = -4f;
    private const float ItemIdHorizontalOffset = 20f;
    private readonly List<CheckboxNode> checkboxes = [];
    private readonly List<TextNode> itemIdTexts = [];
    private bool initialized;
    private nint attachedAddon;

    public void OnStart()
    {
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ItemInspectionList", OnItemInspectionListFinalized);
        Initialize();
    }

    public void OnStop()
    {
        if (framework.IsInFrameworkUpdateThread)
            Cleanup();
        else
            framework.RunOnTick(Cleanup).GetAwaiter().GetResult();
    }

    public unsafe void Update()
    {
        if (!initialized)
            return;

        var addon = (AddonItemInspectionList*)gameGui.GetAddonByName("ItemInspectionList").Address;
        if (addon == null || !addon->AtkUnitBase.IsVisible)
        {
            DisposeCheckboxes();
            return;
        }

        if (attachedAddon != (nint)addon)
        {
            DisposeCheckboxes();
            attachedAddon = (nint)addon;
        }

        var rows = guiInteract.GetItemInspectionRowPositions();
        while (checkboxes.Count < rows.Count)
        {
            checkboxes.Add(CreateCheckbox());
            itemIdTexts.Add(CreateItemIdText());
        }

        for (int i = 0; i < checkboxes.Count; i++)
        {
            var checkbox = checkboxes[i];
            var itemIdText = itemIdTexts[i];
            if (i >= rows.Count)
            {
                checkbox.IsVisible = false;
                itemIdText.IsVisible = false;
                continue;
            }

            var row = rows[i];
            checkbox.IsVisible = true;
            itemIdText.IsVisible = true;
            checkbox.Position = ToAddonPosition(addon, row.CheckboxScreenX + CheckboxHorizontalOffset, row.CheckboxScreenY);
            itemIdText.Position = ToAddonPosition(addon, row.CheckboxScreenX + ItemIdHorizontalOffset, row.CheckboxScreenY);
            checkbox.Size = new Vector2(20f, 20f);
            itemIdText.String = row.TrueItemId.ToString();
            itemIdText.Size = new Vector2(64f, 20f);
            bool shouldBeChecked = guiInteract.IsItemInspectionItemSkipped(row.TrueItemId);
            if (checkbox.IsChecked != shouldBeChecked)
                checkbox.IsChecked = shouldBeChecked;
        }

        addon->AtkUnitBase.UldManager.UpdateDrawNodeList();
        addon->AtkUnitBase.UpdateCollisionNodeList(false);
    }

    private unsafe CheckboxNode CreateCheckbox()
    {
        var checkbox = new CheckboxNode
        {
            String = string.Empty,
            DisableAutoResize = true,
            ShowClickableCursor = true,
        };
        checkbox.OnClick = checkedState =>
        {
            int index = checkboxes.IndexOf(checkbox);
            var rows = guiInteract.GetItemInspectionRowPositions();
            if (index >= 0 && index < rows.Count)
                guiInteract.SetItemInspectionItemSkipped(rows[index].TrueItemId, checkedState);
        };
        checkbox.AttachNode(GetItemInspectionAddon());
        return checkbox;
    }

    private unsafe TextNode CreateItemIdText()
    {
        var text = new TextNode
        {
            FontSize = 14,
            LineSpacing = 14,
            Size = new Vector2(64f, 20f),
        };
        text.AttachNode(GetItemInspectionAddon());
        return text;
    }

    private unsafe AtkUnitBase* GetItemInspectionAddon()
    {
        var addon = (AddonItemInspectionList*)gameGui.GetAddonByName("ItemInspectionList").Address;
        return addon == null ? null : &addon->AtkUnitBase;
    }

    private static unsafe Vector2 ToAddonPosition(AddonItemInspectionList* addon, float screenX, float screenY)
    {
        var root = addon->AtkUnitBase.RootNode;
        if (root == null)
            return new Vector2(screenX - addon->AtkUnitBase.X, screenY - addon->AtkUnitBase.Y);

        float scaleX = root->ScaleX == 0f ? 1f : root->ScaleX;
        float scaleY = root->ScaleY == 0f ? 1f : root->ScaleY;
        return new Vector2(
            (screenX - root->ScreenX) / scaleX,
            (screenY - root->ScreenY) / scaleY);
    }

    private void DisposeCheckboxes()
    {
        foreach (var checkbox in checkboxes)
            checkbox.Dispose();
        foreach (var itemIdText in itemIdTexts)
            itemIdText.Dispose();

        checkboxes.Clear();
        itemIdTexts.Clear();
        attachedAddon = 0;
    }

    private void OnItemInspectionListFinalized(AddonEvent type, AddonArgs args)
        => DisposeCheckboxes();

    private void Initialize()
    {
        try
        {
            KamiToolKitLibrary.Initialize(pluginInterface, "Botja");
            initialized = true;
        }
        catch (Exception exception)
        {
            log.Error(exception, "Failed to initialize KamiToolKit appraising overlay");
        }
    }

    private void Cleanup()
    {
        addonLifecycle.UnregisterListener(OnItemInspectionListFinalized);
        DisposeCheckboxes();
        if (initialized)
        {
            KamiToolKitLibrary.Dispose();
            initialized = false;
        }
    }
}