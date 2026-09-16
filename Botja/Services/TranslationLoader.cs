using Ocelot.Lifecycle;
using Ocelot.Services.Translation;

namespace Botja.Services;

public sealed class TranslationLoader(ITranslationRepository translations) : IOnLoad
{
    public int Order => int.MaxValue;

    public void OnLoad()
    {
        translations.LoadFromDirectory("Translations", "en");
    }
}
