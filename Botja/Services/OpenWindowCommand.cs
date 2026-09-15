using Ocelot.Services;
using Ocelot.Services.Commands;
using Ocelot.Services.Translation;
using System;

namespace Botja.Services;

[OcelotService(Service = typeof(IOcelotCommand))]
public sealed class OpenWindowCommand(
    ITranslator translator,
    Windows.FateListWindow window,
    Ocelot.Windows.IConfigWindow configWindow
) : OcelotCommand(translator)
{
    public override string Command => "cbb";

    public override (string help, bool show) BuildHelp() => ("Open the Fate List window, or use 'config' for settings.", true);

    public override void Execute(CommandContext context)
    {
        if (context.Args.Length == 0)
        {
            window.IsOpen = true;
            return;
        }

        if (context.Args.Length == 1 && context.Args[0].Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            configWindow.Toggle();
            return;
        }

        var argument = context.Args.Length > 0 ? context.Args[0] : string.Empty;
        throw new ArgumentException($"Unknown argument: {argument}");
    }
}