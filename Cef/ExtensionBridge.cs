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
                BrowserTab? selected = BrowserStore.Shared.SelectedTab;
                return MakeResponse(requestId, new object[]
                {
                    new { id = ExtensionBackgroundManager.SelectedTabId,
                        url = selected?.UrlString ?? "", active = true, index = 0 }
                });
            }
            if (method == "tabs.create")
            {
                string? url = null;
                if (args.TryGetProperty("createProperties", out JsonElement properties) &&
                    properties.TryGetProperty("url", out JsonElement urlElement))
                    url = urlElement.GetString();
                if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Relative, out _))
                    url = $"{SkewSchemes.ExtensionScheme}://{extensionId}/" + url.TrimStart('/');
                if (string.IsNullOrWhiteSpace(url))
                    return MakeError(requestId, "The extension did not provide a valid tab URL.");
                App.DispatcherQueue.TryEnqueue(() => BrowserStore.Shared.NewTab(url));
                return MakeResponse(requestId, new { id = -1, url, active = true });
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
            if (method == "runtime.sendMessage")
            {
                object? message = args.TryGetProperty("message", out JsonElement messageElement)
                    ? messageElement.Clone() : null;
                if (!ExtensionBackgroundManager.DispatchRuntimeMessage(
                        extensionId, message, sourceUrl, requestId, browser))
                    return MakeResponse(requestId, (object?)null);
                return MakeDeferredResponse(requestId);
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

    // ── Context Menus ────────────────────────────────────────────────────

    private static Dictionary<string, object?> HandleContextMenus(string requestId, string extensionId, string method, JsonElement args)
    {
        var items = _contextMenuItems.GetOrAdd(extensionId, _ => new List<Dictionary<string, object?>>());

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
            }
            return MakeResponse(requestId, (object?)null);
        }

        if (method == "contextMenus.removeAll")
        {
            lock (items)
            {
                items.Clear();
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

        return MakeError(requestId, $"Unsupported storage method: {method}");
    }

    // ── Context menu item queries (used by context menu handler) ────────

    /// <summary>
    /// Get all registered context menu items for all enabled extensions,
    /// filtered by the given context type (e.g. "page", "image", "link").
    /// </summary>
    public static List<(string extensionId, string itemId, string title)> GetMenuItemsForContext(
        string pageUrl, string? linkUrl, string? mediaType)
    {
        var result = new List<(string, string, string)>();

        var extensions = Skew.Models.ExtensionStore.Shared.GetSnapshot();

        foreach (var kvp in _contextMenuItems)
        {
            var extensionId = kvp.Key;
            var ext = extensions.FirstOrDefault(e => e.Id == extensionId && e.Enabled);
            if (ext == null) continue;

            List<Dictionary<string, object?>> itemsCopy;
            lock (kvp.Value)
            {
                itemsCopy = new List<Dictionary<string, object?>>(kvp.Value);
            }

            foreach (var item in itemsCopy)
            {
                // Skip child items (parentId) for now — we flatten to top-level
                if (item.TryGetValue("parentId", out var pid) && pid != null)
                    continue;

                var title = item.TryGetValue("title", out var t) ? t?.ToString() ?? "" : "";
                var itemId = item.TryGetValue("id", out var id) ? id?.ToString() ?? "" : "";
                var contexts = GetContexts(item);

                // Check if this item applies to the current context
                bool matches = false;
                if (contexts.Contains("all")) matches = true;
                else if (!string.IsNullOrEmpty(mediaType) && mediaType == "image" && contexts.Contains("image")) matches = true;
                else if (!string.IsNullOrEmpty(linkUrl) && contexts.Contains("link")) matches = true;
                else if (string.IsNullOrEmpty(mediaType) && string.IsNullOrEmpty(linkUrl) && contexts.Contains("page")) matches = true;
                // Default context is "page" if none specified
                else if (contexts.Count == 0 && string.IsNullOrEmpty(mediaType) && string.IsNullOrEmpty(linkUrl)) matches = true;

                if (matches && !string.IsNullOrEmpty(title))
                {
                    result.Add((extensionId, itemId, title));
                }
            }
        }

        return result;
    }

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
}
