using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using System;
using System.Collections.Generic;

namespace Botja.Services;

public sealed class BossModActiveProfilesService(IDalamudPluginInterface plugin)
{
    private readonly ICallGateSubscriber<List<string>> getActiveList =
        plugin.GetIpcSubscriber<List<string>>("BossMod.Presets.GetActiveList");

    private readonly ICallGateSubscriber<List<string>, bool> setActiveList =
        plugin.GetIpcSubscriber<List<string>, bool>("BossMod.Presets.SetActiveList");

    private readonly ICallGateSubscriber<bool> clearActive =
        plugin.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive");

    public IReadOnlyList<string> GetActiveNames()
    {
        try
        {
            return getActiveList.HasFunction ? getActiveList.InvokeFunc() : [];
        }
        catch
        {
            return [];
        }
    }

    public bool SetActiveNames(IReadOnlyList<string> names)
    {
        try
        {
            return setActiveList.HasFunction && setActiveList.InvokeFunc([.. names]);
        }
        catch
        {
            return false;
        }
    }

    public bool ClearActive()
    {
        try
        {
            return clearActive.HasFunction && clearActive.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }
}