using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mori.Cef;
using Xilium.CefGlue;

namespace Mori.Controls;

/// <summary>
/// WinUI control wrapping a single CEF browser, presented as a panel. Port of the
/// mac MoriBrowserView (Bridge/MoriBrowserView.h/.mm).
///
/// <para>
/// Why this control hosts Chromium as an embedded child window: Mori is the
/// browser UI; Chromium is the page engine underneath it. A CEF browser created
/// with <c>SetAsChild</c> embeds in our window hierarchy instead of launching
/// Chrome's own top-level window (the mac build uses the equivalent NSView
/// child-view path). Chrome's built-in extension runtime is therefore not
/// available; extension behavior is implemented by Mori itself (see
/// <see cref="ExtensionRuntimeBridge"/> and the scheme handler).
/// </para>
///
/// <para>
/// This file is the only place that bridges WinUI to CEF. <see cref="Models"/>
/// and the rest of the chrome talk to it through its CEF-free public API and the
/// navigation events below — the same boundary the mac bridging header enforces.
/// </para>
/// </summary>
public sealed partial class MoriBrowserView : UserControl, IBrowserViewDelegate
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
    private nint _browserHwnd;
    private HwndHostWindow? _hostWindow; // native child HWND CEF parents into
    private string _pendingUrl;
    private bool _created;
    private bool _webWindowVisible = true;
    private bool _ignoresGlobalSuppression;
    private int _findIdentifier;

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
        SizeChanged += (_, _) => { CreateBrowserIfReady(); SyncBrowserFrame(); };
        LayoutUpdated += (_, _) => SyncBrowserFrame();
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
        System.IO.File.AppendAllText("crash.log", $"CreateBrowserNow entered for url {_pendingUrl}\n");
        if (_hostWindow is null)
        {
            nint parentHwnd = App.WindowHandle;
            System.IO.File.AppendAllText("crash.log", $"Creating HwndHostWindow\n");
            _hostWindow = HwndHostWindow.Create(parentHwnd, HostPanel, this);
        }

        var windowInfo = CefWindowInfo.Create();
        var bounds = _hostWindow.PixelBounds;
        System.IO.File.AppendAllText("crash.log", $"HwndHostWindow bounds: {bounds.Width}x{bounds.Height}\n");
        windowInfo.SetAsChild(_hostWindow.Handle,
            new CefRectangle(0, 0, bounds.Width, bounds.Height));

        var settings = new CefBrowserSettings();
        _client = new BrowserClient(this);
        _client.ContextMenuRequested += Client_ContextMenuRequested;

        if (ExtensionTabId != 0)
            _client.SetExtensionTabId(ExtensionTabId);

        System.IO.File.AppendAllText("crash.log", $"Calling CefBrowserHost.CreateBrowser\n");
        CefBrowserHost.CreateBrowser(windowInfo, _client, settings, _pendingUrl);
        System.IO.File.AppendAllText("crash.log", $"CefBrowserHost.CreateBrowser returned successfully\n");
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

    private void SyncBrowserFrame()
    {
        if (_hostWindow is null)
            return;
        _hostWindow.UpdateBounds();
        if (_browser is not null)
        {
            var browserHwnd = _browser.GetHost().GetWindowHandle();
            _hostWindow.ResizeBrowserWindow(browserHwnd);
            _browser.GetHost().NotifyMoveOrResizeStarted();
            _browser.GetHost().WasResized();
        }
    }

    private void SyncBrowserVisibility()
    {
        bool hidden = !_webWindowVisible ||
            (s_webContentSuppressed && !_ignoresGlobalSuppression);
        _hostWindow?.SetVisible(!hidden);
        _browser?.GetHost().WasHidden(hidden);
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
            _browserHwnd = browser.GetHost().GetWindowHandle();
            EmitEngineAuditMarker(browser);
            SyncBrowserFrame();
            SyncBrowserVisibility();
        });

    void IBrowserViewDelegate.OnBeforeClose(CefBrowser browser)
    {
        if (_browser != null && _browser.Identifier == browser.Identifier)
        {
            _browser = null;
            
            // CEF has officially destroyed the main browser, now it is safe to destroy the parent HWND.
            Post(() => 
            {
                _hostWindow?.Dispose();
                _hostWindow = null;
            });
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
        => Post(() =>
        {
            var shape = type switch
            {
                CefCursorType.Hand => Microsoft.UI.Input.InputSystemCursorShape.Hand,
                CefCursorType.IBeam => Microsoft.UI.Input.InputSystemCursorShape.IBeam,
                CefCursorType.Wait => Microsoft.UI.Input.InputSystemCursorShape.Wait,
                CefCursorType.Cross => Microsoft.UI.Input.InputSystemCursorShape.Cross,
                CefCursorType.Help => Microsoft.UI.Input.InputSystemCursorShape.Help,
                _ => Microsoft.UI.Input.InputSystemCursorShape.Arrow
            };
            this.ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(shape);
        });

    private void Post(Action action)
    {
        if (_dispatcher.HasThreadAccess)
            action();
        else
            _dispatcher.TryEnqueue(() => action());
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern nint FindWindowEx(nint parentHandle, nint childAfter, string className, string? windowTitle);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint PostMessage(nint hWnd, uint Msg, nint wParam, nint lParam);

    private nint GetRenderWidgetHostHwnd()
    {
        if (_browserHwnd == nint.Zero) return nint.Zero;
        
        nint widget = FindWindowEx(_browserHwnd, nint.Zero, "Chrome_WidgetWin_0", null);
        if (widget != nint.Zero)
        {
            nint renderWidget = FindWindowEx(widget, nint.Zero, "Chrome_RenderWidgetHostHWND", null);
            if (renderWidget != nint.Zero) return renderWidget;
        }
        
        nint directRenderWidget = FindWindowEx(_browserHwnd, nint.Zero, "Chrome_RenderWidgetHostHWND", null);
        if (directRenderWidget != nint.Zero) return directRenderWidget;

        return _browserHwnd;
    }

    private nint GetWParam(Microsoft.UI.Input.PointerPoint pt)
    {
        nint wParam = 0;
        if (pt.Properties.IsLeftButtonPressed) wParam |= 0x0001;
        if (pt.Properties.IsRightButtonPressed) wParam |= 0x0002;
        if (pt.Properties.IsMiddleButtonPressed) wParam |= 0x0010;
        return wParam;
    }

    private void HostPanel_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        nint targetHwnd = GetRenderWidgetHostHwnd();
        if (targetHwnd == nint.Zero) return;
        HostPanel.CapturePointer(e.Pointer);
        var pt = e.GetCurrentPoint(this);
        
        uint msg = 0x0201; // WM_LBUTTONDOWN
        if (pt.Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.RightButtonPressed) msg = 0x0204; // WM_RBUTTONDOWN
        else if (pt.Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.MiddleButtonPressed) msg = 0x0207; // WM_MBUTTONDOWN
        
        PostMessage(targetHwnd, msg, GetWParam(pt), MakeLParamScaled(pt.Position.X, pt.Position.Y));
        _browser?.GetHost().SetFocus(true);
        e.Handled = true;
    }

    private void HostPanel_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        nint targetHwnd = GetRenderWidgetHostHwnd();
        if (targetHwnd == nint.Zero) return;
        var pt = e.GetCurrentPoint(this);
        PostMessage(targetHwnd, 0x0200, GetWParam(pt), MakeLParamScaled(pt.Position.X, pt.Position.Y));
        e.Handled = true;
    }

    private void HostPanel_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        nint targetHwnd = GetRenderWidgetHostHwnd();
        if (targetHwnd == nint.Zero) return;
        HostPanel.ReleasePointerCapture(e.Pointer);
        var pt = e.GetCurrentPoint(this);
        
        uint msg = 0x0202; // WM_LBUTTONUP
        if (pt.Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased) msg = 0x0205; // WM_RBUTTONUP
        else if (pt.Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased) msg = 0x0208; // WM_MBUTTONUP
        
        PostMessage(targetHwnd, msg, GetWParam(pt), MakeLParamScaled(pt.Position.X, pt.Position.Y));
        e.Handled = true;
    }

    private void HostPanel_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
    }

    private void HostPanel_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        nint targetHwnd = GetRenderWidgetHostHwnd();
        if (targetHwnd == nint.Zero) return;
        var pt = e.GetCurrentPoint(this);
        int delta = pt.Properties.MouseWheelDelta;
        PostMessage(targetHwnd, 0x020A, (nint)(delta << 16), MakeLParamScaled(pt.Position.X, pt.Position.Y));
        e.Handled = true;
    }

    private nint MakeLParamScaled(double dipX, double dipY)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        int x = (int)(dipX * scale);
        int y = (int)(dipY * scale);
        return (nint)((y << 16) | (x & 0xFFFF));
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
        _browser.GetHost().ShowDevTools(windowInfo, _client, new CefBrowserSettings(), default);
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
        _hostWindow?.Dispose();
        _hostWindow = null;
        
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
            $"__MORI_CHROMIUM_ENGINE__ runtime=alloy embedding=child-window scheme={MoriSchemes.ExtensionScheme}");
    }
}
