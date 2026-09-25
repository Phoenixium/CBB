using Dalamud.Configuration;
using Ocelot.Config;
using Ocelot.Config.Fields;
using Botja.Services;
using Botja.Windows;
using Botja.Windows.Config;
using System.Collections.Generic;

namespace Botja;

public interface IPluginConfig
{
    NavigationConfig Navigation { get; }

    FatePriorityConfig FatePriority { get; }

    ItemInspectionConfig ItemInspection { get; }

    CombatConfig Combat { get; }

    BlacklistConfig Blacklist { get; }

    HostileDetectionConfig HostileDetection { get; }
}

public sealed class PluginConfig : IPluginConfig, IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public NavigationConfig Navigation { get; set; } = new();

    public FatePriorityConfig FatePriority { get; set; } = new();

    public ItemInspectionConfig ItemInspection { get; set; } = new();

    public CombatConfig Combat { get; set; } = new();

    public BlacklistConfig Blacklist { get; set; } = new();

    public HostileDetectionConfig HostileDetection { get; set; } = new();

    public void HydrateMissingSections()
    {
        Navigation ??= new();
        FatePriority ??= new();
        ItemInspection ??= new();
        Combat ??= new();
        Blacklist ??= new();
        HostileDetection ??= new();
        ItemInspection.SkipItemIds ??= [];
        Blacklist.FateIds ??= [];
        Blacklist.CeEventIds ??= [];
        Combat.SelectedAis ??= [];
        Combat.SelectedBossModProfiles ??= [];
    }
}

public enum FateSectorPreference
{
    Any = 0,
    Sector1 = 1,
    Sector2 = 2,
    Sector3 = 3,
}

public sealed class FatePriorityConfig : IAutoConfig
{
    [FloatRange(-10f, 10f)]
    public float ProgressWeight { get; set; } = 1f;

    [FloatRange(-10f, 10f)]
    public float TimeRemainingWeight { get; set; } = 0.35f;

    [FloatRange(-10f, 10f)]
    public float TravelTimeWeight { get; set; } = -0.5f;

    [FloatRange(-10f, 10f)]
    public float ProgressRateWeight { get; set; } = 1f;

    [EnumSelect<FateSectorPreference>]
    public FateSectorPreference PreferredSector { get; set; } = FateSectorPreference.Any;

    [FloatRange(0f, 100f)]
    public float PreferredSectorBonus { get; set; } = 20f;

    [Checkbox]
    public bool ExcludeUnreachable { get; set; } = true;

    // FATEs with progress above this are never picked as auto-navigate targets.
    [FloatRange(0f, 100f)]
    public float MaxProgressPercent { get; set; } = 100f;
}

public sealed class NavigationConfig : IAutoConfig
{
    // Only mount up if the remaining travel distance exceeds this range.
    [FloatRange(0f, 100f)]
    public float MountRange { get; set; } = 30f;
}

public sealed class ItemInspectionConfig : IAutoConfig
{
    [ItemInspectionChecklist]
    public HashSet<int> SkipItemIds { get; set; } = [];
}

public sealed class CombatConfig : IAutoConfig
{
    // Automatically enable/disable the rotation plugin's auto-rotation based on FATE/CE state:
    // off while travelling, off once the current FATE/CE has ended, on while actively fighting it.
    [Checkbox]
    public bool AutoControlEnabled { get; set; } = true;

    [CombatAiChecklist]
    public List<CombatAiSelection> SelectedAis { get; set; } = [];

    [BossModProfileChecklist]
    public List<string> SelectedBossModProfiles { get; set; } = [];
}

// Fate/CE template IDs (not per-instance runtime IDs) the player never wants Auto Mode to pick.
public sealed class BlacklistConfig : IAutoConfig
{
    [BlacklistChecklist(false)]
    public HashSet<uint> FateIds { get; set; } = [];

    [BlacklistChecklist(true)]
    public HashSet<uint> CeEventIds { get; set; } = [];
}

public sealed class HostileDetectionConfig : IAutoConfig
{
    // Maximum distance at which hostile NPCs are tracked.
    [FloatRange(5f, 200f)]
    public float DetectionRadiusYalms { get; set; } = 100f;

    // Distance considered unsafe around a hostile when checking a position.
    [FloatRange(5f, 100f)]
    public float DangerRadiusYalms { get; set; } = 25f;

    // Detour around hostiles blocking the path instead of walking straight through them.
    [Checkbox]
    public bool EnableNavmeshAvoidance { get; set; } = true;

    // How far to the side of a blocking hostile to route the detour.
    [FloatRange(1f, 30f)]
    public float AvoidanceClearanceYalms { get; set; } = 8f;
}


