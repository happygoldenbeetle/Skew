using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Skew.Models;
using Xilium.CefGlue;

namespace Skew.Cef;

/// <summary>
/// C# side of the extension IPC bridge. Routes incoming __SKEW_EXTENSION__
/// requests to the appropriate handler and returns a JSON response dictionary.
/// </summary>
internal static class ExtensionBridge
{
    // Per-extension registered context menu items.
    // Key = extensionId, Value = list of menu item property dictionaries.
    private static readonly ConcurrentDictionary<string, List<Dictionary<string, object?>>> _contextMenuItems = new();

    // Per-extension simple key-value storage (chrome.storage.local).
    private static readonly ConcurrentDictionary<string, Dictionary<string, JsonElement>> _storage = new();

    private const int StorageQuotaBytes = 10 * 1024 * 1024;

    public static void ClearContextMenus(string extensionId)
        => _contextMenuItems.TryRemove(extensionId, out _);

    public static void ClearExtensionData(string extensionId)
    {
        ClearContextMenus(extensionId);
        _storage.TryRemove(extensionId, out _);
        try
        {
            string folder = Path.GetDirectoryName(StoragePath(extensionId))!;
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to remove extension data: {ex.Message}");
        }
    }

    public static bool PrepareContextMenuRegistrationMigration(BrowserExtension extension)
    {
        if (!HasPermission(extension, "contextMenus")) return false;
        string path = ContextMenusPath(extension.Id);
        if (File.Exists(path)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "[]");
        return true;
    }

    /// <summary>
    /// Handle an incoming extension bridge request and return a JSON-serialisable
    /// response dictionary. Returns null if the method is unrecognised.
    /// </summary>
    public static Dictionary<string, object?> HandleRequest(
        CefBrowser browser, BrowserClient client, string sourceUrl,
        string requestId, string extensionId, string method, JsonElement args)
    {
        try
        {
            string sourceKind = sourceUrl.StartsWith(SkewSchemes.ExtensionScheme + "://",
                StringComparison.OrdinalIgnoreCase) ? "extension" : "content";
            ExtensionDiagnostics.Write("api", extensionId,
                $"{sourceKind} called {method}.");
            BrowserExtension? extension = ExtensionStore.Shared.GetSnapshot().FirstOrDefault(item =>
                item.Enabled && string.Equals(item.Id, extensionId, StringComparison.OrdinalIgnoreCase));
            if (extension?.Manifest is null)
                return MakeError(requestId, "Extension is not installed or enabled.");

            bool extensionPage = Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri? sourceUri) &&
                string.Equals(sourceUri.Scheme, SkewSchemes.ExtensionScheme, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(sourceUri.Host, extensionId, StringComparison.OrdinalIgnoreCase);
            bool activeTabPage = HasPermission(extension, "activeTab") &&
                string.Equals(BrowserStore.Shared.SelectedTab?.UrlString, sourceUrl,
                    StringComparison.OrdinalIgnoreCase);
            if (!extensionPage && !activeTabPage && !BrowserClient.ExtensionCanRunAtUrl(extension, sourceUrl))
                return MakeError(requestId, "Extension request came from an unauthorized page.");

            if (method.StartsWith("contextMenus."))
            {
                if (!HasPermission(extension, "contextMenus"))
                    return MakeError(requestId, "The extension has not requested contextMenus permission.");
                return HandleContextMenus(requestId, extensionId, method, args);
            }
            if (method.StartsWith("storage.local."))
            {
                if (!HasPermission(extension, "storage"))
                    return MakeError(requestId, "The extension has not requested storage permission.");
                return HandleStorageLocal(requestId, extensionId, method, args);
            }
            if (method == "tabs.query")
            {
                return MakeResponse(requestId, QueryTabs(
                    args.TryGetProperty("queryInfo", out JsonElement queryInfo) ? queryInfo : default));
            }
            if (method == "tabs.create")
            {
                string? url = null;
                bool active = true;
                if (args.TryGetProperty("createProperties", out JsonElement properties))
                {
                    if (properties.TryGetProperty("url", out JsonElement urlElement))
                        url = urlElement.GetString();
                    // A background tab is a real request: openers that stack up
                    // several links expect to stay where they are.
                    if (properties.TryGetProperty("active", out JsonElement activeElement) &&
                        activeElement.ValueKind == JsonValueKind.False)
                        active = false;
                }

                // A relative path means a page inside the extension.
                if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Relative, out _))
                    url = $"{SkewSchemes.ExtensionScheme}://{extensionId}/" + url.TrimStart('/');
                // chrome.tabs.create({}) with no url opens the new tab page.
                if (string.IsNullOrWhiteSpace(url)) url = SkewSchemes.InternalScheme + "://newtab/";

                string target = url;
                bool selectIt = active;
                // Captured now, not inside the lambda: NewTab selects what it
                // creates, so reading the selection later would hand back the
                // new tab and "restoring" it would be a no-op.
                BrowserTab? wasSelected = BrowserStore.Shared.SelectedTab;
                App.DispatcherQueue.TryEnqueue(() =>
                {
                    BrowserStore.Shared.NewTab(target);
                    if (!selectIt && wasSelected is not null)
                        BrowserStore.Shared.SelectTab(wasSelected.Id);
                });
                return MakeResponse(requestId, new
                {
                    id = -1, url = target, active, index = 0, windowId = 1, status = "loading",
                });
            }
            if (method == "tabs.update")
            {
                int? targetId = args.TryGetProperty("tabId", out JsonElement tabIdElement) &&
                    tabIdElement.TryGetInt32(out int parsedId) ? parsedId : null;
                BrowserTab? target = FindTab(targetId) ?? BrowserStore.Shared.SelectedTab;
                if (target is null) return MakeError(requestId, "No tab to update.");

                string? navigateTo = null;
                bool activate = false;
                if (args.TryGetProperty("updateProperties", out JsonElement update))
                {
                    if (update.TryGetProperty("url", out JsonElement urlElement))
                        navigateTo = urlElement.GetString();
                    if (update.TryGetProperty("active", out JsonElement activeElement) &&
                        activeElement.ValueKind == JsonValueKind.True)
                        activate = true;
                }

                BrowserTab captured = target;
                string? navigation = navigateTo;
                bool shouldActivate = activate;
                App.DispatcherQueue.TryEnqueue(() =>
                {
                    if (shouldActivate) BrowserStore.Shared.SelectTab(captured.Id);
                    if (!string.IsNullOrWhiteSpace(navigation))
                        captured.Load(BrowserStore.Shared.FormatUrl(navigation));
                });
                return MakeResponse(requestId, TabSnapshot(captured, 0));
            }
            if (method == "tabs.get")
            {
                int? wanted = args.TryGetProperty("tabId", out JsonElement getId) &&
                    getId.TryGetInt32(out int parsed) ? parsed : null;
                BrowserTab? found = FindTab(wanted) ?? BrowserStore.Shared.SelectedTab;
                return MakeResponse(requestId, found is null ? null : TabSnapshot(found, 0));
            }
            if (method == "tabs.sendMessage")
            {
                object? message = args.TryGetProperty("message", out JsonElement messageElement)
                    ? messageElement.Clone() : null;
                if (!ExtensionBackgroundManager.DispatchTabMessage(extensionId, message, requestId, browser))
                    return MakeError(requestId, "No active tab is available for the message.");
                return MakeDeferredResponse(requestId);
            }
            if (method == "tabs.reload")
            {
                App.DispatcherQueue.TryEnqueue(() => BrowserStore.Shared.Reload());
                return MakeResponse(requestId, (object?)null);
            }
            if (method == "tabs.remove")
            {
                var requestedIds = new List<int>();
                if (args.TryGetProperty("tabIds", out JsonElement ids))
                {
                    if (ids.ValueKind == JsonValueKind.Number && ids.TryGetInt32(out int singleId))
                        requestedIds.Add(singleId);
                    else if (ids.ValueKind == JsonValueKind.Array)
                        requestedIds.AddRange(ids.EnumerateArray().Where(item => item.TryGetInt32(out _))
                            .Select(item => item.GetInt32()));
                }

                // Any tab by id, not just the selected one — a tab manager
                // closing duplicates names every tab it wants gone.
                var doomed = requestedIds
                    .Select(id => FindTab(id))
                    .Where(tab => tab is not null)
                    .Select(tab => tab!.Id)
                    .ToList();
                if (doomed.Count > 0)
                    App.DispatcherQueue.TryEnqueue(() =>
                    {
                        foreach (Guid id in doomed) BrowserStore.Shared.CloseTab(id);
                    });
                return MakeResponse(requestId, (object?)null);
            }
            if (method == "tabs.detectLanguage")
            {
                string language = System.Globalization.CultureInfo.CurrentUICulture
                    .TwoLetterISOLanguageName.ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(language) || language.Length != 2)
                    language = "en";
                return MakeResponse(requestId, language);
            }
            if (method == "scripting.executeScript")
            {
                if (!HasPermission(extension, "scripting"))
                    return MakeError(requestId, "The extension has not requested scripting permission.");
                if (!args.TryGetProperty("injection", out JsonElement injection) ||
                    !injection.TryGetProperty("files", out JsonElement fileArray) ||
                    fileArray.ValueKind != JsonValueKind.Array)
                    return MakeError(requestId, "Only file based script injection is supported.");
                string[] files = fileArray.EnumerateArray()
                    .Select(item => item.GetString() ?? string.Empty).ToArray();
                bool allFrames = injection.TryGetProperty("target", out JsonElement target) &&
                    target.TryGetProperty("allFrames", out JsonElement allFramesElement) &&
                    allFramesElement.ValueKind == JsonValueKind.True;
                if (files.Length == 0 || !ExtensionBackgroundManager.ExecuteScriptFiles(extensionId, files, allFrames))
                    return MakeError(requestId, "The extension script could not be injected.");
                return MakeResponse(requestId, Array.Empty<object>());
            }
            if (method is "scripting.registerContentScripts" or
                "scripting.unregisterContentScripts" or
                "scripting.getRegisteredContentScripts" or
                "scripting.updateContentScripts")
            {
                if (!HasPermission(extension, "scripting"))
                    return MakeError(requestId, "The extension has not requested scripting permission.");

                switch (method)
                {
                    case "scripting.registerContentScripts":
                    case "scripting.updateContentScripts":
                        if (args.TryGetProperty("scripts", out JsonElement scripts))
                            DynamicContentScripts.Register(extensionId, scripts);
                        return MakeResponse(requestId, (object?)null);

                    case "scripting.unregisterContentScripts":
                    {
                        string[] ids = args.TryGetProperty("filter", out JsonElement filter)
                            ? StringArray(filter, "ids") : [];
                        DynamicContentScripts.Unregister(extensionId, ids);
                        return MakeResponse(requestId, (object?)null);
                    }

                    default:
                        return MakeResponse(requestId, DynamicContentScripts.For(extensionId));
                }
            }
            if (method is "scripting.insertCSS" or "scripting.removeCSS")
            {
                if (!HasPermission(extension, "scripting"))
                    return MakeError(requestId, "The extension has not requested scripting permission.");
                if (!args.TryGetProperty("injection", out JsonElement injection) ||
                    !ExtensionBackgroundManager.ApplyStyle(
                        extensionId, injection, remove: method.EndsWith("removeCSS", StringComparison.Ordinal)))
                    return MakeError(requestId, "The extension style could not be applied.");
                return MakeResponse(requestId, (object?)null);
            }
            if (method == "runtime.sendMessage")
            {
                object? message = args.TryGetProperty("message", out JsonElement messageElement)
                    ? messageElement.Clone() : null;
                if (!ExtensionBackgroundManager.DispatchRuntimeMessage(
                        extensionId, message, sourceUrl, requestId, browser))
                    return MakeResponse(requestId, (object?)null);
                return MakeDeferredResponse(requestId);
            }
            if (method == "popup.size")
            {
                // The popup telling the host how big it wants to be.
                if (args.TryGetProperty("width", out JsonElement widthElement) &&
                    args.TryGetProperty("height", out JsonElement heightElement) &&
                    widthElement.TryGetDouble(out double width) &&
                    heightElement.TryGetDouble(out double height))
                {
                    App.DispatcherQueue.TryEnqueue(() =>
                        MainWindow.Instance?.ResizeExtensionPopup(extensionId, width, height));
                }
                return MakeResponse(requestId, (object?)null);
            }
            if (method is "permissions.request" or "permissions.remove")
            {
                string[] wantedPermissions = StringArray(args, "permissions");
                string[] wantedOrigins = StringArray(args, "origins");

                if (method == "permissions.remove")
                {
                    var removed = ExtensionPermissions.Remove(extensionId, wantedPermissions, wantedOrigins);
                    return MakeResponse(requestId, new
                    {
                        permissions = removed.Permissions, origins = removed.Origins,
                    });
                }

                var result = ExtensionPermissions.Request(extension, wantedPermissions, wantedOrigins);
                return MakeResponse(requestId, new
                {
                    granted = result.Granted,
                    permissions = result.Permissions,
                    origins = result.Origins,
                });
            }
            if (method.StartsWith("cookies."))
            {
                if (!HasPermission(extension, "cookies"))
                    return MakeError(requestId, "The extension has not requested cookies permission.");
                return HandleCookies(browser, requestId, method, args);
            }
            if (method.StartsWith("downloads."))
            {
                if (!HasPermission(extension, "downloads"))
                    return MakeError(requestId, "The extension has not requested downloads permission.");
                return HandleDownloads(browser, requestId, method, args);
            }
            if (method.StartsWith("declarativeNetRequest."))
            {
                if (!HasPermission(extension, "declarativeNetRequest") &&
                    !HasPermission(extension, "declarativeNetRequestWithHostAccess"))
                    return MakeError(requestId,
                        "The extension has not requested declarativeNetRequest permission.");

                switch (method)
                {
                    case "declarativeNetRequest.updateDynamicRules":
                    case "declarativeNetRequest.updateSessionRules":
                        DeclarativeNetRequestEngine.UpdateDynamicRules(extensionId, args);
                        return MakeResponse(requestId, (object?)null);

                    case "declarativeNetRequest.getDynamicRules":
                    case "declarativeNetRequest.getSessionRules":
                        return MakeResponse(requestId,
                            DeclarativeNetRequestEngine.GetDynamicRules(extensionId));

                    case "declarativeNetRequest.updateEnabledRulesets":
                        DeclarativeNetRequestEngine.UpdateEnabledRulesets(extensionId, args);
                        return MakeResponse(requestId, (object?)null);

                    case "declarativeNetRequest.getEnabledRulesets":
                        return MakeResponse(requestId,
                            DeclarativeNetRequestEngine.GetEnabledRulesets(extensionId));

                    case "declarativeNetRequest.getMatchedRuleCount":
                        return MakeResponse(requestId,
                            DeclarativeNetRequestEngine.BlockedCount(extensionId));
                }
                return MakeResponse(requestId, (object?)null);
            }
            if (method == "runtime.fetch")
            {
                if (!extensionPage)
                    return MakeError(requestId,
                        "Privileged extension fetch is available only to extension pages.");
                return ExtensionNativeFetch.Start(browser, requestId, extensionId, args);
            }

            // Unrecognised method — return a generic "not implemented" that
            // doesn't break the extension.
            ExtensionDiagnostics.Write("api-unsupported", extensionId, method);
            return MakeResponse(requestId, (object?)null);
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("api-error", extensionId,
                $"{method}: {ex.GetType().Name}: {ex.Message}");
            return MakeError(requestId, ex.Message);
        }
    }

    // ── Cookies ──────────────────────────────────────────────────────────

    /// <summary>
    /// chrome.cookies, over CEF's cookie manager. Reads are asynchronous in CEF
    /// — a visitor is called back on the IO thread — so the request is deferred
    /// and completed when the visit ends, the same way runtime.sendMessage is.
    /// </summary>
    private static Dictionary<string, object?> HandleCookies(
        CefBrowser browser, string requestId, string method, JsonElement args)
    {
        JsonElement details = args.TryGetProperty("details", out JsonElement value) ? value : default;
        string? url = String(details, "url");
        string? name = String(details, "name");

        switch (method)
        {
            case "cookies.get":
            case "cookies.getAll":
            {
                bool single = method == "cookies.get";
                string? domainFilter = String(details, "domain");
                var manager = CefCookieManager.GetGlobal(null);
                if (manager is null) return MakeResponse(requestId, single ? null : Array.Empty<object>());

                var visitor = new CookieCollector(single, name, domainFilter, collected =>
                    ExtensionBackgroundManager.CompleteMessage(requestId, "",
                        single ? collected.FirstOrDefault() : collected));

                // Register before starting: the visit can finish on the IO
                // thread before this method returns.
                ExtensionBackgroundManager.ExpectResponse(requestId, browser);

                bool started = string.IsNullOrEmpty(url)
                    ? manager.VisitAllCookies(visitor)
                    : manager.VisitUrlCookies(url, includeHttpOnly: true, visitor);
                if (!started)
                {
                    ExtensionBackgroundManager.CompleteMessage(requestId, "",
                        single ? null : new List<object>());
                    return MakeDeferredResponse(requestId);
                }
                return MakeDeferredResponse(requestId);
            }

            case "cookies.set":
            {
                if (string.IsNullOrEmpty(url)) return MakeError(requestId, "cookies.set needs a url.");
                var manager = CefCookieManager.GetGlobal(null);
                if (manager is null) return MakeError(requestId, "No cookie manager.");

                var cookie = new CefCookie
                {
                    Name = name ?? "",
                    Value = String(details, "value") ?? "",
                    Domain = String(details, "domain") ?? "",
                    Path = String(details, "path") ?? "/",
                    Secure = Bool(details, "secure") ?? false,
                    HttpOnly = Bool(details, "httpOnly") ?? false,
                };
                if (details.ValueKind == JsonValueKind.Object &&
                    details.TryGetProperty("expirationDate", out JsonElement expiry) &&
                    expiry.ValueKind == JsonValueKind.Number)
                {
                    // CefBaseTime counts microseconds from 1601, the way
                    // Chromium's base::Time does — not a DateTime and not a
                    // Unix epoch, so the conversion is explicit.
                    DateTime expiresUtc = DateTimeOffset
                        .FromUnixTimeSeconds((long)expiry.GetDouble()).UtcDateTime;
                    cookie.Expires = new CefBaseTime(
                        (expiresUtc - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks / 10);
                }

                manager.SetCookie(url, cookie, null);
                return MakeResponse(requestId, new
                {
                    name = cookie.Name, value = cookie.Value, domain = cookie.Domain,
                    path = cookie.Path, secure = cookie.Secure, httpOnly = cookie.HttpOnly,
                });
            }

            case "cookies.remove":
            {
                if (string.IsNullOrEmpty(url)) return MakeError(requestId, "cookies.remove needs a url.");
                CefCookieManager.GetGlobal(null)?.DeleteCookies(url, name ?? "", null);
                return MakeResponse(requestId, new { url, name = name ?? "" });
            }
        }
        return MakeResponse(requestId, (object?)null);
    }

    /// <summary>Gathers cookies from a CEF visit and hands them over when it ends.</summary>
    private sealed class CookieCollector : CefCookieVisitor
    {
        private readonly List<object> _collected = [];
        private readonly bool _stopAtFirst;
        private readonly string? _name;
        private readonly string? _domain;
        private readonly Action<List<object>> _done;
        private bool _finished;

        public CookieCollector(bool stopAtFirst, string? name, string? domain, Action<List<object>> done)
        {
            _stopAtFirst = stopAtFirst;
            _name = name;
            _domain = domain;
            _done = done;
        }

        protected override bool Visit(CefCookie cookie, int count, int total, out bool delete)
        {
            delete = false;
            try
            {
                bool nameMatches = string.IsNullOrEmpty(_name) ||
                    string.Equals(cookie.Name, _name, StringComparison.Ordinal);
                bool domainMatches = string.IsNullOrEmpty(_domain) ||
                    cookie.Domain.TrimStart('.').EndsWith(_domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase);

                if (nameMatches && domainMatches)
                {
                    _collected.Add(new
                    {
                        name = cookie.Name,
                        value = cookie.Value,
                        domain = cookie.Domain,
                        path = cookie.Path,
                        secure = cookie.Secure,
                        httpOnly = cookie.HttpOnly,
                        session = cookie.Expires is null,
                        storeId = "0",
                    });
                    if (_stopAtFirst) { Finish(); return false; }
                }

                if (count + 1 >= total) Finish();
                return true;
            }
            catch (Exception)
            {
                Finish();
                return false;
            }
        }

        private void Finish()
        {
            if (_finished) return;
            _finished = true;
            _done(_collected);
        }
    }

    // ── Downloads ────────────────────────────────────────────────────────

    /// <summary>
    /// chrome.downloads, over the same download machinery the browser's own UI
    /// uses, so an extension's download appears in the downloads panel rather
    /// than somewhere of its own.
    /// </summary>
    private static Dictionary<string, object?> HandleDownloads(
        CefBrowser browser, string requestId, string method, JsonElement args)
    {
        switch (method)
        {
            case "downloads.download":
            {
                JsonElement options = args.TryGetProperty("options", out JsonElement value) ? value : default;
                string? url = String(options, "url");
                if (string.IsNullOrWhiteSpace(url))
                    return MakeError(requestId, "downloads.download needs a url.");
                browser.GetHost().StartDownload(url);
                return MakeResponse(requestId, 0);
            }

            case "downloads.search":
            {
                var items = new List<object>();
                try
                {
                    foreach (DownloadItem item in DownloadStore.Shared.Items.ToArray())
                        items.Add(new
                        {
                            id = (int)item.Id,
                            url = item.Url,
                            filename = item.Path,
                            bytesReceived = item.Received,
                            totalBytes = item.Total,
                            state = item.IsCanceled ? "interrupted"
                                : item.IsComplete ? "complete" : "in_progress",
                            paused = false,
                            exists = item.IsComplete,
                        });
                }
                catch (Exception) { }
                return MakeResponse(requestId, items);
            }

            case "downloads.cancel":
            {
                if (args.TryGetProperty("id", out JsonElement idElement) &&
                    idElement.TryGetInt32(out int id))
                {
                    App.DispatcherQueue.TryEnqueue(() =>
                    {
                        DownloadItem? item = DownloadStore.Shared.Items
                            .FirstOrDefault(entry => entry.Id == (uint)id);
                        if (item is not null) DownloadStore.Shared.Cancel(item);
                    });
                }
                return MakeResponse(requestId, (object?)null);
            }

            case "downloads.show":
                App.DispatcherQueue.TryEnqueue(() => DownloadStore.Shared.ShowDefaultFolder());
                return MakeResponse(requestId, (object?)null);
        }
        return MakeResponse(requestId, (object?)null);
    }

    // ── Tabs ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The id an extension knows a tab by. Derived from the tab's Guid so it is
    /// stable for the life of the tab and survives reordering, which an index
    /// would not.
    /// </summary>
    internal static int TabIdOf(BrowserTab tab) => tab.Id.GetHashCode();

    private static BrowserTab? FindTab(int? id)
    {
        if (id is null) return null;
        try
        {
            foreach (BrowserTab tab in BrowserStore.Shared.Tabs.ToArray())
                if (TabIdOf(tab) == id.Value) return tab;
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>One tab in the shape chrome.tabs hands to extensions.</summary>
    private static object TabSnapshot(BrowserTab tab, int index)
    {
        bool active = ReferenceEquals(BrowserStore.Shared.SelectedTab, tab);
        return new
        {
            id = TabIdOf(tab),
            index,
            windowId = 1,
            url = tab.UrlString ?? "",
            pendingUrl = tab.UrlString ?? "",
            title = tab.Title ?? "",
            favIconUrl = tab.FaviconUrl ?? "",
            active,
            highlighted = active,
            selected = active,
            pinned = BrowserStore.Shared.IsPinned(tab.Id),
            status = tab.IsLoading ? "loading" : "complete",
            incognito = false,
            audible = false,
            discarded = false,
            autoDiscardable = true,
            groupId = -1,
        };
    }

    /// <summary>
    /// Every open tab, filtered by whatever the extension asked for. Returning
    /// only the active tab — which is what this did — made tab managers,
    /// session savers and "close duplicates" tools see a browser with one tab.
    /// </summary>
    private static List<object> QueryTabs(JsonElement queryInfo)
    {
        var result = new List<object>();
        try
        {
            BrowserTab[] tabs = BrowserStore.Shared.Tabs.ToArray();
            BrowserTab? selected = BrowserStore.Shared.SelectedTab;

            bool? wantActive = Bool(queryInfo, "active");
            bool? wantPinned = Bool(queryInfo, "pinned");
            string? urlPattern = String(queryInfo, "url");
            string? titlePattern = String(queryInfo, "title");

            for (int index = 0; index < tabs.Length; index++)
            {
                BrowserTab tab = tabs[index];
                bool isActive = ReferenceEquals(selected, tab);

                if (wantActive is not null && wantActive != isActive) continue;
                if (wantPinned is not null && wantPinned != BrowserStore.Shared.IsPinned(tab.Id)) continue;
                if (urlPattern is not null && !MatchPattern(urlPattern, tab.UrlString ?? "")) continue;
                if (titlePattern is not null && !MatchPattern(titlePattern, tab.Title ?? "")) continue;

                result.Add(TabSnapshot(tab, index));
            }
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("api-error", "", $"tabs.query: {ex.Message}");
        }
        return result;
    }

    private static bool? Bool(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) &&
           value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.ValueKind == JsonValueKind.True : null;

    private static string[] StringArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => item.Length > 0)
            .ToArray();
    }

    private static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) &&
           value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // ── Context Menus ────────────────────────────────────────────────────

    private static Dictionary<string, object?> HandleContextMenus(string requestId, string extensionId, string method, JsonElement args)
    {
        var items = _contextMenuItems.GetOrAdd(extensionId, LoadContextMenus);

        if (method == "contextMenus.create")
        {
            var props = args.TryGetProperty("createProperties", out var cp)
                ? JsonToDict(cp)
                : new Dictionary<string, object?>();

            // Ensure an id exists
            if (!props.ContainsKey("id") || props["id"] == null)
                props["id"] = Guid.NewGuid().ToString("N");

            props["extensionId"] = extensionId;
            var itemId = props["id"]?.ToString() ?? "";

            lock (items)
            {
                // Remove any existing item with the same id
                items.RemoveAll(i => i.TryGetValue("id", out var v) && v?.ToString() == itemId);
                items.Add(props);
                SaveContextMenus(extensionId, items);
            }
            return MakeResponse(requestId, itemId);
        }

        if (method == "contextMenus.update")
        {
            var itemId = args.TryGetProperty("id", out var idEl) ? idEl.ToString() : "";
            var updateProps = args.TryGetProperty("updateProperties", out var up)
                ? JsonToDict(up)
                : new Dictionary<string, object?>();

            if (string.IsNullOrEmpty(itemId))
                return MakeError(requestId, "Missing context menu id.");

            lock (items)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].TryGetValue("id", out var v) && v?.ToString() == itemId)
                    {
                        foreach (var kv in updateProps)
                            items[i][kv.Key] = kv.Value;
                        SaveContextMenus(extensionId, items);
                        return MakeResponse(requestId, (object?)null);
                    }
                }
            }
            return MakeError(requestId, "No context menu item with that id.");
        }

        if (method == "contextMenus.remove")
        {
            var itemId = args.TryGetProperty("id", out var idEl) ? idEl.ToString() : "";
            lock (items)
            {
                items.RemoveAll(i => i.TryGetValue("id", out var v) && v?.ToString() == itemId);
                SaveContextMenus(extensionId, items);
            }
            return MakeResponse(requestId, (object?)null);
        }

        if (method == "contextMenus.removeAll")
        {
            lock (items)
            {
                items.Clear();
                SaveContextMenus(extensionId, items);
            }
            return MakeResponse(requestId, (object?)null);
        }

        return MakeError(requestId, $"Unsupported contextMenus method: {method}");
    }

    // ── Storage Local ────────────────────────────────────────────────────

    private static Dictionary<string, object?> HandleStorageLocal(string requestId, string extensionId, string method, JsonElement args)
    {
        var store = _storage.GetOrAdd(extensionId, LoadStorage);

        if (method == "storage.local.get")
        {
            var result = new Dictionary<string, object?>();
            lock (store)
            {
                if (args.TryGetProperty("keys", out var keysEl))
                {
                    if (keysEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var k in keysEl.EnumerateArray())
                        {
                            var key = k.GetString() ?? "";
                            if (store.TryGetValue(key, out var val))
                                result[key] = val;
                        }
                    }
                    else if (keysEl.ValueKind == JsonValueKind.String)
                    {
                        var key = keysEl.GetString() ?? "";
                        if (store.TryGetValue(key, out var val))
                            result[key] = val;
                    }
                    else if (keysEl.ValueKind == JsonValueKind.Null || keysEl.ValueKind == JsonValueKind.Undefined)
                    {
                        foreach (var kv in store) result[kv.Key] = kv.Value;
                    }
                    else if (keysEl.ValueKind == JsonValueKind.Object)
                    {
                        // Object form: keys with defaults
                        foreach (var prop in keysEl.EnumerateObject())
                        {
                            result[prop.Name] = store.TryGetValue(prop.Name, out var val) ? val : (object)prop.Value;
                        }
                    }
                }
                else
                {
                    foreach (var kv in store) result[kv.Key] = kv.Value;
                }
            }
            return MakeResponse(requestId, result);
        }

        if (method == "storage.local.set")
        {
            if (args.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Object)
            {
                var changes = new Dictionary<string, object?>();
                lock (store)
                {
                    var updated = store.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
                    foreach (var prop in itemsEl.EnumerateObject())
                    {
                        bool hadOldValue = store.TryGetValue(prop.Name, out JsonElement oldValue);
                        if (!hadOldValue || oldValue.GetRawText() != prop.Value.GetRawText())
                            changes[prop.Name] = new
                            {
                                oldValue = hadOldValue ? oldValue.Clone() : (JsonElement?)null,
                                newValue = prop.Value.Clone()
                            };
                        updated[prop.Name] = prop.Value.Clone();
                    }
                    if (JsonSerializer.SerializeToUtf8Bytes(updated).Length > StorageQuotaBytes)
                        return MakeError(requestId, "chrome.storage.local quota exceeded.");
                    store.Clear();
                    foreach (var pair in updated) store[pair.Key] = pair.Value;
                    SaveStorage(extensionId, store);
                }
                if (changes.Count > 0)
                    ExtensionBackgroundManager.DispatchStorageChanged(extensionId, changes, "local");
            }
            return MakeResponse(requestId, (object?)null);
        }

        if (method == "storage.local.remove")
        {
            if (args.TryGetProperty("keys", out var keysEl))
            {
                var changes = new Dictionary<string, object?>();
                lock (store)
                {
                    IEnumerable<string> keys = keysEl.ValueKind switch
                    {
                        JsonValueKind.Array => keysEl.EnumerateArray()
                            .Select(item => item.GetString() ?? "").ToArray(),
                        JsonValueKind.String => new[] { keysEl.GetString() ?? "" },
                        _ => Array.Empty<string>()
                    };
                    foreach (string key in keys.Where(key => !string.IsNullOrEmpty(key)))
                    {
                        if (store.Remove(key, out JsonElement oldValue))
                            changes[key] = new { oldValue = oldValue.Clone() };
                    }
                    SaveStorage(extensionId, store);
                }
                if (changes.Count > 0)
                    ExtensionBackgroundManager.DispatchStorageChanged(extensionId, changes, "local");
            }
            return MakeResponse(requestId, (object?)null);
        }

        if (method == "storage.local.clear")
        {
            var changes = new Dictionary<string, object?>();
            lock (store)
            {
                foreach (var pair in store)
                    changes[pair.Key] = new { oldValue = pair.Value.Clone() };
                store.Clear();
                SaveStorage(extensionId, store);
            }
            if (changes.Count > 0)
                ExtensionBackgroundManager.DispatchStorageChanged(extensionId, changes, "local");
            return MakeResponse(requestId, (object?)null);
        }

        if (method == "storage.local.getBytesInUse")
        {
            Dictionary<string, JsonElement> selected;
            lock (store)
            {
                if (!args.TryGetProperty("keys", out JsonElement keysElement) ||
                    keysElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    selected = store.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
                else
                {
                    IEnumerable<string> keys = keysElement.ValueKind switch
                    {
                        JsonValueKind.String => new[] { keysElement.GetString() ?? string.Empty },
                        JsonValueKind.Array => keysElement.EnumerateArray()
                            .Select(item => item.GetString() ?? string.Empty).ToArray(),
                        _ => Array.Empty<string>()
                    };
                    selected = keys.Where(store.ContainsKey)
                        .ToDictionary(key => key, key => store[key].Clone());
                }
            }
            return MakeResponse(requestId, JsonSerializer.SerializeToUtf8Bytes(selected).Length);
        }

        return MakeError(requestId, $"Unsupported storage method: {method}");
    }

    // ── Context menu item queries (used by context menu handler) ────────

    /// <summary>One row an extension contributed, with whatever hangs under it.</summary>
    public sealed class ExtensionMenuItem
    {
        public required string ExtensionId { get; init; }
        public required string ItemId { get; init; }
        public required string Title { get; init; }
        public bool Enabled { get; init; } = true;

        /// <summary>"normal", "separator", "checkbox" or "radio".</summary>
        public string Type { get; init; } = "normal";
        public bool Checked { get; init; }
        public List<ExtensionMenuItem> Children { get; } = [];
    }

    /// <summary>
    /// The menu rows every enabled extension wants for this particular click.
    ///
    /// <para>
    /// Chromium decides this from the node under the cursor: a selection, a
    /// link, an image, an editable field, or the bare page. An item declaring
    /// <c>contexts:["selection"]</c> — the "search for %s" shape almost every
    /// dictionary, translator and search extension ships — only appears when
    /// text is selected, which is why passing the selection in matters.
    /// </para>
    ///
    /// <para>
    /// Children come back nested under their parent rather than dropped, so a
    /// submenu survives the trip.
    /// </para>
    /// </summary>
    public static List<ExtensionMenuItem> GetMenuItemsForContext(
        string pageUrl, string? linkUrl, string? mediaType,
        string? selectionText = null, bool isEditable = false)
    {
        var result = new List<ExtensionMenuItem>();

        var extensions = Skew.Models.ExtensionStore.Shared.GetSnapshot();

        foreach (BrowserExtension installed in extensions.Where(extension =>
            extension.Enabled && HasPermission(extension, "contextMenus")))
        {
            string extensionId = installed.Id;
            List<Dictionary<string, object?>> registered =
                _contextMenuItems.GetOrAdd(extensionId, LoadContextMenus);

            List<Dictionary<string, object?>> itemsCopy;
            lock (registered)
            {
                itemsCopy = new List<Dictionary<string, object?>>(registered);
            }

            // Build by id first so children can be attached to their parents.
            var byId = new Dictionary<string, ExtensionMenuItem>(StringComparer.Ordinal);
            var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var item in itemsCopy)
            {
                if (!ItemAppliesHere(item, pageUrl, linkUrl, mediaType, selectionText, isEditable))
                    continue;

                string itemId = Value(item, "id") ?? "";
                string title = Value(item, "title") ?? "";
                string type = (Value(item, "type") ?? "normal").ToLowerInvariant();

                // A separator carries no title; everything else without one is
                // a row that would render blank.
                if (type != "separator" && string.IsNullOrEmpty(title)) continue;
                if (string.IsNullOrEmpty(itemId)) continue;

                byId[itemId] = new ExtensionMenuItem
                {
                    ExtensionId = extensionId,
                    ItemId = itemId,
                    Title = title,
                    Enabled = !(item.TryGetValue("enabled", out var enabled) && enabled is false),
                    Type = type,
                    Checked = item.TryGetValue("checked", out var isChecked) && isChecked is true,
                };

                string? parentId = Value(item, "parentId");
                if (!string.IsNullOrEmpty(parentId)) parentOf[itemId] = parentId;
            }

            foreach (var pair in byId)
            {
                if (parentOf.TryGetValue(pair.Key, out string? parentId) &&
                    byId.TryGetValue(parentId, out ExtensionMenuItem? parent))
                    parent.Children.Add(pair.Value);
                else if (!parentOf.ContainsKey(pair.Key))
                    result.Add(pair.Value);
                // A child whose parent did not match this context is dropped
                // with it, which is what Chromium does.
            }
        }

        return result;
    }

    /// <summary>
    /// Does this registration belong in the menu for the node that was clicked?
    /// </summary>
    private static bool ItemAppliesHere(
        Dictionary<string, object?> item, string pageUrl, string? linkUrl,
        string? mediaType, string? selectionText, bool isEditable)
    {
        if (item.TryGetValue("visible", out var visible) && visible is false) return false;

        var contexts = GetContexts(item);
        // "page" is the default when the extension names none.
        if (contexts.Count == 0) contexts.Add("page");

        bool hasSelection = !string.IsNullOrWhiteSpace(selectionText);
        bool hasLink = !string.IsNullOrEmpty(linkUrl);

        bool matches = contexts.Contains("all");
        if (!matches && hasSelection && contexts.Contains("selection")) matches = true;
        if (!matches && hasLink && contexts.Contains("link")) matches = true;
        if (!matches && isEditable && contexts.Contains("editable")) matches = true;
        if (!matches && mediaType == "image" && contexts.Contains("image")) matches = true;
        if (!matches && mediaType == "video" && contexts.Contains("video")) matches = true;
        if (!matches && mediaType == "audio" && contexts.Contains("audio")) matches = true;
        // The bare page: nothing else claimed this click.
        if (!matches && contexts.Contains("page") &&
            !hasSelection && !hasLink && !isEditable && string.IsNullOrEmpty(mediaType))
            matches = true;
        if (!matches && contexts.Contains("frame") && string.IsNullOrEmpty(mediaType)) matches = false;

        if (!matches) return false;

        if (!UrlPatternsAllow(item, "documentUrlPatterns", pageUrl)) return false;
        if (hasLink && !UrlPatternsAllow(item, "targetUrlPatterns", linkUrl!)) return false;
        if (mediaType is not null && !string.IsNullOrEmpty(mediaType) &&
            !UrlPatternsAllow(item, "targetUrlPatterns", pageUrl)) return false;

        return true;
    }

    /// <summary>
    /// Match-pattern filtering, as declared by documentUrlPatterns and
    /// targetUrlPatterns. No patterns means no restriction.
    /// </summary>
    private static bool UrlPatternsAllow(Dictionary<string, object?> item, string key, string url)
    {
        if (!item.TryGetValue(key, out var value) || value is not JsonElement element ||
            element.ValueKind != JsonValueKind.Array)
            return true;

        bool any = false;
        foreach (JsonElement entry in element.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String) continue;
            string? pattern = entry.GetString();
            if (string.IsNullOrEmpty(pattern)) continue;
            any = true;
            if (MatchPattern(pattern, url)) return true;
        }
        return !any;
    }

    /// <summary>A Chromium match pattern ("*://*.example.com/*") against a URL.</summary>
    private static bool MatchPattern(string pattern, string url)
    {
        if (pattern == "<all_urls>") return true;
        try
        {
            string regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(
                url, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? Value(Dictionary<string, object?> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static List<string> GetContexts(Dictionary<string, object?> item)
    {
        var result = new List<string>();
        if (!item.TryGetValue("contexts", out var ctx) || ctx == null) return result;

        if (ctx is JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in el.EnumerateArray())
                    result.Add(c.GetString()?.ToLowerInvariant() ?? "");
            }
            else if (el.ValueKind == JsonValueKind.String)
            {
                result.Add(el.GetString()?.ToLowerInvariant() ?? "");
            }
        }

        return result;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static Dictionary<string, object?> MakeResponse(string requestId, object? result)
    {
        return new Dictionary<string, object?>
        {
            ["requestId"] = requestId,
            ["result"] = result
        };
    }

    private static Dictionary<string, object?> MakeError(string requestId, string error)
    {
        return new Dictionary<string, object?>
        {
            ["requestId"] = requestId,
            ["error"] = error
        };
    }

    private static Dictionary<string, object?> MakeDeferredResponse(string requestId)
    {
        return new Dictionary<string, object?>
        {
            ["requestId"] = requestId,
            ["deferred"] = true
        };
    }

    private static Dictionary<string, object?> JsonToDict(JsonElement el)
    {
        var dict = new Dictionary<string, object?>();
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => prop.Value.Clone()
                };
            }
        }
        return dict;
    }

    private static bool HasPermission(BrowserExtension extension, string permission)
        => extension.Manifest?.Permissions.Any(item =>
            string.Equals(item, permission, StringComparison.OrdinalIgnoreCase)) == true;

    private static Dictionary<string, JsonElement> LoadStorage(string extensionId)
    {
        try
        {
            string path = StoragePath(extensionId);
            if (!File.Exists(path)) return new Dictionary<string, JsonElement>();
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))
                ?? new Dictionary<string, JsonElement>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load extension storage: {ex.Message}");
            return new Dictionary<string, JsonElement>();
        }
    }

    private static List<Dictionary<string, object?>> LoadContextMenus(string extensionId)
    {
        try
        {
            string path = ContextMenusPath(extensionId);
            if (!File.Exists(path)) return new List<Dictionary<string, object?>>();
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return new List<Dictionary<string, object?>>();
            return document.RootElement.EnumerateArray().Select(JsonToDict).ToList();
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("context-menu-error", extensionId,
                $"Failed to load registered menu items: {ex.Message}");
            return new List<Dictionary<string, object?>>();
        }
    }

    private static void SaveContextMenus(
        string extensionId, List<Dictionary<string, object?>> items)
    {
        string path = ContextMenusPath(extensionId);
        string folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(items));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void SaveStorage(string extensionId, Dictionary<string, JsonElement> store)
    {
        string path = StoragePath(extensionId);
        string folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(store));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string StoragePath(string extensionId)
    {
        if (extensionId.Length != 32 || extensionId.Any(character =>
            !(character is >= 'a' and <= 'z') && !(character is >= '0' and <= '9')))
            throw new InvalidDataException("Invalid extension ID.");
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Skew", "ExtensionData", extensionId, "storage.local.json");
    }

    private static string ContextMenusPath(string extensionId)
    {
        string storage = StoragePath(extensionId);
        return Path.Combine(Path.GetDirectoryName(storage)!, "contextMenus.json");
    }
}
