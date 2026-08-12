using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Skew.Models
{
    public class BrowserExtension
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string? IconPath { get; set; }

        [JsonIgnore]
        public Uri? IconUri => string.IsNullOrEmpty(IconPath) ? null : new Uri(IconPath);

        [JsonIgnore]
        public Microsoft.UI.Xaml.Media.ImageSource? IconImage =>
            IconUri is null ? null : new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(IconUri);

        /// <summary>"v{version}" for the settings row, empty when unknown (mac ExtensionRow).</summary>
        [JsonIgnore]
        public string VersionLabel => string.IsNullOrEmpty(Version) ? string.Empty : $"v{Version}";

        public bool Enabled { get; set; } = true;
        public bool Pinned { get; set; } = false;

        /// <summary>Segoe Fluent pin glyph: filled (0xE840) when pinned, outline (0xE718) otherwise.</summary>
        [JsonIgnore]
        public string PinGlyph => ((char)(Pinned ? 0xE840 : 0xE718)).ToString();

        /// <summary>"Disabled" subtitle shows only when the extension is turned off (mac ExtensionMenuRow).</summary>
        [JsonIgnore]
        public Microsoft.UI.Xaml.Visibility DisabledLabelVisibility =>
            Enabled ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

        // Internal property, not saved to settings, populated on load
        [JsonIgnore]
        public ManifestMeta? Manifest { get; set; }
    }

    public class ManifestMeta
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("short_name")]
        public string ShortName { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("default_locale")]
        public string? DefaultLocale { get; set; }

        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("icons")]
        public Dictionary<string, string>? Icons { get; set; }

        [JsonPropertyName("manifest_version")]
        public int ManifestVersion { get; set; } = 3;

        [JsonPropertyName("content_scripts")]
        public List<ContentScriptMeta> ContentScripts { get; set; } = new();

        [JsonPropertyName("background")]
        public BackgroundMeta? Background { get; set; }

        [JsonPropertyName("permissions")]
        public List<string> Permissions { get; set; } = new();

        [JsonPropertyName("host_permissions")]
        public List<string> HostPermissions { get; set; } = new();

        [JsonPropertyName("web_accessible_resources")]
        public System.Text.Json.JsonElement? WebAccessibleResources { get; set; }

        [JsonPropertyName("action")]
        public ActionMeta? Action { get; set; }

        [JsonPropertyName("browser_action")]
        public ActionMeta? BrowserAction { get; set; }

        [JsonIgnore]
        public ActionMeta? EffectiveAction => Action ?? BrowserAction;
    }

    public class ActionMeta
    {
        [JsonPropertyName("default_popup")]
        public string? DefaultPopup { get; set; }

        [JsonPropertyName("default_title")]
        public string? DefaultTitle { get; set; }

        [JsonPropertyName("default_icon")]
        public System.Text.Json.JsonElement? DefaultIcon { get; set; }
    }

    public class BackgroundMeta
    {
        [JsonPropertyName("service_worker")]
        public string? ServiceWorker { get; set; }

        [JsonPropertyName("scripts")]
        public List<string>? Scripts { get; set; }

        [JsonPropertyName("page")]
        public string? Page { get; set; }
    }

    public class ContentScriptMeta
    {
        [JsonPropertyName("matches")]
        public List<string> Matches { get; set; } = new();

        [JsonPropertyName("exclude_matches")]
        public List<string> ExcludeMatches { get; set; } = new();

        [JsonPropertyName("js")]
        public List<string> Js { get; set; } = new();

        [JsonPropertyName("css")]
        public List<string> Css { get; set; } = new();

        [JsonPropertyName("all_frames")]
        public bool AllFrames { get; set; }

        [JsonPropertyName("run_at")]
        public string RunAt { get; set; } = "document_idle";
    }
}
