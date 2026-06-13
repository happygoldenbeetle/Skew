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
        SizeChanged += (_, _) => SyncBrowserFrame();
    }

    // ── Lifecycle: create the browser once installed & sized ──────────────
    // (mac viewDidMoveToWindow + _createBrowserIfReady + _createBrowserNow)

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CreateBrowserIfReady();
        SyncBrowserVisibility();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => CloseBrowser();

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
        if (_browser is not null)
            return;

        // The native child window CEF parents into, sized to this control.
        nint parentHwnd = App.WindowHandle;
        _hostWindow = HwndHostWindow.Create(parentHwnd, HostPanel, this);

        var windowInfo = CefWindowInfo.Create();
        var bounds = _hostWindow.PixelBounds;
        windowInfo.SetAsChild(_hostWindow.Handle,
            new CefRectangle(0, 0, bounds.Width, bounds.Height));

        var settings = new CefBrowserSettings();
        _client = new BrowserClient(this);
        if (ExtensionTabId != 0)
            _client.SetExtensionTabId(ExtensionTabId);

        CefBrowserHost.CreateBrowser(windowInfo, _client, settings, _pendingUrl);
    }

    private void SyncBrowserFrame()
    {
        if (_hostWindow is null)
            return;
        _hostWindow.UpdateBounds();
        if (_browser is not null)
        {
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

    // ── IBrowserViewDelegate (CEF UI thread → WinUI dispatcher) ───────────
    // Mirrors the mac ViewClientDelegate, hopping every callback to the UI thread.

    void IBrowserViewDelegate.OnAfterCreated(CefBrowser browser)
        => Post(() =>
        {
            _browser = browser;
            EmitEngineAuditMarker(browser);
            SyncBrowserFrame();
            SyncBrowserVisibility();
        });

    void IBrowserViewDelegate.OnBeforeClose(CefBrowser browser)
        => Post(() => _browser = null);

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
        => Post(() => NavigationStarted?.Invoke(url, isRedirect, userGesture));

    void IBrowserViewDelegate.OnLoadStart(string url)
        => Post(() => NavigationCommitted?.Invoke(url));

    void IBrowserViewDelegate.OnLoadEnd(string url, int httpStatusCode)
        => Post(() => NavigationFinished?.Invoke(url, httpStatusCode));

    void IBrowserViewDelegate.OnLoadError(int errorCode, string errorText, string failedUrl)
        => Post(() => LoadFailed?.Invoke(errorText, failedUrl));

    bool IBrowserViewDelegate.OnOpenUrlFromTab(string targetUrl)
    {
        Post(() => RequestsNewTab?.Invoke(targetUrl));
        return true;
    }

    void IBrowserViewDelegate.OnFindResult(int count, int activeMatchOrdinal)
        => Post(() => FindMatchUpdated?.Invoke(activeMatchOrdinal, count));

    private void Post(Action action)
    {
        if (_dispatcher.HasThreadAccess)
            action();
        else
            _dispatcher.TryEnqueue(() => action());
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
        _browser?.GetHost().CloseBrowser(forceClose: true);
        _browser = null;
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
