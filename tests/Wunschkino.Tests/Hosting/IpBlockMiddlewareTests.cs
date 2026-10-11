using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wunschkino;
using Wunschkino.Auth;
using Wunschkino.Hosting;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Hosting;

public class IpBlockMiddlewareTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly ListLogger _log = new();

    public void Dispose() => _db.Dispose();

    private IpBlockMiddleware Create(TrustedProxies? trusted = null, Action? onNext = null) =>
        new(_ => { onNext?.Invoke(); return Task.CompletedTask; },
            new IpBlockService(_db, _time, trusted ?? TrustedProxies.None, NullLogger<IpBlockService>.Instance),
            _time,
            _log);

    private static DefaultHttpContext Request(string remote, string? forwardedFor = null, string? originalFor = null, string? fetchSite = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Connection.LocalIpAddress = IPAddress.Parse("192.168.1.5");
        if (forwardedFor is not null) context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        if (originalFor is not null) context.Request.Headers["X-Original-For"] = originalFor;
        if (fetchSite is not null) context.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        return context;
    }

    [Fact]
    public async Task AnUntrustedSenderThatSendsForwardedFor_IsReportedOnce_ThenAgainAfterAnHour()
    {
        var middleware = Create();

        await middleware.InvokeAsync(Request("203.0.113.5", forwardedFor: "198.51.100.7"));
        await middleware.InvokeAsync(Request("203.0.113.5", forwardedFor: "198.51.100.8"));
        Assert.Single(_log.Messages);
        Assert.StartsWith("ProxyNotTrusted remote=203.0.113.5", _log.Messages[0]);

        _time.Advance(TimeSpan.FromMinutes(61));
        await middleware.InvokeAsync(Request("203.0.113.5", forwardedFor: "198.51.100.9"));

        Assert.Equal(2, _log.Messages.Count);
    }

    [Fact]
    public async Task EveryUntrustedProxyHit_IsRemembered_EvenWhenTheLogLineIsThrottled()
    {
        var state = new ProxyWarningState();
        var middleware = new IpBlockMiddleware(_ => Task.CompletedTask,
            new IpBlockService(_db, _time, TrustedProxies.None, NullLogger<IpBlockService>.Instance), _time, _log, state);

        await middleware.InvokeAsync(Request("203.0.113.5", forwardedFor: "198.51.100.7"));
        _time.Advance(TimeSpan.FromMinutes(10));
        await middleware.InvokeAsync(Request("203.0.113.6", forwardedFor: "198.51.100.8"));

        Assert.Single(_log.Messages);
        var seen = state.LastSeenSince(_time.GetUtcNow().UtcDateTime.AddMinutes(-1));
        Assert.Equal(IPAddress.Parse("203.0.113.6"), seen!.Value.Remote);
    }

    [Fact]
    public async Task NothingIsRemembered_ForTrustedProxiesAppliedHeadersOrPlainRequests()
    {
        var state = new ProxyWarningState();
        var middleware = new IpBlockMiddleware(_ => Task.CompletedTask,
            new IpBlockService(_db, _time, new TrustedProxies([IPAddress.Parse("10.0.0.5")], []), NullLogger<IpBlockService>.Instance), _time, _log, state);

        await middleware.InvokeAsync(Request("10.0.0.5", forwardedFor: "198.51.100.7"));
        await middleware.InvokeAsync(Request("198.51.100.7", forwardedFor: "10.1.1.1", originalFor: "198.51.100.7"));
        await middleware.InvokeAsync(Request("203.0.113.5"));

        Assert.Null(state.LastSeenSince(DateTime.MinValue));
    }

    private static DefaultHttpContext LoopbackRequest(string host, string? forwardedFor = null, string? originalFor = null)
    {
        var context = Request("127.0.0.1", forwardedFor, originalFor);
        context.Request.Host = new HostString(host);
        return context;
    }

    [Theory]
    [InlineData("wishes.example.org")]
    [InlineData("192.168.1.5:8080")]
    public async Task ALoopbackRequestWithAForeignHostAndNoForwardedFor_IsRemembered(string host)
    {
        var state = new ProxyWarningState();
        var middleware = new IpBlockMiddleware(_ => Task.CompletedTask,
            new IpBlockService(_db, _time, TrustedProxies.None, NullLogger<IpBlockService>.Instance), _time, _log, state);

        await middleware.InvokeAsync(LoopbackRequest(host));

        Assert.NotNull(state.LoopbackWithoutForwardingSince(DateTime.MinValue));
    }

    [Theory]
    [InlineData("localhost:8080", null, null)]
    [InlineData("127.0.0.1:8080", null, null)]
    [InlineData("[::1]:8080", null, null)]
    [InlineData("wishes.example.org", "198.51.100.7", null)]
    [InlineData("wishes.example.org", null, "198.51.100.7")]
    public async Task DirectLocalAccessAndForwardedRequests_AreNotRemembered(string host, string? forwardedFor, string? originalFor)
    {
        var state = new ProxyWarningState();
        var middleware = new IpBlockMiddleware(_ => Task.CompletedTask,
            new IpBlockService(_db, _time, TrustedProxies.None, NullLogger<IpBlockService>.Instance), _time, _log, state);

        await middleware.InvokeAsync(LoopbackRequest(host, forwardedFor, originalFor));

        Assert.Null(state.LoopbackWithoutForwardingSince(DateTime.MinValue));
    }

    [Fact]
    public async Task ARemoteRequestWithAForeignHost_IsNotRememberedAsLoopbackWithoutForwarding()
    {
        var state = new ProxyWarningState();
        var middleware = new IpBlockMiddleware(_ => Task.CompletedTask,
            new IpBlockService(_db, _time, TrustedProxies.None, NullLogger<IpBlockService>.Instance), _time, _log, state);
        var context = Request("203.0.113.5");
        context.Request.Host = new HostString("wishes.example.org");

        await middleware.InvokeAsync(context);

        Assert.Null(state.LoopbackWithoutForwardingSince(DateTime.MinValue));
    }

    [Fact]
    public async Task ForwardedHeadersThatWereApplied_AreNotReported()
    {
        var middleware = Create();

        await middleware.InvokeAsync(Request("198.51.100.7", forwardedFor: "10.1.1.1", originalFor: "198.51.100.7"));

        Assert.Empty(_log.Messages);
    }

    [Fact]
    public async Task RequestsWithoutForwardedFor_AreNotReported()
    {
        await Create().InvokeAsync(Request("203.0.113.5"));

        Assert.Empty(_log.Messages);
    }

    [Fact]
    public async Task TheServerItselfAndTrustedProxies_AreNotReported()
    {
        var middleware = Create(new TrustedProxies([IPAddress.Parse("10.0.0.5")], []));

        await middleware.InvokeAsync(Request("127.0.0.1", forwardedFor: "198.51.100.7"));
        await middleware.InvokeAsync(Request("192.168.1.5", forwardedFor: "198.51.100.7"));
        await middleware.InvokeAsync(Request("10.0.0.5", forwardedFor: "198.51.100.7"));

        Assert.Empty(_log.Messages);
    }

    [Fact]
    public async Task ABlockedAddress_IsNotPassedOn()
    {
        var blocks = new IpBlockService(_db, _time, TrustedProxies.None, NullLogger<IpBlockService>.Instance);
        for (var i = 0; i < 3; i++)
            await blocks.RegisterFailureAsync(IPAddress.Parse("203.0.113.5"), IPAddress.Parse("192.168.1.5"));
        var passed = false;
        var middleware = new IpBlockMiddleware(_ => { passed = true; return Task.CompletedTask; }, blocks, _time, _log);

        var context = Request("203.0.113.5");
        await middleware.InvokeAsync(context);

        Assert.False(passed);
        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("cross-site", true)]
    [InlineData("Cross-Site", true)]
    [InlineData("same-origin", false)]
    [InlineData("same-site", false)]
    [InlineData("none", false)]
    [InlineData(null, false)]
    public void OnlyRequestsFromForeignSites_AreRecognizedAsCrossSite(string? header, bool expected)
    {
        Assert.Equal(expected, IpBlockMiddleware.IsCrossSite(Request("203.0.113.5", fetchSite: header).Request));
    }

    private sealed class ListLogger : ILogger<IpBlockMiddleware>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
