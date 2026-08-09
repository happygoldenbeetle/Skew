using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;

namespace Mori.Models;

/// <summary>
/// User preferences, as written to disk. Mirrors the fields the Mac keeps in
/// UserDefaults (BrowserSettings.swift), plus <see cref="RestoreTabsOnLaunch"/>,
/// which has no Mac counterpart — the Mac always reopens the previous session.
/// </summary>
public sealed class PersistedSettings
{
    public string HomepageUrl { get; set; } = "https://";
    public NewTabBehavior NewTabBehavior { get; set; } = NewTabBehavior.Homepage;
    public SearchEngine SearchEngine { get; set; } = SearchEngine.Google;
    public string CustomSearchTemplate { get; set; } = "https://example.com/?q={query}";
    public ElementTheme Theme { get; set; } = ElementTheme.Default;
    public SidebarPosition SidebarPosition { get; set; } = SidebarPosition.Left;
    public bool ShowSidebarOnLaunch { get; set; } = true;
    public bool BlockAds { get; set; } = true;
    public bool AutoPiP { get; set; }

    /// <summary>
    /// Reopen the previous session's ordinary tabs and re-select the tab that
    /// was active. Off by default: pinned tabs and folders always come back,
    /// but a launch otherwise starts on a fresh new tab rather than dropping
    /// the user back wherever they happened to leave off.
    /// </summary>
    public bool RestoreTabsOnLaunch { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(PersistedSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext { }

/// <summary>
/// Reads and writes <c>settings.json</c>. The Windows counterpart to the Mac's
/// UserDefaults-backed BrowserSettings — until this existed the port's settings
/// were in-memory only and every preference reset on restart.
/// </summary>
public static class SettingsStore
{
    private static string SettingsFilePath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MoriBrowser");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    /// <summary>Load saved preferences, or null when there is nothing usable.</summary>
    public static PersistedSettings? Load()
    {
        try
        {
            string path = SettingsFilePath;
            if (!File.Exists(path))
                return null;

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.PersistedSettings);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Write preferences via a temp file so a crash can't truncate them.</summary>
    public static void Save(PersistedSettings settings)
    {
        try
        {
            string path = SettingsFilePath;
            string temp = path + ".tmp";

            using (var stream = File.Create(temp))
                JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.PersistedSettings);

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // A failed preference write shouldn't take the app down.
        }
    }
}
