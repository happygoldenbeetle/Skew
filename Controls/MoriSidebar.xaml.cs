using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mori.Models;
using Mori.Theme;

namespace Mori.Controls;

/// <summary>
/// The vertical sidebar — Arc/SigmaOS-inspired. Top-to-bottom:
/// header (nav + omnibox), pinned tile grid, collapsible folders,
/// loose tabs, and a bottom action bar.
/// Port of Sidebar.swift from the Mac app.
/// </summary>
public sealed partial class MoriSidebar : UserControl
{
    public BrowserStore? Store { get; set; }

    public bool HasPins => Store?.PinnedTabs.Count > 0;

    public MoriSidebar()
    {
        InitializeComponent();
        Loaded += MoriSidebar_Loaded;
    }

    private void MoriSidebar_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshUI();
    }

    /// <summary>
    /// Rebuild the ItemsRepeater data sources from the store.
    /// </summary>
    public void RefreshUI()
    {
        if (Store is null) return;

        PinnedGrid.ItemsSource = Store.PinnedTabs;
        FolderList.ItemsSource = Store.Folders;
        LooseTabList.ItemsSource = Store.LooseTabs;

        // Update omnibox with selected tab URL
        if (Store.SelectedTab is not null)
        {
            OmniboxField.Text = Store.SelectedTab.DisplayUrl;
        }

        // Update nav button states
        BackButton.IsEnabled = Store.SelectedTab?.CanGoBack ?? false;
        ForwardButton.IsEnabled = Store.SelectedTab?.CanGoForward ?? false;
    }

    public void FocusOmnibox()
    {
        OmniboxField.Focus(FocusState.Programmatic);
    }

    // ── Event Handlers ──

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
        => Store?.ToggleSidebar();

    private void Back_Click(object sender, RoutedEventArgs e)
        => Store?.GoBack();

    private void Forward_Click(object sender, RoutedEventArgs e)
        => Store?.GoForward();

    private void Reload_Click(object sender, RoutedEventArgs e)
        => Store?.Reload();

    private void Omnibox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var text = args.QueryText?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            Store?.Navigate(text);
        }
    }

    private void NewTab_Click(object sender, RoutedEventArgs e)
        => Store?.PresentLauncher();

    private void PinnedTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Guid tabId)
            Store?.SelectTab(tabId);
    }

    private void TabRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Guid tabId)
            Store?.SelectTab(tabId);
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        Guid? tabId = null;
        if (sender is Button btn && btn.Tag is Guid id1) tabId = id1;
        else if (sender is MenuFlyoutItem item && item.Tag is Guid id2) tabId = id2;

        if (tabId.HasValue)
        {
            Store?.CloseTab(tabId.Value);
            RefreshUI();
        }
    }

    private void PinTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is Guid tabId)
        {
            Store?.TogglePin(tabId);
            RefreshUI();
        }
    }

    private void FolderHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Guid folderId)
        {
            Store?.ToggleFolder(folderId);
            RefreshUI();
        }
    }

    private void AIToggle_Click(object sender, RoutedEventArgs e)
        => Store?.ToggleAIPanel();

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        ThemeService.Instance.ToggleTheme();
        if (XamlRoot?.Content is FrameworkElement root)
        {
            root.RequestedTheme = ThemeService.Instance.IsDark
                ? ElementTheme.Dark
                : ElementTheme.Light;
        }
        ThemeIcon.Glyph = ThemeService.Instance.IsDark ? "\uE706" : "\uE708";
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
        => Store?.ToggleSettings();
}
