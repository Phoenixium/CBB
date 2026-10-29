using Botja.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
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
    IObjectTable objects,
    IConfigWindow configWindow,
    GuiInteractionService guiInteract,
    CeSignupService ceSignup,
    CombatControlService combatControl,
    AutoModeService autoMode,
    MobFarmService mobFarm,
    HostileDetectionService hostileDetection
) : OcelotWindow("Cant be Bozja'ed"), IOnLoad, IOnTerritoryChanged, IMainWindow
{
    // Bozjan Southern Front and Zadnor — auto-open the window on entering these zones.
    internal static readonly HashSet<uint> BozjaZones = [920, 975];

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
                if (ImGui.Button($"{FontAwesomeIcon.Star.ToIconString()}##topfate") && fatePriority.GetAutoSelectableFates(fateTable).FirstOrDefault() is { } top)
                {
                    combatControl.SetActiveFate(top.FateId);
                    nav.AutoNavigate(top.Position, top.Name.ToString());
                }
            }
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Auto-navigate to the highest-priority FATE");

        ImGui.Separator();

        if (ImGui.Button(ceSignup.IsRunning ? "Stop joining critical engagements" : "Join a recruiting critical engagement"))
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

        if (ImGui.Button(combatControl.ManualEnabled ? "Stop Combat AI" : "Start Combat AI"))
        {
            if (combatControl.ManualEnabled)
                combatControl.StopManualCombat();
            else
                combatControl.StartManualCombat();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Force-enable the selected combat AI right now, regardless of Auto Mode/FATE/CE state");

        if (ImGui.Button(mobFarm.IsRunning ? "Stop Mob Farm" : "Start Mob Farm"))
        {
            if (mobFarm.IsRunning)
                mobFarm.Stop();
            else
                mobFarm.Start();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Farm selected nearby Bozja/Zadnor mobs at configured spots");
        ImGui.TextUnformatted($"Mob Farm: {mobFarm.Status}");
        if (mobFarm.IsRunning)
        {
            ImGui.TextUnformatted($"Phase: {mobFarm.Phase} | Spot: {mobFarm.CurrentSpotName}");
            ImGui.TextUnformatted($"Selected templates: {mobFarm.SelectedMobCount} | Free: {mobFarm.FreeMobCount} | Attacking you: {mobFarm.EngagedMobCount} | Contested: {mobFarm.ContestedMobCount}");
            if (!string.IsNullOrEmpty(mobFarm.CurrentTargetName))
                ImGui.TextUnformatted($"Current target: {mobFarm.CurrentTargetName}");
        }

        if (ImGui.Button(autoMode.IsRunning ? "Stop Auto Mode" : "Start Auto Mode"))
        {
            if (autoMode.IsRunning)
                autoMode.Stop();
            else
                autoMode.Start();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Continuously travel to and fight the next FATE, joining recruiting CEs first");
        if (!string.IsNullOrEmpty(autoMode.Status))
            ImGui.TextUnformatted(autoMode.Status);

        if (nav.DebugPhase != "None" && !string.IsNullOrEmpty(nav.DebugPendingDestinationName))
            ImGui.TextUnformatted($"Going to fate: {nav.DebugPendingDestinationName}");

        RenderHostileStatus();
        RenderDebugInfo();

        ImGui.Separator();

        if (fateTable.Length == 0)
        {
            ImGui.TextUnformatted("There are no active FATEs in this area.");
            return;
        }

        ImGui.Columns(8, "fates", true);
        ImGui.TextUnformatted("Name"); ImGui.NextColumn();
        ImGui.TextUnformatted("ID"); ImGui.NextColumn();
        ImGui.TextUnformatted("Sector"); ImGui.NextColumn();
        ImGui.TextUnformatted("Progress"); ImGui.NextColumn();
        ImGui.TextUnformatted("Time"); ImGui.NextColumn();
        ImGui.TextUnformatted("ETA"); ImGui.NextColumn();
        ImGui.TextUnformatted("Go"); ImGui.NextColumn();
        ImGui.TextUnformatted(""); ImGui.NextColumn();
        ImGui.Separator();

        int i = 0;
        foreach (var fate in fatePriority.GetSortedFates(fateTable))
        {
            var isBlacklisted = fatePriority.IsBlacklisted(fate);
            var isFollowFate = fatePriority.IsFollowFate(fate);
            var rem = fate.TimeRemaining;
            var time = rem > 0 ? $"{rem / 60:D2}:{rem % 60:D2}" : "--:--";
            var eta = nav.EstimateTravelSeconds(fate.Position);
            var etaText = $"{(int)eta / 60:D2}:{(int)eta % 60:D2}";
            var sector = fatePriority.GetSector(fate);

            using (ImRaii.PushColor(ImGuiCol.Text, isBlacklisted ? 0xFF909090 : 0xFFFFFFFF))
                ImGui.TextUnformatted(fate.Name.ToString()); ImGui.NextColumn();
            ImGui.TextUnformatted(fatePriority.GetFateTemplateId(fate).ToString()); ImGui.NextColumn();
            ImGui.TextUnformatted(sector.HasValue ? ((int)sector.Value).ToString() : "--"); ImGui.NextColumn();
            ImGui.TextUnformatted($"{fate.Progress}%"); ImGui.NextColumn();
            ImGui.TextUnformatted(time); ImGui.NextColumn();
            ImGui.TextUnformatted(etaText); ImGui.NextColumn();


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
                ImGui.SetTooltip($"Auto-navigate to {fate.Name} (teleport → walk)");

            ImGui.NextColumn();

            // Blacklist button
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                using (ImRaii.PushColor(ImGuiCol.Button, isBlacklisted ? 0xFFCC6666 : 0xFF4A4A4A))
                {
                    if (!isBlacklisted && ImGui.Button($"{FontAwesomeIcon.Ban.ToIconString()}##blacklist_{i}"))
                        fatePriority.Blacklist(fate);
                    else if (isBlacklisted && ImGui.Button($"{FontAwesomeIcon.Ban.ToIconString()}##blacklisted_{i}"))
                        fatePriority.Unblacklist(fate);
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(isBlacklisted
                    ? $"Stop ignoring {fate.Name} during automatic navigation"
                    : $"Ignore {fate.Name} during automatic navigation");

            ImGui.NextColumn();
            i++;
        }

        ImGui.Columns(1);
    }

    private void RenderHostileStatus()
    {
        var hostiles = hostileDetection.GetNearbyHostiles();
        if (hostiles.Count == 0)
            return;

        bool underAttack = hostileDetection.IsPlayerUnderAttack();
        using (ImRaii.PushColor(ImGuiCol.Text, underAttack ? 0xFF0000FF : 0xFFFFAA00))
            ImGui.TextUnformatted($"Hostile NPCs nearby: {hostiles.Count}{(underAttack ? " (UNDER ATTACK!)" : string.Empty)}");

        if (!ImGui.CollapsingHeader($"Nearby Hostile NPCs ({hostiles.Count})"))
            return;

        if (!ImGui.BeginTable("hostiles", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("Distance");
        ImGui.TableSetupColumn("HP");
        ImGui.TableSetupColumn("Position");
        ImGui.TableSetupColumn("DataId");
        ImGui.TableSetupColumn("AggroType");
        ImGui.TableSetupColumn("FateId");
        ImGui.TableHeadersRow();

        foreach (var hostile in hostiles)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(hostile.Name);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted($"{hostile.DistanceYalms:F1}y");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted($"{hostile.CurrentHp}/{hostile.MaxHp}");
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted($"({hostile.Position.X:F0}, {hostile.Position.Z:F0})");
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(hostile.DataId.ToString());
            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(hostile.AggroType.ToString());
            ImGui.TableSetColumnIndex(6);
            ImGui.TextUnformatted(hostile.FateId == 0 ? "-" : hostile.FateId.ToString());
        }

        ImGui.EndTable();
    }

}
