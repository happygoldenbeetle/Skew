using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Mori.Cef;
using Mori.Cef.Osr;
using WinRT;
using Xilium.CefGlue;

namespace Mori.Controls;

/// <summary>
/// WinUI control wrapping a single CEF browser, presented as a panel. Port of the
/// mac MoriBrowserView (Bridge/MoriBrowserView.h/.mm).
///
/// <para>
/// Why this control renders Chromium offscreen rather than hosting it as a child
/// window: Mori is the browser UI; Chromium is the page engine underneath it. On
/// macOS a <c>SetAsChild</c> browser lands in an NSView that composites normally
/// inside the SwiftUI tree, so the chrome can draw over, clip, and animate it.
/// The Windows equivalent is a child HWND, which always composites above every
/// XAML layer — no rounded card, no translucent peek sidebar, no launcher scrim.
/// So here the browser is windowless: Chromium paints into memory, we present
/// those frames into an <c>Image</c>, and the page becomes ordinary XAML content.
/// Chrome's built-in extension runtime is not available either way; extension
/// behavior is implemented by Mori itself (see
/// <see cref="ExtensionRuntimeBridge"/> and the scheme handler).
/// </para>
///
/// <para>
/// This file is the only place that bridges WinUI to CEF. <see cref="Models"/>
/// and the rest of the chrome talk to it through its CEF-free public API and the
/// navigation events below — the same boundary the mac bridging header enforces.
/// </para>
/// </summary>
public sealed partial class MoriBrowserView : UserControl, IBrowserViewDelegate, IOsrHost
{
    // One press changes the CEF zoom level by this much. CEF zoom is logarithmic
    // (scale = 1.2^level), so 0.5 is roughly a 10% step — close to Chrome's feel.
    private const double ZoomStep = 0.5;

    // Live views, for the class-level fan-out / suppression methods (mac g_all_views).
    private static readonly List<WeakReference<MoriBrowserView>> s_allViews = new();
    private static bool s_webContentSuppressed; // mac g_web_content_suppressed

    private readonly DispatcherQueue _dispatcher;
    private BrowserClient? _client;
    private CefBrowser? _browser;
    private string _pendingUrl;
    private bool _created;
    private bool _webWindowVisible = true;
    private bool _ignoresGlobalSuppression;
    private int _findIdentifier;

    // ── Offscreen rendering state ─────────────────────────────────────────

    private readonly OsrSurface _surface = new();
    private readonly OsrInput.ClickCounter _clicks = new();
    private WriteableBitmap? _bitmap;
    private IntPtr _bitmapPixels;
    private int _bitmapWidth;
    private int _bitmapHeight;
    private bool _surfaceDisposed;

    /// <summary>
    /// Last size we told Chromium about, in DIPs. Resizes are pushed through
    /// <c>WasResized</c> rather than recreating anything.
    /// </summary>
    private int _viewWidthDip = 1;
    private int _viewHeightDip = 1;

    // ── Public, CEF-free state (mac readonly properties) ──────────────────

    public string CurrentUrl { get; private set; } = "";
    public string CurrentTitle { get; private set; } = "";
    public bool IsLoading { get; private set; }
    public bool CanGoBack { get; private set; }
    public bool CanGoForward { get; private set; }
    public int BrowserIdentifier => _browser?.Identifier ?? 0;
    public int ExtensionTabId { get; set; }

    // ── Navigation/display events (mac MoriBrowserViewDelegate) ────────────

    public event Action<string>? TitleChanged;
    public event Action<string>? UrlChanged;
    public event Action<bool, bool, bool>? LoadingStateChanged;
    public event Action<IReadOnlyList<string>>? FaviconUrlsChanged;
    public event Action<string, bool, bool>? NavigationStarted;
    public event Action<string>? NavigationCommitted;
    public event Action<string, int>? NavigationFinished;
    public event Action<string, string>? LoadFailed;
    public event Action<string>? RequestsNewTab;
    public event Action<int, int>? FindMatchUpdated;

    public MoriBrowserView() : this("about:blank") { }

    public MoriBrowserView(string url)
    {
        InitializeComponent();
        _pendingUrl = url;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        lock (s_allViews)
            s_allViews.Add(new WeakReference<MoriBrowserView>(this));

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // Only size matters now. The old windowed path also hooked LayoutUpdated
        // to chase the child HWND across every layout pass, which is what made
        // the page lag a frame behind the chrome during animations.
        SizeChanged += (_, _) => { CreateBrowserIfReady(); SyncBrowserFrame(); };

        GettingFocus += (_, _) => _browser?.GetHost().SetFocus(true);
        LosingFocus += (_, _) => _browser?.GetHost().SetFocus(false);
        KeyDown += HostPanel_KeyDown;
        KeyUp += HostPanel_KeyUp;
        CharacterReceived += HostPanel_CharacterReceived;
    }

    // ── Lifecycle: create the browser once installed & sized ──────────────
    // (mac viewDidMoveToWindow + _createBrowserIfReady + _createBrowserNow)

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _webWindowVisible = true;
        CreateBrowserIfReady();
        SyncBrowserVisibility();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _webWindowVisible = false;
        SyncBrowserVisibility();
    }

    private void CreateBrowserIfReady()
    {
        if (_created || _browser is not null)
            return;
        if (ActualWidth < 1 || ActualHeight < 1)
            return;

        _created = true;

        // Create on the next dispatcher turn, never nested inside the layout pass.
        _dispatcher.TryEnqueue(CreateBrowserNow);
    }

    private void CreateBrowserNow()
    {
        CaptureViewSize();

        var windowInfo = CefWindowInfo.Create();
        // Windowless, with a transparent backing so pages that don't paint an
        // opaque background let the chrome's material show through — the Mac
        // card sits on the same unified surface as the sidebar.
        windowInfo.SetAsWindowless(App.WindowHandle, transparent: true);

        var settings = new CefBrowserSettings
        {
            // Asks for far more than any display will show, so the ceiling is
            // the compositor's rather than this. CEF's default is 30, which
            // visibly stutters against the shell's animations; 60 pinned the
            // page to 60 on displays running above it. CEF has historically
            // clamped this at 60 internally — the two switches in CefAppImpl are
            // what actually lift the limit, and this just stops being the one
            // that binds.
            WindowlessFrameRate = 240,
            // Transparent so the rounded card's material shows through where the
            // page has no background of its own.
            BackgroundColor = new CefColor(0, 0, 0, 0),
        };

        _client = new BrowserClient(this);
        _client.ContextMenuRequested += Client_ContextMenuRequested;
        _client.SetRenderHandler(new OsrRenderHandler(this));

        if (ExtensionTabId != 0)
            _client.SetExtensionTabId(ExtensionTabId);

        CefBrowserHost.CreateBrowser(windowInfo, _client, settings, _pendingUrl);
    }

    /// <summary>Snapshot the control's logical size for <c>GetViewRect</c>.</summary>
    private void CaptureViewSize()
    {
        _viewWidthDip = Math.Max(1, (int)Math.Round(ActualWidth));
        _viewHeightDip = Math.Max(1, (int)Math.Round(ActualHeight));
    }

    private void Client_ContextMenuRequested(object? sender, BrowserContextMenuEventArgs e)
    {
        _dispatcher.TryEnqueue(() =>
        {
            var menu = new MenuFlyout();

            bool[] actionChosen = new bool[1] { false };

            foreach (var item in e.Items)
            {
                var flyoutItem = CreateMenuItem(item, e.Callback, actionChosen);
                if (flyoutItem != null)
                {
                    menu.Items.Add(flyoutItem);
                }
            }

            menu.Closed += (s, args) =>
            {
                if (!actionChosen[0])
                {
                    e.Callback?.Invoke(null);
                }
            };

            menu.ShowAt(this, new Windows.Foundation.Point(e.X, e.Y));
        });
    }

    private MenuFlyoutItemBase? CreateMenuItem(ContextMenuItemModel model, Action<int?>? callback, bool[] actionChosen)
    {
        if (!model.IsVisible) return null;

        if (model.Type == Xilium.CefGlue.CefMenuItemType.Separator)
        {
            return new MenuFlyoutSeparator();
        }
        else if (model.Type == Xilium.CefGlue.CefMenuItemType.SubMenu)
        {
            var subMenu = new MenuFlyoutSubItem
            {
                Text = (model.Label ?? "").Replace("&", ""),
                IsEnabled = model.IsEnabled
            };

            if (model.SubMenuItems != null)
            {
                foreach (var child in model.SubMenuItems)
                {
                    var childItem = CreateMenuItem(child, callback, actionChosen);
                    if (childItem != null)
                    {
                        subMenu.Items.Add(childItem);
                    }
                }
            }
            return subMenu;
        }
        else if (model.Type == Xilium.CefGlue.CefMenuItemType.Check || model.Type == Xilium.CefGlue.CefMenuItemType.Radio)
        {
            var toggle = new ToggleMenuFlyoutItem
            {
                Text = (model.Label ?? "").Replace("&", ""),
                IsChecked = model.IsChecked,
                IsEnabled = model.IsEnabled
            };
            toggle.Click += (s, args) =>
            {
                actionChosen[0] = true;
                callback?.Invoke(model.CommandId);
            };
            return toggle;
        }
        else
        {
            var item = new MenuFlyoutItem
            {
                Text = (model.Label ?? "").Replace("&", ""),
                IsEnabled = model.IsEnabled
            };
            item.Click += (s, args) =>
            {
                actionChosen[0] = true;
                callback?.Invoke(model.CommandId);
            };
            return item;
        }
    }

    /// <summary>
    /// Tell Chromium the view resized. There is no window to move any more — a
    /// resize is just a new view rect plus a repaint at the new size.
    /// </summary>
    private void SyncBrowserFrame()
    {
        int oldW = _viewWidthDip, oldH = _viewHeightDip;
        CaptureViewSize();
        if (_browser is null)
            return;
        if (oldW == _viewWidthDip && oldH == _viewHeightDip)
            return;

        _browser.GetHost().WasResized();
    }

    private void SyncBrowserVisibility()
    {
        bool hidden = !_webWindowVisible ||
            (s_webContentSuppressed && !_ignoresGlobalSuppression);

        // Hidden tabs stop painting but stay alive, which is what the Mac build
        // gets by keeping every realized tab mounted and only toggling isHidden.
        Surface.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        _browser?.GetHost().WasHidden(hidden);

        if (!hidden)
            _browser?.GetHost().Invalidate(CefPaintElementType.View);
    }

    public static void BroadcastThemeChange(bool isDark)
    {
        string js = $"document.documentElement.setAttribute('data-theme', '{ (isDark ? "dark" : "light") }');";
        lock (s_allViews)
        {
            foreach (var w in s_allViews.ToList())
            {
                if (w.TryGetTarget(out var view) && view._browser is not null)
                {
                    view._browser.GetMainFrame().ExecuteJavaScript(js, "mori://internal", 0);
                }
            }
        }
    }

    // ── IBrowserViewDelegate (CEF UI thread → WinUI dispatcher) ───────────
    // Mirrors the mac ViewClientDelegate, hopping every callback to the UI thread.

    void IBrowserViewDelegate.OnAfterCreated(CefBrowser browser)
        => Post(() =>
        {
            _browser = browser;
            EmitEngineAuditMarker(browser);
            CaptureViewSize();
            browser.GetHost().WasResized();
            SyncBrowserVisibility();
        });

    void IBrowserViewDelegate.OnBeforeClose(CefBrowser browser)
    {
        if (_browser != null && _browser.Identifier == browser.Identifier)
        {
            _browser = null;

            // Chromium will not paint again, so the frame buffers can go.
            Post(ReleaseSurface);
        }
    }

    void IBrowserViewDelegate.OnTitleChange(string title)
        => Post(() => { CurrentTitle = title; TitleChanged?.Invoke(title); });

    void IBrowserViewDelegate.OnAddressChange(string url)
        => Post(() => { CurrentUrl = url; UrlChanged?.Invoke(url); });

    void IBrowserViewDelegate.OnLoadingStateChange(bool isLoading, bool canGoBack, bool canGoForward)
        => Post(() =>
        {
            IsLoading = isLoading;
            CanGoBack = canGoBack;
            CanGoForward = canGoForward;
            LoadingStateChanged?.Invoke(isLoading, canGoBack, canGoForward);
        });

    void IBrowserViewDelegate.OnFaviconUrlChange(IReadOnlyList<string> iconUrls)
        => Post(() => FaviconUrlsChanged?.Invoke(iconUrls));

    void IBrowserViewDelegate.OnBeforeBrowse(string url, bool isRedirect, bool userGesture)
        => Post(() => 
        {
            System.IO.File.AppendAllText("nav.log", $"OnBeforeBrowse: {url}\n");
            NavigationStarted?.Invoke(url, isRedirect, userGesture);
        });

    void IBrowserViewDelegate.OnLoadStart(string url)
        => Post(() => 
        {
            System.IO.File.AppendAllText("nav.log", $"OnLoadStart: {url}\n");
            NavigationCommitted?.Invoke(url);
        });

    void IBrowserViewDelegate.OnLoadEnd(string url, int httpStatusCode)
        => Post(() => 
        {
            System.IO.File.AppendAllText("nav.log", $"OnLoadEnd: {url} (Status: {httpStatusCode})\n");
            NavigationFinished?.Invoke(url, httpStatusCode);
        });

    void IBrowserViewDelegate.OnLoadError(int errorCode, string errorText, string failedUrl)
        => Post(() => 
        {
            System.IO.File.AppendAllText("nav.log", $"OnLoadError: {failedUrl} (Code: {errorCode}, Error: {errorText})\n");
            LoadFailed?.Invoke(errorText, failedUrl);
        });

    bool IBrowserViewDelegate.OnOpenUrlFromTab(string targetUrl)
    {
        Post(() => RequestsNewTab?.Invoke(targetUrl));
        return true;
    }

    void IBrowserViewDelegate.OnFindResult(int count, int activeMatchOrdinal)
        => Post(() => FindMatchUpdated?.Invoke(activeMatchOrdinal, count));

    void IBrowserViewDelegate.OnCursorChange(CefCursorType type)
        => Post(() => ApplyCursor(type));

    private void Post(Action action)
    {
        if (_dispatcher.HasThreadAccess)
            action();
        else
            _dispatcher.TryEnqueue(() => action());
    }

    // ── Input (mac gets this free; windowless CEF needs it fed explicitly) ─
    //
    // The previous windowed implementation synthesised WM_* messages and
    // PostMessage'd them to a Chrome_RenderWidgetHostHWND discovered by class
    // name. That depended on Chromium's internal window hierarchy and lost every
    // event Chromium expected to correlate (click counts, capture, modifiers).
    // Windowless mode has a real API for this.

    private CefBrowserHost? Host => _browser?.GetHost();

    private CefMouseEvent MouseEventAt(PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        // DIPs, not device pixels: Chromium scales by the device scale factor it
        // got from GetScreenInfo. Scaling here too would double-apply it.
        return new CefMouseEvent(
            (int)Math.Round(pt.Position.X),
            (int)Math.Round(pt.Position.Y),
            OsrInput.GetModifiers(pt.Properties));
    }

    private void HostPanel_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var host = Host;
        if (host is null) return;

        var pt = e.GetCurrentPoint(this);
        var button = OsrInput.ButtonOf(pt.Properties.PointerUpdateKind) ?? CefMouseButtonType.Left;

        HostPanel.CapturePointer(e.Pointer);
        Focus(FocusState.Pointer);
        host.SetFocus(true);

        int clickCount = _clicks.Register(button, pt.Position.X, pt.Position.Y);
        host.SendMouseClickEvent(MouseEventAt(e), button, mouseUp: false, clickCount);
        e.Handled = true;
    }

    private void HostPanel_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        Host?.SendMouseMoveEvent(MouseEventAt(e), mouseLeave: false);
        e.Handled = true;
    }

    private void HostPanel_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var host = Host;
        if (host is null) return;

        var pt = e.GetCurrentPoint(this);
        var button = OsrInput.ButtonOf(pt.Properties.PointerUpdateKind) ?? CefMouseButtonType.Left;

        HostPanel.ReleasePointerCapture(e.Pointer);
        host.SendMouseClickEvent(MouseEventAt(e), button, mouseUp: true, clickCount: 1);
        e.Handled = true;
    }

    private void HostPanel_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Without the leave event, :hover styles stay stuck on whatever the
        // cursor left the page over.
        Host?.SendMouseMoveEvent(MouseEventAt(e), mouseLeave: true);
    }

    private void HostPanel_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _clicks.Reset();
        Host?.SendCaptureLostEvent();
    }

    private void HostPanel_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => Host?.SendCaptureLostEvent();

    private void HostPanel_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var host = Host;
        if (host is null) return;

        var pt = e.GetCurrentPoint(this);
        int delta = pt.Properties.MouseWheelDelta;
        // Shift+wheel scrolls horizontally, matching every other browser.
        bool horizontal = pt.Properties.IsHorizontalMouseWheel ||
                          (OsrInput.GetModifiers() & CefEventFlags.ShiftDown) != 0;

        host.SendMouseWheelEvent(MouseEventAt(e),
            horizontal ? delta : 0,
            horizontal ? 0 : delta);
        e.Handled = true;
    }

    private void HostPanel_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var host = Host;
        if (host is null) return;
        host.SendKeyEvent(OsrInput.KeyEvent(e.Key, isKeyUp: false, isRepeat: e.KeyStatus.WasKeyDown));
        e.Handled = true;
    }

    private void HostPanel_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        var host = Host;
        if (host is null) return;
        host.SendKeyEvent(OsrInput.KeyEvent(e.Key, isKeyUp: true, isRepeat: false));
        e.Handled = true;
    }

    /// <summary>
    /// Text input. XAML resolves layout, dead keys, and IME composition for us
    /// and delivers the finished character here, so typing works for non-Latin
    /// layouts without driving ImeSetComposition directly.
    /// </summary>
    private void HostPanel_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        var host = Host;
        if (host is null) return;
        host.SendKeyEvent(OsrInput.CharEvent(e.Character));
        e.Handled = true;
    }

    // ── Offscreen presentation (IOsrHost) ─────────────────────────────────

    CefRectangle IOsrHost.GetViewRectDip() => new(0, 0, _viewWidthDip, _viewHeightDip);

    float IOsrHost.DeviceScaleFactor => (float)(XamlRoot?.RasterizationScale ?? 1.0);

    CefRectangle IOsrHost.GetRootScreenRectDip()
    {
        // Chromium uses this to place native popups and to report screen metrics
        // to the page. Window-relative is close enough for both.
        var root = XamlRoot;
        if (root is null)
            return new CefRectangle(0, 0, _viewWidthDip, _viewHeightDip);
        return new CefRectangle(0, 0,
            Math.Max(1, (int)Math.Round(root.Size.Width)),
            Math.Max(1, (int)Math.Round(root.Size.Height)));
    }

    bool IOsrHost.TryGetScreenPoint(int viewX, int viewY, out int screenX, out int screenY)
    {
        screenX = viewX;
        screenY = viewY;
        try
        {
            var transform = TransformToVisual(null);
            var p = transform.TransformPoint(new Windows.Foundation.Point(viewX, viewY));
            screenX = (int)Math.Round(p.X);
            screenY = (int)Math.Round(p.Y);
            return true;
        }
        catch
        {
            // TransformToVisual throws while the element is out of the tree.
            return false;
        }
    }

    void IOsrHost.OnPopupShow(bool show)
    {
        _surface.SetPopupVisible(show);
        Post(PresentFrame);
    }

    void IOsrHost.OnPopupSize(CefRectangle rectDip)
    {
        // The popup rect arrives in DIPs but the buffers are device pixels.
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        _surface.SetPopupRect(new CefRectangle(
            (int)Math.Round(rectDip.X * scale),
            (int)Math.Round(rectDip.Y * scale),
            (int)Math.Round(rectDip.Width * scale),
            (int)Math.Round(rectDip.Height * scale)));
    }

    void IOsrHost.OnCursorChanged(CefCursorType type) => ApplyCursor(type);

    void IOsrHost.OnPaint(CefPaintElementType type, CefRectangle[] dirtyRects,
                          IntPtr buffer, int width, int height)
    {
        if (_surfaceDisposed)
            return;

        _surface.Absorb(type, dirtyRects, buffer, width, height);
        Post(PresentFrame);
    }

    /// <summary>
    /// Copy the composed frame into the XAML bitmap. Runs on the UI thread; CEF
    /// paints on the same thread here because <see cref="CefRuntimeHost"/> pumps
    /// the message loop from the dispatcher, but Post() keeps that an
    /// implementation detail rather than a requirement.
    /// </summary>
    private void PresentFrame()
    {
        if (_surfaceDisposed || !_surface.HasFrame)
            return;

        int w = _surface.Width;
        int h = _surface.Height;
        if (w <= 0 || h <= 0)
            return;

        if (_bitmap is null || _bitmapWidth != w || _bitmapHeight != h)
        {
            _bitmap = new WriteableBitmap(w, h);
            _bitmapWidth = w;
            _bitmapHeight = h;
            _bitmapPixels = GetPixelBufferPointer(_bitmap);
            Surface.Source = _bitmap;
        }

        if (_bitmapPixels == IntPtr.Zero)
            return;

        if (_surface.Present(_bitmapPixels, w, h))
            _bitmap.Invalidate();
    }

    /// <summary>
    /// Raw pointer to a <see cref="WriteableBitmap"/>'s back buffer. Copying
    /// through a managed stream instead would add a second full-frame copy on
    /// every paint.
    /// </summary>
    private static IntPtr GetPixelBufferPointer(WriteableBitmap bitmap)
    {
        var access = bitmap.PixelBuffer.As<IBufferByteAccess>();
        access.Buffer(out IntPtr pixels);
        return pixels;
    }

    [ComImport]
    [Guid("905a0fef-bc53-11df-8c49-001e4fc686da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBufferByteAccess
    {
        void Buffer(out IntPtr buffer);
    }

    private void ApplyCursor(CefCursorType type)
    {
        var shape = type switch
        {
            CefCursorType.Hand or CefCursorType.Grab => Microsoft.UI.Input.InputSystemCursorShape.Hand,
            CefCursorType.IBeam or CefCursorType.VerticalText => Microsoft.UI.Input.InputSystemCursorShape.IBeam,
            CefCursorType.Wait or CefCursorType.Progress => Microsoft.UI.Input.InputSystemCursorShape.Wait,
            CefCursorType.Cross or CefCursorType.Cell => Microsoft.UI.Input.InputSystemCursorShape.Cross,
            CefCursorType.Help => Microsoft.UI.Input.InputSystemCursorShape.Help,
            CefCursorType.Move or CefCursorType.MiddlePanning => Microsoft.UI.Input.InputSystemCursorShape.SizeAll,
            CefCursorType.EastWestResize or CefCursorType.EastResize or CefCursorType.WestResize
                or CefCursorType.ColumnResize => Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast,
            CefCursorType.NorthSouthResize or CefCursorType.NorthResize or CefCursorType.SouthResize
                or CefCursorType.RowResize => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth,
            CefCursorType.NorthEastSouthWestResize or CefCursorType.NorthEastResize
                or CefCursorType.SouthWestResize => Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest,
            CefCursorType.NorthWestSouthEastResize or CefCursorType.NorthWestResize
                or CefCursorType.SouthEastResize => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast,
            CefCursorType.NotAllowed or CefCursorType.NoDrop => Microsoft.UI.Input.InputSystemCursorShape.UniversalNo,
            _ => Microsoft.UI.Input.InputSystemCursorShape.Arrow,
        };
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(shape);
    }

    private void ReleaseSurface()
    {
        if (_surfaceDisposed)
            return;
        _surfaceDisposed = true;

        Surface.Source = null;
        _bitmap = null;
        _bitmapPixels = IntPtr.Zero;
        _bitmapWidth = _bitmapHeight = 0;
        _surface.Dispose();
    }

    // ── Navigation API (mac loadURL/goBack/...) ───────────────────────────

    public void LoadUrl(string url)
    {
        _pendingUrl = url;
        _browser?.GetMainFrame().LoadUrl(url);
    }

    public void GoBack() => _browser?.GoBack();
    public void GoForward() => _browser?.GoForward();
    public void Reload() => _browser?.Reload();
    public void ReloadIgnoringCache() => _browser?.ReloadIgnoreCache();
    public void StopLoading() => _browser?.StopLoad();
    // ── Zoom (mac zoomIn/zoomOut/resetZoom/setZoomFactor) ─────────────────

    public double ZoomLevel => _browser?.GetHost().GetZoomLevel() ?? 0;

    public void ZoomIn() => AdjustZoom(ZoomStep);
    public void ZoomOut() => AdjustZoom(-ZoomStep);
    public void ResetZoom() { if (_browser is not null) _browser.GetHost().SetZoomLevel(0); }

    public void SetZoomFactor(double factor)
    {
        if (_browser is null || factor <= 0)
            return;
        // factor = 1.2^level  =>  level = log(factor)/log(1.2)
        double level = Math.Log(factor) / Math.Log(1.2);
        _browser.GetHost().SetZoomLevel(level);
    }

    private void AdjustZoom(double delta)
    {
        if (_browser is null)
            return;
        var host = _browser.GetHost();
        host.SetZoomLevel(host.GetZoomLevel() + delta);
    }

    // ── Find-in-page (mac findText/stopFinding) ───────────────────────────

    public void FindText(string text, bool forward)
    {
        if (_browser is null)
            return;
        if (string.IsNullOrEmpty(text))
        {
            StopFinding(true);
            return;
        }
        _browser.GetHost().Find(text, forward, matchCase: false, findNext: _findIdentifier != 0);
        _findIdentifier = 1;
    }

    public void StopFinding(bool clearSelection)
    {
        _findIdentifier = 0;
        _browser?.GetHost().StopFinding(clearSelection);
    }

    // ── DevTools / print (mac showDevTools/printPage) ─────────────────────

    public void ShowDevTools()
    {
        if (_browser is null)
            return;
        var windowInfo = CefWindowInfo.Create(); // own window
        windowInfo.SetAsPopup(App.WindowHandle, "DevTools");
        windowInfo.Style = (Xilium.CefGlue.Platform.Windows.WindowStyle)((uint)windowInfo.Style | 0x10000000); // WS_VISIBLE
        windowInfo.Bounds = new CefRectangle(0, 0, 800, 600);

        _browser.GetHost().ShowDevTools(windowInfo, new Cef.BrowserClient(null!), new CefBrowserSettings(), default);
    }

    public void CloseDevTools() => _browser?.GetHost().CloseDevTools();
    public void ToggleDevTools()
    {
        if (_browser?.GetHost().HasDevTools == true) CloseDevTools();
        else ShowDevTools();
    }

    public void PrintPage() => _browser?.GetHost().Print();

    // ── JavaScript (mac executeExtensionJavaScript/evaluateJavaScript) ────

    public void ExecuteExtensionJavaScript(string source, bool allFrames)
    {
        if (_browser is null)
            return;
        if (!allFrames)
        {
            _browser.GetMainFrame().ExecuteJavaScript(source, "", 0);
            return;
        }
        foreach (long id in _browser.GetFrameIdentifiers())
        {
            var frame = _browser.GetFrame(id);
            frame?.ExecuteJavaScript(source, "", 0);
        }
    }

    /// <summary>
    /// Evaluate JS in the main frame and return a JSON-serializable result via
    /// the DevTools protocol (mac evaluateJavaScript + JavaScriptEvalObserver).
    /// </summary>
    public bool EvaluateJavaScript(string source, Action<JsonElement?, string?> completion)
    {
        if (_browser is null)
        {
            completion(null, "No browser.");
            return false;
        }
        DevToolsEvaluator.Evaluate(_browser, source, completion);
        return true;
    }

    /// <summary>
    /// Capture the visible page through the DevTools protocol and resolve the
    /// extension bridge request with a PNG data URL (mac
    /// captureVisiblePNGDataURLForExtensionID + ScreenshotObserver).
    /// </summary>
    public bool CaptureVisiblePngDataUrl(string extensionId, string requestId)
    {
        if (_browser is null)
            return false;
        DevToolsScreenshot.Capture(_browser, extensionId, requestId, response =>
            DispatchExtensionBridgeResponse(response));
        return true;
    }

    public void FocusBrowser() => _browser?.GetHost().SetFocus(true);

    // ── Media / visibility (mac sendMediaCommand/setPageHidden/...) ───────

    public void SendMediaCommand(string action, double value)
    {
        string js = $"if(window.__moriMediaCommand)window.__moriMediaCommand(" +
            $"{JsonSerializer.Serialize(action)},{value.ToString(System.Globalization.CultureInfo.InvariantCulture)});";
        ExecuteExtensionJavaScript(js, allFrames: true);
    }

    public void SetPageHidden(bool hidden)
    {
        string state = hidden ? "hidden" : "visible";
        _browser?.GetHost().SetZoomLevel(_browser.GetHost().GetZoomLevel()); // no-op keep-alive
        ExecuteExtensionJavaScript(
            $"try{{Object.defineProperty(document,'visibilityState',{{configurable:true,get:function(){{return '{state}';}}}});" +
            $"document.dispatchEvent(new Event('visibilitychange'));}}catch(e){{}}",
            allFrames: true);
    }

    public void SetWebWindowVisible(bool visible)
    {
        if (_webWindowVisible == visible)
            return;
        _webWindowVisible = visible;
        SyncBrowserVisibility();
    }

    public void SetIgnoresGlobalWebContentSuppression(bool ignores)
    {
        if (_ignoresGlobalSuppression == ignores)
            return;
        _ignoresGlobalSuppression = ignores;
        SyncBrowserVisibility();
    }

    public void ApplyAutoPiP(bool enabled)
        => ExecuteExtensionJavaScript(
            $"if(window.__moriApplyAutoPiP)window.__moriApplyAutoPiP({(enabled ? "true" : "false")});",
            allFrames: true);

    // ── Downloads (mac startDownload) ─────────────────────────────────────

    public bool StartDownload(string url, string extensionId, string requestId, string? filename)
    {
        if (_browser is null)
            return false;
        _browser.GetHost().StartDownload(url);
        return true;
    }

    public void CloseBrowser()
    {
        _client?.DetachDelegate();
        _browser?.GetHost().CloseBrowser(true);
        ReleaseSurface();

        lock (s_allViews)
            s_allViews.RemoveAll(w => !w.TryGetTarget(out var v) || v == this);
    }

    // ── Class-level fan-out & suppression (mac + class methods) ───────────

    public static void SetAutoPiPEnabled(bool enabled)
    {
        BrowserClient.SetAutoPiPEnabled(enabled);
        ForEachLiveView(v => v.ApplyAutoPiP(enabled));
    }

    public static bool CancelDownloadWithId(uint downloadId)
        => BrowserClient.CancelDownload(downloadId);

    public static void SetWebContentSuppressed(bool suppressed)
    {
        if (s_webContentSuppressed == suppressed)
            return;
        s_webContentSuppressed = suppressed;
        ForEachLiveView(v => v.SyncBrowserVisibility());
    }

    public static void DispatchExtensionMessage(
        object? message, string extensionId, string? requestId = null,
        string? sourceUrl = null, string? sourceOrigin = null)
    {
        if (string.IsNullOrEmpty(extensionId))
            return;
        string js =
            $"if(window.__moriExtDispatchMessage){{window.__moriExtDispatchMessage(" +
            $"{Json(extensionId)},{Json(message)},{Json(requestId)},{Json(sourceUrl)},{Json(sourceOrigin)});}}";
        ForEachLiveView(v => v.ExecuteExtensionJavaScript(js, allFrames: true));
    }

    public static void DispatchExtensionBridgeResponse(IReadOnlyDictionary<string, object?> response)
    {
        string js = $"if(window.__moriExtResolve){{window.__moriExtResolve({Json(response)});}}";
        ForEachLiveView(v => v.ExecuteExtensionJavaScript(js, allFrames: true));
    }

    public static void DispatchExtensionEvent(string eventName, IReadOnlyList<object?> args, string? extensionId)
    {
        if (string.IsNullOrEmpty(eventName))
            return;
        string js =
            $"if(window.__moriExtDispatchEvent){{window.__moriExtDispatchEvent(" +
            $"{Json(eventName)},{Json(args)},{Json(extensionId)});}}";
        ForEachLiveView(v => v.ExecuteExtensionJavaScript(js, allFrames: true));
    }

    public static void BroadcastExtensionJavaScript(string source, string? extensionId)
    {
        if (string.IsNullOrEmpty(source))
            return;
        string guarded = string.IsNullOrEmpty(extensionId)
            ? source
            : $"if(window.__moriExtensionID==={Json(extensionId)}){{{source}}}";
        ForEachLiveView(v => v.ExecuteExtensionJavaScript(guarded, allFrames: true));
    }

    private static void ForEachLiveView(Action<MoriBrowserView> action)
    {
        List<MoriBrowserView> live = new();
        lock (s_allViews)
        {
            s_allViews.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var w in s_allViews)
                if (w.TryGetTarget(out var v))
                    live.Add(v);
        }
        foreach (var v in live)
            v.Post(() => action(v));
    }

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    private static void EmitEngineAuditMarker(CefBrowser browser)
    {
        // Mirrors the mac EmitEngineAuditMarker so automated checks can confirm
        // the embedding model in use.
        Console.Error.WriteLine(
            $"__MORI_CHROMIUM_ENGINE__ runtime=alloy embedding=windowless scheme={MoriSchemes.ExtensionScheme}");
    }
}
