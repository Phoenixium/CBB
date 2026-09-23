using System.Reflection;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using Ocelot.Config;

namespace Botja;

internal static class BotjaConfigRegistration
{
    public static void AddBotjaConfig(this IServiceCollection services, IDalamudPluginInterface plugin)
    {
        var loadedConfig = plugin.GetPluginConfig() as PluginConfig;
        var hasExistingConfig = plugin.ConfigFile.Exists && plugin.ConfigFile.Length > 0;
        var canSave = loadedConfig is not null || !hasExistingConfig;
        var config = loadedConfig ?? new PluginConfig();

        config.HydrateMissingSections();

        services.AddSingleton(config);
        services.AddSingleton<IPluginConfig>(config);
        services.AddSingleton<IPluginConfiguration>(config);
        services.AddSingleton(new BotjaConfigSaveState(canSave));

        var properties = typeof(IPluginConfig).GetProperties(BindingFlags.Instance | BindingFlags.Public);
        foreach (var property in properties)
        {
            var propertyType = property.PropertyType;
            services.AddSingleton(propertyType, serviceProvider =>
            {
                var pluginConfig = serviceProvider.GetRequiredService<IPluginConfig>();
                return property.GetValue(pluginConfig)!;
            });

            if (typeof(IAutoConfig).IsAssignableFrom(propertyType))
            {
                services.AddSingleton(typeof(IAutoConfig), serviceProvider =>
                {
                    var pluginConfig = serviceProvider.GetRequiredService<IPluginConfig>();
                    return property.GetValue(pluginConfig)!;
                });
            }
        }
    }
}

internal sealed record BotjaConfigSaveState(bool CanSave);

internal sealed class BotjaConfigSaver(
    IDalamudPluginInterface plugin,
    IPluginConfiguration config,
    BotjaConfigSaveState saveState,
    IPluginLog log
) : IConfigSaver
{
    private bool warned;

    public void Save()
    {
        if (saveState.CanSave)
        {
            plugin.SavePluginConfig(config);
            return;
        }

        if (warned)
            return;

        warned = true;
        log.Warning("[Config] Save skipped because an existing config could not be loaded; refusing to overwrite it with defaults.");
    }
}