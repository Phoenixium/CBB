using Dalamud.Configuration;
using Ocelot.Config;
using Ocelot.Config.Fields;
using Botja.Windows;
using System.Collections.Generic;

namespace Botja;

public interface IPluginConfig
{
    NavigationConfig Navigation { get; }

    FatePriorityConfig FatePriority { get; }

    ItemInspectionConfig ItemInspection { get; }

    CombatConfig Combat { get; }

    BlacklistConfig Blacklist { get; }
}

public sealed class PluginConfig : IPluginConfig, IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public NavigationConfig Navigation { get; set; } = new();

    public FatePriorityConfig FatePriority { get; set; } = new();

    public ItemInspectionConfig ItemInspection { get; set; } = new();

    public CombatConfig Combat { get; set; } = new();

    public BlacklistConfig Blacklist { get; set; } = new();

    public void HydrateMissingSections()
    {
        Navigation ??= new();
        FatePriority ??= new();
        ItemInspection ??= new();
        Combat ??= new();
        Blacklist ??= new();
        ItemInspection.SkipItemIds ??= [];
        Blacklist.FateIds ??= [];
        Blacklist.CeEventIds ??= [];
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
}

public sealed class NavigationConfig : IAutoConfig
{
    // Only mount up if the remaining travel distance exceeds this range.
    [FloatRange(0f, 100f)]
    public float MountRange { get; set; } = 30f;
}

public sealed class ItemInspectionConfig : IAutoConfig
{
    public HashSet<int> SkipItemIds { get; set; } = [];
}

public sealed class CombatConfig : IAutoConfig
{
    // Automatically enable/disable the rotation plugin's auto-rotation based on FATE/CE state:
    // off while travelling, off once the current FATE/CE has ended, on while actively fighting it.
    [Checkbox]
    public bool AutoControlEnabled { get; set; } = true;
}

// Fate/CE template IDs (not per-instance runtime IDs) the player never wants Auto Mode to pick.
public sealed class BlacklistConfig : IAutoConfig
{
    [BlacklistChecklist(false)]
    public List<uint> FateIds { get; set; } = [];

    [BlacklistChecklist(true)]
    public List<uint> CeEventIds { get; set; } = [];
}


