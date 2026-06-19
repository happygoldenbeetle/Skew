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

    public MainWindow()
    {
        Instance = this;
        _peekCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _peekCloseTimer.Tick += (s, e) => { _peekCloseTimer.Stop(); ExitPeekMode(); };

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

        // Route CEF in-page shortcut presses to the same handler as native
        // chrome (mac OnPreKeyEvent → MoriRoot.handleShortcutEvent).
        Mori.Cef.MoriBrowserHostChannel.ShortcutHandler = HandleCefShortcut;

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
                FindBar.Visibility = Store.FindBarVisible
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                break;

            case nameof(BrowserStore.SelectedTab):
                UpdateLoadingBar();
                ShowSelectedBrowserView();
                break;
        }
    }

    public void UpdateGlobalTint(Windows.UI.Color color)
    {
        // Mica backdrop does not support dynamic tinting like Acrylic
    }

    private void Cef_TitleChanged(object sender, string title)
    {
    }

    private void RootGrid_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
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
            if (key == Windows.System.VirtualKey.F11 || key == Windows.System.VirtualKey.Escape)
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
        if (_isPeeking && Store.SidebarVisible)
        {
            _isPeeking = false;
            Grid.SetColumnSpan(Sidebar, 1);
            Sidebar.HorizontalAlignment = HorizontalAlignment.Stretch;
            Sidebar.Width = double.NaN;
            Canvas.SetZIndex(Sidebar, 0);
            SidebarTranslate.X = 0;
        }

        if (Store.SidebarOnLeft)
        {
            Grid.SetColumn(Sidebar, 0);
            Grid.SetColumn(AIPanel, 2);
            Grid.SetColumn(SidebarRevealButton, 1);
            SidebarRevealButton.HorizontalAlignment = HorizontalAlignment.Left;
            SidebarRevealButton.Margin = new Thickness(16, 16, 0, 0);
            PeekTriggerArea.HorizontalAlignment = HorizontalAlignment.Left;
        }
        else
        {
            Grid.SetColumn(Sidebar, 2);
            Grid.SetColumn(AIPanel, 0);
            Grid.SetColumn(SidebarRevealButton, 1);
            SidebarRevealButton.HorizontalAlignment = HorizontalAlignment.Right;
            SidebarRevealButton.Margin = new Thickness(0, 16, 16, 0);
            PeekTriggerArea.HorizontalAlignment = HorizontalAlignment.Right;
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

        PeekTriggerArea.Visibility = Store.SidebarVisible
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

    private void PeekTriggerArea_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!Store.SidebarVisible && !_isPeeking)
        {
            EnterPeekMode();
        }
    }

    private void RootGrid_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isPeeking) return;
        
        var pos = e.GetCurrentPoint(RootGrid).Position;
        bool inBand = Store.SidebarOnLeft ? pos.X <= 300 : pos.X >= RootGrid.ActualWidth - 300;
        
        if (inBand)
        {
            _peekCloseTimer.Stop();
        }
        else
        {
            if (!_peekCloseTimer.IsEnabled)
                _peekCloseTimer.Start();
        }
    }

    private void EnterPeekMode()
    {
        _isPeeking = true;
        
        // Remove Sidebar from layout flow
        Grid.SetColumnSpan(Sidebar, 3);
        Sidebar.HorizontalAlignment = Store.SidebarOnLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Sidebar.Width = 260;
        Canvas.SetZIndex(Sidebar, 50);

        // Animate in
        SidebarTranslate.X = Store.SidebarOnLeft ? -260 : 260;
        
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.ExponentialEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut, Exponent = 4 }
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, SidebarTranslate);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "X");
        sb.Children.Add(anim);
        sb.Begin();
    }

    private void ExitPeekMode()
    {
        if (!_isPeeking) return;
        _isPeeking = false;

        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = Store.SidebarOnLeft ? -260 : 260,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.ExponentialEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut, Exponent = 4 }
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, SidebarTranslate);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "X");
        sb.Children.Add(anim);
        
        sb.Completed += (s, e) => 
        {
            if (_isPeeking) return; // aborted by moving mouse back
            
            // Restore normal layout flow
            if (!Store.SidebarVisible)
            {
                Grid.SetColumnSpan(Sidebar, 1);
                Sidebar.HorizontalAlignment = HorizontalAlignment.Stretch;
                Sidebar.Width = double.NaN;
                Canvas.SetZIndex(Sidebar, 0);
                SidebarTranslate.X = 0;
            }
        };
        sb.Begin();
    }
}
