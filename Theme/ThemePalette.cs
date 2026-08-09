using Microsoft.UI.Xaml;

namespace Mori.Theme;

/// <summary>
/// A full set of Mori color tokens for one appearance.
/// Transcribed verbatim from the Mac app's Theme.swift — :root (light) and .dark.
/// </summary>
public class ThemePalette
{
    // ── Core tokens ──
    public TokenColor Background { get; set; }
    public TokenColor Foreground { get; set; }
    public TokenColor Card { get; set; }
    public TokenColor CardForeground { get; set; }
    public TokenColor Popover { get; set; }
    public TokenColor PopoverForeground { get; set; }
    public TokenColor Primary { get; set; }
    public TokenColor PrimaryForeground { get; set; }
    public TokenColor Secondary { get; set; }
    public TokenColor SecondaryForeground { get; set; }
    public TokenColor Muted { get; set; }
    public TokenColor MutedForeground { get; set; }
    public TokenColor Accent { get; set; }
    public TokenColor AccentForeground { get; set; }
    public TokenColor Destructive { get; set; }
    public TokenColor DestructiveForeground { get; set; }
    public TokenColor Border { get; set; }
    public TokenColor Input { get; set; }
    public TokenColor Ring { get; set; }

    // ── Sidebar channel ──
    public TokenColor Sidebar { get; set; }
    public TokenColor SidebarForeground { get; set; }
    public TokenColor SidebarPrimary { get; set; }
    public TokenColor SidebarPrimaryForeground { get; set; }
    public TokenColor SidebarAccent { get; set; }
    public TokenColor SidebarAccentForeground { get; set; }
    public TokenColor SidebarBorder { get; set; }
    public TokenColor SidebarRing { get; set; }

    // ── Status tokens ──
    public TokenColor StatusInfoFg { get; set; }
    public TokenColor StatusSuccessFg { get; set; }
    public TokenColor StatusWarningFg { get; set; }

    /// <summary>
    /// Light theme — :root block from globals.css. Exact values from Theme.swift.
    /// </summary>
    public static ThemePalette Light { get; } = new()
    {
        Background = TokenColor.FromHex("#f7f7f7"),
        Foreground = TokenColor.FromOklch(0.165, 0.018, 248.5103),
        Card = TokenColor.FromOklch(0.985, 0.0015, 220),
        CardForeground = TokenColor.FromOklch(0.165, 0.018, 248.5103),
        Popover = TokenColor.FromOklch(0.998, 0.0008, 240),
        PopoverForeground = TokenColor.FromOklch(0.165, 0.018, 248.5103),
        Primary = TokenColor.FromOklch(0.645, 0.11, 241.2),
        PrimaryForeground = TokenColor.FromOklch(1, 0, 0),
        Secondary = TokenColor.FromOklch(0.165, 0.018, 248.5103),
        SecondaryForeground = TokenColor.FromOklch(1, 0, 0),
        Muted = TokenColor.FromOklch(0.935, 0.002, 245),
        MutedForeground = TokenColor.FromOklch(0.48, 0.012, 248.5103),
        Accent = TokenColor.FromHex("#ededed"),
        AccentForeground = TokenColor.FromOklch(0.645, 0.11, 241.2),
        Destructive = TokenColor.FromOklch(0.635, 0.24, 28),
        DestructiveForeground = TokenColor.FromHex("#ededed"),
        Border = TokenColor.FromOklch(0.92, 0, 0),
        Input = TokenColor.FromHex("#ededed"),
        Ring = TokenColor.FromOklch(0.55, 0, 0),
        Sidebar = TokenColor.FromHex("#ebebeb"),
        SidebarForeground = TokenColor.FromOklch(0.165, 0.018, 248.5103),
        SidebarPrimary = TokenColor.FromOklch(0.645, 0.11, 241.2),
        SidebarPrimaryForeground = TokenColor.FromOklch(1, 0, 0),
        SidebarAccent = TokenColor.FromHex("#ffffff"),
        SidebarAccentForeground = TokenColor.FromOklch(0.165, 0.018, 248.5103),
        SidebarBorder = TokenColor.FromOklch(0.915, 0, 0),
        SidebarRing = TokenColor.FromOklch(0.55, 0, 0),
        StatusInfoFg = TokenColor.FromOklch(0.5, 0.134, 242.749),
        StatusSuccessFg = TokenColor.FromOklch(0.527, 0.154, 150.069),
        StatusWarningFg = TokenColor.FromOklch(0.555, 0.163, 48.998),
    };

    /// <summary>
    /// Dark theme — .dark block from globals.css. Neutral chrome is chroma-0.
    /// </summary>
    public static ThemePalette Dark { get; } = new()
    {
        Background = TokenColor.FromHex("#222222"),
        Foreground = TokenColor.FromHex("#E8EAED"),
        Card = TokenColor.FromOklch(0.36, 0, 0),
        CardForeground = TokenColor.FromHex("#E8EAED"),
        Popover = TokenColor.FromOklch(0.3, 0, 0),
        PopoverForeground = TokenColor.FromHex("#E8EAED"),
        Primary = TokenColor.FromOklch(0.62, 0.13, 241.5),
        PrimaryForeground = TokenColor.FromOklch(0.28, 0.008, 235),
        Secondary = TokenColor.FromHex("#E8EAED"),
        SecondaryForeground = TokenColor.FromOklch(0.3, 0, 0),
        Muted = TokenColor.FromOklch(0.36, 0, 0),
        MutedForeground = TokenColor.FromHex("#AEB6BF"),
        Accent = TokenColor.FromOklch(0.42, 0, 0),
        AccentForeground = TokenColor.FromOklch(0.62, 0.13, 241.5),
        Destructive = TokenColor.FromOklch(0.62, 0.22, 27),
        DestructiveForeground = TokenColor.FromOklch(1, 0, 0),
        Border = TokenColor.FromOklch(0.45, 0, 0),
        Input = TokenColor.FromOklch(0.4, 0.02, 240),
        Ring = TokenColor.FromOklch(0.6, 0, 0),
        Sidebar = TokenColor.FromHex("#151515"),
        SidebarForeground = TokenColor.FromHex("#F1F3F5"),
        SidebarPrimary = TokenColor.FromOklch(0.6, 0.12, 241),
        SidebarPrimaryForeground = TokenColor.FromOklch(0.28, 0.008, 235),
        SidebarAccent = TokenColor.FromHex("#2a2a2a"),
        SidebarAccentForeground = TokenColor.FromOklch(0.62, 0.13, 241.5),
        SidebarBorder = TokenColor.FromOklch(0.48, 0, 0),
        SidebarRing = TokenColor.FromOklch(0.6, 0, 0),
        StatusInfoFg = TokenColor.FromOklch(0.746, 0.16, 232.661),
        StatusSuccessFg = TokenColor.FromOklch(0.792, 0.209, 151.711),
        StatusWarningFg = TokenColor.FromOklch(0.828, 0.189, 84.429),
    };

    /// <summary>
    /// Return palette for the given element theme.
    /// </summary>
    public static ThemePalette ForTheme(ElementTheme theme)
        => theme == ElementTheme.Dark ? Dark : Light;
}

/// <summary>
/// Radius scale. Base --radius: 0.4rem ≈ 6.4px. Exact port from Theme.swift.
/// </summary>
public static class MoriRadius
{
    public const double Base = 6.4;
    public const double Sm = 2.4;
    public const double Md = 4.4;
    public const double Lg = 6.4;
    public const double Xl = 10.4;
    public const double Button = 10;
    public const double Popover = 12;
    public const double Window = 10;  // Arc-style floating web card
}

/// <summary>
/// Typography scale. Base interactive text is 13px; labels 12px. Port from Theme.swift.
/// </summary>
public static class MoriTypography
{
    public const double Base = 13;
    public const double Label = 12;
    public const double Small = 11;
    public const double Title = 13;

    /// <summary>
    /// The primary UI font family — Segoe UI Variable on Windows 11 (equivalent of SF Pro on Mac).
    /// </summary>
    public const string FontFamily = "Segoe UI Variable";
    public const string MonoFontFamily = "Cascadia Code";
}

/// <summary>
/// Motion tokens (MASTER §3): snappy easing, 150ms default.
/// </summary>
public static class MoriMotion
{
    public static readonly TimeSpan Snappy = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan State = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan Reveal = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// Visual language for the sidebar tab/tile surface. Port of TabSurface from
/// Theme.swift.
///
/// <para>
/// The fills are deliberately plain white/black alphas rather than Fluent theme
/// brushes: on the Mac these sit over the translucent sidebar material, and
/// substituting <c>SubtleFillColorSecondaryBrush</c> or
/// <c>CardBackgroundFillColorDefaultBrush</c> gives a visibly different (and
/// heavier) selection than the Mac's soft translucent lift.
/// </para>
/// </summary>
public static class TabSurface
{
    public const double Radius = 10;
    public const double PressScale = 0.985;
    public const double ShadowRadius = 1.5;
    public const double ShadowY = 0.8;

    /// <summary>Faint resting fill for pinned/icon tiles.</summary>
    public static TokenColor TileRestFill(bool isDark)
        => isDark ? White(0.06) : Black(0.05);

    /// <summary>Translucent fill for the selected item.</summary>
    public static TokenColor SelectedFill(bool isDark)
        => isDark ? White(0.18) : White(0.85);

    /// <summary>Quiet overlay on hover.</summary>
    public static TokenColor HoverFill(bool isDark)
        => isDark ? White(0.10) : Black(0.07);

    /// <summary>Soft elevation shadow under the selected item.</summary>
    public static TokenColor Shadow(bool isDark)
        => isDark ? Black(0.05) : Black(0.15);

    /// <summary>
    /// Hover wash used by the folder header and New Tab rows
    /// (<c>p.foreground.opacity(0.05)</c> on the Mac).
    /// </summary>
    public static TokenColor RowHoverFill(ThemePalette p) => p.Foreground.WithOpacity(0.05);

    private static TokenColor White(double a) => new(1, 1, 1, a);
    private static TokenColor Black(double a) => new(0, 0, 0, a);
}
