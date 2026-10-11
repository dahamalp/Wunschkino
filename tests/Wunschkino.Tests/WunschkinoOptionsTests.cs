using Microsoft.Extensions.Configuration;

namespace Wunschkino.Tests;

public class WunschkinoOptionsTests
{
    private static WunschkinoOptions Bind(params string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddCommandLine(args, WunschkinoOptions.CreateSwitchMappings())
            .Build();
        return configuration.GetSection(WunschkinoOptions.SectionName).Get<WunschkinoOptions>() ?? new WunschkinoOptions();
    }

    private static string DataDir => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wk-daten"));

    [Fact]
    public void Backup_DefaultsToABackupsFolderInTheDataDirectory_WithSevenKeptAndAWeekToWarn()
    {
        var settings = new WunschkinoOptions().ResolveBackup(DataDir);

        Assert.Equal(Path.Combine(DataDir, "backups"), settings.Directory);
        Assert.Equal(Path.Combine(DataDir, "wunschkino.db"), settings.DatabasePath);
        Assert.Equal(7, settings.Keep);
        Assert.Equal(7, settings.WarnAfterDays);
    }

    [Fact]
    public void Backup_AnAbsoluteDirectory_IsTakenAsIs_ARelativeOneIsRelativeToTheDataDirectory()
    {
        var other = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wk-andere-platte"));

        Assert.Equal(other, new WunschkinoOptions { BackupDirectory = other }.ResolveBackup(DataDir).Directory);
        Assert.Equal(Path.Combine(DataDir, "sicher"), new WunschkinoOptions { BackupDirectory = "sicher" }.ResolveBackup(DataDir).Directory);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("keys")]
    [InlineData("keys/unter")]
    [InlineData("backups/../keys")]
    public void Backup_NeverInTheDataDirectoryItselfOrTheKeyFolder(string directory)
    {
        var options = new WunschkinoOptions { BackupDirectory = directory };

        var error = Assert.Throws<InvalidOperationException>(() => options.ResolveBackup(DataDir));

        Assert.Contains("Schlüssel", error.Message);
    }

    [Theory]
    [InlineData(0, 7)]
    [InlineData(101, 7)]
    [InlineData(7, 0)]
    [InlineData(7, 366)]
    public void Backup_RejectsOutOfRangeCountsAndWarnDays(int keep, int warnDays)
    {
        var options = new WunschkinoOptions { BackupKeepCount = keep, BackupWarnAfterDays = warnDays };

        Assert.Throws<InvalidOperationException>(() => options.ResolveBackup(DataDir));
    }

    [Fact]
    public void Backup_AcceptsTheBoundaries()
    {
        Assert.Equal(1, new WunschkinoOptions { BackupKeepCount = 1, BackupWarnAfterDays = 1 }.ResolveBackup(DataDir).Keep);
        Assert.Equal(100, new WunschkinoOptions { BackupKeepCount = 100, BackupWarnAfterDays = 365 }.ResolveBackup(DataDir).Keep);
    }

    [Fact]
    public void WithoutPort_NoListenUrlIsForced()
    {
        Assert.Null(Bind().Port);
        Assert.Null(Bind().ResolveListenUrl());
    }

    [Theory]
    [InlineData("--port", "8080")]
    [InlineData("--port=8080")]
    [InlineData("--Wunschkino:Port=8080")]
    public void PortArgument_IsBoundFromAllSupportedForms(params string[] args)
    {
        var options = Bind(args);

        Assert.Equal(8080, options.Port);
        Assert.Equal("http://*:8080", options.ResolveListenUrl());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public void BoundaryPorts_AreAccepted(int port) =>
        Assert.Equal($"http://*:{port}", new WunschkinoOptions { Port = port }.ResolveListenUrl());

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void PortsOutsideTheValidRange_AreRejectedWithAHelpfulMessage(int port)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new WunschkinoOptions { Port = port }.ResolveListenUrl());

        Assert.Contains("--port", exception.Message);
    }

    [Fact]
    public void NonNumericPort_FailsWhileBinding()
    {
        Assert.ThrowsAny<Exception>(() => Bind("--port", "abc"));
    }

    [Theory]
    [InlineData("127.0.0.1", "http://127.0.0.1:8080")]
    [InlineData("192.168.1.10", "http://192.168.1.10:8080")]
    [InlineData("::1", "http://[::1]:8080")]
    [InlineData("localhost", "http://localhost:8080")]
    [InlineData("LOCALHOST", "http://localhost:8080")]
    [InlineData("*", "http://*:8080")]
    public void BindAddress_IsUsedInTheListenUrl(string bind, string expected)
    {
        var options = Bind("--port", "8080", "--bind", bind);

        Assert.Equal(expected, options.ResolveListenUrl());
    }

    [Theory]
    [InlineData("kein-host.example")]
    [InlineData("999.1.1.1")]
    [InlineData("http://127.0.0.1")]
    public void InvalidBindAddress_IsRejected(string bind)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Bind("--port", "8080", "--bind", bind).ResolveListenUrl());

        Assert.Contains("Bind-Adresse", exception.Message);
    }

    [Fact]
    public void BindAddressWithoutPort_IsRejectedInsteadOfBeingSilentlyIgnored()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Bind("--bind", "127.0.0.1").ResolveListenUrl());

        Assert.Contains("--port", exception.Message);
    }

    [Fact]
    public void Domain_BecomesAllowedHosts_WithLocalhostForTheHealthCheck()
    {
        var options = Bind("--domain", "wunsch.example.org,kino.example.net");

        Assert.Equal("wunsch.example.org;kino.example.net;localhost", options.ResolveAllowedHosts());
    }

    [Fact]
    public void Domain_DoesNotDuplicateLocalhost_AndWithoutDomainNothingIsRestricted()
    {
        Assert.Equal("wunsch.example.org;localhost", Bind("--domain", "wunsch.example.org localhost").ResolveAllowedHosts());
        Assert.Null(Bind().ResolveAllowedHosts());
    }

    [Theory]
    [InlineData("https://wunsch.example.org")]
    [InlineData("wunsch.example.org/pfad")]
    [InlineData("wunsch.example.org:8080")]
    [InlineData("-bad.example.org")]
    [InlineData("a b.example.org;;;;evil/x")]
    public void InvalidDomain_IsRejected(string domain)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new WunschkinoOptions { Domain = domain }.ResolveAllowedHosts());

        Assert.Contains("Domain", exception.Message);
    }

    [Fact]
    public void TrustedProxies_AcceptAddressesAndNetworksInAnyListFormat()
    {
        var trusted = Bind("--proxy", "10.0.0.5, 192.168.1.0/24;fd00::1").ResolveTrustedProxies();

        Assert.Equal(["10.0.0.5", "fd00::1"], trusted.Addresses.Select(a => a.ToString()));
        Assert.Equal(["192.168.1.0/24"], trusted.Networks.Select(n => n.ToString()));
    }

    [Fact]
    public void WithoutProxies_NoneIsTrustedBeyondLoopback()
    {
        var trusted = Bind().ResolveTrustedProxies();

        Assert.Empty(trusted.Addresses);
        Assert.Empty(trusted.Networks);
    }

    [Theory]
    [InlineData("192.168.1")]
    [InlineData("10.1")]
    [InlineData("3232235777")]
    [InlineData("0x7f.0.0.1")]
    [InlineData("010.0.0.1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("fe80::1%eth0")]
    public void ShortenedOrAmbiguousIpForms_AreRejected_BecauseTheyWouldTrustTheWrongAddress(string entry)
    {
        Assert.Throws<InvalidOperationException>(() => new WunschkinoOptions { TrustedProxies = entry }.ResolveTrustedProxies());
        Assert.Throws<InvalidOperationException>(() => new WunschkinoOptions { Port = 80, BindAddress = entry }.ResolveListenUrl());
    }

    [Theory]
    [InlineData("192.168.1/24")]
    [InlineData("10.0.0.5/24")]
    [InlineData("10.0.0.0/33")]
    [InlineData("fd00::1/8")]
    public void CidrNetworks_NeedAFullNetworkAddressWithoutHostBits(string entry) =>
        Assert.Throws<InvalidOperationException>(() => new WunschkinoOptions { TrustedProxies = entry }.ResolveTrustedProxies());

    [Fact]
    public void Ipv6AddressesAndNetworks_AreAccepted()
    {
        var trusted = new WunschkinoOptions { TrustedProxies = "fd00::1 fd00::/8 2001:db8::/32" }.ResolveTrustedProxies();

        Assert.Equal(["fd00::1"], trusted.Addresses.Select(a => a.ToString()));
        Assert.Equal(2, trusted.Networks.Count);
    }

    [Fact]
    public void WildcardDomains_AreAccepted_AndWhitespaceOnlyDomainsMeanNoRestriction()
    {
        Assert.Equal("*.example.org;localhost", new WunschkinoOptions { Domain = "*.example.org" }.ResolveAllowedHosts());
        Assert.Null(new WunschkinoOptions { Domain = "   " }.ResolveAllowedHosts());
        Assert.Null(new WunschkinoOptions { Domain = "" }.ResolveAllowedHosts());
    }

    [Theory]
    [InlineData("*")]
    [InlineData("a..example.org")]
    [InlineData("example.org.")]
    public void MalformedDomains_AreRejected(string domain) =>
        Assert.Throws<InvalidOperationException>(() => new WunschkinoOptions { Domain = domain }.ResolveAllowedHosts());

    [Theory]
    [InlineData("kein-ip", "Proxy-Adresse")]
    [InlineData("10.0.0.256", "Proxy-Adresse")]
    [InlineData("10.0.0.0/99", "Proxy-Netz")]
    [InlineData("10.0.0.5/abc", "Proxy-Netz")]
    public void InvalidProxyEntries_AreRejectedWithTheOffendingValue(string proxies, string expectedFragment)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new WunschkinoOptions { TrustedProxies = proxies }.ResolveTrustedProxies());

        Assert.Contains(expectedFragment, exception.Message);
    }

    [Fact]
    public void SourceUrl_DefaultsToTheOriginalRepository_AndCanBeSetByArgument()
    {
        Assert.Equal("https://github.com/dahamalp/Wunschkino", Bind().ResolveSourceUrl());
        Assert.Equal("https://codeberg.org/jemand/fork", Bind("--source-url", "https://codeberg.org/jemand/fork").ResolveSourceUrl());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptySourceUrl_FallsBackToTheDefault(string value) =>
        Assert.Equal(WunschkinoOptions.DefaultSourceUrl, new WunschkinoOptions { SourceUrl = value }.ResolveSourceUrl());

    [Fact]
    public void SourceUrl_IsNormalised_WithoutTrailingSlash()
    {
        Assert.Equal("https://gitlab.example.org/gruppe/wunschkino", new WunschkinoOptions { SourceUrl = " https://gitlab.example.org/gruppe/wunschkino/ " }.ResolveSourceUrl());
    }

    [Theory]
    [InlineData("http://github.com/x/y")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("ftp://github.com/x")]
    [InlineData("https://")]
    [InlineData("https://user:pw@github.com/x/y")]
    [InlineData("https://github.com/x y")]
    [InlineData("https://github.com/x\ny")]
    [InlineData("github.com/x/y")]
    public void SourceUrl_OnlyAcceptsPlainHttpsLinks(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new WunschkinoOptions { SourceUrl = value }.ResolveSourceUrl());

        Assert.Contains("Quelltext-Adresse", exception.Message);
    }

    [Fact]
    public void SourceUrl_RejectsVeryLongValues() =>
        Assert.Throws<InvalidOperationException>(() => new WunschkinoOptions { SourceUrl = "https://example.org/" + new string('a', 200) }.ResolveSourceUrl());

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(365, 1024, true)]
    [InlineData(30, 64, true)]
    [InlineData(0, 64, false)]
    [InlineData(366, 64, false)]
    [InlineData(30, -1, false)]
    [InlineData(30, 1025, false)]
    public void RuntimeSettings_AreLimited(int sessionDays, int posterMegabytes, bool valid)
    {
        var options = new WunschkinoOptions { SessionMaxDays = sessionDays, PosterCacheMegabytes = posterMegabytes };

        if (valid)
            options.ValidateRuntimeSettings();
        else
            Assert.Throws<InvalidOperationException>(options.ValidateRuntimeSettings);
    }

    [Fact]
    public void SessionAndPosterSettings_HaveSensibleDefaults()
    {
        var options = new WunschkinoOptions();

        Assert.Equal(30, options.SessionMaxDays);
        Assert.Equal(64, options.PosterCacheMegabytes);
    }

    [Fact]
    public void SecureCookies_AreOnByDefault_AndCanBeSwitchedOffByArgument()
    {
        Assert.True(Bind().SecureCookies);
        Assert.False(Bind("--secure-cookies", "false").SecureCookies);
    }
}
