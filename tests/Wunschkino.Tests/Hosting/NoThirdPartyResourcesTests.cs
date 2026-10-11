using System.Text.RegularExpressions;

namespace Wunschkino.Tests.Hosting;

/// <summary>
/// Rechtliche Leitplanke: Der Browser darf keine Ressource von Dritten laden, denn schon die
/// IP-Adresse an einen Fremdserver kann bei Betreibern in der EU zum Problem werden (Google-Fonts-Urteil). Textlinks
/// (<c>&lt;a href&gt;</c>) sind erlaubt, weil sie erst nach einem Klick etwas übertragen.
/// </summary>
public partial class NoThirdPartyResourcesTests
{
    private static readonly string[] SourceExtensions = [".razor", ".css", ".js", ".html", ".cshtml"];

    // Schrift, Skript, Stylesheet, Bild, Frame, Medium: jede Ressource, die der Browser von selbst nachlädt.
    [GeneratedRegex(@"<(?!a[\s>])\w+[^>]*\b(?:src|href|data|poster|srcset)\s*=\s*[""']\s*(?:https?:)?//", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalAttribute();

    [GeneratedRegex(@"(?:@import|url\()\s*[""']?\s*(?:https?:)?//", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalCssReference();

    [GeneratedRegex(@"<iframe\b|rel\s*=\s*[""'](?:preconnect|dns-prefetch|prefetch|prerender)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenTagOrHint();

    [GeneratedRegex(@"\b(?:fetch|WebSocket|EventSource|XMLHttpRequest)\b[^;\n]*[""']\s*(?:https?|wss?):", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalScriptCall();

    private static IEnumerable<string> FindViolations(string content)
    {
        foreach (var regex in new[] { ExternalAttribute(), ExternalCssReference(), ForbiddenTagOrHint(), ExternalScriptCall() })
            foreach (Match match in regex.Matches(content))
                yield return match.Value.Length > 80 ? match.Value[..80] : match.Value;
    }

    private static string FindAppRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wunschkino.slnx")))
            directory = directory.Parent;

        return directory is null
            ? throw new InvalidOperationException("Wunschkino.slnx wurde über dem Testordner nicht gefunden.")
            : Path.Combine(directory.FullName, "Wunschkino");
    }

    [Fact]
    public void TheAppSources_LoadNothingFromThirdParties()
    {
        var appRoot = FindAppRoot();
        var folders = new[] { Path.Combine(appRoot, "Components"), Path.Combine(appRoot, "wwwroot") };

        var files = folders.Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            .Where(file => SourceExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(files);

        var violations = files
            .SelectMany(file => FindViolations(File.ReadAllText(file)).Select(v => $"{Path.GetRelativePath(appRoot, file)}: {v}"))
            .ToList();

        Assert.True(violations.Count == 0, "Externe Ressourcen im Frontend:\n" + string.Join("\n", violations));
    }

    [Theory]
    [InlineData("<link href=\"https://fonts.googleapis.com/css2?family=Roboto\" rel=\"stylesheet\" />")]
    [InlineData("<script src=\"https://cdn.example.org/lib.js\"></script>")]
    [InlineData("<script src='//cdn.example.org/lib.js'></script>")]
    [InlineData("<img src=\"http://example.org/pixel.gif\">")]
    [InlineData("<MudImage Src=\"https://example.org/a.png\" />")]
    [InlineData("@import url('https://fonts.googleapis.com/css2?family=Inter');")]
    [InlineData("body { background: url(https://example.org/bg.png); }")]
    [InlineData("<link rel=\"preconnect\" href=\"/x\">")]
    [InlineData("<link rel=\"dns-prefetch\" href=\"/x\">")]
    [InlineData("<iframe src=\"/x\"></iframe>")]
    [InlineData("const s = new WebSocket(\"wss://example.org/socket\");")]
    public void TheScanner_FlagsEveryWayOfLoadingThirdPartyContent(string sample)
    {
        Assert.NotEmpty(FindViolations(sample));
    }

    [Theory]
    [InlineData("<a href=\"https://www.themoviedb.org/\" target=\"_blank\">TMDB</a>")]
    [InlineData("<MudTextField Placeholder=\"http://192.168.1.10:8096\" />")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"></svg>")]
    [InlineData("<link href=\"app.css\" rel=\"stylesheet\" />")]
    [InlineData("body { background: url(images/tmdb-logo.svg); }")]
    public void TheScanner_AllowsTextLinksPlaceholdersAndOwnFiles(string sample)
    {
        Assert.Empty(FindViolations(sample));
    }
}
