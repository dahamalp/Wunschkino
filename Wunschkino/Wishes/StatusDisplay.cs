using MudBlazor;
using Wunschkino.Data;
using Wunschkino.Resources;

namespace Wunschkino.Wishes;

public static class StatusDisplay
{
    public static string Label(WishStatus status) => status switch
    {
        WishStatus.Open => Texts.Status_Open,
        WishStatus.Accepted => Texts.Status_Accepted,
        WishStatus.Rejected => Texts.Status_Rejected,
        WishStatus.Done => Texts.Status_Done,
        _ => status.ToString()
    };

    public static Color Color(WishStatus status) => status switch
    {
        WishStatus.Accepted => MudBlazor.Color.Warning,
        WishStatus.Rejected => MudBlazor.Color.Error,
        WishStatus.Done => MudBlazor.Color.Success,
        _ => MudBlazor.Color.Default
    };

    /// <summary>Symbol neben dem Text, damit der Status nie nur über die Farbe erkennbar ist.</summary>
    public static string Icon(WishStatus status) => status switch
    {
        WishStatus.Accepted => Icons.Material.Filled.Timer,
        WishStatus.Rejected => Icons.Material.Filled.Cancel,
        WishStatus.Done => Icons.Material.Filled.CheckCircle,
        _ => Icons.Material.Filled.HourglassEmpty
    };
}
