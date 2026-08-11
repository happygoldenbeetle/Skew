using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace Mori.Theme;

/// <summary>
/// Singleton service providing the active theme palette to the entire UI.
/// Listens to system theme changes and provides the matching palette.
/// </summary>
public partial class ThemeService : ObservableObject
{
    private static readonly Lazy<ThemeService> _instance = new(() => new ThemeService());
    public static ThemeService Instance => _instance.Value;

    [ObservableProperty]
    private ThemePalette _palette = ThemePalette.Dark;

    [ObservableProperty]
    private ElementTheme _currentTheme = ElementTheme.Dark;

    [ObservableProperty]
    private bool _isDark = true;

    private ThemeService() { }

    /// <summary>
    /// Update the palette when theme changes.
    /// </summary>
    public void SetTheme(ElementTheme theme)
    {
        CurrentTheme = theme;
        IsDark = theme == ElementTheme.Dark;

        var palette = ThemePalette.ForTheme(theme);
        ApplyThemeColor(palette);
        Palette = palette;

        Controls.MoriBrowserView.BroadcastThemeChange(IsDark);
    }

    /// <summary>
    /// Re-read the user's accent and republish the palette, so a colour picked
    /// now lands without a theme switch.
    /// </summary>
    public void RefreshThemeColor()
    {
        var palette = ThemePalette.ForTheme(CurrentTheme);
        ApplyThemeColor(palette);

        // Same instance as before — ForTheme hands back a shared palette — so an
        // assignment alone raises nothing. The listeners are what repaint.
        Palette = palette;
        OnPropertyChanged(nameof(Palette));
    }

    /// <summary>
    /// Overwrite the palette's Primary with the user's colour, if they set one.
    ///
    /// <para>
    /// Replacing the token rather than adding one beside it is what makes this
    /// reach anything: every surface already drawn from Primary follows without
    /// being rewired.
    /// </para>
    /// </summary>
    private static void ApplyThemeColor(ThemePalette palette)
    {
        CaptureDefaultPrimaries();

        bool dark = ReferenceEquals(palette, ThemePalette.Dark);
        TokenColor fallback = dark ? _darkPrimary : _lightPrimary;
        string hex = Models.BrowserSettings.Shared.ThemeColor;

        if (string.IsNullOrWhiteSpace(hex))
        {
            palette.Primary = fallback;
            return;
        }

        try
        {
            palette.Primary = TokenColor.FromHex(hex);
        }
        catch
        {
            // A hand-edited settings file should not take the app down.
            palette.Primary = fallback;
        }
    }

    // ForTheme hands back a shared palette, so writing Primary overwrites the
    // one the theme shipped with. Kept here, or clearing the colour would have
    // nothing to go back to until a restart.
    private static TokenColor _lightPrimary;
    private static TokenColor _darkPrimary;
    private static bool _defaultsCaptured;

    private static void CaptureDefaultPrimaries()
    {
        if (_defaultsCaptured) return;
        _lightPrimary = ThemePalette.Light.Primary;
        _darkPrimary = ThemePalette.Dark.Primary;
        _defaultsCaptured = true;
    }

    /// <summary>
    /// Toggle between light and dark.
    /// </summary>
    public void ToggleTheme()
    {
        SetTheme(IsDark ? ElementTheme.Light : ElementTheme.Dark);
    }
}
