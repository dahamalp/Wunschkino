using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using MudBlazor.Services;
using Wunschkino.Resources;
using Wunschkino;
using Wunschkino.Anime;
using Wunschkino.Auth;
using Wunschkino.Components;
using Wunschkino.DatabaseBackup;
using Wunschkino.Data;
using Wunschkino.Gaps;
using Wunschkino.Hosting;
using Wunschkino.Library;
using Wunschkino.Localization;
using Wunschkino.Services;
using Wunschkino.Setup;
using Wunschkino.Wishes;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddCommandLine(args, WunschkinoOptions.CreateSwitchMappings());

var options = builder.Configuration.GetSection(WunschkinoOptions.SectionName).Get<WunschkinoOptions>() ?? new WunschkinoOptions();
CommonPasswords.Enabled = options.CheckCommonPasswords;
// „--validate-config“ prüft nur die Einstellungen und beendet sich: Das Install-Script nutzt es, bevor es eine Konfiguration
// in Betrieb nimmt, damit eine ungültige Angabe nie einen laufenden Dienst abschießt.
if (args.Contains("--validate-config"))
{
    try
    {
        options.ResolveListenUrl();
        options.ResolveAllowedHosts();
        options.ResolveTrustedProxies();
        options.ValidateRuntimeSettings();
        options.ResolveSourceUrl();
        options.ResolveBackup(options.ResolveDataDirectory(builder.Environment.ContentRootPath));
        Console.WriteLine("Die Konfiguration ist gültig.");
        return 0;
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

var listenUrl = options.ResolveListenUrl();
var urlsAlsoConfigured = listenUrl is not null && !string.IsNullOrEmpty(builder.Configuration["urls"]);
if (listenUrl is not null)
    builder.WebHost.UseUrls(listenUrl);
if (options.ResolveAllowedHosts() is { } allowedHosts)
    builder.Configuration["AllowedHosts"] = allowedHosts;
var trustedProxies = options.ResolveTrustedProxies();
options.ValidateRuntimeSettings();
var appInfo = new AppInfo(options.ResolveSourceUrl(), HealthEndpoint.Version);
builder.Services.AddSingleton(appInfo);
// Development läuft über http://localhost, dort wäre ein Secure-Cookie unbrauchbar.
var cookieSecurePolicy = options.SecureCookies && !builder.Environment.IsDevelopment()
    ? CookieSecurePolicy.Always
    : CookieSecurePolicy.SameAsRequest;
var dataDirectory = options.ResolveDataDirectory(builder.Environment.ContentRootPath);
// Konsolenbefehle des Betreibers (gesperrte Adressen) laufen auf der Datenbank, ohne die Webanwendung zu starten.
if (BlockCommands.IsBlockCommand(args))
    return await BlockCommands.RunAsync(args, Path.Combine(dataDirectory, "wunschkino.db"), Console.Out, Console.Error, operatorName: Environment.GetEnvironmentVariable("SUDO_USER"));
if (UserCommands.IsUserCommand(args))
    return await UserCommands.RunAsync(args, Path.Combine(dataDirectory, "wunschkino.db"), new ConsolePasswordPrompt(), Console.Out, Console.Error,
        operatorName: Environment.GetEnvironmentVariable("SUDO_USER"));

// Enthält DB und Data-Protection-Schlüssel (Klartext auf Linux) – nur der Dienstnutzer darf hinein.
if (OperatingSystem.IsWindows())
    Directory.CreateDirectory(dataDirectory);
else
    Directory.CreateDirectory(dataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(circuits =>
    {
        // Getrennte Verbindungen (zum Beispiel Handy im Funkloch) bleiben kurz erhalten; weniger davon begrenzt den Speicher.
        circuits.DisconnectedCircuitMaxRetained = 50;
    });
builder.Services.AddMudServices();

builder.Services.AddDbContextFactory<AppDbContext>(db =>
    db.UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(dataDirectory, "wunschkino.db"),
        Pooling = options.SqlitePooling
    }.ToString()));
builder.Services.AddDataProtection()
    .SetApplicationName("Wunschkino")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(cookie =>
    {
        cookie.Cookie.Name = AuthCookie.NameFor(cookieSecurePolicy);
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        cookie.Cookie.SecurePolicy = cookieSecurePolicy;
        cookie.LoginPath = "/login";
        cookie.AccessDeniedPath = "/";
        cookie.ExpireTimeSpan = TimeSpan.FromDays(14);
        cookie.SlidingExpiration = true;
        cookie.Events.OnValidatePrincipal = async context =>
        {
            var validator = context.HttpContext.RequestServices.GetRequiredService<PrincipalValidator>();
            if (context.Principal is null || !await validator.IsValidAsync(context.Principal, context.HttpContext.RequestAborted))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, RevalidatingAuthStateProvider>();

builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Mit eigenem Antworttext greift UseStatusCodePagesWithReExecute nicht: Das würde den POST auf /not-found
    // erneut ausführen, dort am Antiforgery-Check scheitern und aus der 429 eine 400 machen.
    // Jede abgewiesene Anfrage zählt für die gestaffelte Sperre der Adresse: Wer das Limit sprengt, wird nach drei Abweisungen gesperrt.
    limiter.OnRejected = async (context, ct) =>
    {
        var http = context.HttpContext;
        // Ausgelöst von einer fremden Seite im Browser des Opfers? Dann zählt es nicht, sonst ließe sich jede Adresse von außen sperren.
        if (!IpBlockMiddleware.IsCrossSite(http.Request))
        {
            await http.RequestServices.GetRequiredService<IpBlockService>()
                .RegisterFailureAsync(http.Connection.RemoteIpAddress, http.Connection.LocalIpAddress, ct);
        }
        await http.Response.WriteAsync(Texts.Msg_RateLimited, ct);
    };
    limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var remote = context.Connection.RemoteIpAddress;
        // Fremde Seiten zählen in einem eigenen Topf, damit sie das Limit des echten Nutzers nicht verbrauchen.
        var key = $"{(IpBlockMiddleware.IsCrossSite(context.Request) ? "fremd" : "eigen")}:{ClientAddress.PartitionKey(remote)}";
        if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/login", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetFixedWindowLimiter(
                $"login:{key}", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) });
        }

        var blocks = context.RequestServices.GetRequiredService<IpBlockService>();
        return options.RequestLimitPerMinute > 0 && !blocks.IsExempt(remote, context.Connection.LocalIpAddress)
            ? RateLimitPartition.GetFixedWindowLimiter(
                $"alle:{key}", _ => new FixedWindowRateLimiterOptions { PermitLimit = options.RequestLimitPerMinute, Window = TimeSpan.FromMinutes(1) })
            : RateLimitPartition.GetNoLimiter("frei");
    });
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<Wunschkino.Localization.UserTimeZone>();
builder.Services.AddSingleton<IPasswordHasher, Argon2IdPasswordHasher>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton(new SetupTokenStore(dataDirectory));
builder.Services.AddSingleton<SetupState>();
builder.Services.AddSingleton<SetupService>();
builder.Services.AddSingleton(trustedProxies);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ProxyWarningState>();
builder.Services.AddSingleton<SecurityCheckService>();
builder.Services.AddSingleton(options.ResolveBackup(dataDirectory));
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService(services => services.GetRequiredService<BackupService>());
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<AdminGuard>();
builder.Services.AddSingleton<PosterCache>();
builder.Services.AddSingleton<IpBlockService>();
builder.Services.AddSingleton<TwoFactorService>();
builder.Services.AddSingleton(services => new TwoFactorTicket(services.GetRequiredService<IDataProtectionProvider>(), cookieSecurePolicy));
builder.Services.AddSingleton<LoginService>();
builder.Services.AddSingleton<LoginGuard>();
builder.Services.AddSingleton<UserService>();
builder.Services.AddSingleton<ThemePreferenceService>();
builder.Services.AddSingleton<LanguagePreferenceService>();
builder.Services.AddTransient<MudLocalizer, WunschkinoMudLocalizer>();
builder.Services.AddSingleton<Wunschkino.Legal.LegalService>();
builder.Services.AddSingleton<PrincipalValidator>();
builder.Services.AddScoped<ConnectionSettingsService>();
builder.Services.AddScoped<WishService>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<AnimeIdMapping>();
builder.Services.AddSingleton(new AnimeListStore(dataDirectory));
builder.Services.AddSingleton<AniListThrottle>();
builder.Services.AddSingleton<UserSearchThrottle>();
builder.Services.AddSingleton<AnimeIdMappingUpdater>();
// Die Integrationstests starten die ganze App – dabei soll keine 6-MB-Liste aus dem Netz geladen werden.
if (!builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AnimeIdMappingUpdater>());
builder.Services.AddSingleton<LibraryCache>();
builder.Services.AddSingleton<WishAutoCompleter>();
builder.Services.AddScoped<WishMetadataRefresher>();
builder.Services.AddScoped<SeriesGapService>();
builder.Services.AddSingleton<SeriesGapProgress>();
builder.Services.AddSingleton<LibraryRefreshService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LibraryRefreshService>());

// Keine automatischen Redirects: sonst könnte ein (falsch eingetragener) Server Requests samt API-Key auf andere Ziele umlenken.
builder.Services.AddHttpClient<JellyfinClient>(http => http.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => OutboundAddressGuard.CreateHandler(options.AllowLinkLocalUrls));
builder.Services.AddHttpClient<TmdbClient>(http => http.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => OutboundAddressGuard.CreateHandler(options.AllowLinkLocalUrls));
builder.Services.AddHttpClient<AniListClient>(http => http.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => OutboundAddressGuard.CreateHandler(options.AllowLinkLocalUrls));
builder.Services.AddHttpClient<AnimeListClient>(http => http.Timeout = TimeSpan.FromMinutes(2))
    .ConfigurePrimaryHttpMessageHandler(() => OutboundAddressGuard.CreateHandler(options.AllowLinkLocalUrls));

var app = builder.Build();
if (urlsAlsoConfigured)
{
    app.Logger.LogWarning("Wunschkino:Port ist gesetzt und überschreibt die Einstellungen --urls/ASPNETCORE_URLS. Es gilt {Url}.", listenUrl);
}

await using (var db = await app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
{
    await db.Database.MigrateAsync();
}
var setupService = app.Services.GetRequiredService<SetupService>();
await setupService.InitializeAsync();
if (!app.Services.GetRequiredService<SetupState>().IsCompleted)
{
    app.Logger.LogWarning("Wunschkino ist noch nicht eingerichtet. Das Setup-Token liegt in {TokenPath}.",
        app.Services.GetRequiredService<SetupTokenStore>().TokenPath);
}

// Zuerst: Alles danach (Rate-Limit, Cookies, HSTS) soll die echte Client-IP und das Protokoll des Proxys sehen.
app.UseForwardedHeaders(ForwardedHeadersSetup.Create(trustedProxies));
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<IpBlockMiddleware>();
app.UseMiddleware<OriginCheckMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseMiddleware<SetupRedirectMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<PasswordChangeMiddleware>();
// Nach der Anmeldung, damit die Sprache des Kontos zählt: Konto, dann Browser, dann Standardsprache des Admins.
app.UseRequestLocalization(localization =>
{
    localization.DefaultRequestCulture = new RequestCulture(AppLanguages.Default);
    localization.SupportedCultures = [.. AppLanguages.Cultures];
    localization.SupportedUICultures = [.. AppLanguages.Cultures];
    localization.RequestCultureProviders = [new AccountLanguageProvider(), new AcceptLanguageHeaderRequestCultureProvider(), new DefaultLanguageProvider()];
});
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHealth();

app.MapPost("/account/logout", async (HttpContext context, IAntiforgery antiforgery, UserService users) =>
{
    // Minimal-API-Endpoints ohne Form-Binding prüfen das Antiforgery-Token nicht automatisch.
    if (!await antiforgery.IsRequestValidAsync(context))
        return Results.BadRequest(Texts.Msg_LogoutInvalid);

    // Das Cookie ist zustandslos: Es zu löschen beendet nur diese Sitzung im Browser. Ein neuer Stempel macht auch eine
    // gestohlene Kopie ungültig (und meldet das Konto auf den anderen Geräten ab).
    if (AuthClaims.GetUserId(context.User) is { } userId)
        await users.RevokeSessionsAsync(userId, context.RequestAborted);
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/login");
});

// Poster laufen über den Server, damit der Jellyfin-API-Key nie im Browser landet. Nur IDs aus dem Cache sind erlaubt.
app.MapGet("/posters/{jellyfinId}", async (string jellyfinId, int? w, HttpContext context, LibraryCache cache, SettingsService settings, JellyfinClient jellyfin, PosterCache posters) =>
{
    if (!cache.TryGet(jellyfinId, out var item) || !item.HasPoster)
        return Results.NotFound();

    var width = Math.Clamp(w ?? 300, 100, 600);
    var cacheKey = $"{item.JellyfinId}:{width}";
    if (posters.TryGet(cacheKey, out var cachedPoster) && cachedPoster is not null)
        return ImageProxyResponse.FromCache(cachedPoster, context);

    var url = await settings.GetAsync(SettingKeys.JellyfinUrl, context.RequestAborted);
    var apiKey = await settings.GetAsync(SettingKeys.JellyfinApiKey, context.RequestAborted);
    if (url is null || apiKey is null)
        return Results.NotFound();

    var poster = await jellyfin.GetPosterAsync(url, apiKey, item.JellyfinId, width, context.RequestAborted);
    if (poster is null)
        return Results.NotFound();

    var image = await ImageProxyResponse.BufferAsync(poster, context.RequestAborted);
    if (image is null)
        return Results.StatusCode(StatusCodes.Status502BadGateway);

    posters.Set(cacheKey, image);
    return ImageProxyResponse.FromCache(image, context);
}).RequireAuthorization();

// TMDB-Poster laufen ebenfalls über den Server: kein Kontakt des Browsers zu TMDB, und der Pfad wird streng geprüft.
app.MapGet("/tmdb-posters/{file}", async (string file, int? w, HttpContext context, TmdbClient tmdb) =>
{
    var path = "/" + file;
    if (!TmdbPosters.IsValidPath(path))
        return Results.NotFound();

    var poster = await tmdb.GetPosterAsync(path, TmdbPosters.ParseSize(w), context.RequestAborted);
    if (poster is null)
        return Results.NotFound();

    return await ImageProxyResponse.ToResultAsync(poster, context);
}).RequireAuthorization();

app.MapGet("/anilist-covers/{**path}", async (string path, HttpContext context, AniListClient aniList) =>
{
    var coverPath = "/" + path;
    if (!AniListCovers.IsValidPath(coverPath))
        return Results.NotFound();

    var cover = await aniList.GetCoverAsync(coverPath, context.RequestAborted);
    if (cover is null)
        return Results.NotFound();

    return await ImageProxyResponse.ToResultAsync(cover, context);
}).RequireAuthorization();

app.Run();
return 0;

public partial class Program;
