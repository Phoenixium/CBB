using System.Linq;
using Botja.Services;
using Botja.Windows;
using Botja.Windows.Config;
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
using Ocelot.Rotation.Services.BossMod;
using Ocelot.Services;
using Ocelot.Windows;
using Ocelot.Services.WindowManager;

namespace Botja;

public sealed class Plugin(IDalamudPluginInterface pluginInterface, IPluginLog log)
    : OcelotPlugin(pluginInterface, log)
{
    public override string Name => "Botja";

    protected override void Bootstrap(IServiceCollection services)
    {
        // OcelotPlugin's ctor already registers the plugin interface as a singleton instance
        // before calling Bootstrap; read it back instead of also capturing the ctor parameter.
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
        services.AddSingleton<ItemInspectionChecklistRenderer>();
        services.AddSingleton<CombatAiChecklistRenderer>();
        services.AddSingleton<BossModProfileChecklistRenderer>();
        services.AddSingleton<AutoModeState>();
        services.LoadPictomancy();
        services.LoadRotations();
        services.AddSingleton<ISelectableCombatAi>(sp => new CombatAiBackendAdapter(
            sp.GetRequiredService<BossModMiscAiBackend>(),
            CombatAiSelection.MiscAi,
            "BossMod Misc AI"));
        services.AddSingleton<ISelectableCombatAi>(sp => new JobRotationCombatAiAdapter(
            sp.GetServices<IJobRotationBackend>().Single(backend => backend.Kind == JobRotationBackendKind.Wrath),
            CombatAiSelection.Wrath,
            "Wrath"));
        services.AddSingleton<ISelectableCombatAi>(sp => new JobRotationCombatAiAdapter(
            sp.GetServices<IJobRotationBackend>().Single(backend => backend.Kind == JobRotationBackendKind.RotationSolverReborn),
            CombatAiSelection.RotationSolverReborn,
            "Rotation Solver Reborn"));
        services.AddSingleton<ISelectableCombatAi>(sp => new JobRotationCombatAiAdapter(
            sp.GetServices<IJobRotationBackend>().Single(backend => backend.Kind == JobRotationBackendKind.BossMod),
            CombatAiSelection.BossMod,
            "BossMod"));
        services.AddSingleton<ISelectableCombatAi>(sp => new JobRotationCombatAiAdapter(
            sp.GetServices<IJobRotationBackend>().Single(backend => backend.Kind == JobRotationBackendKind.BossModReborn),
            CombatAiSelection.BossModReborn,
            "BossMod Reborn"));
        services.AddSingleton<FateNavigationService>();
        services.AddSingleton<FatePriorityService>();
        services.AddSingleton<GuiInteractionService>();
        services.AddSingleton<AppraisingNativeOverlayService>();
        services.AddSingleton<CeSignupService>();
        services.AddSingleton<BossModProfileService>();
        services.AddSingleton<BossModActiveProfilesService>();
        services.AddSingleton<CombatControlService>();
        services.AddSingleton<HostileDetectionService>();
        services.AddSingleton<HostileOverlayService>();
        services.AddSingleton<AutoModeService>();
        services.AddSingleton<TranslationLoader>();
        services.AddSingleton<Windows.FateListWindow>();
        services.AddSingleton<Windows.AppraisingOverlayWindow>();
        // IWindow isn't auto-wired like the Lifecycle hooks (IOnUpdate etc.) — register explicitly
        // or the WindowManager never adds it to the WindowSystem and it's never drawn.
        services.AddSingleton<IWindow>(sp => sp.GetRequiredService<Windows.AppraisingOverlayWindow>());
        // Registering as IMainWindow (not IWindow) so it replaces Ocelot's default empty main window.
        services.AddSingleton<IMainWindow>(sp => sp.GetRequiredService<Windows.FateListWindow>());
    }
}

