using System.Linq;
using Botja.Services;
using Botja.Windows;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Ocelot.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ocelot;
using Ocelot.Config;
using Ocelot.Config.Renderers.Enum;
using Ocelot.ECommons.Services;
using Ocelot.Pictomancy.Services;
using Ocelot.Rotation.Services;
using Ocelot.Services;
using Ocelot.Windows;
using Ocelot.Services.WindowManager;

namespace Botja;

public sealed class Plugin(IDalamudPluginInterface pluginInterface, IPluginLog log)
    : OcelotPlugin(pluginInterface, log)
{
    public override string Name => "Botja";

    protected override void Boostrap(IServiceCollection services)
    {
        // OcelotPlugin's ctor already registers the plugin interface as a singleton instance
        // before calling Boostrap; read it back instead of also capturing the ctor parameter.
        var pluginInterface = (IDalamudPluginInterface)services
            .First(d => d.ServiceType == typeof(IDalamudPluginInterface))
            .ImplementationInstance!;

        services.LoadECommons();
        services.AddBotjaConfig(pluginInterface);
        services.RemoveAll<IConfigSaver>();
        services.AddSingleton<IConfigSaver, BotjaConfigSaver>();
        services.AddSingleton<IConfigRenderer, ConfigRenderer>();
        services.AddSingleton(typeof(GenericDisplay<>));
        services.AddSingleton(typeof(NoOpFilter<>));
        services.AddSingleton(typeof(Ocelot.Config.Renderers.Excel.GenericDisplay<>));
        services.AddSingleton(typeof(Ocelot.Config.Renderers.Excel.NoOpFilter<>));
        services.AddSingleton<BlacklistChecklistRenderer>();
        services.LoadPictomancy();
        services.LoadRotations();
        services.AddSingleton<FateNavigationService>();
        services.AddSingleton<FatePriorityService>();
        services.AddSingleton<GuiInteractionService>();
        services.AddSingleton<CeSignupService>();
        services.AddSingleton<CombatControlService>();
        services.AddSingleton<AutoModeService>();
        services.AddSingleton<TranslationLoader>();
        services.AddSingleton<Windows.FateListWindow>();
        services.AddSingleton<Windows.AppraisingOverlayWindow>();
        services.AddSingleton<Windows.AppraisingSkipOverlayWindow>();
        // IWindow isn't auto-wired like the Lifecycle hooks (IOnUpdate etc.) — register explicitly
        // or the WindowManager never adds it to the WindowSystem and it's never drawn.
        services.AddSingleton<IWindow>(sp => sp.GetRequiredService<Windows.AppraisingOverlayWindow>());
        services.AddSingleton<IWindow>(sp => sp.GetRequiredService<Windows.AppraisingSkipOverlayWindow>());
        // Registering as IMainWindow (not IWindow) so it replaces Ocelot's default empty main window.
        services.AddSingleton<IMainWindow>(sp => sp.GetRequiredService<Windows.FateListWindow>());
    }
}

