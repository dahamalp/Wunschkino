using System.Globalization;
using System.Runtime.CompilerServices;

namespace Wunschkino.Tests;

/// <summary>
/// Die Tests prüfen die deutschen Texte. Ohne feste Kultur bekäme ein Rechner mit englischer Systemsprache die englischen.
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void UseGerman()
    {
        var german = CultureInfo.GetCultureInfo("de-DE");
        CultureInfo.DefaultThreadCurrentCulture = german;
        CultureInfo.DefaultThreadCurrentUICulture = german;
        CultureInfo.CurrentCulture = german;
        CultureInfo.CurrentUICulture = german;
    }
}
