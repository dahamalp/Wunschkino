using Wunschkino.Data;
using Wunschkino.Resources;

namespace Wunschkino.Wishes;

public static class PriorityDisplay
{
    public static readonly IReadOnlyList<WishPriority> All = [WishPriority.Low, WishPriority.Normal, WishPriority.High];

    public static string Label(WishPriority priority) => priority switch
    {
        WishPriority.Low => Texts.Priority_Low,
        WishPriority.High => Texts.Priority_High,
        _ => Texts.Priority_Normal
    };
}
