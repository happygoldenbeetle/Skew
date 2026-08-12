using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;
using Xilium.CefGlue;

namespace Skew.Cef;

/// <summary>
/// CefClient implementation for a single browser (tab). Direct port of the mac
/// BrowserClient (App/BrowserClient.h/.mm).
///
/// <para>
/// All navigation/display state is forwarded to an <see cref="IBrowserViewDelegate"/>,
/// which the view layer (<see cref="Controls.SkewBrowserView"/>) implements to
/// drive the WinUI chrome. CefGlue invokes these handlers on the CEF UI thread;
/// the delegate implementation is responsible for marshalling to the WinUI
/// dispatcher before touching UI (see <see cref="IBrowserViewDelegate"/>).
/// </para>
/// </summary>
public sealed class BrowserClient : CefClient
{
    // Process-wide auto-PiP preference, read when injecting the media agent into
    // newly loaded frames (mac SkewSetAutoPiPEnabled / SkewAutoPiPEnabled).
    private static volatile bool s_autoPiPEnabled;
    public static void SetAutoPiPEnabled(bool enabled) => s_autoPiPEnabled = enabled;
    public static bool AutoPiPEnabled => s_autoPiPEnabled;

    // Live download callbacks keyed by CEF download id, so a tab-less request can
    // be canceled later (mac SkewCancelDownload).
    private static readonly ConcurrentDictionary<uint, CefDownloadItemCallback> s_downloads = new();

    public static bool CancelDownload(uint downloadId)
    {
        if (s_downloads.TryGetValue(downloadId, out var cb))
        {
            cb.Cancel();
            return true;
        }
        return false;
    }

    private IBrowserViewDelegate? _delegate; // not owned; cleared via Detach.
    private int _extensionTabId = -1;

    private readonly SkewLifeSpanHandler _lifeSpan;
    private readonly SkewLoadHandler _load;
    private readonly SkewDisplayHandler _display;
    private readonly SkewDownloadHandler _download;
    private readonly SkewJSDialogHandler _jsDialog;
    private readonly SkewFindHandler _find;
    private readonly SkewKeyboardHandler _keyboard;
    private readonly SkewRequestHandler _request;
    private readonly SkewContextMenuHandler _contextMenu;
    private readonly SkewFocusHandler _focus;

    // Only set for windowless browsers. CEF decides windowed vs windowless by
    // whether this returns null, so it must stay null for the DevTools client.
    private CefRenderHandler? _render;

    public event EventHandler<BrowserContextMenuEventArgs>? ContextMenuRequested;

    public void InvokeContextMenu(CefBrowser browser, CefFrame frame, BrowserContextMenuEventArgs args)
    {
        ContextMenuRequested?.Invoke(this, args);
    }

    public BrowserClient(IBrowserViewDelegate viewDelegate)
    {
        _delegate = viewDelegate;
        _lifeSpan = new SkewLifeSpanHandler(this);
        _load = new SkewLoadHandler(this);
        _display = new SkewDisplayHandler(this);
        _download = new SkewDownloadHandler(this);
        _jsDialog = new SkewJSDialogHandler(this);
        _find = new SkewFindHandler(this);
        _keyboard = new SkewKeyboardHandler(this);
        _request = new SkewRequestHandler(this);
        _contextMenu = new SkewContextMenuHandler(this);
        _focus = new SkewFocusHandler(this);
    }

    /// <summary>Detach when the hosting view goes away to avoid dangling callbacks.</summary>
    public void DetachDelegate() => _delegate = null;

    /// <summary>
    /// Install the offscreen render sink. Must be called before
    /// <c>CreateBrowser</c> — CEF reads the render handler once, at creation,
    /// to decide whether the browser is windowless.
    /// </summary>
    public void SetRenderHandler(CefRenderHandler handler) => _render = handler;

    public void SetExtensionTabId(int tabId) => _extensionTabId = tabId;
    internal int ExtensionTabId => _extensionTabId;
    internal IBrowserViewDelegate? Delegate => _delegate;

    // ── CefClient handler accessors ──────────────────────────────────────

    protected override CefLifeSpanHandler GetLifeSpanHandler() => _lifeSpan;
    protected override CefLoadHandler GetLoadHandler() => _load;
    protected override CefDisplayHandler GetDisplayHandler() => _display;
    protected override CefDownloadHandler GetDownloadHandler() => _download;
    protected override CefJSDialogHandler GetJSDialogHandler() => _jsDialog;
    protected override CefFindHandler GetFindHandler() => _find;
    protected override CefKeyboardHandler GetKeyboardHandler() => _keyboard;
    protected override CefRequestHandler GetRequestHandler() => _request;
    protected override CefContextMenuHandler GetContextMenuHandler() => _contextMenu;
    protected override CefFocusHandler GetFocusHandler() => _focus;
    protected override CefRenderHandler GetRenderHandler() => _render!;

    // ── Agent injection helpers (mac OnLoadStart/OnLoadEnd) ───────────────

    internal static void InjectPasskeyShim(CefFrame frame)
    {
        // Injected as early as possible — before page scripts can capture the
        // original navigator.credentials methods (mac OnLoadStart).
        string js = SkewAgentScripts.PasskeyAgent;
        frame.ExecuteJavaScript(js, frame.Url, 0);
    }

    internal static void InjectContentScripts(CefFrame frame)
    {
        string urlString = frame.Url;
        if (string.IsNullOrEmpty(urlString) || urlString.StartsWith("chrome://"))
            return;

        if (!Uri.TryCreate(urlString, UriKind.Absolute, out Uri? url) || url == null)
            return;

        // Track which extensions have had their shim injected this frame
        var shimInjected = new HashSet<string>();

        var extensions = Skew.Models.ExtensionStore.Shared.GetSnapshot();

        foreach (var ext in extensions)
        {
            if (!ext.Enabled || ext.Manifest == null) continue;

            foreach (var script in ext.Manifest.ContentScripts)
            {
                if (ScriptMatchesURL(script, url))
                {
                    // Inject runtime shim once per extension per frame
                    if (shimInjected.Add(ext.Id))
                    {
                        string shim = ExtensionRuntimeShim.Generate(ext.Id, ext.Manifest);
                        frame.ExecuteJavaScript(shim, frame.Url, 0);

                        // Also inject background scripts so contextMenus.create etc.
                        // can register their items. In a proper implementation these
                        // would run in a hidden background WebView, but for now we
                        // run them in-page alongside content scripts.
                        if (ext.Manifest.Background != null)
                        {
                            var bgScripts = new List<string>();
                            if (!string.IsNullOrEmpty(ext.Manifest.Background.ServiceWorker))
                                bgScripts.Add(ext.Manifest.Background.ServiceWorker);
                            if (ext.Manifest.Background.Scripts != null)
                                bgScripts.AddRange(ext.Manifest.Background.Scripts);

                            foreach (var bgPath in bgScripts)
                            {
                                var fullBgPath = Path.Combine(ext.Path, bgPath);
                                if (File.Exists(fullBgPath))
                                {
                                    string bgCode = File.ReadAllText(fullBgPath);
                                    frame.ExecuteJavaScript(bgCode, frame.Url, 0);
                                }
                            }

                            // Fire onInstalled and onStartup asynchronously so the background
                            // scripts have time to attach their listeners.
                            string fireEventsJs = @"
                                setTimeout(function() {
                                    if (chrome && chrome.runtime && chrome.runtime.onInstalled && chrome.runtime.onInstalled._fire) {
                                        chrome.runtime.onInstalled._fire({ reason: 'install' });
                                    }
                                    if (chrome && chrome.runtime && chrome.runtime.onStartup && chrome.runtime.onStartup._fire) {
                                        chrome.runtime.onStartup._fire();
                                    }
                                }, 10);
                            ";
                            frame.ExecuteJavaScript(fireEventsJs, frame.Url, 0);
                        }
                    }

                    // Inject JS
                    foreach (var jsPath in script.Js)
                    {
                        var fullPath = Path.Combine(ext.Path, jsPath);
                        if (File.Exists(fullPath))
                        {
                            string code = File.ReadAllText(fullPath);
                            frame.ExecuteJavaScript(code, frame.Url, 0);
                        }
                    }
                    
                    // Inject CSS
                    foreach (var cssPath in script.Css)
                    {
                        var fullPath = Path.Combine(ext.Path, cssPath);
                        if (File.Exists(fullPath))
                        {
                            string css = File.ReadAllText(fullPath);
                            // Basic CSS injection using JS
                            string js = $@"
                            (function() {{
                                var style = document.createElement('style');
                                style.textContent = `{css.Replace("`", "\\`")}`;
                                (document.head || document.documentElement).appendChild(style);
                            }})();";
                            frame.ExecuteJavaScript(js, frame.Url, 0);
                        }
                    }
                }
            }
        }
    }

    private static bool ScriptMatchesURL(Skew.Models.ContentScriptMeta script, Uri url)
    {
        if (script.Matches == null || script.Matches.Count == 0) return false;

        bool included = false;
        foreach (var pattern in script.Matches)
        {
            if (MatchExtensionPattern(pattern, url))
            {
                included = true;
                break;
            }
        }
        if (!included) return false;

        if (script.ExcludeMatches != null)
        {
            foreach (var pattern in script.ExcludeMatches)
            {
                if (MatchExtensionPattern(pattern, url))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool MatchExtensionPattern(string pattern, Uri url)
    {
        if (pattern == "<all_urls>")
        {
            return url.Scheme == "http" || url.Scheme == "https" || url.Scheme == "file";
        }

        int schemeSep = pattern.IndexOf("://");
        if (schemeSep == -1) return false;

        string schemePattern = pattern.Substring(0, schemeSep);
        string rest = pattern.Substring(schemeSep + 3);
        int slash = rest.IndexOf('/');
        string hostPattern = slash == -1 ? rest : rest.Substring(0, slash);
        string pathPattern = slash == -1 ? "/*" : rest.Substring(slash);

        string scheme = url.Scheme.ToLowerInvariant();
        string host = url.Host.ToLowerInvariant();
        string path = string.IsNullOrEmpty(url.AbsolutePath) ? "/" : url.AbsolutePath;

        if (schemePattern != "*" && schemePattern.ToLowerInvariant() != scheme)
            return false;

        if (hostPattern.StartsWith("*."))
        {
            string suffix = hostPattern.Substring(1).ToLowerInvariant();
            if (!host.EndsWith(suffix) && host != hostPattern.Substring(2).ToLowerInvariant())
                return false;
        }
        else if (!WildcardMatch(hostPattern.ToLowerInvariant(), host))
        {
            return false;
        }

        return WildcardMatch(pathPattern, path);
    }

    private static bool WildcardMatch(string pattern, string value)
    {
        if (pattern == "*") return true;
        string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
    }

    internal static void InjectMediaAgent(CefFrame frame)
    {
        // Injected once a frame finishes loading (mac OnLoadEnd). The auto-PiP
        // attribute is only set when the process-wide preference is enabled.
        string js = SkewAgentScripts.MediaAgent(s_autoPiPEnabled);
        frame.ExecuteJavaScript(js, frame.Url, 0);
    }

    /// <summary>
    /// For skew-extension:// pages (background pages, popups, options), inject
    /// the runtime shim so chrome.contextMenus, chrome.storage, etc. work.
    /// Also runs background scripts declared in manifest.json.
    /// </summary>
    internal static void InjectExtensionPageShim(CefFrame frame)
    {
        string urlString = frame.Url;
        if (string.IsNullOrEmpty(urlString) || !urlString.StartsWith("skew-extension://"))
            return;

        // Extract extension ID from URL: skew-extension://<extensionId>/...
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out Uri? uri) || uri == null)
            return;

        string extensionId = uri.Host;

        var extensions = Skew.Models.ExtensionStore.Shared.GetSnapshot();
        var ext = extensions.FirstOrDefault(e => e.Id.Equals(extensionId, StringComparison.OrdinalIgnoreCase) && e.Enabled);

        if (ext?.Manifest == null) return;

        // Inject the runtime shim
        string shim = ExtensionRuntimeShim.Generate(ext.Id, ext.Manifest);
        frame.ExecuteJavaScript(shim, frame.Url, 0);

        // If this is a background page, inject the background scripts
        if (ext.Manifest.Background != null)
        {
            var scripts = new List<string>();
            if (!string.IsNullOrEmpty(ext.Manifest.Background.ServiceWorker))
                scripts.Add(ext.Manifest.Background.ServiceWorker);
            if (ext.Manifest.Background.Scripts != null)
                scripts.AddRange(ext.Manifest.Background.Scripts);

            foreach (var scriptPath in scripts)
            {
                var fullPath = Path.Combine(ext.Path, scriptPath);
                if (File.Exists(fullPath))
                {
                    string code = File.ReadAllText(fullPath);
                    frame.ExecuteJavaScript(code, frame.Url, 0);
                }
            }

            string fireEventsJs = @"
                setTimeout(function() {
                    if (chrome && chrome.runtime && chrome.runtime.onInstalled && chrome.runtime.onInstalled._fire) {
                        chrome.runtime.onInstalled._fire({ reason: 'install' });
                    }
                    if (chrome && chrome.runtime && chrome.runtime.onStartup && chrome.runtime.onStartup._fire) {
                        chrome.runtime.onStartup._fire();
                    }
                }, 10);
            ";
            frame.ExecuteJavaScript(fireEventsJs, frame.Url, 0);
        }
    }

    internal static void RegisterDownload(uint id, CefDownloadItemCallback callback)
        => s_downloads[id] = callback;

    internal static void ForgetDownload(uint id) => s_downloads.TryRemove(id, out _);
}

public class BrowserContextMenuEventArgs : EventArgs
{
    public int X { get; set; }
    public int Y { get; set; }
    public List<ContextMenuItemModel> Items { get; set; } = new();
    public Action<int?>? Callback { get; set; }
}

public class ContextMenuItemModel
{
    public int CommandId { get; set; }
    public string Label { get; set; } = "";
    public CefMenuItemType Type { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsChecked { get; set; }
    public bool IsVisible { get; set; }
    public List<ContextMenuItemModel>? SubMenuItems { get; set; }
}
