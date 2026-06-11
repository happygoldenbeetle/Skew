using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Mori.Models;
using Mori.Theme;

namespace Mori;

/// <summary>
/// The application window — root of the Mori browser chrome.
/// Hosts the sidebar, web content card, AI panel, and launcher overlay.
/// </summary>
public sealed partial class MainWindow : Window
{
    public BrowserStore Store => BrowserStore.Shared;

    public MainWindow()
    {
        InitializeComponent();

        // Custom title bar — extend into content, no separate bar
        ExtendsContentIntoTitleBar = true;

        // Set window size and icon
        var appWindow = AppWindow;
        appWindow.SetIcon("Assets/AppIcon.ico");
        appWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));
        appWindow.Title = "Mori";

        // Use OverlappedPresenter for a real, chrome-less window
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
            presenter.SetBorderAndTitleBar(true, false);
        }

        // Wire up store state to UI
        Store.PropertyChanged += Store_PropertyChanged;

        // Set initial sidebar data context
        Sidebar.DataContext = Store;
        Sidebar.Store = Store;

        // Apply initial layout state
        UpdateColumnLayout();

        // Apply theme
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = ElementTheme.Dark;
            ThemeService.Instance.SetTheme(ElementTheme.Dark);
        }

        // Listen for launcher keyboard shortcut
        Content.KeyDown += Content_KeyDown;
    }

    private void Store_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BrowserStore.SidebarVisible):
            case nameof(BrowserStore.AiPanelVisible):
            case nameof(BrowserStore.SidebarOnLeft):
                UpdateColumnLayout();
                break;

            case nameof(BrowserStore.LauncherVisible):
                Launcher.Visibility = Store.LauncherVisible
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                if (Store.LauncherVisible)
                    Launcher.FocusSearchBox();
                break;

            case nameof(BrowserStore.FindBarVisible):
                FindBar.Visibility = Store.FindBarVisible
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                break;

            case nameof(BrowserStore.SelectedTab):
                UpdateLoadingBar();
                break;
        }
    }

    private void UpdateLoadingBar()
    {
        if (Store.SelectedTab is not null)
        {
            Store.SelectedTab.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(BrowserTab.IsLoading))
                {
                    LoadingBar.Visibility = Store.SelectedTab.IsLoading
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            };
        }
    }

    private void UpdateColumnLayout()
    {
        if (Store.SidebarOnLeft)
        {
            Grid.SetColumn(Sidebar, 0);
            Grid.SetColumn(AIPanel, 2);
            Grid.SetColumn(SidebarRevealButton, 1);
            SidebarRevealButton.HorizontalAlignment = HorizontalAlignment.Left;
            SidebarRevealButton.Margin = new Thickness(16, 16, 0, 0);
        }
        else
        {
            Grid.SetColumn(Sidebar, 2);
            Grid.SetColumn(AIPanel, 0);
            Grid.SetColumn(SidebarRevealButton, 1);
            SidebarRevealButton.HorizontalAlignment = HorizontalAlignment.Right;
            SidebarRevealButton.Margin = new Thickness(0, 16, 16, 0);
        }
        
        SidebarColumn.Width = Store.SidebarOnLeft 
            ? (Store.SidebarVisible ? new GridLength(260) : new GridLength(0))
            : (Store.AiPanelVisible ? new GridLength(360) : new GridLength(0));
            
        AIPanelColumn.Width = Store.SidebarOnLeft
            ? (Store.AiPanelVisible ? new GridLength(360) : new GridLength(0))
            : (Store.SidebarVisible ? new GridLength(260) : new GridLength(0));

        SidebarRevealButton.Visibility = Store.SidebarVisible
            ? Visibility.Collapsed
            : Visibility.Visible;

        AIPanel.Visibility = Store.AiPanelVisible
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Ensure reveal button icon flips appropriately
        if (SidebarRevealButtonIcon.RenderTransform is Microsoft.UI.Xaml.Media.ScaleTransform st)
        {
            st.ScaleX = Store.SidebarOnLeft ? -1 : 1;
        }
    }

    private void Content_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ctrl)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.T:
                    Store.ToggleLauncher();
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.W:
                    if (Store.SelectedTabId is not null)
                        Store.CloseTab(Store.SelectedTabId.Value);
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.L:
                    Sidebar.FocusOmnibox();
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.S:
                    Store.ToggleSidebar();
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.K:
                    Store.ToggleAIPanel();
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.F:
                    Store.ToggleFindBar();
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.R:
                    Store.Reload();
                    e.Handled = true;
                    break;
            }
        }

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (Store.LauncherVisible) { Store.DismissLauncher(); e.Handled = true; }
            else if (Store.FindBarVisible) { Store.ToggleFindBar(); e.Handled = true; }
        }
    }

    private void SidebarReveal_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Store.ToggleSidebar();
    }

    private void TopDragArea_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            if (presenter.State == OverlappedPresenterState.Maximized)
                presenter.Restore();
            else
                presenter.Maximize();
        }
    }
}
