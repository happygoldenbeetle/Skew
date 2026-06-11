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
        Palette = ThemePalette.ForTheme(theme);
    }

    /// <summary>
    /// Toggle between light and dark.
    /// </summary>
    public void ToggleTheme()
    {
        SetTheme(IsDark ? ElementTheme.Light : ElementTheme.Dark);
    }
}
