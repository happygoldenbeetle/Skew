using System.Text.Json;
using System.Text.Json.Serialization;
using Skew.Models;

namespace Skew.Cef;

/// <summary>
/// Content scripts an extension registers at runtime through
/// <c>chrome.scripting.registerContentScripts</c>.
///
/// <para>
/// The manifest is not the only place content scripts come from. An extension
/// whose behaviour depends on a setting registers them when the setting
/// changes — uBlock Origin Lite's filtering modes do exactly this, and with the
/// call missing the mode switch fails and the slider springs back to where it
/// was. Registrations are kept per extension and injected by the same path that
/// runs the manifest's own scripts.
/// </para>
///
/// <para>
/// Persisted, because <c>persistAcrossSessions</c> defaults to true and an
/// extension that registered its scripts once does not do it again on the next
/// launch.
/// </para>
/// </summary>
internal static class DynamicContentScripts
{
    internal sealed class Registration
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("matches")] public List<string> Matches { get; set; } = [];
        [JsonPropertyName("excludeMatches")] public List<string> ExcludeMatches { get; set; } = [];
        [JsonPropertyName("js")] public List<string> Js { get; set; } = [];
        [JsonPropertyName("css")] public List<string> Css { get; set; } = [];
        [JsonPropertyName("runAt")] public string RunAt { get; set; } = "document_idle";
        [JsonPropertyName("allFrames")] public bool AllFrames { get; set; }
        [JsonPropertyName("world")] public string World { get; set; } = "ISOLATED";
        [JsonPropertyName("persistAcrossSessions")] public bool Persist { get; set; } = true;

        /// <summary>The manifest shape, so one matcher serves both kinds.</summary>
        public ContentScriptMeta AsContentScript() => new()
        {
            Matches = Matches,
            ExcludeMatches = ExcludeMatches,
            Js = Js,
            Css = Css,
            RunAt = RunAt,
            AllFrames = AllFrames,
        };
    }

    private static readonly Dictionary<string, List<Registration>> s_registered =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object s_lock = new();

    /// <summary>Registrations for an extension, loading them from disk on first use.</summary>
    public static List<Registration> For(string extensionId)
    {
        lock (s_lock)
        {
            if (!s_registered.TryGetValue(extensionId, out List<Registration>? list))
            {
                list = Load(extensionId);
                s_registered[extensionId] = list;
            }
            return [.. list];
        }
    }

    /// <summary>
    /// Add registrations. Chrome rejects a duplicate id outright; replacing is
    /// friendlier and an extension re-registering after a setting change is the
    /// common case.
    /// </summary>
    public static void Register(string extensionId, JsonElement scripts)
    {
        if (scripts.ValueKind != JsonValueKind.Array) return;

        lock (s_lock)
        {
            List<Registration> list = s_registered.TryGetValue(extensionId, out List<Registration>? existing)
                ? existing : Load(extensionId);

            foreach (JsonElement entry in scripts.EnumerateArray())
            {
                Registration? registration = Parse(entry);
                if (registration is null) continue;
                list.RemoveAll(item => string.Equals(item.Id, registration.Id, StringComparison.Ordinal));
                list.Add(registration);
            }

            s_registered[extensionId] = list;
            Save(extensionId, list);
            ExtensionDiagnostics.Write("scripting", extensionId,
                $"Registered content scripts; {list.Count} live.");
        }
    }

    public static void Unregister(string extensionId, IReadOnlyCollection<string> ids)
    {
        lock (s_lock)
        {
            List<Registration> list = s_registered.TryGetValue(extensionId, out List<Registration>? existing)
                ? existing : Load(extensionId);

            // No ids means all of them, which is what the API specifies.
            if (ids.Count == 0) list.Clear();
            else list.RemoveAll(item => ids.Contains(item.Id, StringComparer.Ordinal));

            s_registered[extensionId] = list;
            Save(extensionId, list);
            ExtensionDiagnostics.Write("scripting", extensionId,
                $"Unregistered content scripts; {list.Count} live.");
        }
    }

    private static Registration? Parse(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        string id = entry.TryGetProperty("id", out JsonElement idElement) &&
            idElement.ValueKind == JsonValueKind.String ? idElement.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(id)) return null;

        return new Registration
        {
            Id = id,
            Matches = Strings(entry, "matches"),
            ExcludeMatches = Strings(entry, "excludeMatches"),
            Js = Strings(entry, "js"),
            Css = Strings(entry, "css"),
            RunAt = entry.TryGetProperty("runAt", out JsonElement runAt) &&
                runAt.ValueKind == JsonValueKind.String ? runAt.GetString() ?? "document_idle" : "document_idle",
            AllFrames = entry.TryGetProperty("allFrames", out JsonElement allFrames) &&
                allFrames.ValueKind == JsonValueKind.True,
            World = entry.TryGetProperty("world", out JsonElement world) &&
                world.ValueKind == JsonValueKind.String ? world.GetString() ?? "ISOLATED" : "ISOLATED",
            Persist = !entry.TryGetProperty("persistAcrossSessions", out JsonElement persist) ||
                persist.ValueKind != JsonValueKind.False,
        };
    }

    private static List<string> Strings(JsonElement entry, string name)
    {
        var result = new List<string>();
        if (entry.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                    result.Add(text);
        return result;
    }

    private static List<Registration> Load(string extensionId)
    {
        try
        {
            string path = RegistrationsPath(extensionId);
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<List<Registration>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void Save(string extensionId, List<Registration> list)
    {
        try
        {
            string path = RegistrationsPath(extensionId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Only the ones asking to outlive the session.
            File.WriteAllText(path, JsonSerializer.Serialize(list.Where(item => item.Persist)));
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("scripting-error", extensionId, ex.Message);
        }
    }

    private static string RegistrationsPath(string extensionId)
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Skew", "ExtensionData", extensionId, "contentScripts.json");
}
