using Dalamud.Game.ClientState.Fates;
using Dalamud.Plugin.Services;
using Ocelot.Config;
using System.Collections.Generic;
using System.Linq;

namespace Botja.Services;

// Sector IDs match the explicit activity groupings shown by the blacklist checklist.
public enum FateSector
{
    Sector1 = 1,
    Sector2 = 2,
    Sector3 = 3,
}

public sealed record BozjaActivity(uint Id, string Name);

public sealed record BozjaSectorDefinition(
    FateSector Sector,
    string Name,
    BozjaActivity[] Fates,
    BozjaActivity[] CriticalEngagements);

public static class BozjaSectorCatalog
{
    private static readonly BozjaSectorDefinition[] SouthernFront =
    [
        new(FateSector.Sector1, "Southern Entrenchment",
        [new(1597, "Sneak & Spell"), new(1598, "None of Them Knew They Were Robots"), new(1599, "The Beasts Must Die"), new(1600, "Unrest for the Wicked"), new(1601, "More Machine Now than Man"), new(1602, "Can Carnivorous Plants Bloom Even on a Battlefield?"), new(1603, "Seeq and Destroy"), new(1604, "All Pets Are Off"), new(1605, "Conflicting with the First Law"), new(1606, "Brought to Heal")],
        [new(1, "Kill It with Fire"), new(2, "The Baying of the Hound(s)"), new(3, "Vigil for the Lost"), new(4, "Aces High"), new(5, "The Shadow of Death's Hand")]),
        new(FateSector.Sector2, "Old Bozja",
        [new(1607, "The Monster Mash"), new(1608, "Red (Chocobo) Alert"), new(1609, "Unicorn Flakes"), new(1610, "Parts and Recreation"), new(1611, "The Element of Supplies"), new(1612, "Heavy Boots of Lead"), new(1613, "No Camping Allowed"), new(1614, "Scavengers of Man's Sorrow"), new(1615, "Help Wanted"), new(1616, "Pyromancer Supreme")],
        [new(6, "The Final Furlong"), new(7, "The Hunt for Red Choctober"), new(8, "Beast of Man"), new(9, "The Fires of War"), new(10, "Patriot Games")]),
        new(FateSector.Sector3, "The Alermuc Climb",
        [new(1617, "Waste the Rainbow"), new(1618, "The Wild Bunch"), new(1619, "My Family and Other Animals"), new(1620, "I'm a Mechanical Man"), new(1621, "Murder Death Kill"), new(1622, "Desperately Seeking Something"), new(1623, "Supplies Party"), new(1624, "Demonstrably Demonic"), new(1625, "For Absent Friends"), new(1626, "Of Steel and Flame"), new(1627, "Let Slip the Dogs of War"), new(1628, "The War Against the Machines")],
        [new(11, "Trampled under Hoof"), new(12, "And the Flames Went Higher"), new(13, "Metal Fox Chaos"), new(14, "Rise of the Robots"), new(15, "Where Strode the Behemoth"), new(16, "The Battle of Castrum Lacus Litore")]),
    ];

    private static readonly BozjaSectorDefinition[] Zadnor =
    [
        new(FateSector.Sector1, "The Southern Plateau",
        [new(1717, "Of Beasts and Braggadocio"), new(1718, "Parts and Parcel"), new(1719, "An Immoral Dilemma"), new(1720, "Deadly Divination"), new(1721, "A Wrench in the Reconnaissance Effort"), new(1722, "Another Pilot Episode"), new(1723, "Breaking the Ice"), new(1724, "Meet the Puppetmaster")],
        [new(17, "On Serpents' Wings"), new(19, "The Broken Blade"), new(20, "From Beyond the Grave"), new(21, "With Diremite and Main"), new(29, "A Familiar Face")]),
        new(FateSector.Sector2, "The Western Plateau",
        [new(1725, "Challenge Accepted"), new(1726, "Th'uban the Terrible"), new(1727, "An End to Atrocities"), new(1728, "A Just Pursuit"), new(1729, "Tanking Up"), new(1730, "Supersoldier Rising"), new(1731, "Demented Mentor"), new(1732, "Sever the Strings")],
        [new(22, "Here Comes the Cavalry"), new(23, "Head of the Snake"), new(24, "There Would Be Blood"), new(25, "Never Cry Wolf"), new(26, "Time to Burn")]),
        new(FateSector.Sector3, "The Northern Plateau",
        [new(1733, "The Beasts Are Back"), new(1734, "Still Only Counts as One"), new(1735, "Seeq and You Will Find"), new(1736, "Mean-spirited"), new(1737, "A Relic Unleashed"), new(1738, "When Mages Rage"), new(1739, "Hypertuned Havoc"), new(1740, "Attack of the Supersoldiers"), new(1741, "The Student Becalms the Master"), new(1742, "Attack of the Machines")],
        [new(18, "Feeling the Burn"), new(27, "Lean, Mean, Magitek Machines"), new(28, "Worn to a Shadow"), new(30, "Looks to Die For"), new(31, "Taking the Lyon's Share")]),
    ];

    public static IReadOnlyList<BozjaSectorDefinition>? GetSectors(uint territoryType) => territoryType switch
    {
        920 => SouthernFront,
        975 => Zadnor,
        _ => null,
    };

    public static FateSector? GetFateSector(uint territoryType, uint fateTemplateId)
    {
        var sectors = GetSectors(territoryType);
        if (sectors == null)
            return null;

        foreach (var sector in sectors)
        {
            if (sector.Fates.Any(fate => fate.Id == fateTemplateId))
                return sector.Sector;
        }

        return null;
    }
}

// Ranks active Bozja FATEs for auto-navigation: progress, time remaining, travel cost,
// how fast progress is accumulating (a proxy for "other players are actively working this"),
// and an optional bonus for a user-preferred map sector.
public class FatePriorityService(
    FateNavigationService nav,
    FatePriorityConfig config,
    BlacklistConfig blacklist,
    FollowConfig follow,
    IConfigSaver configSaver
)
{
    // Normalization scales so default weights of ~1 have comparable influence on the score.
    private const float TimeRemainingNormSeconds = 12f;  // 1200s (20 min) -> 100
    private const float TravelTimeNormCap = 100f;         // cap travel penalty at 100s
    private const float ProgressRateNormScale = 1000f;    // %/sec -> a small, comparable bonus
    private const float PreferredSectorNormValue = 100f;  // "in preferred sector" -> full 100, same scale as the other terms

    public float GetPriorityScore(IFate fate)
    {
        float progress = fate.Progress;
        float timeRemaining = fate.TimeRemaining;
        float travelSeconds = nav.EstimateTravelSeconds(fate.Position);

        if (travelSeconds > timeRemaining)
            return float.NegativeInfinity;

        float elapsed = System.Math.Max(1f, fate.Duration - fate.TimeRemaining);
        float progressRate = progress / elapsed;

        float score = config.ProgressWeight * progress
                    + config.TimeRemainingWeight * System.Math.Clamp(timeRemaining / TimeRemainingNormSeconds, 0f, 100f)
                    + config.TravelTimeWeight * System.Math.Clamp(travelSeconds, 0f, TravelTimeNormCap)
                    + config.ProgressRateWeight * System.Math.Clamp(progressRate * ProgressRateNormScale, 0f, 100f);

        if (config.PreferredSector != FateSectorPreference.Any && GetSector(fate) == (FateSector)config.PreferredSector)
            score += config.PreferredSectorWeight * PreferredSectorNormValue;

        return score;
    }

    public FateSector? GetSector(IFate fate) =>
        BozjaSectorCatalog.GetFateSector(fate.TerritoryType.RowId, GetFateTemplateId(fate));

    // Highest priority first; keep ignored FATEs here so the UI can still display them.
    public IEnumerable<IFate> GetSortedFates(IEnumerable<IFate> fates) =>
        fates.OrderByDescending(GetPriorityScore);

    public IEnumerable<IFate> GetAutoSelectableFates(IEnumerable<IFate> fates) =>
        GetSortedFates(fates).Where(fate =>
            !IsBlacklisted(fate)
            && fate.Progress <= config.MaxProgressPercent
            && fate.TimeRemaining > 0
            && (!config.LimitToPreferredSector
                || config.PreferredSector == FateSectorPreference.Any
                || GetSector(fate) == (FateSector)config.PreferredSector));

    // The stable FATE template ID (shared by every spawn of the same FATE, unlike the per-spawn
    // runtime FateId) — used as the blacklist key so a blacklisted FATE stays blacklisted across respawns.
    public uint GetFateTemplateId(IFate fate) => fate.GameData.RowId;

    public bool IsBlacklisted(IFate fate) => blacklist.FateIds.Contains(GetFateTemplateId(fate));

    public void Blacklist(IFate fate)
    {
        var fateId = GetFateTemplateId(fate);
        if (blacklist.FateIds.Contains(fateId))
            return;

        blacklist.FateIds = [.. blacklist.FateIds, fateId];
        configSaver.Save();
    }

    public void Unblacklist(IFate fate)
    {
        if (!blacklist.FateIds.Remove(GetFateTemplateId(fate)))
            return;
        configSaver.Save();
    }

    public bool IsFollowFate(IFate fate) => follow.FateIds.Contains(GetFateTemplateId(fate));

    public void MarkFollow(IFate fate)
    {
        var fateId = GetFateTemplateId(fate);
        if (follow.FateIds.Contains(fateId))
            return;

        follow.FateIds = [.. follow.FateIds, fateId];
        configSaver.Save();
    }

    public void UnmarkFollow(IFate fate)
    {
        if (!follow.FateIds.Remove(GetFateTemplateId(fate)))
            return;
        configSaver.Save();
    }
}