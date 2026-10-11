using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Wunschkino.Auth;

namespace Wunschkino.Tests.Components;

/// <summary>
/// Gemeinsamer Unterbau für Komponententests: MudBlazor mit seinen Providern (ohne sie rendern Popover, Dialoge und
/// Snackbar-Meldungen nichts), lockeres JS-Interop und eine angemeldete Testperson.
/// Dienste der Testklasse werden im Konstruktor registriert; bUnit sperrt die Registrierung mit dem ersten Rendern.
/// Deshalb rendern die Provider erst beim ersten <see cref="RenderPage{TComponent}"/> oder Zugriff auf <see cref="Snackbars"/>.
/// </summary>
public abstract class ComponentTestBase : BunitContext, IAsyncLifetime
{
    /// <summary>Wie lange Tests auf asynchrone Zustände warten, bevor sie scheitern.</summary>
    protected static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly BunitAuthorizationContext _auth;
    private readonly List<IDisposable> _owned = [];
    private IRenderedComponent<MudSnackbarProvider>? _snackbars;
    private IRenderedComponent<MudDialogProvider>? _dialogs;
    private IRenderedComponent<MudPopoverProvider>? _popovers;

    /// <summary>Zeigt geöffnete Menüs und Auswahllisten (MudBlazor rendert sie nicht an der Stelle des Auslösers, sondern hier).</summary>
    protected IRenderedComponent<MudPopoverProvider> Popovers
    {
        get
        {
            EnsureProviders();
            return _popovers!;
        }
    }

    protected ComponentTestBase()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddScoped<Wunschkino.Localization.UserTimeZone>();
        _auth = this.AddAuthorization();
    }

    /// <summary>Zeigt die Snackbar-Meldungen, die eine Komponente über <see cref="ISnackbar"/> auslöst.</summary>
    protected IRenderedComponent<MudSnackbarProvider> Snackbars
    {
        get
        {
            EnsureProviders();
            return _snackbars!;
        }
    }

    /// <summary>Zeigt die Dialoge (zum Beispiel Bestätigungen), die eine Komponente über <see cref="IDialogService"/> öffnet.</summary>
    protected IRenderedComponent<MudDialogProvider> Dialogs
    {
        get
        {
            EnsureProviders();
            return _dialogs!;
        }
    }

    /// <summary>Gibt das Objekt am Ende des Tests frei (MudBlazors Aufräumen ist asynchron, ein <c>IDisposable</c> der Testklasse käme zu spät oder gar nicht).</summary>
    protected T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    protected IRenderedComponent<TComponent> RenderPage<TComponent>(Action<ComponentParameterCollectionBuilder<TComponent>>? parameters = null)
        where TComponent : IComponent
    {
        EnsureProviders();
        return Render(parameters);
    }

    protected void SignInAsAdmin(int userId = 1, string name = "admin")
    {
        _auth.SetAuthorized(name);
        _auth.SetRoles(AuthClaims.AdminRole);
        _auth.SetClaims(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
    }

    protected void SignInAsUser(int userId, string name)
    {
        _auth.SetAuthorized(name);
        _auth.SetClaims(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
    }

    private void EnsureProviders()
    {
        if (_snackbars is not null)
            return;
        _popovers = Render<MudPopoverProvider>();
        _dialogs = Render<MudDialogProvider>();
        _snackbars = Render<MudSnackbarProvider>();
    }

    // MudBlazors PopoverService kennt nur DisposeAsync; das synchrone Dispose, das xUnit sonst aufruft, würde werfen.
    public virtual Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        try
        {
            await DisposeAsync();
        }
        finally
        {
            // Auch wenn das Aufräumen des Kontexts wirft, sollen Test-DB und Temp-Ordner nicht liegen bleiben.
            foreach (var disposable in Enumerable.Reverse(_owned))
                disposable.Dispose();
        }
    }
}
