using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace Mori.Models;

public enum NewTabBehavior { Homepage, Blank, Dashboard }
public enum SearchEngine { Google, Bing, DuckDuckGo, Brave, Custom }
public enum SidebarPosition { Left, Right }

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

    private BrowserSettings() { }
}
