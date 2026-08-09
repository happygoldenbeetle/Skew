using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mori.Models;

/// <summary>One tab, as written to disk.</summary>
public sealed class PersistedTab
{
    public Guid Id { get; set; }
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>
    /// The icon the tab was last showing. Saved because a restored tab is not
    /// loaded until it is selected, so it would otherwise have no way to know
    /// its own favicon and would fall back to a letter tile.
    /// </summary>
    public string? FaviconUrl { get; set; }
}

/// <summary>
/// One folder. Membership is stored as tab ids rather than nested tabs so a tab
/// appears exactly once in the file and can't be resurrected twice.
/// </summary>
public sealed class PersistedFolder
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Folder";
    public string Symbol { get; set; } = "";
    public bool IsExpanded { get; set; }
    public List<Guid> TabIds { get; set; } = [];
}

/// <summary>
/// The saved session. Mirrors PersistedSession in BrowserStore.swift: every tab
/// once in <see cref="Tabs"/>, with the pinned grid, folders and loose list
/// referring to them by id.
/// </summary>
public sealed class PersistedSession
{
    public List<PersistedTab> Tabs { get; set; } = [];
    public Guid? SelectedTabId { get; set; }
    public List<Guid> PinnedTabIds { get; set; } = [];
    public List<Guid> LooseTabIds { get; set; } = [];
    public List<PersistedFolder> Folders { get; set; } = [];
}

/// <summary>
/// Source-generated serialization. Reflection-based JSON would be stripped by
/// the trimmer, which the project enables for Release.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PersistedSession))]
internal partial class SessionJsonContext : JsonSerializerContext { }

/// <summary>
/// Reads and writes <c>session.json</c>, so pinned tabs, folders and open tabs
/// survive a restart. Port of the session-restore half of BrowserStore.swift.
///
/// <para>
/// Lives next to the Chromium profile in <c>%LOCALAPPDATA%\MoriBrowser</c>, the
/// same folder <see cref="Cef.CefRuntimeHost"/> uses for its cache, so a user
/// clearing that directory clears the session with it.
/// </para>
/// </summary>
public static class SessionStore
{
    private static string SessionFilePath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MoriBrowser");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "session.json");
        }
    }

    /// <summary>
    /// Load the saved session, or null when there is nothing usable — no file,
    /// unreadable, corrupt, or empty. Every failure is a null so a bad file
    /// degrades to a fresh session instead of blocking startup.
    /// </summary>
    public static PersistedSession? Load()
    {
        try
        {
            string path = SessionFilePath;
            if (!File.Exists(path))
                return null;

            using var stream = File.OpenRead(path);
            var session = JsonSerializer.Deserialize(stream, SessionJsonContext.Default.PersistedSession);
            return session is { Tabs.Count: > 0 } ? session : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Write the session. Serializes to a temp file and moves it into place, so
    /// a crash mid-write can't leave a truncated file that would lose the whole
    /// session on next launch.
    /// </summary>
    public static void Save(PersistedSession session)
    {
        try
        {
            string path = SessionFilePath;
            string temp = path + ".tmp";

            using (var stream = File.Create(temp))
                JsonSerializer.Serialize(stream, session, SessionJsonContext.Default.PersistedSession);

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Losing a session save is not worth taking the app down for.
        }
    }
}
