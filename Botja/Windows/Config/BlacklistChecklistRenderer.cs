using System;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Ocelot.Config.Fields;
using Ocelot.Config.Renderers;
using Ocelot.Services.Translation;
using Botja.Services;

namespace Botja.Windows.Config;

public sealed class BlacklistChecklistAttribute(bool criticalEngagements)
    : UIFieldAttribute(typeof(BlacklistChecklistRenderer))
{
    public bool CriticalEngagements { get; } = criticalEngagements;
}

public sealed class BlacklistChecklistRenderer(IClientState clientState)
    : IFieldRenderer<BlacklistChecklistAttribute>
{
    public bool Render(object target, PropertyInfo prop, BlacklistChecklistAttribute attr, Type owner, ITranslator translator)
    {
        var sectors = BozjaSectorCatalog.GetSectors(clientState.TerritoryType);

        if (sectors == null)
        {
            ImGui.TextUnformatted("Blacklist checklists are available in the Bozjan Southern Front and Zadnor.");
            return false;
        }

        var ignoredIds = (HashSet<uint>?)prop.GetValue(target) ?? [];
        var changed = false;
        ImGui.TextUnformatted(attr.CriticalEngagements ? "Critical Engagements" : "FATEs / Skirmishes");
        ImGui.Separator();

        for (var sectorIndex = 0; sectorIndex < sectors.Count; sectorIndex++)
        {
            var sector = sectors[sectorIndex];
            if (!ImGui.CollapsingHeader($"Sector {(int)sector.Sector}: {sector.Name}"))
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