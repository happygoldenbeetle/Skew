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

    internal static void InjectContentScripts(CefFrame frame, string runAt)
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
                string declaredRunAt = string.IsNullOrWhiteSpace(script.RunAt)
                    ? "document_idle" : script.RunAt;
                if (string.Equals(declaredRunAt, runAt, StringComparison.OrdinalIgnoreCase) &&
                    (frame.IsMain || script.AllFrames) && ScriptMatchesURL(script, url))
                {
                    // Inject runtime shim once per extension per frame
                    if (shimInjected.Add(ext.Id))
                    {
                        string shim = ExtensionRuntimeShim.Generate(ext.Id, ext.Manifest);
                        frame.ExecuteJavaScript(shim, frame.Url, 0);

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
                            string cssJson = System.Text.Json.JsonSerializer.Serialize(css);
                            string js = $@"
                            (function() {{
                                var style = document.createElement('style');
                                style.textContent = {cssJson};
                                (document.head || document.documentElement).appendChild(style);
                            }})();";
                            frame.ExecuteJavaScript(js, frame.Url, 0);
                        }
                    }
                }
            }
        }
    }

    internal static bool ExtensionCanRunAtUrl(Skew.Models.BrowserExtension extension, string urlString)
    {
        if (extension.Manifest is null ||
            !Uri.TryCreate(urlString, UriKind.Absolute, out Uri? url))
            return false;
        return extension.Manifest.ContentScripts.Any(script => ScriptMatchesURL(script, url));
    }

    internal static string ExtensionRuntimeJavaScript(Skew.Models.BrowserExtension extension)
        => ExtensionRuntimeShim.Generate(extension.Id, extension.Manifest);

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

        if (schemePattern == "*")
        {
            if (scheme != "http" && scheme != "https") return false;
        }
        else if (schemePattern.ToLowerInvariant() != scheme)
        {
            return false;
        }

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

        string? declaredPage = ext.Manifest.Background?.Page?.Replace('\\', '/').TrimStart('/');
        bool syntheticBackground = string.Equals(
            uri.AbsolutePath, SkewSchemes.ExtensionBackgroundPath, StringComparison.OrdinalIgnoreCase);
        bool declaredBackground = !string.IsNullOrWhiteSpace(declaredPage) &&
            string.Equals(uri.AbsolutePath.TrimStart('/'), declaredPage, StringComparison.OrdinalIgnoreCase) &&
            GetQueryParameter(uri.Query, "__skew_background_reason") is not null;

        // Background code belongs only to the dedicated hidden background page.
        if (ext.Manifest.Background != null && (syntheticBackground || declaredBackground))
        {
            var scripts = new List<string>();
            // A declared MV2 background page loads its own script tags. The
            // synthetic page hosts service workers and script arrays directly.
            if (!declaredBackground)
            {
                if (!string.IsNullOrEmpty(ext.Manifest.Background.ServiceWorker))
                    scripts.Add(ext.Manifest.Background.ServiceWorker);
                if (ext.Manifest.Background.Scripts != null)
                    scripts.AddRange(ext.Manifest.Background.Scripts);
            }

            var backgroundSource = new System.Text.StringBuilder();
            backgroundSource.Append("if(!window.__skewBackgroundScriptsLoaded){window.__skewBackgroundScriptsLoaded=true;");
            foreach (var scriptPath in scripts.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string? fullPath = SkewExtensionCatalog.SafeExtensionFilePath(ext.Path, scriptPath);
                if (fullPath is not null)
                {
                    backgroundSource.Append("\n").Append(File.ReadAllText(fullPath)).Append("\n");
                }
            }
            string reason = GetQueryParameter(uri.Query, "__skew_background_reason") ?? "startup";
            string reasonJson = System.Text.Json.JsonSerializer.Serialize(reason);
            backgroundSource.Append($@"
setTimeout(function(){{
  var reason={reasonJson};
  if(reason==='install'||reason==='update'){{
    if(chrome.runtime.onInstalled&&chrome.runtime.onInstalled._fire)
      chrome.runtime.onInstalled._fire({{reason:reason}});
  }} else if(chrome.runtime.onStartup&&chrome.runtime.onStartup._fire) {{
    chrome.runtime.onStartup._fire();
  }}
}},0);
}}");
            frame.ExecuteJavaScript(backgroundSource.ToString(), frame.Url, 0);
        }
    }

    private static string? GetQueryParameter(string query, string name)
    {
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            string key = equals < 0 ? pair : pair[..equals];
            if (string.Equals(Uri.UnescapeDataString(key), name, StringComparison.Ordinal))
                return Uri.UnescapeDataString(equals < 0 ? string.Empty : pair[(equals + 1)..]);
        }
        return null;
    }

    /// <summary>
    /// Turn the disabled Chrome-only action on Web Store detail pages into a
    /// native Skew install request. The store normally calls
    /// chrome.webstorePrivate, which is part of Chrome rather than CEF.
    /// </summary>
    internal static void InjectWebStoreShim(CefFrame frame, string? navigationUrl = null)
    {
        if (!frame.IsMain ||
            !Uri.TryCreate(navigationUrl ?? frame.Url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "chromewebstore.google.com", StringComparison.OrdinalIgnoreCase))
            return;

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 3 || !string.Equals(segments[0], "detail", StringComparison.OrdinalIgnoreCase))
            return;

        string extensionId = segments[^1].ToLowerInvariant();
        if (extensionId.Length != 32 || extensionId.Any(c => c is < 'a' or > 'p'))
            return;

        bool installed = Skew.Models.ExtensionStore.Shared.GetSnapshot()
            .Any(ext => string.Equals(ext.Id, extensionId, StringComparison.OrdinalIgnoreCase));
        string idJson = System.Text.Json.JsonSerializer.Serialize(extensionId);
        string installedJson = installed ? "true" : "false";

        string js = $$"""
            (() => {
              const incomingExtensionId = {{idJson}};
              const incomingInstalled = {{installedJson}};
              if (window.__skewWebStoreShim) {
                window.__skewWebStoreShim.update(incomingExtensionId, incomingInstalled);
                return;
              }

              let extensionId = incomingExtensionId;
              let state = incomingInstalled ? 'installed' : 'ready';
              let observer;
              let renderScheduled = false;
              let lastBannerScan = 0;

              const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
              const labels = [
                'Add to Chrome', 'Add to Desktop', 'Add to Skew', 'Installing...',
                'Added to Skew', 'Remove from Skew', 'Removing...'
              ];
              const isVisible = element => {
                if (!(element instanceof Element) || !element.isConnected ||
                    element.hidden || element.getAttribute('aria-hidden') === 'true') return false;
                const rect = element.getBoundingClientRect();
                return rect.width > 0 && rect.height > 0;
              };
              const findButton = () => {
                const candidates = Array.from(document.querySelectorAll('button')).filter(button => {
                  const text = normalize(button.textContent);
                  const ariaLabel = normalize(button.getAttribute('aria-label'));
                  return labels.some(label => text.includes(label) || ariaLabel.includes(label)) &&
                    isVisible(button);
                });
                candidates.sort((left, right) => {
                  const score = button => {
                    const text = normalize(button.textContent);
                    let value = button.dataset.skewWebStoreExtensionId === extensionId ? 1000 : 0;
                    if (text === 'Add to Chrome' || text === 'Add to Desktop') value += 500;
                    if (button.dataset.skewWebStoreButton === 'true') value += 100;
                    const rect = button.getBoundingClientRect();
                    if (rect.top >= 0 && rect.top <= innerHeight && rect.left >= 0 && rect.left <= innerWidth)
                      value += 50;
                    return value;
                  };
                  return score(right) - score(left);
                });
                return candidates[0];
              };

              const hideUnavailableBanner = () => {
                const now = performance.now();
                if (now - lastBannerScan < 750) return;
                lastBannerScan = now;
                const message = 'Item currently unavailable. Please check the troubleshooting guide.';
                const root = document.body || document.documentElement;
                if (!root) return;

                const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
                const messageElements = [];
                for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                  const element = node.parentElement;
                  if (normalize(node.nodeValue).includes(message) && element &&
                    !element.closest('[data-skew-unavailable-banner-hidden="true"]'))
                    messageElements.push(element);
                }

                for (const messageElement of messageElements) {
                  let current = messageElement;
                  for (let depth = 0; current && depth < 5; depth++, current = current.parentElement) {
                    if (current === document.body || current === document.documentElement) break;
                    const text = normalize(current.textContent);
                    const rect = current.getBoundingClientRect();
                    const hasGuideControl = Array.from(
                      current.querySelectorAll('a, button, [role="button"]')
                    ).some(element => normalize(element.textContent) === 'View guide');
                    const compactAlert = rect.width >= 200 && rect.width <= innerWidth &&
                      rect.height >= 32 && rect.height <= 180 && text.length <= message.length + 80;
                    if (hasGuideControl && compactAlert && text.includes(message) && isVisible(current)) {
                      current.dataset.skewUnavailableBannerHidden = 'true';
                      current.style.setProperty('display', 'none', 'important');
                      return;
                    }
                  }
                }
              };

              const render = () => {
                const button = findButton();
                if (!button) return;
                hideUnavailableBanner();
                const label = button.querySelector('[jsname="V67aGc"]') || button;
                const text = state === 'installing' ? 'Installing...' :
                  state === 'removing' ? 'Removing...' :
                  state === 'installed' ? 'Remove from Skew' : 'Add to Skew';
                if (normalize(label.textContent) !== text) label.textContent = text;
                if (button.dataset.skewWebStoreButton !== 'true')
                  button.dataset.skewWebStoreButton = 'true';
                if (button.dataset.skewWebStoreExtensionId !== extensionId)
                  button.dataset.skewWebStoreExtensionId = extensionId;

                const disabled = state === 'installing' || state === 'removing';
                if (button.disabled !== disabled) button.disabled = disabled;
                if (button.hasAttribute('disabled') !== disabled)
                  button.toggleAttribute('disabled', disabled);
                if (button.getAttribute('aria-label') !== text)
                  button.setAttribute('aria-label', text);
                if (disabled) {
                  if (button.getAttribute('aria-disabled') !== 'true')
                    button.setAttribute('aria-disabled', 'true');
                } else if (button.hasAttribute('aria-disabled')) {
                  button.removeAttribute('aria-disabled');
                }
              };

              const scheduleRender = () => {
                if (renderScheduled) return;
                renderScheduled = true;
                setTimeout(() => {
                  renderScheduled = false;
                  render();
                }, 50);
              };

              document.addEventListener('click', event => {
                const button = event.target instanceof Element ? event.target.closest('button') : null;
                if (!button || button.dataset.skewWebStoreExtensionId !== extensionId ||
                    (state !== 'ready' && state !== 'installed')) return;
                event.preventDefault();
                event.stopImmediatePropagation();
                const removing = state === 'installed';
                state = removing ? 'removing' : 'installing';
                render();
                console.info((removing ? '__SKEW_WEBSTORE_REMOVE__' :
                  '__SKEW_WEBSTORE_INSTALL__') + extensionId);
              }, true);

              window.__skewWebStoreInstallResult = result => {
                if (!result || result.id !== extensionId) return;
                state = result.status === 'installed' ? 'installed' : 'ready';
                scheduleRender();
              };

              window.__skewWebStoreShim = {
                update(nextExtensionId, nextInstalled) {
                  if (extensionId !== nextExtensionId) {
                    document.querySelectorAll('button[data-skew-web-store-button="true"]').forEach(button => {
                      delete button.dataset.skewWebStoreButton;
                      delete button.dataset.skewWebStoreExtensionId;
                    });
                  }
                  extensionId = nextExtensionId;
                  state = nextInstalled ? 'installed' : 'ready';
                  scheduleRender();
                }
              };

              const observe = () => {
                if (observer || !document.documentElement) return;
                observer = new MutationObserver(scheduleRender);
                observer.observe(document.documentElement, {
                  childList: true,
                  subtree: true
                });
                scheduleRender();
              };

              if (document.documentElement) observe();
              else document.addEventListener('DOMContentLoaded', observe, { once: true });
              // The Web Store retains listing DOM trees and swaps them during
              // client side navigation. Keep a low frequency reconciliation
              // pass alive for transitions that do not mutate the current root.
              setInterval(scheduleRender, 1000);
              scheduleRender();
            })();
            """;

        frame.ExecuteJavaScript(js, frame.Url, 0);
    }

    internal static void CompleteWebStoreInstall(CefBrowser browser, string extensionId, bool installed)
    {
        string payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = extensionId,
            status = installed ? "installed" : "failed"
        });
        var frame = browser.GetMainFrame();
        frame?.ExecuteJavaScript(
            $"window.__skewWebStoreInstallResult?.({payload});", frame.Url, 0);
    }

    internal static void CompleteWebStoreRemove(CefBrowser browser, string extensionId, bool removed)
    {
        string payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = extensionId,
            status = removed ? "ready" : "installed"
        });
        CefFrame? frame = browser.GetMainFrame();
        frame?.ExecuteJavaScript(
            $"window.__skewWebStoreInstallResult?.({payload});", frame.Url, 0);
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
