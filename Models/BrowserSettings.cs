using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace Mori.Models;

public enum NewTabBehavior { Homepage, Blank, Dashboard }
public enum SearchEngine { Google, Bing, DuckDuckGo, Brave, Custom }
public enum SidebarPosition { Left, Right }

/// <summary>
/// User-facing browser preferences. Port of BrowserSettings.swift, which backs
/// each property with UserDefaults; here they are persisted to
/// <c>settings.json</c> via <see cref="SettingsStore"/> whenever one changes.
/// </summary>
public partial class BrowserSettings : ObservableObject
{
    public static BrowserSettings Shared { get; } = new();

    [ObservableProperty]
    private string _homepageURL = "https://";

    [ObservableProperty]
    private NewTabBehavior _newTabBehavior = NewTabBehavior.Homepage;

    [ObservableProperty]
    private SearchEngine _searchEngine = SearchEngine.Google;

    [ObservableProperty]
    private string _customSearchTemplate = "https://example.com/?q={query}";

    [ObservableProperty]
    private ElementTheme _theme = ElementTheme.Default;

    [ObservableProperty]
    private SidebarPosition _sidebarPosition = SidebarPosition.Left;

    [ObservableProperty]
    private bool _showSidebarOnLaunch = true;

    [ObservableProperty]
    private bool _blockAds = true;

    [ObservableProperty]
    private bool _autoPiP = false;

    /// <summary>
    /// Reopen the previous session's ordinary tabs and re-select whichever was
    /// active. Off by default.
    ///
    /// <para>
    /// Pinned tabs and folders are restored either way — they are structure the
    /// user curated. This governs only the transient tabs, so that a launch
    /// starts on a fresh new tab instead of resuming wherever the last session
    /// happened to end. The Mac has no equivalent switch and always restores.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _restoreTabsOnLaunch;

    /// <summary>
    /// Width of the docked sidebar, in DIPs. The Mac pins this at 256; here it
    /// is user-draggable from the sidebar's inner edge and remembered between
    /// sessions.
    /// </summary>
    [ObservableProperty]
    private double _sidebarWidth = DefaultSidebarWidth;

    public const double DefaultSidebarWidth = 260;

    /// <summary>Narrow enough stays usable — the tab rows still fit their close buttons.</summary>
    public const double MinSidebarWidth = 200;

    /// <summary>Beyond this the sidebar starts crowding the page rather than framing it.</summary>
    public const double MaxSidebarWidth = 480;

    /// <summary>Clamp an arbitrary drag position to the allowed range.</summary>
    public static double ClampSidebarWidth(double width)
        => Math.Clamp(width, MinSidebarWidth, MaxSidebarWidth);

    /// <summary>
    /// Width of the floating peek card, in DIPs. Tracked separately from the
    /// docked width: the two are dragged in different contexts and a comfortable
    /// size for one is not the other — the peek card floats over the page, where
    /// wide is intrusive, while the docked sidebar takes space the page never had.
    /// </summary>
    [ObservableProperty]
    private double _peekWidth = DefaultPeekWidth;

    public const double DefaultPeekWidth = 224;
    public const double MinPeekWidth = 200;
    public const double MaxPeekWidth = 480;

    public static double ClampPeekWidth(double width)
        => Math.Clamp(width, MinPeekWidth, MaxPeekWidth);

    /// <summary>
    /// Whether the sidebar was docked when the window last closed, so a launch
    /// comes back the way it was left rather than always docked.
    /// </summary>
    [ObservableProperty]
    private bool _sidebarDocked = true;

    /// <summary>
    /// The user's accent, as "#rrggbb". Empty means the palette's own — each
    /// theme ships a Primary, and this replaces it rather than adding a token,
    /// so anything already drawn from Primary follows without being rewired.
    /// </summary>
    [ObservableProperty]
    private string _themeColor = "";

    /// <summary>Guards the initial load so applying it doesn't write straight back.</summary>
    private bool _loading;

    private BrowserSettings()
    {
        Load();
        PropertyChanged += (_, _) => Save();
    }

    private void Load()
    {
        var saved = SettingsStore.Load();
        if (saved is null)
        {
            // Nothing on disk yet. Write the defaults out so the file exists and
            // can be inspected or hand-edited, rather than appearing only after
            // the user happens to change something.
            Save();
            return;
        }

        _loading = true;
        HomepageURL = saved.HomepageUrl;
        NewTabBehavior = saved.NewTabBehavior;
        SearchEngine = saved.SearchEngine;
        CustomSearchTemplate = saved.CustomSearchTemplate;
        Theme = saved.Theme;
        SidebarPosition = saved.SidebarPosition;
        ShowSidebarOnLaunch = saved.ShowSidebarOnLaunch;
        SidebarWidth = ClampSidebarWidth(saved.SidebarWidth);
        PeekWidth = ClampPeekWidth(saved.PeekWidth);
        SidebarDocked = saved.SidebarDocked;
        ThemeColor = saved.ThemeColor ?? "";
        BlockAds = saved.BlockAds;
        AutoPiP = saved.AutoPiP;
        RestoreTabsOnLaunch = saved.RestoreTabsOnLaunch;
        _loading = false;

        // Write back after loading so a file saved by an older build gains any
        // newly added keys. Without this a new preference stays absent from the
        // file until it happens to change, since loading a missing key yields
        // the default and so raises no PropertyChanged to trigger a save.
        Save();
    }

    private void Save()
    {
        if (_loading) return;

        SettingsStore.Save(new PersistedSettings
        {
            HomepageUrl = HomepageURL,
            NewTabBehavior = NewTabBehavior,
            SearchEngine = SearchEngine,
            CustomSearchTemplate = CustomSearchTemplate,
            Theme = Theme,
            SidebarPosition = SidebarPosition,
            ShowSidebarOnLaunch = ShowSidebarOnLaunch,
            SidebarWidth = SidebarWidth,
            PeekWidth = PeekWidth,
            SidebarDocked = SidebarDocked,
            ThemeColor = ThemeColor,
            BlockAds = BlockAds,
            AutoPiP = AutoPiP,
            RestoreTabsOnLaunch = RestoreTabsOnLaunch,
        });
    }
}
