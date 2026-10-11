using Microsoft.AspNetCore.Http;
using Wunschkino.Setup;

namespace Wunschkino.Tests.Setup;

public class SetupRedirectMiddlewareTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/wuensche")]
    public async Task BeforeSetup_RedirectsPagesToSetup(string path)
    {
        var (context, nextCalled) = await InvokeAsync(path, completed: false);

        Assert.False(nextCalled());
        Assert.Equal("/setup", context.Response.Headers.Location);
    }

    [Theory]
    [InlineData("/setup")]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/_blazor")]
    [InlineData("/app.css")]
    [InlineData("/health")]
    public async Task BeforeSetup_LetsSetupAndInfrastructureThrough(string path)
    {
        var (_, nextCalled) = await InvokeAsync(path, completed: false);

        Assert.True(nextCalled());
    }

    [Fact]
    public async Task AfterSetup_RedirectsSetupToHome()
    {
        var (context, nextCalled) = await InvokeAsync("/setup", completed: true);

        Assert.False(nextCalled());
        Assert.Equal("/", context.Response.Headers.Location);
    }

    [Fact]
    public async Task AfterSetup_LetsPagesThrough()
    {
        var (_, nextCalled) = await InvokeAsync("/", completed: true);

        Assert.True(nextCalled());
    }

    private static async Task<(HttpContext Context, Func<bool> NextCalled)> InvokeAsync(string path, bool completed)
    {
        var state = new SetupState();
        if (completed)
            state.MarkCompleted();

        var called = false;
        var middleware = new SetupRedirectMiddleware(_ => { called = true; return Task.CompletedTask; }, state);
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context);
        return (context, () => called);
    }
}
