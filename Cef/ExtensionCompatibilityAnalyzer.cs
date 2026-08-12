using System.Text.Json;
using System.Text.RegularExpressions;
using Skew.Models;

namespace Skew.Cef;

internal static partial class ExtensionCompatibilityAnalyzer
{
    private const long MaxSourceBytes = 128L * 1024 * 1024;

    private static readonly HashSet<string> SupportedApis = new(StringComparer.Ordinal)
    {
        "runtime.id", "runtime.onMessage", "runtime.onMessageExternal", "runtime.onInstalled",
        "runtime.onStartup", "runtime.onConnect", "runtime.getURL", "runtime.getManifest",
        "runtime.sendMessage", "runtime.setUninstallURL", "runtime.getPlatformInfo",
        "runtime.openOptionsPage", "runtime.reload", "runtime.connect",
        "runtime.OnInstalledReason", "runtime.lastError",
        "contextMenus.ACTION_MENU_TOP_LEVEL_LIMIT", "contextMenus.onClicked", "contextMenus.create",
        "contextMenus.update", "contextMenus.remove", "contextMenus.removeAll",
        "menus.ACTION_MENU_TOP_LEVEL_LIMIT", "menus.onClicked", "menus.create", "menus.update",
        "menus.remove", "menus.removeAll",
        "storage.onChanged", "storage.local.onChanged", "storage.local.get", "storage.local.set",
        "storage.local.remove", "storage.local.clear", "storage.sync.onChanged", "storage.sync.get",
        "storage.sync.set", "storage.sync.remove", "storage.sync.clear", "storage.session.onChanged",
        "storage.session.get", "storage.session.set", "storage.session.remove", "storage.session.clear",
        "tabs.onActivated", "tabs.onCreated", "tabs.onRemoved", "tabs.onReplaced", "tabs.onUpdated",
        "tabs.query", "tabs.create", "tabs.sendMessage", "tabs.reload", "tabs.detectLanguage",
        "tabs.get", "tabs.getCurrent", "tabs.update", "tabs.executeScript",
        "alarms.onAlarm", "alarms.create", "alarms.clear", "alarms.clearAll", "alarms.get", "alarms.getAll",
        "idle.onStateChanged", "idle.queryState", "windows.onRemoved", "windows.update",
        "webNavigation.onBeforeNavigate", "webNavigation.onCommitted", "webNavigation.onCompleted",
        "webNavigation.onDOMContentLoaded", "webNavigation.onCreatedNavigationTarget",
        "webNavigation.onErrorOccurred", "webNavigation.onHistoryStateUpdated",
        "webNavigation.onReferenceFragmentUpdated", "webNavigation.onTabReplaced",
        "webNavigation.getAllFrames", "webNavigation.patchedForOnHistoryStateUpdated",
        "webRequest.ResourceType", "webRequest.OnBeforeRequestOptions",
        "webRequest.OnBeforeSendHeadersOptions", "webRequest.OnSendHeadersOptions",
        "webRequest.OnHeadersReceivedOptions", "webRequest.OnCompletedOptions",
        "webRequest.OnAuthRequiredOptions", "webRequest.onBeforeRequest",
        "webRequest.onBeforeSendHeaders", "webRequest.onSendHeaders", "webRequest.onHeadersReceived",
        "webRequest.onAuthRequired", "webRequest.onBeforeRedirect", "webRequest.onResponseStarted",
        "webRequest.onCompleted", "webRequest.onErrorOccurred", "webRequest.handlerBehaviorChanged",
        "notifications.onClicked", "notifications.onButtonClicked", "notifications.create",
        "declarativeNetRequest.MAX_NUMBER_OF_DYNAMIC_AND_SESSION_RULES",
        "declarativeNetRequest.MAX_NUMBER_OF_DYNAMIC_RULES",
        "declarativeNetRequest.MAX_NUMBER_OF_ENABLED_STATIC_RULESETS",
        "declarativeNetRequest.getDynamicRules", "declarativeNetRequest.getSessionRules",
        "declarativeNetRequest.getEnabledRulesets", "declarativeNetRequest.getAvailableStaticRuleCount",
        "declarativeNetRequest.getDisabledRuleIds", "declarativeNetRequest.isRegexSupported",
        "declarativeNetRequest.updateDynamicRules", "declarativeNetRequest.updateEnabledRulesets",
        "declarativeNetRequest.updateStaticRules",
        "action.onClicked", "action.getPopup", "action.setPopup", "action.getTitle",
        "action.getBadgeText", "action.getBadgeBackgroundColor", "action.setTitle", "action.setIcon",
        "action.setBadgeText", "action.setBadgeBackgroundColor", "action.enable", "action.disable",
        "browserAction.onClicked", "browserAction.getPopup", "browserAction.setPopup",
        "browserAction.getTitle", "browserAction.getBadgeText", "browserAction.getBadgeBackgroundColor",
        "browserAction.setTitle", "browserAction.setIcon", "browserAction.setBadgeText",
        "browserAction.setBadgeBackgroundColor", "browserAction.enable", "browserAction.disable",
        "scripting.ExecutionWorld", "scripting.executeScript", "scripting.insertCSS", "scripting.removeCSS",
        "permissions.getAll", "permissions.contains", "permissions.request", "permissions.remove",
        "management.getSelf", "management.getAll", "management.get", "dom.openOrClosedShadowRoot",
        "devtools.inspectedWindow.tabId", "devtools.inspectedWindow.reload", "devtools.panels.themeName",
        "devtools.panels.openResource", "devtools.panels.create",
        "storage.local.getBytesInUse", "storage.managed.get", "storage.managed.getBytesInUse",
        "tabs.TAB_ID_NONE", "tabs.insertCSS", "tabs.removeCSS", "tabs.remove",
        "commands.onCommand", "i18n.getUILanguage", "i18n.getMessage", "extension.getURL"
    };

    internal static void AnalyzeAndWrite(BrowserExtension extension)
    {
        if (extension.Manifest is null || !Directory.Exists(extension.Path)) return;
        try
        {
            SortedSet<string> required = ScanJavaScript(extension.Path);
            string[] missing = required.Where(api => !IsSupported(api)).ToArray();
            string reportFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Skew", "Logs", "ExtensionCompatibility");
            Directory.CreateDirectory(reportFolder);
            string reportPath = Path.Combine(reportFolder, extension.Id + ".json");
            var report = new
            {
                extensionId = extension.Id,
                extensionName = extension.Name,
                extensionVersion = extension.Version,
                manifestVersion = extension.Manifest.ManifestVersion,
                analyzedAt = DateTimeOffset.Now,
                note = "Static analysis is conservative. Dynamically constructed API names may only appear at runtime.",
                permissions = extension.Manifest.Permissions.OrderBy(value => value).ToArray(),
                hostPermissions = extension.Manifest.HostPermissions.OrderBy(value => value).ToArray(),
                requiredApis = required,
                missingApis = missing
            };
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }));
            ExtensionDiagnostics.Write("compatibility", extension.Id,
                $"Static scan found {required.Count} API paths and {missing.Length} missing paths. Report: {reportPath}");
            if (missing.Length > 0)
                ExtensionDiagnostics.Write("compatibility-missing", extension.Id,
                    string.Join(", ", missing.Take(80)) + (missing.Length > 80 ? ", ..." : string.Empty));
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("compatibility-error", extension.Id, ex.Message);
        }
    }

    private static SortedSet<string> ScanJavaScript(string root)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        long scanned = 0;
        foreach (string path in Directory.EnumerateFiles(root, "*.js", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxSourceBytes || scanned > MaxSourceBytes - info.Length)
                continue;
            scanned += info.Length;
            string source = File.ReadAllText(path);
            source = BlockCommentRegex().Replace(source, " ");
            source = LineCommentRegex().Replace(source, " ");
            source = BracketPropertyRegex().Replace(source, ".$1");
            foreach (Match match in ApiPathRegex().Matches(source))
            {
                string api = Normalize(match.Groups[1].Value);
                if (!string.IsNullOrEmpty(api)) found.Add(api);
            }
            foreach (Match match in BundledAliasApiPathRegex().Matches(source))
            {
                string api = Normalize(match.Groups[1].Value);
                if (!string.IsNullOrEmpty(api)) found.Add(api);
            }
        }
        return found;
    }

    private static string Normalize(string api)
    {
        if (api.StartsWith("A.", StringComparison.Ordinal)) api = api[2..];
        string[] suffixes = { ".addListener", ".removeListener", ".hasListener", ".hasListeners" };
        foreach (string suffix in suffixes)
            if (api.EndsWith(suffix, StringComparison.Ordinal))
                return api[..^suffix.Length];
        return api;
    }

    private static bool IsSupported(string required) => SupportedApis.Any(supported =>
        string.Equals(required, supported, StringComparison.Ordinal) ||
        required.StartsWith(supported + ".", StringComparison.Ordinal) ||
        supported.StartsWith(required + ".", StringComparison.Ordinal));

    [GeneratedRegex(@"/\*[\s\S]*?\*/")]
    private static partial Regex BlockCommentRegex();

    [GeneratedRegex(@"(?m)//[^\r\n]*$")]
    private static partial Regex LineCommentRegex();

    [GeneratedRegex("\\[\\s*['\\\"]([A-Za-z_$][A-Za-z0-9_$]*)['\\\"]\\s*\\]")]
    private static partial Regex BracketPropertyRegex();

    [GeneratedRegex(@"\b(?:chrome|browser|browser_polyfill|globalThis\.chrome|window\.chrome)\.([A-Za-z_$][A-Za-z0-9_$]*(?:\.[A-Za-z_$][A-Za-z0-9_$]*)+)")]
    private static partial Regex ApiPathRegex();

    [GeneratedRegex(@"\b[A-Za-z_$][A-Za-z0-9_$]*(?:_browser|browser_polyfill)(?:\s*/\*[\s\S]*?\*/\s*)?\.(?:A\.)?([A-Za-z_$][A-Za-z0-9_$]*(?:\.[A-Za-z_$][A-Za-z0-9_$]*)+)")]
    private static partial Regex BundledAliasApiPathRegex();
}
