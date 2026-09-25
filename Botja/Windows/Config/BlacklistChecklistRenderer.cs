using System;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Ocelot.Config.Fields;
using Ocelot.Config.Renderers;
using Ocelot.Services.Translation;

namespace Botja.Windows.Config;

public sealed class BlacklistChecklistAttribute(bool criticalEngagements)
    : UIFieldAttribute(typeof(BlacklistChecklistRenderer))
{
    public bool CriticalEngagements { get; } = criticalEngagements;
}

public sealed class BlacklistChecklistRenderer(IClientState clientState)
    : IFieldRenderer<BlacklistChecklistAttribute>
{
    private sealed record Entry(uint Id, string Name);
    private sealed record Sector(string Name, Entry[] Fates, Entry[] CriticalEngagements);

    private static readonly Sector[] SouthernFront =
    [
        new("Southern Entrenchment",
        [new(1597, "Sneak & Spell"), new(1598, "None of Them Knew They Were Robots"), new(1599, "The Beasts Must Die"), new(1600, "Unrest for the Wicked"), new(1601, "More Machine Now than Man"), new(1602, "Can Carnivorous Plants Bloom Even on a Battlefield?"), new(1603, "Seeq and Destroy"), new(1604, "All Pets Are Off"), new(1605, "Conflicting with the First Law"), new(1606, "Brought to Heal")],
        [new(1, "Kill It with Fire"), new(2, "The Baying of the Hound(s)"), new(3, "Vigil for the Lost"), new(4, "Aces High"), new(5, "The Shadow of Death's Hand")]),
        new("Old Bozja",
        [new(1607, "The Monster Mash"), new(1608, "Red (Chocobo) Alert"), new(1609, "Unicorn Flakes"), new(1610, "Parts and Recreation"), new(1611, "The Element of Supplies"), new(1612, "Heavy Boots of Lead"), new(1613, "No Camping Allowed"), new(1614, "Scavengers of Man's Sorrow"), new(1615, "Help Wanted"), new(1616, "Pyromancer Supreme")],
        [new(6, "The Final Furlong"), new(7, "The Hunt for Red Choctober"), new(8, "Beast of Man"), new(9, "The Fires of War"), new(10, "Patriot Games")]),
        new("The Alermuc Climb",
        [new(1617, "Waste the Rainbow"), new(1618, "The Wild Bunch"), new(1619, "My Family and Other Animals"), new(1620, "I'm a Mechanical Man"), new(1621, "Murder Death Kill"), new(1622, "Desperately Seeking Something"), new(1623, "Supplies Party"), new(1624, "Demonstrably Demonic"), new(1625, "For Absent Friends"), new(1626, "Of Steel and Flame"), new(1627, "Let Slip the Dogs of War"), new(1628, "The War Against the Machines")],
        [new(11, "Trampled under Hoof"), new(12, "And the Flames Went Higher"), new(13, "Metal Fox Chaos"), new(14, "Rise of the Robots"), new(15, "Where Strode the Behemoth"), new(16, "The Battle of Castrum Lacus Litore")]),
    ];

    private static readonly Sector[] Zadnor =
    [
        new("The Southern Plateau",
        [new(1717, "Of Beasts and Braggadocio"), new(1718, "Parts and Parcel"), new(1719, "An Immoral Dilemma"), new(1720, "Deadly Divination"), new(1721, "A Wrench in the Reconnaissance Effort"), new(1722, "Another Pilot Episode"), new(1723, "Breaking the Ice"), new(1724, "Meet the Puppetmaster")],
        [new(17, "On Serpents' Wings"), new(19, "The Broken Blade"), new(20, "From Beyond the Grave"), new(21, "With Diremite and Main"), new(29, "A Familiar Face")]),
        new("The Western Plateau",
        [new(1725, "Challenge Accepted"), new(1726, "Th'uban the Terrible"), new(1727, "An End to Atrocities"), new(1728, "A Just Pursuit"), new(1729, "Tanking Up"), new(1730, "Supersoldier Rising"), new(1731, "Demented Mentor"), new(1732, "Sever the Strings")],
        [new(22, "Here Comes the Cavalry"), new(23, "Head of the Snake"), new(24, "There Would Be Blood"), new(25, "Never Cry Wolf"), new(26, "Time to Burn")]),
        new("The Northern Plateau",
        [new(1733, "The Beasts Are Back"), new(1734, "Still Only Counts as One"), new(1735, "Seeq and You Will Find"), new(1736, "Mean-spirited"), new(1737, "A Relic Unleashed"), new(1738, "When Mages Rage"), new(1739, "Hypertuned Havoc"), new(1740, "Attack of the Supersoldiers"), new(1741, "The Student Becalms the Master"), new(1742, "Attack of the Machines")],
        [new(18, "Feeling the Burn"), new(27, "Lean, Mean, Magitek Machines"), new(28, "Worn to a Shadow"), new(30, "Looks to Die For"), new(31, "Taking the Lyon's Share")]),
    ];

    public bool Render(object target, PropertyInfo prop, BlacklistChecklistAttribute attr, Type owner, ITranslator translator)
    {
        var sectors = clientState.TerritoryType switch
        {
            920 => SouthernFront,
            975 => Zadnor,
            _ => null,
        };

        if (sectors == null)
        {
            ImGui.TextUnformatted("Blacklist checklists are available in the Bozjan Southern Front and Zadnor.");
            return false;
        }

        var ignoredIds = (HashSet<uint>?)prop.GetValue(target) ?? [];
        var changed = false;
        ImGui.TextUnformatted(attr.CriticalEngagements ? "Critical Engagements" : "FATEs / Skirmishes");
        ImGui.Separator();

        for (var sectorIndex = 0; sectorIndex < sectors.Length; sectorIndex++)
        {
            var sector = sectors[sectorIndex];
            if (!ImGui.CollapsingHeader($"Sector {sectorIndex + 1}: {sector.Name}"))
                continue;

            var entries = attr.CriticalEngagements ? sector.CriticalEngagements : sector.Fates;
            foreach (var entry in entries)
            {
                var ignored = ignoredIds.Contains(entry.Id);
                if (!ImGui.Checkbox($"{entry.Name}##{prop.Name}_{entry.Id}", ref ignored))
                    continue;

                if (ignored)
                    ignoredIds.Add(entry.Id);
                else
                    ignoredIds.Remove(entry.Id);

                changed = true;
            }
        }

        if (changed)
            prop.SetValue(target, ignoredIds);

        return changed;
    }
}