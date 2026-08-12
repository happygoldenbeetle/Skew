using Skew.Cef.Osr;
using Skew.Models;
using Xilium.CefGlue;

namespace Skew.Cef;

/// <summary>
/// Owns one isolated hidden CEF browser for each enabled extension background
/// context. Privileged background code never executes in an ordinary web page.
/// </summary>
internal static class ExtensionBackgroundManager
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, BackgroundBrowser> Browsers =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool _initialized;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CefBrowser>
        PendingMessages = new();

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        ExtensionStore.Shared.ExtensionChanged += OnExtensionChanged;
        foreach (BrowserExtension extension in ExtensionStore.Shared.GetSnapshot())
            StartIfNeeded(extension, "startup");
    }

    public static void Shutdown()
    {
        if (!_initialized) return;
        _initialized = false;
        ExtensionStore.Shared.ExtensionChanged -= OnExtensionChanged;
        List<BackgroundBrowser> browsers;
        lock (Sync)
        {
            browsers = Browsers.Values.ToList();
            Browsers.Clear();
        }
        foreach (BackgroundBrowser browser in browsers) browser.Close();
    }

    public static bool Activate(string extensionId)
    {
        BrowserExtension? extension = ExtensionStore.Shared.GetSnapshot().FirstOrDefault(item =>
            item.Enabled && string.Equals(item.Id, extensionId, StringComparison.OrdinalIgnoreCase));
        if (extension?.Manifest is null) return false;

        string? popup = extension.Manifest.EffectiveAction?.DefaultPopup;
        if (!string.IsNullOrWhiteSpace(popup) &&
            SkewExtensionCatalog.SafeExtensionFilePath(extension.Path, popup) is not null)
        {
            string url = $"{SkewSchemes.ExtensionScheme}://{extension.Id}/" +
                string.Join('/', popup.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
            BrowserStore.Shared.NewTab(url);
            return true;
        }

        BrowserTab? selected = BrowserStore.Shared.SelectedTab;
        string tabJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = SelectedTabId,
            url = selected?.UrlString ?? string.Empty,
            active = true,
            index = 0
        });
        return ExecuteInBackground(extensionId,
            $"if(chrome.action&&chrome.action.onClicked&&chrome.action.onClicked._fire)" +
            $"chrome.action.onClicked._fire({tabJson});" +
            $"if(chrome.browserAction&&chrome.browserAction.onClicked&&chrome.browserAction.onClicked._fire)" +
            $"chrome.browserAction.onClicked._fire({tabJson});");
    }

    public static bool DispatchRuntimeMessage(
        string extensionId, object? message, string? sourceUrl,
        string requestId, CefBrowser responseBrowser)
    {
        string messageJson = System.Text.Json.JsonSerializer.Serialize(message);
        string senderJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = extensionId,
            url = sourceUrl ?? string.Empty
        });
        PendingMessages[requestId] = responseBrowser;
        bool dispatched = ExecuteInBackground(extensionId,
            $"if(chrome.runtime&&chrome.runtime.onMessage&&chrome.runtime.onMessage._fire)" +
            $"(function(){{var sent=false;function reply(value){{if(sent)return;sent=true;" +
            $"console.info('__SKEW_EXTENSION_RESPONSE__'+JSON.stringify({{requestId:{System.Text.Json.JsonSerializer.Serialize(requestId)},extensionId:{System.Text.Json.JsonSerializer.Serialize(extensionId)},result:value}}));}}" +
            $"var ls=chrome.runtime.onMessage._listeners.slice();for(var i=0;i<ls.length;i++){{try{{var r=ls[i]({messageJson},{senderJson},reply);if(r&&typeof r.then==='function')r.then(reply);}}catch(e){{}}}}" +
            $"setTimeout(function(){{reply(null);}},1000);}})();");
        if (!dispatched) PendingMessages.TryRemove(requestId, out _);
        else ScheduleMessageTimeout(requestId);
        return dispatched;
    }

    public static bool DispatchContextMenuClick(
        string extensionId, IReadOnlyDictionary<string, object?> info, object tab)
    {
        string infoJson = System.Text.Json.JsonSerializer.Serialize(info);
        string tabJson = System.Text.Json.JsonSerializer.Serialize(tab);
        return ExecuteInBackground(extensionId,
            $"if(chrome.contextMenus&&chrome.contextMenus.onClicked&&chrome.contextMenus.onClicked._fire)" +
            $"chrome.contextMenus.onClicked._fire({infoJson},{tabJson});");
    }

    public static bool DispatchTabMessage(
        string extensionId, object? message, string requestId, CefBrowser responseBrowser)
    {
        BrowserTab? selected = BrowserStore.Shared.SelectedTab;
        if (selected is null || !selected.HasBrowserView) return false;
        PendingMessages[requestId] = responseBrowser;
        Skew.Controls.SkewBrowserView.DispatchExtensionMessage(
            message, extensionId, requestId, sourceUrl: selected.UrlString,
            sourceOrigin: Uri.TryCreate(selected.UrlString, UriKind.Absolute, out Uri? uri)
                ? uri.GetLeftPart(UriPartial.Authority) : null);
        ScheduleMessageTimeout(requestId);
        return true;
    }

    public static void CompleteMessage(string requestId, string extensionId, object? result)
    {
        if (!PendingMessages.TryRemove(requestId, out CefBrowser? browser)) return;
        var response = new Dictionary<string, object?>
        {
            ["requestId"] = requestId,
            ["result"] = result
        };
        string json = System.Text.Json.JsonSerializer.Serialize(response);
        foreach (long frameId in browser.GetFrameIdentifiers())
        {
            CefFrame? frame = browser.GetFrame(frameId);
            frame?.ExecuteJavaScript(
                $"if(window.__skewExtResolve)window.__skewExtResolve({json});", frame.Url, 0);
        }
    }

    private static async void ScheduleMessageTimeout(string requestId)
    {
        await Task.Delay(1500);
        App.DispatcherQueue.TryEnqueue(() => CompleteMessage(requestId, string.Empty, null));
    }

    public static bool ExecuteScriptFiles(string extensionId, IReadOnlyList<string> files, bool allFrames)
    {
        BrowserExtension? extension = ExtensionStore.Shared.GetSnapshot().FirstOrDefault(item =>
            item.Enabled && string.Equals(item.Id, extensionId, StringComparison.OrdinalIgnoreCase));
        BrowserTab? selected = BrowserStore.Shared.SelectedTab;
        if (extension?.Manifest is null || selected is null || !selected.HasBrowserView) return false;

        var source = new System.Text.StringBuilder();
        source.Append(BrowserClient.ExtensionRuntimeJavaScript(extension));
        foreach (string file in files)
        {
            string? path = SkewExtensionCatalog.SafeExtensionFilePath(extension.Path, file);
            if (path is null) return false;
            source.Append("\n").Append(File.ReadAllText(path)).Append("\n");
        }
        selected.BrowserView.ExecuteExtensionJavaScript(source.ToString(), allFrames);
        return true;
    }

    internal static int SelectedTabId => BrowserStore.Shared.SelectedTab is { } tab
        ? tab.Id.GetHashCode() : -1;

    private static bool ExecuteInBackground(string extensionId, string javaScript)
    {
        BackgroundBrowser? background;
        lock (Sync) Browsers.TryGetValue(extensionId, out background);
        return background?.Execute(javaScript) == true;
    }

    private static void OnExtensionChanged(BrowserExtension extension, ExtensionChangeKind kind)
    {
        App.DispatcherQueue.TryEnqueue(() =>
        {
            if (kind is ExtensionChangeKind.Removed or ExtensionChangeKind.Disabled)
            {
                Stop(extension.Id);
                if (kind == ExtensionChangeKind.Removed)
                    ExtensionBridge.ClearExtensionData(extension.Id);
                return;
            }

            string reason = kind switch
            {
                ExtensionChangeKind.Installed => "install",
                ExtensionChangeKind.Updated => "update",
                _ => "startup"
            };
            StartIfNeeded(extension, reason, restart: kind == ExtensionChangeKind.Updated);
        });
    }

    private static void StartIfNeeded(BrowserExtension extension, string reason, bool restart = false)
    {
        if (!extension.Enabled || extension.Manifest?.Background is null) return;
        lock (Sync)
        {
            if (Browsers.ContainsKey(extension.Id) && !restart) return;
        }
        if (restart) Stop(extension.Id);

        var background = new BackgroundBrowser(extension, reason);
        lock (Sync) Browsers[extension.Id] = background;
        background.Start();
    }

    private static void Stop(string extensionId)
    {
        BackgroundBrowser? background = null;
        lock (Sync)
        {
            if (Browsers.Remove(extensionId, out BackgroundBrowser? removed))
                background = removed;
        }
        background?.Close();
        ExtensionBridge.ClearContextMenus(extensionId);
    }

    private sealed class BackgroundBrowser : IBrowserViewDelegate, IOsrHost
    {
        private readonly BrowserExtension _extension;
        private readonly string _reason;
        private BrowserClient? _client;
        private CefBrowser? _browser;

        public BackgroundBrowser(BrowserExtension extension, string reason)
        {
            _extension = extension;
            _reason = reason;
        }

        public void Start()
        {
            string path = SkewSchemes.ExtensionBackgroundPath;
            if (!string.IsNullOrWhiteSpace(_extension.Manifest?.Background?.Page))
                path = "/" + _extension.Manifest.Background.Page.TrimStart('/', '\\').Replace('\\', '/');
            string url = $"{SkewSchemes.ExtensionScheme}://{_extension.Id}{path}" +
                $"?__skew_background_reason={Uri.EscapeDataString(_reason)}";

            var windowInfo = CefWindowInfo.Create();
            windowInfo.SetAsWindowless(App.WindowHandle, transparent: true);
            var settings = new CefBrowserSettings
            {
                WindowlessFrameRate = 1,
                BackgroundColor = new CefColor(0, 0, 0, 0)
            };
            _client = new BrowserClient(this);
            _client.SetRenderHandler(new OsrRenderHandler(this));
            CefBrowserHost.CreateBrowser(windowInfo, _client, settings, url);
        }

        public bool Execute(string source)
        {
            CefFrame? frame = _browser?.GetMainFrame();
            if (frame is null) return false;
            frame.ExecuteJavaScript(source, frame.Url, 0);
            return true;
        }

        public void Close()
        {
            _client?.DetachDelegate();
            _browser?.GetHost().CloseBrowser(true);
            _browser = null;
        }

        void IBrowserViewDelegate.OnAfterCreated(CefBrowser browser)
        {
            _browser = browser;
            browser.GetHost().WasHidden(true);
        }
        void IBrowserViewDelegate.OnBeforeClose(CefBrowser browser) { if (_browser?.Identifier == browser.Identifier) _browser = null; }
        void IBrowserViewDelegate.OnTitleChange(string title) { }
        void IBrowserViewDelegate.OnAddressChange(string url) { }
        void IBrowserViewDelegate.OnLoadingStateChange(bool isLoading, bool canGoBack, bool canGoForward) { }
        void IBrowserViewDelegate.OnFaviconUrlChange(IReadOnlyList<string> iconUrls) { }
        void IBrowserViewDelegate.OnBeforeBrowse(string url, bool isRedirect, bool userGesture) { }
        void IBrowserViewDelegate.OnLoadStart(string url) { }
        void IBrowserViewDelegate.OnLoadEnd(string url, int httpStatusCode) { }
        void IBrowserViewDelegate.OnLoadError(int errorCode, string errorText, string failedUrl) { }
        bool IBrowserViewDelegate.OnOpenUrlFromTab(string targetUrl)
        {
            App.DispatcherQueue.TryEnqueue(() => BrowserStore.Shared.NewTab(targetUrl));
            return true;
        }
        void IBrowserViewDelegate.OnFindResult(int count, int activeMatchOrdinal) { }
        void IBrowserViewDelegate.OnCursorChange(CefCursorType type) { }

        CefRectangle IOsrHost.GetViewRectDip() => new(0, 0, 1, 1);
        float IOsrHost.DeviceScaleFactor => 1;
        void IOsrHost.OnPaint(CefPaintElementType type, CefRectangle[] dirtyRects, IntPtr buffer, int width, int height) { }
        void IOsrHost.OnPopupShow(bool show) { }
        void IOsrHost.OnPopupSize(CefRectangle rectDip) { }
        void IOsrHost.OnCursorChanged(CefCursorType type) { }
        bool IOsrHost.TryGetScreenPoint(int viewX, int viewY, out int screenX, out int screenY)
        {
            screenX = viewX;
            screenY = viewY;
            return false;
        }
        CefRectangle IOsrHost.GetRootScreenRectDip() => new(0, 0, 1, 1);
    }
}
