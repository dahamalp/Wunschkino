using Microsoft.AspNetCore.Localization;
using Wunschkino.Auth;

namespace Wunschkino.Localization;

/// <summary>Erste Wahl: die Sprache, die der angemeldete Nutzer im Kopfmenü gewählt hat.</summary>
public sealed class AccountLanguageProvider : RequestCultureProvider
{
    public override async Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        if (AuthClaims.GetUserId(httpContext.User) is not { } userId)
            return null;
        var language = await httpContext.RequestServices.GetRequiredService<LanguagePreferenceService>().GetAsync(userId, httpContext.RequestAborted);
        return language is null ? null : new ProviderCultureResult(language);
    }
}

/// <summary>Letzte Wahl, wenn weder Konto noch Browser eine unterstützte Sprache nennen: die Standardsprache des Admins.</summary>
public sealed class DefaultLanguageProvider : RequestCultureProvider
{
    public override async Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext) =>
        new(await httpContext.RequestServices.GetRequiredService<LanguagePreferenceService>().GetDefaultAsync(httpContext.RequestAborted));
}
