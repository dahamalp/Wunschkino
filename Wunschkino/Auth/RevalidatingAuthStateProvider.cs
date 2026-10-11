using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace Wunschkino.Auth;

/// <summary>Laufende Blazor-Circuits sehen keine neuen HTTP-Requests; deshalb wird der Login hier regelmäßig nachgeprüft.</summary>
public sealed class RevalidatingAuthStateProvider(ILoggerFactory loggerFactory, PrincipalValidator validator)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken) =>
        validator.IsValidAsync(authenticationState.User, cancellationToken);
}
