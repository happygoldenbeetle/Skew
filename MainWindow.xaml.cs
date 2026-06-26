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

        // Custom title bar — extend into content, no separate bar
        ExtendsContentIntoTitleBar = true;

        // Set window size and icon
        var appWindow = AppWindow;
        appWindow.SetIcon("Assets/AppIcon.ico");
        appWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));
        appWindow.Title = "Mori";

        // 100ms timer to detect trackpad release
        _swipeResetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _swipeResetTimer.Tick += (s, e) => EvaluateAndResetSwipe();

        // Use OverlappedPresenter for a real, chrome-less window
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
            presenter.SetBorderAndTitleBar(true, false);
        }

        // Recreate the floating peek popup whenever the window maximizes/restores
        // (the windowed popup otherwise stops tracking the owner's bounds).
        appWindow.Changed += AppWindow_Changed;

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

        // Pre-warm the peek popup once the visual tree (and XamlRoot) is ready,
        // so the first hover doesn't pay the popup-creation lag.
        RootGrid.Loaded += (s, e) =>
        {
            EnsurePeekReady();
            UpdateColumnLayout();
        };

        // Keep the floating peek card's themed brushes in sync with light/dark.
        ThemeService.Instance.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ThemeService.Palette))
                DispatcherQueue.TryEnqueue(ApplyPeekTheme);
        };

        // Apply initial layout state
        UpdateColumnLayout();

        // Apply theme
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = Microsoft.UI.Xaml.ElementTheme.Dark;
            ThemeService.Instance.SetTheme(Microsoft.UI.Xaml.ElementTheme.Dark);
        }

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

        // Show the selected tab's CEF browser view in the web-content card.
        ShowSelectedBrowserView();
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
                    LauncherPopup.IsOpen = true;
                    SyncLauncherPopupSize();
                    Launcher.FocusSearchBox();
                }
                else
                {
                    if (LauncherPopup.IsOpen)
                    {
                        Launcher.PlayHideAnimation(() => LauncherPopup.IsOpen = false);
                    }
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
                if (Store.SettingsVisible)
                {
                    SettingsFlyout.FocusPanel();
                }
                break;

            case nameof(BrowserStore.SelectedTab):
                UpdateLoadingBar();
                ShowSelectedBrowserView();
                break;
        }
    }





    private void Cef_TitleChanged(object sender, string title)
    {
    }


    private void WebContentBorder_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
        if (LauncherPopup.IsOpen)
        {
            SyncLauncherPopupSize();
        }
        
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
    }

    private void SyncLauncherPopupSize()
    {
        // Align the popup perfectly over the web content card
        // Because LauncherPopup is declared inside the Content grid, its (0,0) origin
        // is automatically aligned to the top-left of the web content card!
        Launcher.Width = WebContentBorder.ActualWidth;
        Launcher.Height = WebContentBorder.ActualHeight;
        
        LauncherPopup.HorizontalOffset = 0;
        LauncherPopup.VerticalOffset = 0;
    }

    /// <summary>
    /// Swap the web-content host to display the currently selected tab's CEF
    /// browser view (mac shows the selected tab's MoriBrowserView). Other tabs'
    /// views are detached so only the active engine surface composites.
    /// </summary>
    private void ShowSelectedBrowserView()
    {
        System.IO.File.AppendAllText("crash.log", "ShowSelectedBrowserView entered\n");
        var tab = Store.SelectedTab;
        if (tab is null) return;
        System.IO.File.AppendAllText("crash.log", $"SelectedTab is {tab.Title}. Getting BrowserView\n");
        var view = tab.BrowserView;
        System.IO.File.AppendAllText("crash.log", $"Got BrowserView. Checking Parent\n");
        if (view.Parent is Panel p)
        {
            if (p == WebContentHost)
            {
                System.IO.File.AppendAllText("crash.log", "View is already in WebContentHost\n");
                // Already shown
                return;
            }
            System.IO.File.AppendAllText("crash.log", "Removing view from old panel\n");
            p.Children.Remove(view);
        }
        System.IO.File.AppendAllText("crash.log", "Clearing WebContentHost\n");
        WebContentHost.Children.Clear();
        System.IO.File.AppendAllText("crash.log", "Adding view to WebContentHost\n");
        WebContentHost.Children.Add(view);
        System.IO.File.AppendAllText("crash.log", "View added to WebContentHost successfully\n");

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
                        case Windows.System.VirtualKey.L: Sidebar.FocusOmnibox(); break;
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

    private void UpdateColumnLayout()
    {
        // If user docks sidebar while peeking, cancel peek
        if (_isPeeking && Store.SidebarVisible)
        {
            _isPeeking = false;
            _peekCloseTimer.Stop();
            _sidebarAnimStoryboard?.Stop();
        }

        // Position sidebar and AI panel based on side preference
        if (Store.SidebarOnLeft)
        {
            Grid.SetColumn(SidebarBorder, 0);
            Grid.SetColumn(AIPanel, 2);
            Grid.SetColumn(SidebarRevealButton, 1);
            SidebarRevealButton.HorizontalAlignment = HorizontalAlignment.Left;
            SidebarRevealButton.Margin = new Thickness(16, 16, 0, 0);
        }
        else
        {
            Grid.SetColumn(SidebarBorder, 2);
            Grid.SetColumn(AIPanel, 0);
            Grid.SetColumn(SidebarRevealButton, 1);
            SidebarRevealButton.HorizontalAlignment = HorizontalAlignment.Right;
            SidebarRevealButton.Margin = new Thickness(0, 16, 16, 0);
        }

        // Set column widths
        SidebarColumn.Width = Store.SidebarOnLeft
            ? (Store.SidebarVisible ? new GridLength(260) : new GridLength(0))
            : (Store.AiPanelVisible ? new GridLength(360) : new GridLength(0));

        AIPanelColumn.Width = Store.SidebarOnLeft
            ? (Store.AiPanelVisible ? new GridLength(360) : new GridLength(0))
            : (Store.SidebarVisible ? new GridLength(260) : new GridLength(0));

        // The docked sidebar owns the visible state; the floating peek popup owns
        // the hidden state.
        SidebarBorder.Visibility = Store.SidebarVisible ? Visibility.Visible : Visibility.Collapsed;

        // Reveal button
        SidebarRevealButton.Visibility = Store.SidebarVisible
            ? Visibility.Collapsed
            : Visibility.Visible;

        // Peek overlay is live only while the sidebar is hidden.
        if (Store.SidebarVisible)
        {
            PeekHost.Visibility = Visibility.Collapsed;
        }
        else
        {
            EnsurePeekReady();
            LayoutPeek();
            if (_peekReady)
                PeekHost.Visibility = Visibility.Visible;
        }

        // AI Panel
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
                presenter.SetBorderAndTitleBar(true, false);

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

            if (!SwipePopup.IsOpen)
            {
                SwipePopup.IsOpen = true;
            }

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
    }

    // ── Sidebar peek (mac-style floating overlay) ───────────────────────────
    //
    // The peek sidebar floats above the page as an inset rounded card and does
    // NOT reflow the web content (mirroring SidebarPeek.swift). Because the CEF
    // browser is a native child HWND that composites above XAML, the card is
    // hosted in a pre-warmed Popup — the only surface that draws above it.

    // Geometry mirrors SidebarPeek.swift: a 256pt card inset 8pt from the edge.
    private const double PeekCardWidth = 256;
    private const double PeekInset = 8;
    private const double PeekHostWidth = 300; // mac panelBand
    private bool _peekReady;
    private OverlappedPresenterState _lastPresenterState = OverlappedPresenterState.Restored;

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (sender.Presenter is OverlappedPresenter p && p.State != _lastPresenterState)
        {
            _lastPresenterState = p.State;
            ReassertPeek();
        }
    }

    // The peek lives in a Popup whose backing window is a SIBLING child-window of
    // the CEF host window. CEF re-raises its own window to the top on focus/resize
    // (HwndHostWindow.BringWindowToTop / SWP_SHOWWINDOW), which sinks the peek
    // popup beneath the page after a few interactions. We counter that by raising
    // the popup's window back to the top of the sibling z-order whenever we peek.
    private nint _mainHwnd;
    private nint _peekPopupHwnd;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly nint HWND_TOP = nint.Zero;

    private const uint GW_OWNER = 4;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowProc cb, nint lParam);
    private delegate bool EnumWindowProc(nint hwnd, nint lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, System.Text.StringBuilder s, int n);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// Find the peek popup's backing window. A windowed WinUI Popup is hosted in a
    /// TOP-LEVEL window owned by the main window, with class
    /// "Microsoft.UI.Content.PopupWindowSiteBridge". We match the owned window of
    /// that class whose width is ~PeekHostWidth (distinguishes it from any other
    /// open popup such as the launcher/settings, which are full-card width).
    /// </summary>
    private nint FindPeekPopupHwnd()
    {
        if (_mainHwnd == nint.Zero) return nint.Zero;
        double scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
        int target = (int)Math.Round(PeekHostWidth * scale);
        nint found = nint.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            if (GetWindow(h, GW_OWNER) != _mainHwnd) return true;
            var sb = new System.Text.StringBuilder(128);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString().IndexOf("PopupWindowSiteBridge", StringComparison.Ordinal) < 0) return true;
            if (GetWindowRect(h, out var r) && Math.Abs((r.Right - r.Left) - target) <= 40)
            {
                found = h;
                return false; // stop
            }
            return true;
        }, nint.Zero);
        return found;
    }

    /// <summary>Raise the peek popup to the top so the card draws above the CEF page.</summary>
    private void RaisePeekPopup()
    {
        _peekPopupHwnd = FindPeekPopupHwnd();
        if (_peekPopupHwnd == nint.Zero) return;
        SetWindowPos(_peekPopupHwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private double ClosedCardOffset =>
        Store.SidebarOnLeft ? -(PeekCardWidth + PeekInset + 16) : (PeekCardWidth + PeekInset + 16);
    private double RestingHandleOffset => Store.SidebarOnLeft ? 6 : -6;
    private double OpenHandleOffset =>
        Store.SidebarOnLeft ? -(PeekCardWidth + PeekInset + 8) : (PeekCardWidth + PeekInset + 8);

    /// <summary>Open the pre-warmed popup once and apply themed brushes.</summary>
    private void EnsurePeekReady()
    {
        if (_peekReady) return;
        if (Content?.XamlRoot is null) return;

        SidebarPeekPopup.XamlRoot = Content.XamlRoot;
        _mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ApplyPeekTheme();
        // Depth gives the ThemeShadow something to cast.
        SidebarPeekCard.Translation = new System.Numerics.Vector3(0, 0, 32);
        if (!SidebarPeekPopup.IsOpen)
            SidebarPeekPopup.IsOpen = true;
        _peekReady = true;
    }

    /// <summary>Position the popup at the active sidebar edge and orient the card.</summary>
    private void LayoutPeek()
    {
        if (!_peekReady) return;

        double w = RootGrid.ActualWidth;
        double h = RootGrid.ActualHeight;
        if (w <= 0 || h <= 0) return;

        PeekHost.Height = h;
        // Clip to the host's own bounds so the card slides out *under* the window
        // edge instead of flashing in the popup window's shadow overhang (the
        // windowed popup is a few px wider than the host and sits at the edge).
        PeekHost.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, PeekHostWidth, h)
        };
        if (PeekHost.Visibility == Visibility.Visible)
            RaisePeekPopup();

        bool left = Store.SidebarOnLeft;
        var edge = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        PeekEdgeStrip.HorizontalAlignment = edge;
        PeekEdgeHandle.HorizontalAlignment = edge;
        SidebarPeekCard.HorizontalAlignment = edge;
        SidebarPeekCard.Margin = left ? new Thickness(8, 8, 0, 8) : new Thickness(0, 8, 8, 8);
        PeekHandleChevron.Glyph = left ? "" : ""; // point toward the page

        // Popup origin: hug the active edge.
        SidebarPeekPopup.HorizontalOffset = left ? 0 : w - PeekHostWidth;
        SidebarPeekPopup.VerticalOffset = 0;

        // Settle into the resting position unless mid-peek.
        if (!_isPeeking)
        {
            PeekCardTranslate.X = ClosedCardOffset;
            PeekHandleTranslate.X = RestingHandleOffset;
            PeekHandleCapsule.Opacity = 0.22;
            PeekHandleChevron.Opacity = 0;
        }
    }

    private void ApplyPeekTheme()
    {
        // Match SidebarPeek.swift: the panel is the sidebar color over a blur
        // (sidebar @ 0.85 + .sidebar material), with a border @ 0.7. We approximate
        // the blurred material with an AcrylicBrush tinted by the sidebar color and
        // fall back to the opaque sidebar color where acrylic isn't available.
        var p = ThemeService.Instance.Palette;
        var sidebar = p.Sidebar.ToColor();
        SidebarPeekCard.Background = new Microsoft.UI.Xaml.Media.AcrylicBrush
        {
            TintColor = sidebar,
            TintOpacity = 0.85,
            FallbackColor = sidebar,
        };
        SidebarPeekCard.BorderBrush = p.Border.WithOpacity(0.7).ToBrush();
    }

    /// <summary>
    /// Recreate the windowed peek popup after a maximize/restore. WinUI's windowed
    /// Popup does not re-track its owner's bounds across a state change, so we
    /// close and reopen it to rebuild the site-bridge window at the new size and
    /// re-raise it above the CEF page.
    /// </summary>
    private void ReassertPeek()
    {
        if (Store.SidebarVisible || !_peekReady) return;
        _isPeeking = false;
        _peekCloseTimer.Stop();
        SidebarPeekPopup.IsOpen = false;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Store.SidebarVisible) return;
            SidebarPeekPopup.IsOpen = true;
            LayoutPeek();
            RaisePeekPopup();
        });
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

    private void EnterPeek()
    {
        if (_isPeeking) return;
        _isPeeking = true;
        _peekCloseTimer.Stop();
        RaisePeekPopup(); // ensure the card draws above the live CEF page
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
        ExitPeek();
    }

    /// <summary>True while a flyout/context menu (not the peek popup itself) is open.</summary>
    private bool IsPeekFlyoutOpen()
    {
        if (Content?.XamlRoot is null) return false;
        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot))
        {
            if (popup == SidebarPeekPopup) continue;
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
