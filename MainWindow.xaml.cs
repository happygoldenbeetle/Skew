using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Skew.Models;
using Skew.Cef;
using Skew.Theme;

namespace Skew;

/// <summary>
/// The application window — root of the Skew browser chrome.
/// Hosts the sidebar, web content card, AI panel, and launcher overlay.
/// </summary>
public sealed partial class MainWindow : Window
{
    public BrowserStore Store => BrowserStore.Shared;

    public static MainWindow Instance { get; private set; }

    // Removed Acrylic fields

    private bool _isPeeking = false;
    private DispatcherTimer _peekCloseTimer;
    private Microsoft.UI.Xaml.Media.Animation.Storyboard _sidebarAnimStoryboard;
    private Controls.SkewBrowserView? _extensionActionPopupView;
    private Flyout? _extensionActionPopupFlyout;

    public MainWindow()
    {
        Instance = this;
        _peekCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _peekCloseTimer.Tick += (s, e) => OnPeekCloseTick();

        this.InitializeComponent();

        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        // A real title bar, with the content extended into it: Windows draws the
        // caption buttons and owns drag, double-click to maximise, snap layouts
        // and the Alt+Space menu, while AppTitleBar is the drag region and is
        // ours to put things in. The hover-revealed strip and the hand-drawn
        // caption buttons it carried are gone.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        // Standard is 32 DIPs, which is what AppTitleBar is sized to. Tall (48)
        // would leave the buttons and the drag region disagreeing.
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        ApplyTitleBarColors();

        // Set window size and icon
        var appWindow = AppWindow;
        appWindow.SetIcon("Assets/AppIcon.ico");
        appWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));
        appWindow.Title = "Skew";

        WatchDownloads();

        // 100ms timer to detect trackpad release
        _swipeResetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _swipeResetTimer.Tick += (s, e) => EvaluateAndResetSwipe();

        // Border and title bar both on: the title bar is what the caption
        // buttons and the system drag behaviour hang off, and turning it off is
        // what forced the old hand-drawn ones.
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
            presenter.SetBorderAndTitleBar(true, true);
        }

        // Come back the way the window was left. Set before anything subscribes,
        // so restoring the state is not mistaken for the user changing it and
        // written straight back.
        Store.SidebarVisible = BrowserSettings.Shared.SidebarDocked;
        Store.SidebarOnLeft = BrowserSettings.Shared.SidebarPosition == SidebarPosition.Left;

        // Wire up store state to UI
        Store.PropertyChanged += Store_PropertyChanged;

        // The side is one piece of state with two front doors — the sidebar's
        // context menu, which sets the store, and the dropdown in Settings,
        // which sets the preference. They were not connected: the menu moved the
        // sidebar without remembering it, and the dropdown remembered a side it
        // never moved. Both now meet here. The loop this looks like it closes
        // does not, because assigning a property its current value raises
        // nothing.
        BrowserSettings.Shared.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BrowserSettings.SidebarPosition))
                Store.SidebarOnLeft = BrowserSettings.Shared.SidebarPosition == SidebarPosition.Left;
        };

        // Set initial sidebar data context. There are two SkewSidebar instances
        // bound to the same store: the docked one (visible state) and the one
        // inside the floating peek popup (hidden state) — mirroring mac, which
        // instantiates a separate Sidebar inside its peek overlay.
        Sidebar.DataContext = Store;
        Sidebar.Store = Store;
        PeekSidebar.DataContext = Store;
        PeekSidebar.Store = Store;

        RootGrid.Loaded += (s, e) =>
        {
            EnsurePeekReady();
            UpdateColumnLayout();
            // Both need the XamlRoot: one for its scale, the other for a
            // selected tab that may already have been restored by now.
            SyncCaptionButtonSpacer();
            UpdateTitleBarChrome();
        };

        // Keep the floating peek card's themed brushes in sync with light/dark.
        ThemeService.Instance.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ThemeService.Palette))
                DispatcherQueue.TryEnqueue(() =>
                {
                    ApplyChromeTheme();
                    // The caption buttons are drawn by the system, outside the
                    // XAML tree, so they do not follow ElementTheme on their own.
                    ApplyTitleBarColors();
                });
        };

        // Apply initial layout state
        UpdateColumnLayout();

        // Apply theme
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = Microsoft.UI.Xaml.ElementTheme.Dark;
            ThemeService.Instance.SetTheme(Microsoft.UI.Xaml.ElementTheme.Dark);
        }

        // Paint the chrome surface up front. Waiting for RootGrid.Loaded leaves the
        // first frame with an unpainted tint rectangle over bare Mica.
        ApplyChromeTheme();

        // Film grain generation removed
        // Listen for launcher keyboard shortcut
        Content.KeyDown += Content_KeyDown;

        // Listen for mouse side buttons globally (Back/Forward)
        Content.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Content_PointerPressed), true);

        // Listen for trackpad swipes globally
        Content.AddHandler(UIElement.PointerWheelChangedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Content_PointerWheelChanged), true);

        // Wire CEF callbacks
        Skew.Cef.SkewBrowserHostChannel.ShortcutHandler = HandleCefShortcut;
        Skew.Cef.SkewBrowserHostChannel.DownloadUpdateHandler = (id, url, path, received, total, percent, speed, complete, canceled) =>
        {
            DispatcherQueue.TryEnqueue(() => 
            {
                var filename = System.IO.Path.GetFileName(path);
                Skew.Models.DownloadStore.Shared.Ingest(id, url, filename, path, received, total, percent, speed, complete, canceled, !complete && !canceled);
            });
        };

        // Media markers (__SKEW_MEDIA__) from each page's injected agent feed the
        // sidebar media player. The browser id maps back to its owning tab inside
        // MediaController when issuing transport commands.
        Skew.Cef.SkewBrowserHostChannel.ConsoleMarkerHandler = (browser, message) =>
        {
            if (message is null) return;
            const string mediaPrefix = "__SKEW_MEDIA__";
            if (message.StartsWith(mediaPrefix, StringComparison.Ordinal))
            {
                int bid = browser.Identifier;
                string json = message.Substring(mediaPrefix.Length);
                DispatcherQueue.TryEnqueue(() => Skew.Models.MediaController.Shared.Ingest(bid, json));
            }
        };

        Skew.Cef.SkewBrowserHostChannel.WebStoreInstallHandler = (browser, extensionId) =>
            DispatcherQueue.TryEnqueue(() => ConfirmAndInstallWebStoreExtensionAsync(browser, extensionId));
        Skew.Cef.SkewBrowserHostChannel.WebStoreRemoveHandler = (browser, extensionId) =>
            DispatcherQueue.TryEnqueue(() => RemoveWebStoreExtensionAsync(browser, extensionId));

        // Show the selected tab's CEF browser view in the web-content card.
        ShowSelectedBrowserView();
        WatchSelectedTabUrl();
    }

    private async void ConfirmAndInstallWebStoreExtensionAsync(
        Xilium.CefGlue.CefBrowser browser, string extensionId)
    {
        var confirmation = new ContentDialog
        {
            Title = "Add extension to Skew?",
            Content = "Skew will download this extension from the Chrome Web Store. " +
                      "Extensions can read or change page content according to their permissions.",
            PrimaryButtonText = "Add extension",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };

        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            Skew.Cef.BrowserClient.CompleteWebStoreInstall(browser, extensionId, installed: false);
            return;
        }

        string? error = await ExtensionStore.Shared.BeginWebStoreInstallAsync(extensionId);
        bool installed = error is null;
        Skew.Cef.BrowserClient.CompleteWebStoreInstall(browser, extensionId, installed);

        if (installed) return;

        await new ContentDialog
        {
            Title = "Extension Error",
            Content = error,
            CloseButtonText = "OK",
            XamlRoot = RootGrid.XamlRoot,
        }.ShowAsync();
    }

    private async void RemoveWebStoreExtensionAsync(
        Xilium.CefGlue.CefBrowser browser, string extensionId)
    {
        BrowserExtension? extension = ExtensionStore.Shared.Extensions.FirstOrDefault(item =>
            string.Equals(item.Id, extensionId, StringComparison.OrdinalIgnoreCase));
        if (extension is null)
        {
            BrowserClient.CompleteWebStoreRemove(browser, extensionId, removed: true);
            return;
        }

        var confirmation = new ContentDialog
        {
            Title = $"Remove {extension.Name} from Skew?",
            Content = "The extension and its saved data will be removed from Skew.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            BrowserClient.CompleteWebStoreRemove(browser, extensionId, removed: false);
            return;
        }

        try
        {
            await ExtensionStore.Shared.RemoveExtensionAsync(extension);
            BrowserClient.CompleteWebStoreRemove(browser, extensionId, removed: true);
        }
        catch (Exception error)
        {
            BrowserClient.CompleteWebStoreRemove(browser, extensionId, removed: false);
            await new ContentDialog
            {
                Title = "Extension Error",
                Content = $"Failed to remove the extension: {error.Message}",
                CloseButtonText = "OK",
                XamlRoot = RootGrid.XamlRoot,
            }.ShowAsync();
        }
    }

    private void Store_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BrowserStore.SidebarVisible):
                // Remember docked or peek, so the next launch matches.
                BrowserSettings.Shared.SidebarDocked = Store.SidebarVisible;
                UpdateColumnLayout();
                break;

            case nameof(BrowserStore.SidebarOnLeft):
                // Remember which side, so the next launch comes back on it.
                BrowserSettings.Shared.SidebarPosition = Store.SidebarOnLeft
                    ? SidebarPosition.Left : SidebarPosition.Right;
                UpdateColumnLayout();
                break;

            case nameof(BrowserStore.LauncherVisible):
                if (Store.LauncherVisible)
                {
                    LauncherHost.Visibility = Visibility.Visible;
                    Launcher.FocusSearchBox();
                }
                else if (LauncherHost.Visibility == Visibility.Visible)
                {
                    Launcher.PlayHideAnimation(() => LauncherHost.Visibility = Visibility.Collapsed);
                }
                break;

            case nameof(BrowserStore.FindBarVisible):
                if (Store.FindBarVisible)
                {
                    DispatcherQueue.TryEnqueue(() => FindBar.FocusSearchBox());
                }
                else
                {
                    Store.SelectedTab?.StopFinding(clearSelection: true);
                }
                break;

            case nameof(BrowserStore.SettingsVisible):
                SettingsHost.Visibility = Store.SettingsVisible
                    ? Visibility.Visible : Visibility.Collapsed;
                if (Store.SettingsVisible)
                {
                    SettingsFlyout.FocusPanel();
                }
                break;

            case nameof(BrowserStore.SelectedTab):
                UpdateLoadingBar();
                ShowSelectedBrowserView();
                WatchSelectedTabUrl();
                UpdateTitleBarChrome();
                SyncHomepagePeek();
                break;
        }
    }





    private void Cef_TitleChanged(object sender, string title)
    {
    }


    private void WebContentBorder_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
        SwipeOverlay.UpdateSize(e.NewSize.Width, e.NewSize.Height);
    }

    private void RootGrid_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
        if (SettingsFlyout != null)
        {
            SettingsFlyout.Width = e.NewSize.Width;
            SettingsFlyout.Height = e.NewSize.Height;
        }

        if (_peekReady && !Store.SidebarVisible)
            LayoutPeek();

        // The caption buttons' width changes with the window: maximising drops
        // the rounded corner, and a monitor with a different scale rewrites it
        // outright.
        SyncCaptionButtonSpacer();
    }

    // SyncLauncherSize and LauncherScrim_Tapped are gone with the scrim: the
    // launcher fills the window by stretching, so there is no size to keep in
    // step, and its own transparent root is what a click outside now lands on.

    private void SettingsScrim_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        => Store.SettingsVisible = false;

    // AnimateScrim went with the launcher's scrim, which was its only caller.

    /// <summary>
    /// Swap the web-content host to display the currently selected tab's CEF
    /// browser view (mac shows the selected tab's SkewBrowserView). Other tabs'
    /// views are detached so only the active engine surface composites.
    /// </summary>
    private void ShowSelectedBrowserView()
    {
        var tab = Store.SelectedTab;
        if (tab is null) return;

        // Every realized tab stays mounted and only the selected one is visible —
        // the same model as WebContainerView.swift, so background tabs keep
        // running like real tabs. The old code cleared and re-added the host's
        // children on every switch, which cycled each view through
        // Unloaded/Loaded and tore down its browser state.
        var view = tab.BrowserView;
        if (view.Parent is Panel other && other != WebContentHost)
            other.Children.Remove(view);
        if (view.Parent is null)
            WebContentHost.Children.Add(view);

        foreach (var child in WebContentHost.Children)
        {
            if (child is Controls.SkewBrowserView browser)
                browser.SetWebWindowVisible(ReferenceEquals(browser, view) && !tab.DidFail);
        }

        // Route popup/target=_blank requests into new Skew tabs (mac OnOpenURLFromTab).
        view.RequestsNewTab -= OnViewRequestsNewTab;
        view.RequestsNewTab += OnViewRequestsNewTab;
    }

    private void OnViewRequestsNewTab(string url)
    {
        DispatcherQueue.TryEnqueue(() => Store.NewTab(url));
    }

    /// <summary>
    /// Maps a CEF key event (Windows virtual-key + modifiers) to Skew's shortcut
    /// actions. Returns true when consumed so the page never sees it. Mirrors the
    /// Ctrl-shortcut set in <see cref="Content_KeyDown"/>.
    /// </summary>
    private bool HandleCefShortcut(int windowsKeyCode, Xilium.CefGlue.CefEventFlags modifiers)
    {
        bool ctrl = (modifiers & Xilium.CefGlue.CefEventFlags.ControlDown) != 0;
        bool alt = (modifiers & Xilium.CefGlue.CefEventFlags.AltDown) != 0;

        System.IO.File.AppendAllText("keys.log", $"HandleCefShortcut: key={windowsKeyCode}, ctrl={ctrl}, alt={alt}\n");

        if (!ctrl && !alt)
        {
            if (windowsKeyCode != (int)Windows.System.VirtualKey.F11 && windowsKeyCode != (int)Windows.System.VirtualKey.Escape)
                return false;
        }

        var key = (Windows.System.VirtualKey)windowsKeyCode;
        bool handled = false;

        if (ctrl)
        {
            if (key == Windows.System.VirtualKey.T || key == Windows.System.VirtualKey.W ||
                key == Windows.System.VirtualKey.L || key == Windows.System.VirtualKey.S ||
                key == Windows.System.VirtualKey.K || key == Windows.System.VirtualKey.F ||
                key == Windows.System.VirtualKey.R)
            {
                handled = true;
            }
        }
        else if (alt)
        {
            if (key == Windows.System.VirtualKey.Left || key == Windows.System.VirtualKey.Right)
            {
                handled = true;
            }
        }
        else
        {
            if (key == Windows.System.VirtualKey.F11 || key == Windows.System.VirtualKey.F12 || key == Windows.System.VirtualKey.Escape)
            {
                handled = true;
            }
        }

        if (handled)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ctrl)
                {
                    switch (key)
                    {
                        case Windows.System.VirtualKey.T: Store.ToggleLauncher(); break;
                        case Windows.System.VirtualKey.W:
                            if (Store.SelectedTabId is not null)
                                Store.CloseTab(Store.SelectedTabId.Value);
                            break;
                        // Ctrl+L is unbound until the omnibox reappears in the
                        // title bar; the sidebar no longer has one to focus.
                        case Windows.System.VirtualKey.S: Store.ToggleSidebar(); break;
                        case Windows.System.VirtualKey.F: Store.ToggleFindBar(); break;
                        case Windows.System.VirtualKey.R: Store.Reload(); break;
                    }
                }
                else if (alt)
                {
                    if (key == Windows.System.VirtualKey.Left) Store.GoBack();
                    else if (key == Windows.System.VirtualKey.Right) Store.GoForward();
                }
                else
                {
                    if (key == Windows.System.VirtualKey.F11)
                    {
                        ToggleFullScreen();
                    }
                    else if (key == Windows.System.VirtualKey.Escape)
                    {
                        if (Store.LauncherVisible) { Store.DismissLauncher(); }
                        else if (Store.FindBarVisible) { Store.ToggleFindBar(); }
                        else if (AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen) { ToggleFullScreen(); }
                    }
                }
            });
        }
        return handled;
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

    // ── Sidebar resize ──────────────────────────────────────────────────────
    //
    // Drag the sidebar's page-facing edge to set its width. The Mac pins the
    // sidebar at 256, so this is an addition rather than a port.

    private bool _resizingSidebar;
    private double _resizeStartX;
    private double _resizeStartWidth;

    /// <summary>
    /// The web card's inset from the chrome around it (RootView.webCard sides
    /// and bottom). UpdateColumnLayout drops it on the side the docked sidebar
    /// sits on, so it never stacks with the sidebar's own padding.
    /// </summary>
    private const double WebCardInset = 8;


    /// <summary>
    /// Nothing above the card. The Mac floats it 4 below the top chrome, but
    /// that chrome only appeared on hover there; here the title bar is always
    /// present, and a gap under it made the run from the window's top edge to
    /// the page 36 rather than the title bar's own 32.
    /// </summary>
    private const double WebCardTopInset = 0;

    private void SidebarResizeGrip_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _resizingSidebar = true;
        // Track against the window, not the grip: the grip moves as the sidebar
        // resizes, so grip-relative deltas would feed back on themselves.
        _resizeStartX = e.GetCurrentPoint(RootGrid).Position.X;
        _resizeStartWidth = BrowserSettings.Shared.SidebarWidth;
        SidebarResizeGrip.CapturePointer(e.Pointer);
        // Keep the indicator lit: the captured pointer leaves the strip almost
        // immediately, and the hover state alone would drop it mid-drag.
        SidebarResizeGrip.IsDragging = true;
        e.Handled = true;
    }

    private void SidebarResizeGrip_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_resizingSidebar) return;

        double delta = e.GetCurrentPoint(RootGrid).Position.X - _resizeStartX;
        // Dragging right grows a left-docked sidebar and shrinks a right-docked one.
        double target = Store.SidebarOnLeft
            ? _resizeStartWidth + delta
            : _resizeStartWidth - delta;

        double width = BrowserSettings.ClampSidebarWidth(target);

        // Apply straight to the column while dragging. Writing through the
        // setting on every pointer move would rewrite settings.json dozens of
        // times per drag, since preferences save synchronously on change.
        var length = new GridLength(width);
        if (Store.SidebarOnLeft) SidebarColumn.Width = length;
        else RightColumn.Width = length;

        // Straight to the column too, for the same reason the width is: the
        // setting is not written until the drag ends, so reading it here would
        // leave the nav buttons a drag behind the edge they follow.
        SyncTitleBarSidebarColumn(width);

        e.Handled = true;
    }

    private void SidebarResizeGrip_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_resizingSidebar) return;
        _resizingSidebar = false;
        SidebarResizeGrip.ReleasePointerCapture(e.Pointer);
        SidebarResizeGrip.IsDragging = false;

        // Commit once, at the end of the drag — one file write instead of many.
        double finalWidth = Store.SidebarOnLeft
            ? SidebarColumn.Width.Value
            : RightColumn.Width.Value;
        BrowserSettings.Shared.SidebarWidth = BrowserSettings.ClampSidebarWidth(finalWidth);

        // Keep the peek card in step with the docked width.
        if (_peekReady) LayoutPeek();
        e.Handled = true;
    }

    // ── Peek card resize ────────────────────────────────────────────────────
    //
    // The same gesture as the docked sidebar, against a different stored width.
    // The card is open throughout, so the drag reads directly as the card
    // growing under the pointer.

    private bool _resizingPeek;
    private double _peekResizeStartX;
    private double _peekResizeStartWidth;

    private void PeekResizeGrip_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _resizingPeek = true;
        _peekResizeStartX = e.GetCurrentPoint(RootGrid).Position.X;
        _peekResizeStartWidth = PeekCardWidth;
        PeekResizeGrip.CapturePointer(e.Pointer);
        PeekResizeGrip.IsDragging = true;
        // The close timer would retract the card out from under the drag.
        _peekCloseTimer.Stop();
        e.Handled = true;
    }

    private void PeekResizeGrip_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_resizingPeek) return;

        double delta = e.GetCurrentPoint(RootGrid).Position.X - _peekResizeStartX;
        // Dragging away from the docked edge grows the card, either side.
        double target = Store.SidebarOnLeft
            ? _peekResizeStartWidth + delta
            : _peekResizeStartWidth - delta;

        _peekDragWidth = BrowserSettings.ClampPeekWidth(target);
        LayoutPeek();
        e.Handled = true;
    }

    private void PeekResizeGrip_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_resizingPeek) return;
        _resizingPeek = false;
        PeekResizeGrip.ReleasePointerCapture(e.Pointer);
        PeekResizeGrip.IsDragging = false;

        if (_peekDragWidth is double width)
            BrowserSettings.Shared.PeekWidth = BrowserSettings.ClampPeekWidth(width);
        _peekDragWidth = null;

        LayoutPeek();
        e.Handled = true;
    }

    private void UpdateColumnLayout()
    {
        // If user docks sidebar while peeking, cancel peek
        if (_isPeeking && Store.SidebarVisible)
        {
            _isPeeking = false;
            _peekCloseTimer.Stop();
            _sidebarAnimStoryboard?.Stop();
        }

        // Position the sidebar by side preference. The gripper rides with it and
        // always hugs its page-facing edge — the right edge when docked left,
        // the left edge when docked right.
        if (Store.SidebarOnLeft)
        {
            Grid.SetColumn(SidebarBorder, 0);
            Grid.SetColumn(SidebarResizeGrip, 0);
            SidebarResizeGrip.HorizontalAlignment = HorizontalAlignment.Right;
        }
        else
        {
            Grid.SetColumn(SidebarBorder, 2);
            Grid.SetColumn(SidebarResizeGrip, 2);
            SidebarResizeGrip.HorizontalAlignment = HorizontalAlignment.Left;
        }

        // The indicator runs the card's height, not the column's: the same top
        // and bottom insets the card takes, so its ends finish level with the
        // card's corners rather than carrying on past them to the window edge.
        SidebarResizeGrip.Margin = new Thickness(0, WebCardTopInset, 0, WebCardInset);

        // Set column widths. The sidebar's is whatever the user last dragged to;
        // the column it is not on stays closed.
        var open = Store.SidebarVisible
            ? new GridLength(BrowserSettings.Shared.SidebarWidth)
            : new GridLength(0);
        var closed = new GridLength(0);

        SidebarColumn.Width = Store.SidebarOnLeft ? open : closed;
        RightColumn.Width = Store.SidebarOnLeft ? closed : open;

        SyncTitleBarSidebarColumn();

        // The card's own 8pt side inset stacks with the sidebar's 10pt padding
        // on the edge the two share, so the gap there read 18 against the 10 on
        // the window edge — the sidebar's contents looked pushed off centre.
        // Drop the card's inset on whichever side the docked sidebar is on, and
        // the sidebar's padding alone sets both gaps. Only while it is docked:
        // with the sidebar hidden the card meets the window edge and wants its
        // inset back.
        bool sidebarLeftOfCard = Store.SidebarVisible && Store.SidebarOnLeft;
        bool sidebarRightOfCard = Store.SidebarVisible && !Store.SidebarOnLeft;
        WebContentBorder.Margin = new Thickness(
            sidebarLeftOfCard ? 0 : WebCardInset,
            WebCardTopInset,
            sidebarRightOfCard ? 0 : WebCardInset,
            WebCardInset);

        // Only grabbable while the sidebar is actually docked.
        SidebarResizeGrip.Visibility = Store.SidebarVisible
            ? Visibility.Visible : Visibility.Collapsed;

        // Only one of the two sidebars is on screen at a time, and only that one
        // should take keyboard focus — see SkewSidebar.IsLive.
        Sidebar.IsLive = Store.SidebarVisible;
        PeekSidebar.IsLive = !Store.SidebarVisible && _isPeeking;

        // The docked sidebar owns the visible state; the floating peek popup owns
        // the hidden state.
        SidebarBorder.Visibility = Store.SidebarVisible ? Visibility.Visible : Visibility.Collapsed;

        // No reveal button to show or hide: the title bar's toggle is always
        // there, whichever state the sidebar is in. Its mark still points at the
        // docked edge, which is the one thing the reveal button did that the
        // toggle inherited.
        TitleBarSidebarToggleIcon.Glyph = Store.SidebarOnLeft ? "" : ""; // DockLeft : DockRight

        // Peek overlay is live only while the sidebar is hidden.
        if (Store.SidebarVisible)
        {
            SetPeekHostShown(false);
        }
        else
        {
            EnsurePeekReady();
            LayoutPeek();
            if (_peekReady)
                SetPeekHostShown(true);
        }

        // Either way, not just when hidden. Docking has to release the latch as
        // much as undocking has to take it: left set, the latch still read as
        // held on the way back out, so the state matched and the peek was never
        // told to open. It took a hover to start it, and then held correctly —
        // which is what a stale latch looks like from the outside.
        //
        // snap, because this is the path where the sidebar was just docked in
        // that spot: the peek replaces it where it stood.
        SyncHomepagePeek(snap: true);

    }

    private void Content_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

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
                // Ctrl+L falls through unhandled for now — see the accelerator
                // path above.
                case Windows.System.VirtualKey.S:
                    Store.ToggleSidebar();
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
        else if (alt)
        {
            if (e.Key == Windows.System.VirtualKey.Left)
            {
                Store.GoBack();
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Right)
            {
                Store.GoForward();
                e.Handled = true;
            }
        }

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (Store.LauncherVisible) { Store.DismissLauncher(); e.Handled = true; }
            else if (Store.FindBarVisible) { Store.ToggleFindBar(); e.Handled = true; }
            else if (AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen) { ToggleFullScreen(); e.Handled = true; }
        }
        else if (e.Key == Windows.System.VirtualKey.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.F12)
        {
            Store.ShowDevTools();
            e.Handled = true;
        }
    }

    private bool _wasMaximizedBeforeFullScreen = false;

    private async void ToggleFullScreen()
    {
        if (AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen)
        {
            AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped);
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.IsResizable = true;
                presenter.IsMaximizable = true;
                presenter.IsMinimizable = true;
                // Matching the constructor: leaving the title bar off here gave
                // a window back from full screen with no caption buttons.
                presenter.SetBorderAndTitleBar(true, true);

                if (_wasMaximizedBeforeFullScreen)
                {
                    await System.Threading.Tasks.Task.Delay(50);
                    presenter.Maximize();
                }
            }
        }
        else
        {
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                _wasMaximizedBeforeFullScreen = presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;
                if (_wasMaximizedBeforeFullScreen)
                {
                    presenter.Restore();
                    // Wait for the native window to process the restore
                    await System.Threading.Tasks.Task.Delay(50);
                }
            }
            AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
        }
    }

    private void SidebarReveal_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Store.ToggleSidebar();
    }

    private void Content_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint((UIElement)sender);
        if (point.Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.XButton1Pressed)
        {
            Store.GoBack();
            e.Handled = true;
        }
        else if (point.Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.XButton2Pressed)
        {
            Store.GoForward();
            e.Handled = true;
        }
    }

    private void Accelerator_GoBack(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        Store.GoBack();
        args.Handled = true;
    }

    private void Accelerator_GoForward(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        Store.GoForward();
        args.Handled = true;
    }

    private int _horizontalScrollDeltaAccumulator = 0;
    private DateTime _lastSwipeTime = DateTime.MinValue;
    private DispatcherTimer _swipeResetTimer;

    private void Content_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint((UIElement)sender);
        var props = point.Properties;

        if (props.IsHorizontalMouseWheel)
        {
            var tab = Store.SelectedTab;
            if (tab == null) return;

            // Cooldown between swipe navigations
            if ((DateTime.Now - _lastSwipeTime).TotalMilliseconds < 800)
            {
                _horizontalScrollDeltaAccumulator = 0;
                return;
            }
            
            // Check if we can actually go back/forward before accumulating
            if (props.MouseWheelDelta < 0 && !tab.CanGoBack && _horizontalScrollDeltaAccumulator <= 0) return;
            if (props.MouseWheelDelta > 0 && !tab.CanGoForward && _horizontalScrollDeltaAccumulator >= 0) return;

            _horizontalScrollDeltaAccumulator += props.MouseWheelDelta;

            // Trackpad scroll detection requires a timer because there are no "Finger Lifted" events for scroll wheels in Windows.
            // A 75ms timer gives us enough time to let you stretch the pill and cancel it if you want.
            _swipeResetTimer.Stop();
            _swipeResetTimer.Start();

            SwipeOverlay.Visibility = Visibility.Visible;

            SwipeOverlay.UpdateProgress(_horizontalScrollDeltaAccumulator);

            e.Handled = true;
        }
        else
        {
            EvaluateAndResetSwipe();
        }
    }

    private void EvaluateAndResetSwipe()
    {
        _swipeResetTimer.Stop();
        
        bool navigated = false;
        if (_horizontalScrollDeltaAccumulator > 300)
        {
            Store.GoForward();
            navigated = true;
        }
        else if (_horizontalScrollDeltaAccumulator < -300)
        {
            Store.GoBack();
            navigated = true;
        }

        _horizontalScrollDeltaAccumulator = 0;
        _lastSwipeTime = DateTime.Now;
        SwipeOverlay.AnimateOut(navigated);
        // The indicator used to live in its own popup window that was simply
        // closed; as an in-tree overlay it is collapsed once the fade finishes so
        // it stops participating in hit-testing and layout.
        var hide = DispatcherQueue.CreateTimer();
        hide.Interval = TimeSpan.FromMilliseconds(350);
        hide.IsRepeating = false;
        hide.Tick += (_, _) => SwipeOverlay.Visibility = Visibility.Collapsed;
        hide.Start();
    }

    // ── Sidebar peek (SidebarPeek.swift) ────────────────────────────────────
    //
    // The peek sidebar floats above the page as an inset rounded card and does
    // NOT reflow the web content. It is a plain overlay in the main visual tree:
    // with the browser rendering offscreen there is no foreign window to fight,
    // so the ~120 lines of EnumWindows / SetWindowPos / WS_EX_NOACTIVATE
    // machinery this used to need are gone, along with the separate popup window
    // that made the card opaque.

    // Geometry mirrors SidebarPeek.swift: a card inset 8pt from the edge.
    // Its width is the user's, dragged from the card's page-facing edge and
    // remembered, but kept separate from the docked sidebar's — the peek card
    // floats over the page, where a width that suits a docked sidebar is
    // intrusive, so dragging one must not move the other.
    private const double PeekInset = 8;
    /// <summary>
    /// Hover margin beyond the card — the Mac's panelBand. It used to hold the
    /// edge handle as well; with that gone it is purely the band the pointer can
    /// be in while the card counts as hovered, which is what keeps the card from
    /// snapping shut the moment the pointer drifts a few pixels off it.
    /// </summary>
    private const double PeekHoverBand = 44;

    /// <summary>
    /// Live width while the card is being dragged, before it is committed. The
    /// setting saves synchronously on change, so writing it per pointer-move
    /// would rewrite settings.json dozens of times a drag.
    /// </summary>
    private double? _peekDragWidth;

    private double PeekCardWidth =>
        _peekDragWidth ?? BrowserSettings.ClampPeekWidth(BrowserSettings.Shared.PeekWidth);

    private double PeekHostWidth => PeekCardWidth + PeekHoverBand;
    private bool _peekReady;

    private double ClosedCardOffset =>
        Store.SidebarOnLeft ? -(PeekCardWidth + PeekInset + 16) : (PeekCardWidth + PeekInset + 16);

    /// <summary>Apply themed brushes to the peek card.</summary>
    private void EnsurePeekReady()
    {
        if (_peekReady) return;
        if (Content?.XamlRoot is null) return;

        ApplyChromeTheme();
        // Depth gives the ThemeShadow something to cast.
        SidebarPeekCard.Translation = new System.Numerics.Vector3(0, 0, 32);
        _peekReady = true;
    }

    // ── Title bar chrome ────────────────────────────────────────────────────

    /// <summary>
    /// Line the title bar's first column up with the docked sidebar, so the nav
    /// buttons that follow it start where the page starts — and travel with the
    /// sidebar's edge when it is dragged, the way Arc's do.
    ///
    /// <para>
    /// Only while the sidebar is docked on the left. Docked right or hidden,
    /// there is nothing on that side to line up with, and the column falls back
    /// to the width of the app icon and toggle it holds.
    /// </para>
    /// </summary>
    private void SyncTitleBarSidebarColumn()
        => SyncTitleBarSidebarColumn(BrowserSettings.Shared.SidebarWidth);

    private void SyncTitleBarSidebarColumn(double sidebarWidth)
    {
        TitleBarSidebarColumn.Width = Store.SidebarVisible && Store.SidebarOnLeft
            ? new GridLength(sidebarWidth)
            : GridLength.Auto;
    }

    /// <summary>
    /// Follow the selected tab: the address, and whether back, forward and copy
    /// have anything to act on.
    /// </summary>
    private void UpdateTitleBarChrome()
    {
        var tab = Store.SelectedTab;

        TitleBarBack.IsEnabled = tab?.CanGoBack ?? false;
        TitleBarForward.IsEnabled = tab?.CanGoForward ?? false;

        // DisplayUrl is empty on the new tab page and on skew:// pages, which is
        // where the placeholder belongs — there is no address to show yet.
        string host = tab?.DisplayUrl ?? "";
        bool hasAddress = !string.IsNullOrEmpty(host);

        TitleBarUrl.Text = hasAddress ? host : "Search or enter address";
        TitleBarUrl.Opacity = hasAddress ? 0.9 : 0.5;

        // Nothing to copy, reload or adjust on the new tab page: it is Skew's
        // own blank page, not a site. The ghost style dims a disabled button's
        // content, so these read as unavailable rather than merely inert.
        bool onPage = !IsHomepageTab;
        TitleBarCopyLink.IsEnabled = hasAddress;
        TitleBarReload.IsEnabled = onPage;
        TitleBarPageOptions.IsEnabled = onPage;

        // Reload doubles as stop while the page is loading, the way the old
        // sidebar header's did.
        bool loading = tab?.IsLoading ?? false;
        TitleBarReloadIcon.Glyph = loading ? "" : "";
        ToolTipService.SetToolTip(TitleBarReload, loading ? "Stop" : "Reload");
    }

    /// <summary>
    /// Hold the right-hand column open by exactly as much as the system's
    /// caption buttons take, so nothing in the bar ends up underneath them.
    /// RightInset is in physical pixels, hence the scale.
    /// </summary>
    private void SyncCaptionButtonSpacer()
    {
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
        if (scale <= 0) scale = 1;
        CaptionButtonSpacer.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
    }

    // ── Downloads (up from the sidebar's bottom bar) ──────────────────────

    /// <summary>Cleared when a download starts, set once the panel is opened.</summary>
    private bool _downloadsAcknowledged;

    /// <summary>
    /// Pulse the button while something is downloading that has not been looked
    /// at. Subscribed from the constructor so it is running before the first
    /// download can start.
    /// </summary>
    private void WatchDownloads()
    {
        DownloadStore.Shared.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(DownloadStore.ActivityToken))
            {
                _downloadsAcknowledged = false;
                UpdateDownloadPulse();
            }
            else if (e.PropertyName == nameof(DownloadStore.HasActiveDownloads))
            {
                UpdateDownloadPulse();
            }
        };
    }

    private void UpdateDownloadPulse()
    {
        RootGrid.DispatcherQueue.TryEnqueue(() =>
        {
            if (DownloadStore.Shared.HasActiveDownloads && !_downloadsAcknowledged)
            {
                DownloadPulseAnim.Begin();
            }
            else
            {
                DownloadPulseAnim.Stop();
                TitleBarDownloads.ClearValue(UIElement.OpacityProperty);
                TitleBarDownloads.Opacity = 1.0;
            }
        });
    }

    private void TitleBarDownloadsFlyout_Opened(object sender, object e)
    {
        _downloadsAcknowledged = true;
        UpdateDownloadPulse();
    }

    private void TitleBarBack_Click(object sender, RoutedEventArgs e) => Store.GoBack();

    private void TitleBarForward_Click(object sender, RoutedEventArgs e) => Store.GoForward();

    private void TitleBarReload_Click(object sender, RoutedEventArgs e)
    {
        if (Store.SelectedTab?.IsLoading == true) Store.Stop();
        else Store.Reload();
    }

    private void TitleBarCopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (!CopyLinkToClipboard()) return;

        // Say it landed. The button's whole job is a state change with nothing
        // on screen to show for it, so the glyph becomes a tick for a moment.
        TitleBarCopyLinkIcon.Glyph = "";   // Accept
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        timer.Tick += (s, args) =>
        {
            timer.Stop();
            TitleBarCopyLinkIcon.Glyph = "";   // Link
        };
        timer.Start();
    }

    /// <summary>
    /// Put the current tab's URL on the clipboard.
    ///
    /// <para>
    /// The call throws (CLIPBRD_E_CANT_OPEN) while another app holds the
    /// clipboard open, which is what made the button look inert — so a failure
    /// is worth one retry, with a fresh package each time since a set one is
    /// not reusable.
    /// </para>
    ///
    /// <para>
    /// No Flush. It is the documented way to leave the data behind after the
    /// app exits, but in an unpackaged desktop app it fails with E_UNEXPECTED
    /// and the failure arrives as a stowed exception — a fail-fast that takes
    /// the process with it before any handler, including App.UnhandledException,
    /// gets a look. A copy that does not outlive the browser is the lesser
    /// problem; Windows' own clipboard history still captures it.
    /// </para>
    /// </summary>
    private bool CopyLinkToClipboard()
    {
        string? url = Store.SelectedTab?.UrlString;
        if (string.IsNullOrEmpty(url)) return false;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(url);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                return true;
            }
            catch (Exception)
            {
                // Whoever had it open is usually done by the next pass.
            }
        }
        return false;
    }

    /// <summary>
    /// Standing in for an editable field: the launcher is where an address gets
    /// typed today, so the address chip opens it rather than pretending to be a
    /// text box.
    /// </summary>
    private void TitleBarAddress_Click(object sender, RoutedEventArgs e)
        => Store.ToggleLauncher();

    // ── Page options popover (the sliders mark) ───────────────────────────

    /// <summary>
    /// Fill the popover from live state each time it opens: the extension list
    /// and the PiP switch are shared with Settings, and the security chip reads
    /// the tab that is in front right now.
    /// </summary>
    private void PageOptionsFlyout_Opened(object sender, object e)
    {
        PageOptionsExtensions.ItemsSource = ExtensionStore.Shared.Extensions;

        // Assigned, not bound: this fires Toggled, and a bound switch would
        // write the value it was just handed straight back through Settings.
        _syncingAutoPiP = true;
        PageOptionsAutoPiP.IsOn = BrowserSettings.Shared.AutoPiP;
        _syncingAutoPiP = false;

        UpdatePageOptionsSecurity();
    }

    /// <summary>
    /// The connection chip, on the omnibox's three states: https is secure,
    /// plain http is not, and anything else (skew: pages, files) is neither —
    /// there is no connection to speak for.
    /// </summary>
    private void UpdatePageOptionsSecurity()
    {
        string url = Store.SelectedTab?.UrlString ?? string.Empty;

        if (url.StartsWith("https", StringComparison.OrdinalIgnoreCase))
        {
            PageOptionsSecurityIcon.Glyph = "";   // Lock
            PageOptionsSecurityLabel.Text = "Secure";
            PageOptionsSecurityIcon.Foreground = PageOptionsSecurityLabel.Foreground =
                (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        }
        else if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            PageOptionsSecurityIcon.Glyph = "";   // Warning
            PageOptionsSecurityLabel.Text = "Not secure";
            PageOptionsSecurityIcon.Foreground = PageOptionsSecurityLabel.Foreground =
                (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        }
        else
        {
            PageOptionsSecurityIcon.Glyph = "";   // Page
            PageOptionsSecurityLabel.Text = "Local page";
            PageOptionsSecurityIcon.Foreground = PageOptionsSecurityLabel.Foreground =
                (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        }
    }

    /// <summary>
    /// Put the URL on the clipboard. Sharing a link is copying it: the system
    /// share sheet wants an HWND shim to reach a desktop window and then offers
    /// a target list nobody came here for, when the link itself is the thing.
    /// </summary>
    private void PageOptionsShare_Click(object sender, RoutedEventArgs e)
    {
        if (CopyLinkToClipboard()) FlashShareLabel("Link copied");
    }

    /// <summary>Say what happened, then go back to naming the action.</summary>
    private void FlashShareLabel(string message)
    {
        PageOptionsShareLabel.Text = message;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            PageOptionsShareLabel.Text = "Share URL";
        };
        timer.Start();
    }

    private bool _syncingAutoPiP;

    private void PageOptionsAutoPiP_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingAutoPiP) return;
        BrowserSettings.Shared.AutoPiP = PageOptionsAutoPiP.IsOn;
    }

    /// <summary>
    /// Where extensions come from: the store, in a tab. The omnibox already
    /// grows an "Add to Skew" button once a detail page is open, so this only
    /// has to get the user there.
    /// </summary>
    private void PageOptionsWebStore_Click(object sender, RoutedEventArgs e)
    {
        PageOptionsFlyout.Hide();
        Store.NewTab("https://chromewebstore.google.com/");
    }

    private void PageOptionsExtension_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string extensionId)
        {
            PageOptionsFlyout.Hide();
            string? popupUrl = ExtensionBackgroundManager.ActionPopupUrl(extensionId);
            if (popupUrl is null)
            {
                ExtensionBackgroundManager.Activate(extensionId);
                return;
            }

            ShowExtensionActionPopup(TitleBarPageOptions, extensionId, popupUrl);
        }
    }

    /// <summary>
    /// Open an extension's action popup anchored to a button anywhere in the
    /// chrome, or fire its onClicked when it has none. The omnibox's pinned
    /// buttons come through here so a click means the same thing there as it
    /// does in the page options popover — it was opening the popup as a tab.
    /// </summary>
    public void ActivateExtension(string extensionId, Button anchor)
    {
        string? popupUrl = ExtensionBackgroundManager.ActionPopupUrl(extensionId);
        if (popupUrl is null)
        {
            ExtensionBackgroundManager.Activate(extensionId);
            return;
        }
        ShowExtensionActionPopup(anchor, extensionId, popupUrl);
    }

    private void ShowExtensionActionPopup(Button anchor, string extensionId, string popupUrl)
    {
        CloseExtensionActionPopup();

        var browserView = new Controls.SkewBrowserView(popupUrl)
        {
            Width = 360,
            Height = 480,
            ExtensionTabId = ExtensionBackgroundManager.SelectedTabId
        };
        var flyout = new Flyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShouldConstrainToRootBounds = false,
            Content = browserView,
            FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
            {
                Setters =
                {
                    new Setter(Control.PaddingProperty, new Thickness(0)),
                    new Setter(Control.MinWidthProperty, 0d),
                    new Setter(Control.MaxWidthProperty, 1000d),
                    new Setter(Control.MinHeightProperty, 0d),
                    new Setter(Control.MaxHeightProperty, 1000d),
                    new Setter(Control.CornerRadiusProperty, new CornerRadius(10))
                }
            }
        };

        flyout.Closed += (_, _) => CloseExtensionActionPopup();
        _extensionActionPopupView = browserView;
        _extensionActionPopupFlyout = flyout;
        flyout.ShowAt(anchor);
        ExtensionDiagnostics.Write("action", extensionId, "Opened toolbar action popup.");
    }

    /// <summary>
    /// Fit the popup panel to the document inside it. Chrome sizes a popup to
    /// its own page; here the panel opens at a guess and the page reports what
    /// it actually needs, otherwise every popup carries dead space along its
    /// right and bottom edges.
    /// </summary>
    public void ResizeExtensionPopup(string extensionId, double width, double height)
    {
        Controls.SkewBrowserView? view = _extensionActionPopupView;
        if (view is null) return;

        // Chrome's own limits, and a floor so a page that measures zero while
        // it is still building does not collapse the panel.
        double clampedWidth = Math.Clamp(width, 200, 760);
        double clampedHeight = Math.Clamp(height, 100, 600);

        if (Math.Abs(view.Width - clampedWidth) < 1 && Math.Abs(view.Height - clampedHeight) < 1)
            return;

        view.Width = clampedWidth;
        view.Height = clampedHeight;
        ExtensionDiagnostics.Write("action", extensionId,
            $"Popup resized to {clampedWidth:n0}x{clampedHeight:n0}.");
    }

    private void CloseExtensionActionPopup()
    {
        Flyout? flyout = _extensionActionPopupFlyout;
        Controls.SkewBrowserView? browserView = _extensionActionPopupView;
        _extensionActionPopupFlyout = null;
        _extensionActionPopupView = null;
        if (flyout?.IsOpen == true) flyout.Hide();
        browserView?.CloseBrowser();
    }

    /// <summary>
    /// The unpacked-folder import Settings offers, reachable from the page the
    /// extension would run on. The store is the other route, on the + above.
    /// </summary>
    private async void PageOptionsAddExtension_Click(object sender, RoutedEventArgs e)
    {
        PageOptionsFlyout.Hide();

        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        string? error = await ExtensionStore.Shared.ImportExtensionAsync(folder.Path);
        if (error is null) return;

        await new ContentDialog
        {
            Title = "Extension Error",
            Content = error,
            CloseButtonText = "OK",
            XamlRoot = RootGrid.XamlRoot,
        }.ShowAsync();
    }

    private void PageOptionsClearCache_Click(object sender, RoutedEventArgs e)
    {
        PageOptionsFlyout.Hide();
        Store.SelectedTab?.ClearBrowserCache();
    }

    private void PageOptionsClearCookies_Click(object sender, RoutedEventArgs e)
    {
        PageOptionsFlyout.Hide();
        Store.SelectedTab?.ClearBrowserCookies();
    }

    private void PageOptionsManageExtensions_Click(object sender, RoutedEventArgs e)
    {
        PageOptionsFlyout.Hide();
        Store.SettingsVisible = true;
    }

    private void PageOptionsSiteSettings_Click(object sender, RoutedEventArgs e)
    {
        PageOptionsFlyout.Hide();
        Store.SettingsVisible = true;
    }

    /// <summary>
    /// Tint the system caption buttons to the active theme.
    ///
    /// <para>
    /// They are drawn by the window, not by XAML, so ElementTheme does not reach
    /// them: left alone they keep the system's own foreground and paint an
    /// opaque backdrop that cuts a grey block out of the Mica. Transparent
    /// backgrounds let the chrome surface run behind them; the hover and pressed
    /// washes stay because a caption button with no hover state does not read as
    /// a button.
    /// </para>
    /// </summary>
    private void ApplyTitleBarColors()
    {
        var bar = AppWindow.TitleBar;
        bool dark = ThemeService.Instance.IsDark;

        var fg = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        var wash = dark
            ? Windows.UI.Color.FromArgb(24, 255, 255, 255)
            : Windows.UI.Color.FromArgb(24, 0, 0, 0);
        var press = dark
            ? Windows.UI.Color.FromArgb(40, 255, 255, 255)
            : Windows.UI.Color.FromArgb(40, 0, 0, 0);

        bar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonForegroundColor = fg;
        bar.ButtonInactiveForegroundColor = dark
            ? Windows.UI.Color.FromArgb(140, 255, 255, 255)
            : Windows.UI.Color.FromArgb(140, 0, 0, 0);
        bar.ButtonHoverBackgroundColor = wash;
        bar.ButtonHoverForegroundColor = fg;
        bar.ButtonPressedBackgroundColor = press;
        bar.ButtonPressedForegroundColor = fg;
    }

    /// <summary>Orient the peek host and card to the active sidebar edge.</summary>
    private void LayoutPeek()
    {
        if (!_peekReady) return;

        double w = RootGrid.ActualWidth;
        double h = RootGrid.ActualHeight;
        if (w <= 0 || h <= 0) return;

        bool left = Store.SidebarOnLeft;
        var edge = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;

        // Follow the docked width, which the user can drag.
        PeekHost.Width = PeekHostWidth;
        SidebarPeekCard.Width = PeekCardWidth;

        PeekHost.HorizontalAlignment = edge;
        // Clip so the card slides out under the window edge rather than past it.
        PeekHost.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, PeekHostWidth, h)
        };

        PeekEdgeStrip.HorizontalAlignment = edge;
        SidebarPeekCard.HorizontalAlignment = edge;
        // The grip sits on the card's page-facing edge, opposite the docked one.
        PeekResizeGrip.HorizontalAlignment = left
            ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        // Top inset matches the web card's, which is none: the card beside it
        // meets the title bar, and the peek hanging 8 below it was the gap that
        // showed. Sides and bottom keep the Mac's 8, so it still floats.
        SidebarPeekCard.Margin = left
            ? new Thickness(PeekInset, WebCardTopInset, 0, PeekInset)
            : new Thickness(0, WebCardTopInset, PeekInset, PeekInset);

        // Settle into the resting position unless mid-peek.
        if (!_isPeeking)
        {
            PeekCardTranslate.X = ClosedCardOffset;
            SidebarPeekCard.IsHitTestVisible = false;
        }
    }

    /// <summary>
    /// Paint the chrome surface, the web card, and the peek card from the palette.
    ///
    /// <para>
    /// This is RootView.swift's <c>.background</c> plus <c>webCard</c>. Mica gives
    /// the behind-window blur that <c>NSVisualEffectView(.sidebar)</c> provides on
    /// the Mac; ChromeTint carries the palette wash over it at the same 0.55; and
    /// the card sits on that one surface, so its inset gaps and the sidebar show no
    /// colour step between them. The peek card matches SidebarPeek.swift: sidebar
    /// colour at 0.85 over the blur, hairline border at 0.7.
    /// </para>
    /// </summary>
    private void ApplyChromeTheme()
    {
        var p = ThemeService.Instance.Palette;

        ChromeTint.Fill = p.Sidebar.WithOpacity(0.55).ToBrush();

        WebContentBorder.Background = p.Card.ToBrush();
        // No BorderBrush: the card's hairline is off (BorderThickness 0), since
        // against the live page it read as a seam. The peek card keeps its own.
        // Depth so the ThemeShadow reads as the Mac's soft drop shadow hugging the
        // rounded corners, rather than the flat default.
        WebContentBorder.Translation = new System.Numerics.Vector3(0, 0, 16);

        // Solid, not acrylic. WinUI's acrylic recipe composites a noise texture
        // along with the blur and tint — deliberate, to stop large translucent
        // surfaces banding — and at the 0.85 tint this ran at, the tint hid most
        // of the blur while the noise stayed, so the card read as grainy against
        // the smooth web card beside it.
        //
        // The cost is that the peek card no longer shows the page through it,
        // which was the reason it was acrylic and the docked sidebar is not.
        SidebarPeekCard.Background = p.Sidebar.ToBrush();
        SidebarPeekCard.BorderBrush = p.Border.WithOpacity(0.7).ToBrush();
    }

    private void PeekEdgeStrip_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (Store.SidebarVisible) return;
        // The strip is part of the keep-open zone: cancel any pending close (so
        // sliding from the card onto the very window edge doesn't retract it) and
        // open the peek if it isn't already.
        _peekCloseTimer.Stop();
        EnterPeek();
    }

    private void PeekEdgeStrip_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isPeeking) return;
        if (!_peekCloseTimer.IsEnabled)
            _peekCloseTimer.Start();
    }

    private void SidebarPeekCard_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => _peekCloseTimer.Stop();

    private void SidebarPeekCard_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isPeeking) return;
        if (!_peekCloseTimer.IsEnabled)
            _peekCloseTimer.Start();
    }

    /// <summary>
    /// True while the peek is being held open because the new tab page is
    /// showing, rather than because the pointer is near the edge.
    /// </summary>
    private bool _homepagePeekLatched;

    private BrowserTab? _urlWatchedTab;

    /// <summary>
    /// Follow the selected tab's URL, so navigating away from the new tab page
    /// releases the peek. Selection alone is not enough — typing a URL into the
    /// same tab changes where it points without changing which tab is selected.
    /// </summary>
    private void WatchSelectedTabUrl()
    {
        if (_urlWatchedTab is not null)
            _urlWatchedTab.PropertyChanged -= SelectedTabUrlChanged;

        _urlWatchedTab = Store.SelectedTab;

        if (_urlWatchedTab is not null)
            _urlWatchedTab.PropertyChanged += SelectedTabUrlChanged;
    }

    private void SelectedTabUrlChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTab.UrlString))
            SyncHomepagePeek();

        // The title bar rides on this subscription rather than its own: it is
        // the one that is properly torn down when the selection moves, so the
        // chrome cannot end up following a tab that is no longer on screen.
        if (e.PropertyName is nameof(BrowserTab.UrlString)
                           or nameof(BrowserTab.DisplayUrl)
                           or nameof(BrowserTab.CanGoBack)
                           or nameof(BrowserTab.CanGoForward)
                           or nameof(BrowserTab.IsLoading))
        {
            UpdateTitleBarChrome();
        }
    }

    /// <summary>The selected tab is Skew's own new tab page.</summary>
    private bool IsHomepageTab =>
        Store.SelectedTab?.UrlString?.StartsWith("skew://newtab", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Hold the peek sidebar out on the new tab page, the way Arc does, and let
    /// it retract once the tab navigates somewhere real.
    ///
    /// <para>
    /// Only applies while the sidebar is hidden — docked, there is nothing to
    /// peek. The latch is what stops the hover close-timer from pulling it back
    /// in while the pointer is elsewhere.
    /// </para>
    /// </summary>
    /// <param name="snap">
    /// True when the sidebar has just stopped being docked. The peek is taking
    /// over from a sidebar that was already on screen in that spot, so it should
    /// be there already rather than sliding in from off-window — the slide is
    /// for a peek arriving from nothing, which is what a hover or a navigation
    /// to the new tab page is.
    /// </param>
    private void SyncHomepagePeek(bool snap = false)
    {
        bool shouldLatch = !Store.SidebarVisible && IsHomepageTab;
        if (shouldLatch == _homepagePeekLatched) return;

        _homepagePeekLatched = shouldLatch;
        if (shouldLatch)
        {
            EnsurePeekReady();
            LayoutPeek();
            EnterPeek(animate: !snap);
        }
        else
        {
            _peekCloseTimer.Start();
        }
    }

    /// <summary>
    /// Show or hide the peek host without collapsing it, so its ItemsRepeaters
    /// stay laid out and keep their tiles and folders realized.
    /// </summary>
    private void SetPeekHostShown(bool shown)
    {
        PeekHost.Opacity = shown ? 1 : 0;
        PeekHost.IsHitTestVisible = shown;
    }

    private void EnterPeek(bool animate = true)
    {
        if (_isPeeking) return;
        _isPeeking = true;
        // On screen now, so this is the copy that may take focus.
        PeekSidebar.IsLive = true;
        _peekCloseTimer.Stop();

        if (animate)
        {
            AnimatePeek(open: true);
            return;
        }

        // Already open, no travel. Any in-flight slide has to be stopped first,
        // or it would finish onto its own destination and undo this.
        _sidebarAnimStoryboard?.Stop();
        PeekCardTranslate.X = 0;
        SidebarPeekCard.IsHitTestVisible = true;
    }

    private void ExitPeek()
    {
        if (!_isPeeking) return;
        _isPeeking = false;
        PeekSidebar.IsLive = false;
        AnimatePeek(open: false);
    }

    /// <summary>
    /// Close-timer tick. If a context menu / flyout opened from the peek sidebar
    /// is showing, keep the peek open and re-check next tick — opening the menu
    /// moves the pointer off the card, which would otherwise retract the peek out
    /// from under its own menu. Closes once the pointer is away and no menu is up.
    /// </summary>
    private void OnPeekCloseTick()
    {
        if (IsPeekFlyoutOpen()) return; // DispatcherTimer repeats; re-check next tick
        _peekCloseTimer.Stop();
        // Held open by the new tab page, not by the pointer.
        if (_homepagePeekLatched) return;
        ExitPeek();
    }

    /// <summary>True while a flyout/context menu opened from the peek is showing.</summary>
    private bool IsPeekFlyoutOpen()
    {
        if (Content?.XamlRoot is null) return false;
        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot))
        {
            if (popup.Child is Microsoft.UI.Xaml.Controls.MenuFlyoutPresenter
                || popup.Child is Microsoft.UI.Xaml.Controls.FlyoutPresenter)
                return true;
        }
        return false;
    }

    /// <summary>Slide the card in/out and morph the edge handle (capsule ⇄ chevron).</summary>
    private void AnimatePeek(bool open)
    {
        // Capture the live values BEFORE stopping. These are dependent animations,
        // so the property getters return the current animated value — animating
        // from here means no snap, even mid-flight.
        double cardNow = PeekCardTranslate.X;

        _sidebarAnimStoryboard?.Stop();

        // Commit the captured values as the local base so Stop() above can't snap
        // the card to a stale resting value — that snap was why the close had no
        // visible out-animation (the card jumped shut, then "animated" in place).
        PeekCardTranslate.X = cardNow;

        double cardTo = open ? 0 : ClosedCardOffset;
        SidebarPeekCard.IsHitTestVisible = open;

        var dur = TimeSpan.FromMilliseconds(220);
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();

        void Add(Microsoft.UI.Xaml.DependencyObject target, string path, double from, double to, bool eased)
        {
            var a = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = from,
                To = to,
                Duration = dur,
                EnableDependentAnimation = true
            };
            if (eased)
            {
                a.EasingFunction = new Microsoft.UI.Xaml.Media.Animation.ExponentialEase
                {
                    EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
                    Exponent = 4
                };
            }
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(a, target);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(a, path);
            sb.Children.Add(a);
        }

        Add(PeekCardTranslate, "X", cardNow, cardTo, eased: true);

        // Commit the destination as the local base so the next Stop() starts clean.
        sb.Completed += (s, e) => PeekCardTranslate.X = cardTo;

        _sidebarAnimStoryboard = sb;
        sb.Begin();
    }
}
