using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Mori.Models;

namespace Mori.Cef;

/// <summary>
/// C# side of the extension IPC bridge. Routes incoming __MORI_EXTENSION__
/// requests to the appropriate handler and returns a JSON response dictionary.
/// </summary>
internal static class ExtensionBridge
{
    // Per-extension registered context menu items.
    // Key = extensionId, Value = list of menu item property dictionaries.
    private static readonly ConcurrentDictionary<string, List<Dictionary<string, object?>>> _contextMenuItems = new();

    // Per-extension simple key-value storage (chrome.storage.local).
    private static readonly ConcurrentDictionary<string, Dictionary<string, JsonElement>> _storage = new();

    /// <summary>
    /// Handle an incoming extension bridge request and return a JSON-serialisable
    /// response dictionary. Returns null if the method is unrecognised.
    /// </summary>
    public static Dictionary<string, object?> HandleRequest(string requestId, string extensionId, string method, JsonElement args)
    {
        try
        {
            if (method.StartsWith("contextMenus."))
                return HandleContextMenus(requestId, extensionId, method, args);
            if (method.StartsWith("storage.local."))
                return HandleStorageLocal(requestId, extensionId, method, args);
            if (method == "tabs.query")
                return MakeResponse(requestId, new object[] { new { id = 1, url = "", active = true } });
            if (method == "tabs.create")
                return MakeResponse(requestId, new { id = 1 });
            if (method == "runtime.sendMessage")
                return MakeResponse(requestId, (object?)null);

            // Unrecognised method — return a generic "not implemented" that
            // doesn't break the extension.
            return MakeResponse(requestId, (object?)null);
        }
        catch (Exception ex)
        {
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
        var store = _storage.GetOrAdd(extensionId, _ => new Dictionary<string, JsonElement>());

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
                lock (store)
                {
                    foreach (var prop in itemsEl.EnumerateObject())
                    {
                        store[prop.Name] = prop.Value.Clone();
                    }
                }
            }
            return MakeResponse(requestId, (object?)null);
        }

        if (method == "storage.local.remove")
        {
            if (args.TryGetProperty("keys", out var keysEl))
            {
                lock (store)
                {
                    if (keysEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var k in keysEl.EnumerateArray())
                            store.Remove(k.GetString() ?? "");
                    }
                    else if (keysEl.ValueKind == JsonValueKind.String)
                    {
                        store.Remove(keysEl.GetString() ?? "");
                    }
                }
            }
            return MakeResponse(requestId, (object?)null);
        }

        if (method == "storage.local.clear")
        {
            lock (store) { store.Clear(); }
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

        var extensions = Mori.Models.ExtensionStore.Shared.GetSnapshot();

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
                    _ => prop.Value // Keep as JsonElement for complex types
                };
            }
        }
        return dict;
    }
}
