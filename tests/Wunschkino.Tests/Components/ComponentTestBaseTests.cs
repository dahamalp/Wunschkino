using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Wunschkino.Tests.Components;

public class ComponentTestBaseTests : ComponentTestBase
{
    [Fact]
    public void MudBlazorComponents_Render_AndSnackbarMessagesAppear()
    {
        var button = RenderPage<MudButton>(parameters => parameters.AddChildContent("Hallo"));
        Assert.Contains("Hallo", button.Markup);

        Services.GetRequiredService<ISnackbar>().Add("Gespeichert", Severity.Success);

        Snackbars.WaitForAssertion(() => Assert.Contains("Gespeichert", Snackbars.Markup));
    }
}
