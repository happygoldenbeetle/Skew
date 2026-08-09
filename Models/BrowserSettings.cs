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
        BlockAds = saved.BlockAds;
        AutoPiP = saved.AutoPiP;
        RestoreTabsOnLaunch = saved.RestoreTabsOnLaunch;
        _loading = false;
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
            BlockAds = BlockAds,
            AutoPiP = AutoPiP,
            RestoreTabsOnLaunch = RestoreTabsOnLaunch,
        });
    }
}
