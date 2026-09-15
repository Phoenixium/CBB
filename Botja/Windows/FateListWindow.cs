using Botja.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Ocelot.Lifecycle;
using Ocelot.Windows;
using System.Collections.Generic;
using System.Linq;

namespace Botja.Windows;

public partial class FateListWindow(
    IFateTable fateTable,
    FateNavigationService nav,
    FatePriorityService fatePriority,
    IAetheryteList aetheryteList,
    IClientState clientState,
    ITargetManager targets,
    IConfigWindow configWindow,
    GuiInteractionService guiInteract,
    CeSignupService ceSignup,
    CombatControlService combatControl,
    AutoModeService autoMode
) : OcelotWindow("Fate List"), IOnLoad, IOnTerritoryChanged, IMainWindow
{
    // Bozjan Southern Front and Zadnor — auto-open the window on entering these zones.
    private static readonly HashSet<uint> BozjaZones = [920, 975];

    public void OnLoad()
    {
    }

    public void OnTerritoryChanged(uint territory)
    {
        if (BozjaZones.Contains(territory))
            IsOpen = true;
    }

    protected override void Render()
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            if (ImGui.Button($"{FontAwesomeIcon.Stop.ToIconString()}##stop"))
                nav.Stop();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Stop movement");

        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            if (ImGui.Button($"{FontAwesomeIcon.Cog.ToIconString()}##settings"))
                configWindow.Toggle();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Settings");

        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            using (ImRaii.Disabled(fateTable.Length == 0))
            {
                if (ImGui.Button($"{FontAwesomeIcon.Star.ToIconString()}##topfate") && fatePriority.GetSortedFates(fateTable).FirstOrDefault() is { } top)
                {
                    combatControl.SetActiveFate(top.FateId);
                    nav.AutoNavigate(top.Position, top.Name.ToString());
                }
            }
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Auto-navigate to the top-priority FATE");

        ImGui.Separator();

        ImGui.TextUnformatted("Item inspection:");
        if (ImGui.Button("Start ItemInspection loop"))
            guiInteract.StartItemInspectionAutomation();
        ImGui.SameLine();
        if (ImGui.Button("STOP ItemInspection loop"))
            guiInteract.StopItemInspectionAutomation();
        ImGui.TextUnformatted(guiInteract.ItemInspectionAutomationStatus);

        if (ImGui.Button(ceSignup.IsRunning ? "Stop CE signup" : "Join recruiting CE"))
        {
            if (ceSignup.IsRunning)
                ceSignup.Stop();
            else
                ceSignup.Start();
        }
        ImGui.TextUnformatted(ceSignup.Status);
        ImGui.TextUnformatted($"Detected CE timer: {ceSignup.DetectedTimer}");

        if (ceSignup.TryFindRecruitingEvent(out var recruitingCeId))
        {
            ImGui.TextUnformatted($"Recruiting CE: {ceSignup.GetCeName(recruitingCeId)} (ID {recruitingCeId})");
            ImGui.SameLine();
            using (ImRaii.Disabled(ceSignup.IsCeBlacklisted(recruitingCeId)))
            {
                if (ImGui.Button("Blacklist this CE##ceblacklist"))
                    ceSignup.BlacklistCe(recruitingCeId);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Never auto-join this CE again");
        }

        ImGui.Separator();

        if (ImGui.Button(autoMode.IsRunning ? "Stop Auto Mode" : "Start Auto Mode"))
        {
            if (autoMode.IsRunning)
                autoMode.Stop();
            else
                autoMode.Start();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Continuously travel to and fight the next FATE, joining recruiting CEs first");
        ImGui.TextUnformatted(autoMode.Status);

        if (nav.DebugPhase != "None" && !string.IsNullOrEmpty(nav.DebugPendingDestinationName))
            ImGui.TextUnformatted($"Going to fate: {nav.DebugPendingDestinationName}");

        RenderDebugInfo();

        ImGui.Separator();

        if (fateTable.Length == 0)
        {
            ImGui.TextUnformatted("No active FATEs in this area.");
            return;
        }

        ImGui.Columns(8, "fates", true);
        ImGui.TextUnformatted("Name");     ImGui.NextColumn();
        ImGui.TextUnformatted("ID");       ImGui.NextColumn();
        ImGui.TextUnformatted("Sector");   ImGui.NextColumn();
        ImGui.TextUnformatted("Progress"); ImGui.NextColumn();
        ImGui.TextUnformatted("Time");     ImGui.NextColumn();
        ImGui.TextUnformatted("ETA");      ImGui.NextColumn();
        ImGui.TextUnformatted("Go");       ImGui.NextColumn();
        ImGui.TextUnformatted("");         ImGui.NextColumn();
        ImGui.Separator();

        int i = 0;
        foreach (var fate in fatePriority.GetSortedFates(fateTable))
        {
            var rem = fate.TimeRemaining;
            var time = rem > 0 ? $"{rem / 60:D2}:{rem % 60:D2}" : "--:--";
            var eta = nav.EstimateTravelSeconds(fate.Position);
            var etaText = $"{(int)eta / 60:D2}:{(int)eta % 60:D2}";
            var sector = fatePriority.GetSector(fate.Position);

            ImGui.TextUnformatted(fate.Name.ToString());  ImGui.NextColumn();
            ImGui.TextUnformatted(fatePriority.GetFateTemplateId(fate).ToString()); ImGui.NextColumn();
            ImGui.TextUnformatted(((int)sector).ToString()); ImGui.NextColumn();
            ImGui.TextUnformatted($"{fate.Progress}%");   ImGui.NextColumn();
            ImGui.TextUnformatted(time);                  ImGui.NextColumn();
            ImGui.TextUnformatted(etaText);               ImGui.NextColumn();


            // Walk button
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                if (ImGui.Button($"{FontAwesomeIcon.Running.ToIconString()}##walk_{i}"))
                {
                    combatControl.SetActiveFate(fate.FateId);
                    nav.PathTo(fate.Position, fate.Name.ToString());
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Walk to {fate.Name}");

            ImGui.SameLine();

            // Auto button — /return to camp if needed, then aethernet teleport, then walk
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                if (ImGui.Button($"{FontAwesomeIcon.Magic.ToIconString()}##auto_{i}"))
                {
                    combatControl.SetActiveFate(fate.FateId);
                    nav.AutoNavigate(fate.Position, fate.Name.ToString());
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Auto-navigate to {fate.Name} (return → teleport → walk)");

            ImGui.NextColumn();

            // Blacklist button
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                if (ImGui.Button($"{FontAwesomeIcon.Ban.ToIconString()}##blacklist_{i}"))
                    fatePriority.Blacklist(fate);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Never auto-navigate to {fate.Name} again");

            ImGui.NextColumn();
            i++;
        }

        ImGui.Columns(1);
    }
}
