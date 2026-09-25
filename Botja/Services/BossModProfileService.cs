using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using System;
using System.Collections.Generic;

namespace Botja.Services;

public sealed class BossModProfileService(IDalamudPluginInterface plugin)
{
    private readonly ICallGateSubscriber<List<string>> getList =
        plugin.GetIpcSubscriber<List<string>>("BossMod.Presets.GetList");

    public bool IsAvailable
    {
        get
        {
            try
            {
                return getList.HasFunction;
            }
            catch
            {
                return false;
            }
        }
    }

    public IReadOnlyList<string> GetNames()
    {
        try
        {
            return getList.HasFunction ? getList.InvokeFunc() : [];
        }
        catch
        {
            return [];
        }
    }
}