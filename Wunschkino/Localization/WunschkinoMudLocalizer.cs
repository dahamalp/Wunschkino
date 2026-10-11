using System.Globalization;
using Microsoft.Extensions.Localization;
using MudBlazor;
using Wunschkino.Resources;

namespace Wunschkino.Localization;

/// <summary>
/// Übersetzt die eingebauten Texte von MudBlazor (etwa „Clear“ am Suchfeld oder „Close“ an Meldungen) aus <c>Texts.resx</c>
/// mit dem Präfix <c>Mud_</c>. Für Englisch und fehlende Schlüssel nimmt MudBlazor seine eigenen englischen Texte.
/// </summary>
public sealed class WunschkinoMudLocalizer : MudLocalizer
{
    public override LocalizedString this[string key]
    {
        get
        {
            var culture = CultureInfo.CurrentUICulture;
            if (culture.TwoLetterISOLanguageName == "en")
                return new LocalizedString(key, key, resourceNotFound: true);
            var text = Texts.ResourceManager.GetString("Mud_" + key, culture);
            return text is null ? new LocalizedString(key, key, resourceNotFound: true) : new LocalizedString(key, text);
        }
    }

    public override LocalizedString this[string key, params object[] arguments]
    {
        get
        {
            var text = this[key];
            return text.ResourceNotFound ? text : new LocalizedString(key, string.Format(CultureInfo.CurrentCulture, text.Value, arguments));
        }
    }
}
