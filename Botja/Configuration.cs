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

    FollowConfig Follow { get; }

    AutoModeConfig AutoMode { get; }
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

    public FollowConfig Follow { get; set; } = new();

    public AutoModeConfig AutoMode { get; set; } = new();

    public void HydrateMissingSections()
    {
        Navigation ??= new();
        FatePriority ??= new();
        ItemInspection ??= new();
        Combat ??= new();
        Blacklist ??= new();
        HostileDetection ??= new();
        Follow ??= new();
        AutoMode ??= new();
        ItemInspection.SkipItemIds ??= [];
        Blacklist.FateIds ??= [];
        Blacklist.CeEventIds ??= [];
        Combat.SelectedAis ??= [];
        Combat.SelectedBossModProfiles ??= [];
        Follow.FateIds ??= [];
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

    [Checkbox]
    public bool LimitToPreferredSector { get; set; } = false;

    [FloatRange(-10f, 10f)]
    public float PreferredSectorWeight { get; set; } = 0.2f;

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

// Fate template IDs (not per-instance runtime IDs) known to chain into a follow-up FATE at the
// same spot — Auto Mode waits there after one of these finishes instead of moving on immediately.
public sealed class FollowConfig : IAutoConfig
{
    public HashSet<uint> FateIds { get; set; } = [];

    [FloatRange(10f, 300f)]
    public float WaitSeconds { get; set; } = 90f;

    [FloatRange(5f, 100f)]
    public float RadiusYalms { get; set; } = 30f;
}

public sealed class AutoModeConfig : IAutoConfig
{
    [Checkbox]
    public bool DoCriticalEngagements { get; set; } = true;

    [Checkbox]
    public bool DoFates { get; set; } = true;
}

public sealed class HostileDetectionConfig : IAutoConfig
{
    // Maximum distance at which hostile NPCs are tracked.
    [FloatRange(5f, 200f)]
    public float DetectionRadiusYalms { get; set; } = 100f;

    // Detour around hostiles blocking the path instead of walking straight through them.
    [Checkbox]
    public bool EnableNavmeshAvoidance { get; set; } = true;

    // Omnidirectional aggro range for Sound-type mobs — anything closer than this triggers them
    // regardless of facing.
    [FloatRange(5f, 30f)]
    public float SoundAggroRangeYalms { get; set; } = 12f;

    // Extra buffer added on top of every sight/sound obstacle to account for mobs wandering/turning
    // between replans.
    [FloatRange(0f, 10f)]
    public float AvoidanceSafetyMarginYalms { get; set; } = 1f;

    // Minimum distance always kept from a hostile's own position regardless of aggro type/facing —
    // a Sight-type mob's cone only blocks its front, so without this the path could otherwise brush
    // right past its side/back at near-zero range, which "passes by" safely but looks/feels wrong.
    [FloatRange(0f, 15f)]
    public float AvoidancePersonalSpaceYalms { get; set; } = 5f;

    // How far beyond an obstacle's real aggro radius still counts as "worth avoiding when the extra
    // detour distance is cheap" — the knob for how wide a berth routes prefer to give hostiles when
    // it's free to do so, versus only barely clearing their edge.
    [FloatRange(0f, 20f)]
    public float AvoidanceComfortDistanceYalms { get; set; } = 6f;
}


