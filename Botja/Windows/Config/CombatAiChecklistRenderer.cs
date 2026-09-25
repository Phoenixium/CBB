using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Botja.Services;
using Ocelot.Config.Fields;
using Ocelot.Config.Renderers;
using Ocelot.Ipc.BossMod;
using Ocelot.Services.Translation;

namespace Botja.Windows.Config;

public sealed class CombatAiChecklistAttribute() : UIFieldAttribute(typeof(CombatAiChecklistRenderer))
{
}

public sealed class CombatAiChecklistRenderer(
    IEnumerable<ISelectableCombatAi> backends,
    IPluginLog log,
    IBossModIpc bossMod,
    BossModProfileService bossModProfiles,
    BossModActiveProfilesService activeProfiles
)
    : IFieldRenderer<CombatAiChecklistAttribute>
{
    // Config windows re-render every frame while open; only dump the profile diagnostics once per
    // open session (large gap since the last render = the window was closed) instead of every frame.
    private const long ReopenGapMs = 1000;
    private static long lastRenderMs;

    public bool Render(object target, PropertyInfo prop, CombatAiChecklistAttribute attr, Type owner, ITranslator translator)
    {
        var selected = (List<CombatAiSelection>?)prop.GetValue(target) ?? [];
        var available = backends
            .OrderBy(backend => backend.DisplayName, StringComparer.Ordinal)
            .ToArray();

        long now = Environment.TickCount64;
        var justOpened = now - lastRenderMs > ReopenGapMs;
        lastRenderMs = now;
        if (justOpened)
            DumpProfiles(available);

        if (available.Length == 0)
        {
            ImGui.TextUnformatted("No combat AIs are registered.");
            return false;
        }

        ImGui.TextUnformatted("Combat AIs enabled inside FATEs / CEs:");
        var changed = false;
        foreach (var backend in available)
        {
            var isSelected = selected.Contains(backend.Kind);
            if (!ImGui.Checkbox(backend.Kind.ToString(), ref isSelected))
                continue;

            if (isSelected)
            {
                if (!selected.Contains(backend.Kind))
                    selected.Add(backend.Kind);
            }
            else
            {
                selected.Remove(backend.Kind);
            }

            changed = true;
        }

        if (changed)
            prop.SetValue(target, selected);

        return changed;
    }

    private void DumpProfiles(IReadOnlyList<ISelectableCombatAi> available)
    {
        log.Information("[CombatAI] Ocelot AIs ({Count}): {Names}", available.Count, string.Join(", ", available.Select(backend => backend.DisplayName)));

        if (!bossMod.IsAvailable)
        {
            log.Information("[CombatAI] BossMod profiles: IPC unavailable (profile enumeration is not exposed)");
            return;
        }

        var activeNames = activeProfiles.GetActiveNames();
        log.Information("[CombatAI] BossMod active profiles ({Count}): {Names}", activeNames.Count, activeNames.Count == 0 ? "<none>" : string.Join(", ", activeNames));

        if (!bossModProfiles.IsAvailable)
        {
            log.Information("[CombatAI] BossMod profile list IPC unavailable");
            return;
        }

        var profiles = bossModProfiles.GetNames();
        log.Information("[CombatAI] BossMod profiles ({Count}): {Names}", profiles.Count, string.Join(", ", profiles));
    }
}