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

    public static MainWindow Instance { get; private set; }

    // Removed Acrylic fields

    private bool _isPeeking = false;
    private DispatcherTimer _peekCloseTimer;
    private Microsoft.UI.Xaml.Media.Animation.Storyboard _sidebarAnimStoryboard;

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
        appWindow.Title = "Mori";

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

        // Wire up store state to UI
        Store.PropertyChanged += Store_PropertyChanged;

        // Set initial sidebar data context. There are two MoriSidebar instances
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
        Mori.Cef.MoriBrowserHostChannel.ShortcutHandler = HandleCefShortcut;
        Mori.Cef.MoriBrowserHostChannel.DownloadUpdateHandler = (id, url, path, received, total, percent, speed, complete, canceled) =>
        {
            DispatcherQueue.TryEnqueue(() => 
            {
                var filename = System.IO.Path.GetFileName(path);
                Mori.Models.DownloadStore.Shared.Ingest(id, url, filename, path, received, total, percent, speed, complete, canceled, !complete && !canceled);
            });
        };

        // Media markers (__MORI_MEDIA__) from each page's injected agent feed the
        // sidebar media player. The browser id maps back to its owning tab inside
        // MediaController when issuing transport commands.
        Mori.Cef.MoriBrowserHostChannel.ConsoleMarkerHandler = (browser, message) =>
        {
            if (message is null) return;
            const string mediaPrefix = "__MORI_MEDIA__";
            if (message.StartsWith(mediaPrefix, StringComparison.Ordinal))
            {
                int bid = browser.Identifier;
                string json = message.Substring(mediaPrefix.Length);
                DispatcherQueue.TryEnqueue(() => Mori.Models.MediaController.Shared.Ingest(bid, json));
            }
        };

        // Show the selected tab's CEF browser view in the web-content card.
        ShowSelectedBrowserView();
        WatchSelectedTabUrl();
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
                if (Store.LauncherVisible)
                {
                    LauncherHost.Visibility = Visibility.Visible;
                    SyncLauncherSize();
                    AnimateScrim(LauncherScrim, to: 0.28);
                    Launcher.FocusSearchBox();
                }
                else if (LauncherHost.Visibility == Visibility.Visible)
                {
                    AnimateScrim(LauncherScrim, to: 0);
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
        if (LauncherHost.Visibility == Visibility.Visible)
            SyncLauncherSize();

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

    /// <summary>Size the launcher to the web card, which it floats over.</summary>
    private void SyncLauncherSize()
    {
        Launcher.Width = WebContentBorder.ActualWidth;
        Launcher.Height = WebContentBorder.ActualHeight;
    }

    private void LauncherScrim_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        => Store.LauncherVisible = false;

    private void SettingsScrim_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        => Store.SettingsVisible = false;

    /// <summary>
    /// Fade a scrim over the live page. Only possible now that the page is a
    /// XAML layer — a scrim could never have covered the old child window.
    /// </summary>
    private static void AnimateScrim(Microsoft.UI.Xaml.Shapes.Rectangle scrim, double to)
    {
        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = to,
            Duration = new Duration(MoriMotion.Reveal),
            EnableDependentAnimation = true,
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
            },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, scrim);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        sb.Children.Add(fade);
        sb.Begin();
    }

    /// <summary>
    /// Swap the web-content host to display the currently selected tab's CEF
    /// browser view (mac shows the selected tab's MoriBrowserView). Other tabs'
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
            if (child is Controls.MoriBrowserView browser)
                browser.SetWebWindowVisible(ReferenceEquals(browser, view) && !tab.DidFail);
        }

        // Route popup/target=_blank requests into new Mori tabs (mac OnOpenURLFromTab).
        view.RequestsNewTab -= OnViewRequestsNewTab;
        view.RequestsNewTab += OnViewRequestsNewTab;
    }

    private void OnViewRequestsNewTab(string url)
    {
        DispatcherQueue.TryEnqueue(() => Store.NewTab(url));
    }

    /// <summary>
    /// Maps a CEF key event (Windows virtual-key + modifiers) to Mori's shortcut
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
                        case Windows.System.VirtualKey.K: Store.ToggleAIPanel(); break;
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
        else AIPanelColumn.Width = length;

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

        // Commit once, at the end of the drag — one file write instead of many.
        double finalWidth = Store.SidebarOnLeft
            ? SidebarColumn.Width.Value
            : AIPanelColumn.Width.Value;
        BrowserSettings.Shared.SidebarWidth = BrowserSettings.ClampSidebarWidth(finalWidth);

        // Keep the peek card in step with the docked width.
        if (_peekReady) LayoutPeek();
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

        // Position sidebar and AI panel based on side preference. The gripper
        // rides with the sidebar and always hugs its page-facing edge — the
        // right edge when docked left, the left edge when docked right.
        if (Store.SidebarOnLeft)
        {
            Grid.SetColumn(SidebarBorder, 0);
            Grid.SetColumn(SidebarResizeGrip, 0);
            SidebarResizeGrip.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(AIPanel, 2);
        }
        else
        {
            Grid.SetColumn(SidebarBorder, 2);
            Grid.SetColumn(SidebarResizeGrip, 2);
            SidebarResizeGrip.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetColumn(AIPanel, 0);
        }

        // Set column widths. The sidebar's is whatever the user last dragged to.
        var sidebarWidth = new GridLength(BrowserSettings.Shared.SidebarWidth);

        SidebarColumn.Width = Store.SidebarOnLeft
            ? (Store.SidebarVisible ? sidebarWidth : new GridLength(0))
            : (Store.AiPanelVisible ? new GridLength(360) : new GridLength(0));

        AIPanelColumn.Width = Store.SidebarOnLeft
            ? (Store.AiPanelVisible ? new GridLength(360) : new GridLength(0))
            : (Store.SidebarVisible ? sidebarWidth : new GridLength(0));

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

        // The docked sidebar owns the visible state; the floating peek popup owns
        // the hidden state.
        SidebarBorder.Visibility = Store.SidebarVisible ? Visibility.Visible : Visibility.Collapsed;

        // No reveal button to show or hide: the title bar's toggle is always
        // there, whichever state the sidebar is in. Its mark still points at the
        // docked edge, which is the one thing the reveal button did that the
        // toggle inherited.
        TitleBarSidebarToggleFlip.ScaleX = Store.SidebarOnLeft ? -1 : 1;

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
            SyncHomepagePeek();
        }

        // AI Panel
        AIPanel.Visibility = Store.AiPanelVisible
            ? Visibility.Visible
            : Visibility.Collapsed;

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
    // Fixed rather than following the docked sidebar's dragged width — the peek
    // is a transient overlay, so resizing the docked sidebar should not move it.
    // 224 DIP is 280 physical px at 125% scaling.
    private const double PeekCardWidth = 224;
    private const double PeekInset = 8;
    /// <summary>Mac panelBand — the card plus the hover margin beyond it.</summary>
    private const double PeekHostWidth = PeekCardWidth + 44;
    private bool _peekReady;

    private double ClosedCardOffset =>
        Store.SidebarOnLeft ? -(PeekCardWidth + PeekInset + 16) : (PeekCardWidth + PeekInset + 16);
    private double RestingHandleOffset => Store.SidebarOnLeft ? 6 : -6;
    private double OpenHandleOffset =>
        Store.SidebarOnLeft ? -(PeekCardWidth + PeekInset + 8) : (PeekCardWidth + PeekInset + 8);

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

        // DisplayUrl is empty on the new tab page and on mori:// pages, which is
        // where the placeholder belongs — there is no address to show yet.
        string host = tab?.DisplayUrl ?? "";
        bool hasAddress = !string.IsNullOrEmpty(host);

        TitleBarUrl.Text = hasAddress ? host : "Search or enter address";
        TitleBarUrl.Opacity = hasAddress ? 0.9 : 0.5;
        TitleBarCopyLink.IsEnabled = hasAddress;

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

    private void TitleBarBack_Click(object sender, RoutedEventArgs e) => Store.GoBack();

    private void TitleBarForward_Click(object sender, RoutedEventArgs e) => Store.GoForward();

    private void TitleBarReload_Click(object sender, RoutedEventArgs e)
    {
        if (Store.SelectedTab?.IsLoading == true) Store.Stop();
        else Store.Reload();
    }

    private void TitleBarCopyLink_Click(object sender, RoutedEventArgs e)
    {
        string? url = Store.SelectedTab?.UrlString;
        if (string.IsNullOrEmpty(url)) return;

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(url);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    /// <summary>
    /// Standing in for an editable field: the launcher is where an address gets
    /// typed today, so the address chip opens it rather than pretending to be a
    /// text box.
    /// </summary>
    private void TitleBarAddress_Click(object sender, RoutedEventArgs e)
        => Store.ToggleLauncher();

    private void TitleBarPageOptions_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        Mori.Helpers.MenuBuilder.BuildSidebarMenu(flyout);
        flyout.ShowAt((FrameworkElement)sender);
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
        PeekEdgeHandle.HorizontalAlignment = edge;
        SidebarPeekCard.HorizontalAlignment = edge;
        SidebarPeekCard.Margin = left ? new Thickness(8, 8, 0, 8) : new Thickness(0, 8, 8, 8);
        PeekHandleChevron.Glyph = left ? "" : ""; // point toward the page

        // Settle into the resting position unless mid-peek.
        if (!_isPeeking)
        {
            PeekCardTranslate.X = ClosedCardOffset;
            SidebarPeekCard.IsHitTestVisible = false;
            PeekHandleTranslate.X = RestingHandleOffset;
            PeekHandleCapsule.Opacity = 0.22;
            PeekHandleChevron.Opacity = 0;
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
        WebContentBorder.BorderBrush = p.Border.WithOpacity(0.7).ToBrush();
        // Depth so the ThemeShadow reads as the Mac's soft drop shadow hugging the
        // rounded corners, rather than the flat default.
        WebContentBorder.Translation = new System.Numerics.Vector3(0, 0, 16);

        var sidebar = p.Sidebar.ToColor();
        SidebarPeekCard.Background = new Microsoft.UI.Xaml.Media.AcrylicBrush
        {
            TintColor = sidebar,
            TintOpacity = 0.85,
            FallbackColor = sidebar,
        };
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

    /// <summary>The selected tab is Mori's own new tab page.</summary>
    private bool IsHomepageTab =>
        Store.SelectedTab?.UrlString?.StartsWith("mori://newtab", StringComparison.OrdinalIgnoreCase) == true;

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
    private void SyncHomepagePeek()
    {
        bool shouldLatch = !Store.SidebarVisible && IsHomepageTab;
        if (shouldLatch == _homepagePeekLatched) return;

        _homepagePeekLatched = shouldLatch;
        if (shouldLatch)
        {
            EnsurePeekReady();
            LayoutPeek();
            EnterPeek();
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

    private void EnterPeek()
    {
        if (_isPeeking) return;
        _isPeeking = true;
        _peekCloseTimer.Stop();
        AnimatePeek(open: true);

    }

    private void ExitPeek()
    {
        if (!_isPeeking) return;
        _isPeeking = false;
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
        double handleNow = PeekHandleTranslate.X;
        double capNow = PeekHandleCapsule.Opacity;
        double chevNow = PeekHandleChevron.Opacity;

        _sidebarAnimStoryboard?.Stop();

        // Commit the captured values as the local base so Stop() above can't snap
        // the card to a stale resting value — that snap was why the close had no
        // visible out-animation (the card jumped shut, then "animated" in place).
        PeekCardTranslate.X = cardNow;
        PeekHandleTranslate.X = handleNow;
        PeekHandleCapsule.Opacity = capNow;
        PeekHandleChevron.Opacity = chevNow;

        double cardTo = open ? 0 : ClosedCardOffset;
        SidebarPeekCard.IsHitTestVisible = open;
        double handleTo = open ? OpenHandleOffset : RestingHandleOffset;
        double capTo = open ? 0 : 0.22;
        double chevTo = open ? 1 : 0;

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
        Add(PeekHandleTranslate, "X", handleNow, handleTo, eased: true);
        Add(PeekHandleCapsule, "Opacity", capNow, capTo, eased: false);
        Add(PeekHandleChevron, "Opacity", chevNow, chevTo, eased: false);

        // Commit the destination as the local base so the next Stop() starts clean.
        sb.Completed += (s, e) =>
        {
            PeekCardTranslate.X = cardTo;
            PeekHandleTranslate.X = handleTo;
            PeekHandleCapsule.Opacity = capTo;
            PeekHandleChevron.Opacity = chevTo;
        };

        _sidebarAnimStoryboard = sb;
        sb.Begin();
    }
}
