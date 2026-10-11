using MudBlazor;
using MudBlazor.Utilities;

namespace Wunschkino.Components.Theme;

/// <summary>Zinkgrau mit einem einzigen Akzent (Projektor-Orange); die Farben sind auf 4,5:1 Kontrast für Text ausgelegt.</summary>
public static class WunschkinoTheme
{
    private static readonly string[] FontStack = ["Geist", "system-ui", "-apple-system", "Segoe UI", "Roboto", "sans-serif"];

    public static MudTheme Instance { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#b83f00",
            PrimaryContrastText = "#ffffff",
            Secondary = "#55555e",
            SecondaryContrastText = "#ffffff",
            Background = "#f6f6f7",
            Surface = "#ffffff",
            AppbarBackground = "#f6f6f7",
            AppbarText = "#17171a",
            DrawerBackground = "#ffffff",
            DrawerText = "#17171a",
            TextPrimary = "#17171a",
            TextSecondary = "#55555e",
            TextDisabled = "#8a8a94",
            ActionDefault = "#55555e",
            BackgroundGray = "#ececef",
            Divider = "#d9d9de",
            DividerLight = "#ececef",
            LinesDefault = "#d9d9de",
            LinesInputs = "#8a8a94",
            TableLines = "#d9d9de",
            Success = "#17692f",
            SuccessContrastText = "#ffffff",
            Warning = "#8a5a00",
            WarningContrastText = "#ffffff",
            Error = "#b3261e",
            ErrorContrastText = "#ffffff",
            Info = "#1d4f91",
            InfoContrastText = "#ffffff"
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#ff8a3d",
            PrimaryContrastText = "#1a0d00",
            Secondary = "#a6a6b0",
            SecondaryContrastText = "#121214",
            Background = "#121214",
            Surface = "#1b1b1f",
            AppbarBackground = "#121214",
            AppbarText = "#ededf0",
            DrawerBackground = "#1b1b1f",
            DrawerText = "#ededf0",
            TextPrimary = "#ededf0",
            TextSecondary = "#a6a6b0",
            TextDisabled = "#6f6f7a",
            ActionDefault = "#a6a6b0",
            BackgroundGray = "#26262c",
            Divider = "#34343b",
            DividerLight = "#26262c",
            LinesDefault = "#34343b",
            LinesInputs = "#6f6f7a",
            TableLines = "#34343b",
            Success = "#6fd48c",
            SuccessContrastText = "#0b1f12",
            Warning = "#f0c15a",
            WarningContrastText = "#241a00",
            Error = "#ff8f87",
            ErrorContrastText = "#2a0907",
            Info = "#8fbaf5",
            InfoContrastText = "#0a1a30"
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "12px",
            AppbarHeight = "64px"
        },
        Typography = CreateTypography()
    };

    private static Typography CreateTypography() => new()
    {
        Default = new DefaultTypography { FontFamily = FontStack },
        H1 = Heading(new H1Typography(), "2.25rem", "1.15"),
        H2 = Heading(new H2Typography(), "1.875rem", "1.2"),
        H3 = Heading(new H3Typography(), "1.8125rem", "1.2"),
        H4 = Heading(new H4Typography(), "1.75rem", "1.2"),
        H5 = Heading(new H5Typography(), "1.375rem", "1.3"),
        H6 = Heading(new H6Typography(), "1.125rem", "1.35"),
        Subtitle1 = new Subtitle1Typography { FontFamily = FontStack },
        Subtitle2 = new Subtitle2Typography { FontFamily = FontStack },
        Body1 = new Body1Typography { FontFamily = FontStack },
        Body2 = new Body2Typography { FontFamily = FontStack },
        Button = new ButtonTypography { FontFamily = FontStack, TextTransform = "none", FontWeight = "600", LetterSpacing = "0" },
        Caption = new CaptionTypography { FontFamily = FontStack },
        Overline = new OverlineTypography { FontFamily = FontStack }
    };

    private static T Heading<T>(T typography, string size, string lineHeight) where T : BaseTypography
    {
        typography.FontFamily = FontStack;
        typography.FontSize = size;
        typography.FontWeight = "650";
        typography.LineHeight = lineHeight;
        typography.LetterSpacing = "-0.02em";
        return typography;
    }

    /// <summary>
    /// Seiten ohne laufende Verbindung (Anmeldung) rendert MudBlazor statisch und damit immer hell. Dieses Stylesheet legt die dunkle
    /// Palette über die hellen Variablen, wenn der Browser Dunkel bevorzugt.
    /// </summary>
    public static string SystemDarkStyle { get; } = BuildSystemDarkStyle();

    private static string BuildSystemDarkStyle()
    {
        var dark = Instance.PaletteDark;
        var variables = new (string Name, MudColor Color)[]
        {
            ("primary", dark.Primary), ("primary-text", dark.PrimaryContrastText), ("secondary", dark.Secondary),
            ("secondary-text", dark.SecondaryContrastText), ("background", dark.Background), ("background-gray", dark.BackgroundGray),
            ("surface", dark.Surface), ("appbar-background", dark.AppbarBackground), ("appbar-text", dark.AppbarText),
            ("drawer-background", dark.DrawerBackground), ("drawer-text", dark.DrawerText), ("text-primary", dark.TextPrimary),
            ("text-secondary", dark.TextSecondary), ("text-disabled", dark.TextDisabled), ("action-default", dark.ActionDefault),
            ("divider", dark.Divider), ("divider-light", dark.DividerLight), ("lines-default", dark.LinesDefault),
            ("lines-inputs", dark.LinesInputs), ("table-lines", dark.TableLines), ("success", dark.Success),
            ("success-text", dark.SuccessContrastText), ("warning", dark.Warning), ("warning-text", dark.WarningContrastText),
            ("error", dark.Error), ("error-text", dark.ErrorContrastText), ("info", dark.Info), ("info-text", dark.InfoContrastText)
        };
        var body = string.Concat(variables.Select(v => $"--mud-palette-{v.Name}:{v.Color.ToString(MudColorOutputFormats.RGBA)};"));
        return $"@media (prefers-color-scheme: dark){{:root{{color-scheme:dark;{body}}}}}";
    }
}
