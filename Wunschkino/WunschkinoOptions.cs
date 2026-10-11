using System.Net;
using System.Text.RegularExpressions;
using Wunschkino.DatabaseBackup;

namespace Wunschkino;

/// <summary>Adressen, von denen die App <c>X-Forwarded-*</c>-Header übernimmt (der Reverse-Proxy davor).</summary>
public sealed record TrustedProxies(IReadOnlyList<IPAddress> Addresses, IReadOnlyList<IPNetwork> Networks)
{
    public static readonly TrustedProxies None = new([], []);
}

public sealed partial class WunschkinoOptions
{
    public const string SectionName = "Wunschkino";

    /// <summary>Ablage für SQLite-DB, Data-Protection-Schlüssel und Setup-Token. Produktiv: /var/lib/wunschkino.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>HTTP-Port, auf dem die App lauscht. Ohne Angabe gelten die üblichen ASP.NET-Einstellungen (<c>--urls</c>, <c>ASPNETCORE_URLS</c>, launchSettings).</summary>
    public int? Port { get; set; }

    /// <summary>Adresse, auf der die App lauscht (IP, <c>localhost</c> oder <c>*</c> für alle Schnittstellen). Gilt nur zusammen mit <see cref="Port"/>.</summary>
    public string BindAddress { get; set; } = "*";

    /// <summary>
    /// Öffentlicher Hostname der App, mehrere durch Komma getrennt (zum Beispiel <c>wunsch.example.org</c>). Ohne Angabe
    /// akzeptiert die App jeden Host. <c>localhost</c> bleibt immer erlaubt, damit der Health-Check funktioniert.
    /// </summary>
    public string? Domain { get; set; }

    /// <summary>
    /// IP-Adressen oder Netze (CIDR, etwa <c>10.0.0.5,192.168.1.0/24</c>) des Reverse-Proxys, getrennt durch Komma oder Leerzeichen.
    /// Nur von diesen übernimmt die App die echte Client-IP und das Protokoll; Loopback ist immer vertraut.
    /// </summary>
    public string? TrustedProxies { get; set; }

    /// <summary>
    /// Das Anmelde-Cookie nur über HTTPS senden (Standard: ja, außer in Development). Der Browser verwirft es sonst bei
    /// Klartext-HTTP, die Anmeldung schlägt dann stillschweigend fehl. Nur abschalten, wenn die App bewusst ohne HTTPS im
    /// eigenen Netz läuft – hinter einem TLS-Proxy bleibt es eingeschaltet. Das Antiforgery-Cookie folgt immer der Anfrage:
    /// ASP.NET wirft sonst bei jeder nicht als HTTPS erkannten Anfrage einen 500er.
    /// </summary>
    public bool SecureCookies { get; set; } = true;

    /// <summary>
    /// Anfragen pro Minute und Client-Adresse, bevor die App weitere abweist und die Adresse nach der Stufenleiter der
    /// Fehlversuche sperrt. Hoch genug für ein Poster-Raster mit mehreren Nutzern hinter einer Adresse; 0 schaltet das
    /// allgemeine Limit ab (das Login-Limit bleibt).
    /// </summary>
    public int RequestLimitPerMinute { get; set; } = 1500;

    /// <summary>
    /// Erlaubt ausgehende Verbindungen zu Link-Local- und Cloud-Metadaten-Adressen (169.254.0.0/16, fe80::/10 u. a.). Standard:
    /// aus, damit eine eingetragene Jellyfin-Adresse nicht auf Metadaten-Dienste des Servers zeigen kann. Nur einschalten, wenn
    /// Jellyfin wirklich unter einer solchen Adresse läuft.
    /// </summary>
    public bool AllowLinkLocalUrls { get; set; }

    public const int DefaultSessionMaxDays = 30;

    /// <summary>
    /// Wie lange eine Anmeldung höchstens gilt, auch wenn jemand die Seite täglich nutzt (1 bis 365 Tage, Standard 30). Das Cookie
    /// verlängert sich bei Nutzung selbst; diese Grenze verhindert, dass ein gestohlenes Cookie dadurch ewig lebt.
    /// </summary>
    public int SessionMaxDays { get; set; } = DefaultSessionMaxDays;

    /// <summary>Lehnt neue Passwörter ab, die auf einer eingebauten Liste häufiger Passwörter stehen (Standard: an, rein lokal).</summary>
    public bool CheckCommonPasswords { get; set; } = true;

    /// <summary>
    /// Ordner der täglichen Datenbanksicherungen. Ohne Angabe <c>&lt;Datenordner&gt;/backups</c>. Besser ein anderes Laufwerk: Auf
    /// demselben schützt eine Sicherung nicht vor einem Plattenausfall (die Sicherheitscheck-Seite weist darauf hin). Er darf nicht
    /// im Schlüsselordner <c>keys</c> liegen.
    /// </summary>
    public string? BackupDirectory { get; set; }

    /// <summary>So viele Sicherungen bleiben liegen (1 bis 100), ältere werden gelöscht.</summary>
    public int BackupKeepCount { get; set; } = 7;

    /// <summary>Ab so vielen Tagen ohne neue Sicherung warnt die App (1 bis 365).</summary>
    public int BackupWarnAfterDays { get; set; } = 7;

    /// <summary>
    /// Gemeinsamer SQLite-Verbindungspool (Standard: an). Die Tests schalten ihn aus: <c>SqliteConnection.ClearAllPools()</c> wirkt im
    /// ganzen Prozess und schloss sonst Verbindungen, die ein parallel laufender Test gerade benutzte.
    /// </summary>
    public bool SqlitePooling { get; set; } = true;

    public const string DefaultSourceUrl = "https://github.com/dahamalp/Wunschkino";

    /// <summary>
    /// Adresse des Quelltexts, die die App allen Nutzern anzeigt (AGPL-3.0, Abschnitt 13). Wer eine geänderte Version betreibt,
    /// stellt sie auf sein eigenes Repository um; das Install-Script leitet sie aus <c>--repo</c> ab. Nur <c>https://</c>.
    /// </summary>
    public string SourceUrl { get; set; } = DefaultSourceUrl;

    /// <summary>Kurzformen für die Kommandozeile, zum Beispiel <c>--port 8080</c> statt <c>--Wunschkino:Port=8080</c>.</summary>
    public static Dictionary<string, string> CreateSwitchMappings() =>
        new()
        {
            ["--port"] = $"{SectionName}:{nameof(Port)}",
            ["--bind"] = $"{SectionName}:{nameof(BindAddress)}",
            ["--domain"] = $"{SectionName}:{nameof(Domain)}",
            ["--proxy"] = $"{SectionName}:{nameof(TrustedProxies)}",
            ["--secure-cookies"] = $"{SectionName}:{nameof(SecureCookies)}",
            ["--source-url"] = $"{SectionName}:{nameof(SourceUrl)}"
        };

    /// <summary>
    /// Größe des Arbeitsspeichers für Jellyfin-Poster in Megabyte (0 bis 1024, Standard 64; 0 schaltet ihn ab). Er entlastet Jellyfin,
    /// wenn dieselben Poster oft abgerufen werden.
    /// </summary>
    public int PosterCacheMegabytes { get; set; } = 64;

    /// <exception cref="InvalidOperationException">Bei einer Sitzungsdauer außerhalb von 1 bis 365 Tagen oder einem Poster-Speicher außerhalb von 0 bis 1024 MB.</exception>
    public void ValidateRuntimeSettings()
    {
        if (SessionMaxDays is < 1 or > 365)
            throw new InvalidOperationException($"Wunschkino:SessionMaxDays muss zwischen 1 und 365 liegen (erhalten: {SessionMaxDays}).");
        if (PosterCacheMegabytes is < 0 or > 1024)
            throw new InvalidOperationException($"Wunschkino:PosterCacheMegabytes muss zwischen 0 und 1024 liegen (erhalten: {PosterCacheMegabytes}).");
    }

    public string ResolveDataDirectory(string contentRootPath) =>
        Path.GetFullPath(Path.Combine(contentRootPath, DataDirectory));

    /// <exception cref="InvalidOperationException">Bei ungültigen Sicherungs-Einstellungen.</exception>
    public BackupSettings ResolveBackup(string dataDirectory)
    {
        if (BackupKeepCount is < 1 or > 100)
            throw new InvalidOperationException($"Wunschkino:BackupKeepCount muss zwischen 1 und 100 liegen (erhalten: {BackupKeepCount}).");
        if (BackupWarnAfterDays is < 1 or > 365)
            throw new InvalidOperationException($"Wunschkino:BackupWarnAfterDays muss zwischen 1 und 365 liegen (erhalten: {BackupWarnAfterDays}).");

        var data = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directory = string.IsNullOrWhiteSpace(BackupDirectory)
            ? Path.Combine(data, "backups")
            : Path.GetFullPath(BackupDirectory, data).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var keys = Path.Combine(data, "keys");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (directory.Equals(data, comparison)
            || directory.Equals(keys, comparison)
            || directory.StartsWith(keys + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidOperationException(
                "Wunschkino:BackupDirectory darf weder der Datenordner selbst noch der Schlüsselordner (keys) sein: " +
                "Sicherung und Schlüssel dürfen nicht ungeschützt zusammen liegen.");
        }

        return new BackupSettings(Path.Combine(data, "wunschkino.db"), directory, BackupKeepCount, BackupWarnAfterDays);
    }

    /// <returns>Die Lausch-Adresse oder null, wenn weder Port noch Adresse angegeben sind.</returns>
    /// <exception cref="InvalidOperationException">Bei ungültigem Port oder ungültiger Adresse.</exception>
    public string? ResolveListenUrl()
    {
        var bindGiven = !string.IsNullOrWhiteSpace(BindAddress) && BindAddress != "*";
        if (Port is null)
        {
            if (bindGiven)
                throw new InvalidOperationException("--bind gilt nur zusammen mit --port (zum Beispiel --bind 127.0.0.1 --port 8080).");
            return null;
        }
        if (Port is < 1 or > 65535)
            throw new InvalidOperationException($"Ungültiger Port {Port}. Erlaubt sind Werte von 1 bis 65535 (zum Beispiel --port 8080).");

        var host = (BindAddress ?? "*").Trim();
        if (host.Length == 0)
            host = "*";
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            host = "localhost";
        }
        else if (host != "*")
        {
            if (!TryParseStrictIp(host, out var address))
                throw new InvalidOperationException($"Ungültige Bind-Adresse „{BindAddress}“. Erlaubt sind eine IP-Adresse, localhost oder * (alle Schnittstellen).");
            // IPv6-Adressen stehen in URLs in eckigen Klammern.
            host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        }
        // Standard ist „alle Schnittstellen“: Ein Reverse-Proxy auf einem anderen Rechner muss die App erreichen können.
        return $"http://{host}:{Port}";
    }

    /// <returns>Der Wert für <c>AllowedHosts</c> oder null, wenn keine Domain gesetzt ist.</returns>
    /// <exception cref="InvalidOperationException">Bei einem ungültigen Hostnamen.</exception>
    public string? ResolveAllowedHosts()
    {
        if (string.IsNullOrWhiteSpace(Domain))
            return null;

        var hosts = SplitList(Domain).ToList();
        foreach (var host in hosts)
        {
            if (!HostPattern().IsMatch(host))
                throw new InvalidOperationException($"Ungültige Domain „{host}“. Erwartet wird ein Hostname wie wunsch.example.org (ohne http://, Pfad und Port).");
        }
        if (!hosts.Contains("localhost", StringComparer.OrdinalIgnoreCase))
            hosts.Add("localhost");
        return string.Join(';', hosts);
    }

    /// <returns>Die geprüfte Quelltext-Adresse; bei leerem Wert die Standardadresse.</returns>
    /// <exception cref="InvalidOperationException">Wenn die Adresse kein <c>https://</c>-Link ohne Zugangsdaten ist.</exception>
    public string ResolveSourceUrl()
    {
        var value = string.IsNullOrWhiteSpace(SourceUrl) ? DefaultSourceUrl : SourceUrl.Trim();
        // Der Wert landet als Link auf jeder Seite: Nur ein ausgeschriebenes https-Ziel ist erlaubt (kein javascript:, kein http:).
        if (value.Length > 200
            || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException($"Ungültige Quelltext-Adresse „{SourceUrl}“. Erwartet wird eine https://-Adresse ohne Zugangsdaten, zum Beispiel {DefaultSourceUrl}.");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    /// <exception cref="InvalidOperationException">Bei einer ungültigen IP-Adresse oder einem ungültigen Netz.</exception>
    public TrustedProxies ResolveTrustedProxies()
    {
        if (string.IsNullOrWhiteSpace(TrustedProxies))
            return Wunschkino.TrustedProxies.None;

        var addresses = new List<IPAddress>();
        var networks = new List<IPNetwork>();
        foreach (var entry in SplitList(TrustedProxies))
        {
            if (entry.Contains('/'))
            {
                // Die Adresse vor dem „/“ muss ausgeschrieben sein, sonst wird aus „192.168.1/24“ stillschweigend ein anderes Netz.
                var addressPart = entry[..entry.IndexOf('/')];
                // IPNetwork.TryParse rundet Host-Bits still auf die Netzadresse ab: Aus „10.0.0.5/24“ würde unbemerkt das
                // ganze /24 vertraut. Die Eingabe muss deshalb genau die Netzadresse sein.
                if (!TryParseStrictIp(addressPart, out var baseAddress)
                    || !IPNetwork.TryParse(entry, out var network)
                    || !network.BaseAddress.Equals(baseAddress))
                    throw new InvalidOperationException($"Ungültiges Proxy-Netz „{entry}“. Erwartet wird die CIDR-Schreibweise mit der Netzadresse, zum Beispiel 192.168.1.0/24.");
                networks.Add(network);
            }
            else
            {
                if (!TryParseStrictIp(entry, out var address))
                    throw new InvalidOperationException($"Ungültige Proxy-Adresse „{entry}“. Erwartet wird eine ausgeschriebene IP-Adresse, zum Beispiel 192.168.1.10.");
                addresses.Add(address);
            }
        }
        return new TrustedProxies(addresses, networks);
    }

    /// <summary>
    /// Strenger als <see cref="IPAddress.TryParse(string, out IPAddress)"/>: Das akzeptiert Kurzformen wie „192.168.1“ (= 192.168.0.1)
    /// oder „3232235777“. Bei einer Vertrauensentscheidung würde ein Tippfehler so stillschweigend eine andere Adresse vertrauen.
    /// </summary>
    private static bool TryParseStrictIp(string text, out IPAddress address)
    {
        address = IPAddress.None;
        if (text.Contains('%'))
            return false;
        if (text.Contains(':'))
            return IPAddress.TryParse(text, out var v6) && (address = v6).AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        return Ipv4Pattern().IsMatch(text) && IPAddress.TryParse(text, out address!);
    }

    private static IEnumerable<string> SplitList(string value) =>
        value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex(@"^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}\z")]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex(@"^(\*\.)?[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\z")]
    private static partial Regex HostPattern();
}
