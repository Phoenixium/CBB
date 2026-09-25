using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Ocelot.Config.Fields;
using Ocelot.Config.Renderers;
using Ocelot.Services.Translation;

namespace Botja.Windows.Config;

public sealed class BossModProfileChecklistAttribute() : UIFieldAttribute(typeof(BossModProfileChecklistRenderer))
{
}

public sealed class BossModProfileChecklistRenderer(
    Services.BossModProfileService profiles
) : IFieldRenderer<BossModProfileChecklistAttribute>
{
    // Config windows re-render every frame while open; only rescan the IPC once per open session
    // (large gap since the last render = the window was closed) instead of every frame.
    private const long ReopenGapMs = 1000;
    private long lastRenderMs;
    private bool ipcAvailable;
    private string[] cachedNames = [];

    public bool Render(object target, PropertyInfo prop, BossModProfileChecklistAttribute attr, Type owner, ITranslator translator)
    {
        var now = Environment.TickCount64;
        if (now - lastRenderMs > ReopenGapMs)
        {
            ipcAvailable = profiles.IsAvailable;
            cachedNames = ipcAvailable
                ? profiles.GetNames().Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray()
                : [];
        }
        lastRenderMs = now;

        var selected = (List<string>?)prop.GetValue(target) ?? [];
        var available = cachedNames;

        if (!ipcAvailable)
        {
            ImGui.TextUnformatted("BossMod profile list IPC is unavailable.");
            return false;
        }

        if (available.Length == 0)
        {
            ImGui.TextUnformatted("No BossMod profiles found.");
            return false;
        }

        ImGui.TextUnformatted("BossMod profiles enabled inside FATEs / CEs:");
        var changed = false;
        foreach (var name in available)
        {
            var isSelected = selected.Contains(name, StringComparer.Ordinal);
            if (!ImGui.Checkbox(name, ref isSelected))
                continue;

            if (isSelected)
            {
                if (!selected.Contains(name, StringComparer.Ordinal))
                    selected.Add(name);
            }
            else
            {
                selected.RemoveAll(existing => string.Equals(existing, name, StringComparison.Ordinal));
            }

            changed = true;
        }

        if (changed)
            prop.SetValue(target, selected);

        return changed;
    }
}