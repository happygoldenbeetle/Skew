using System.Text.Json;

namespace Skew.Cef;

/// <summary>
/// Optional permissions an extension has been granted since install.
///
/// <para>
/// Chrome asks the user and remembers the answer. There is no prompt here, so
/// the manifest's own <c>optional_permissions</c> and
/// <c>optional_host_permissions</c> stand as the declaration of what may be
/// granted — an extension cannot obtain reach it never asked for — and the
/// grant is written down so it outlives the page that asked. Without that, a
/// setting like uBlock Origin Lite's filtering mode reverts the moment its
/// popup closes.
/// </para>
/// </summary>
internal static class ExtensionPermissions
{
    private sealed class GrantRecord
    {
        public List<string> Permissions { get; set; } = [];
        public List<string> Origins { get; set; } = [];
    }

    private static readonly object s_lock = new();

    /// <summary>What has been granted, as the shim's initial state object.</summary>
    public static string GrantedJson(string extensionId)
    {
        GrantRecord record = Load(extensionId);
        return JsonSerializer.Serialize(new
        {
            permissions = record.Permissions,
            origins = record.Origins,
        });
    }

    /// <summary>
    /// Grant what the manifest allows and report the full granted set back.
    /// Anything not declared optional is refused, which is the only check
    /// standing in for the prompt.
    /// </summary>
    public static (bool Granted, List<string> Permissions, List<string> Origins) Request(
        Models.BrowserExtension extension, IEnumerable<string> permissions, IEnumerable<string> origins)
    {
        var declaredPermissions = new HashSet<string>(
            (extension.Manifest?.Permissions ?? []).Concat(extension.Manifest?.OptionalPermissions ?? []),
            StringComparer.OrdinalIgnoreCase);
        var declaredOrigins = new HashSet<string>(
            (extension.Manifest?.HostPermissions ?? []).Concat(extension.Manifest?.OptionalHostPermissions ?? []),
            StringComparer.OrdinalIgnoreCase);

        lock (s_lock)
        {
            GrantRecord record = Load(extension.Id);
            bool granted = true;

            foreach (string permission in permissions)
            {
                if (!declaredPermissions.Contains(permission)) { granted = false; continue; }
                if (!record.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase))
                    record.Permissions.Add(permission);
            }

            foreach (string origin in origins)
            {
                // A declared broad origin covers a narrow request.
                bool covered = declaredOrigins.Contains(origin) ||
                    declaredOrigins.Contains("<all_urls>") ||
                    declaredOrigins.Contains("*://*/*") ||
                    declaredOrigins.Any(declared => OriginCovers(declared, origin));
                if (!covered) { granted = false; continue; }
                if (!record.Origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
                    record.Origins.Add(origin);
            }

            Save(extension.Id, record);
            ExtensionDiagnostics.Write("permissions", extension.Id,
                granted ? "Granted requested optional permissions."
                        : "Refused permissions the manifest never declared as optional.");
            return (granted, record.Permissions, record.Origins);
        }
    }

    public static (List<string> Permissions, List<string> Origins) Remove(
        string extensionId, IEnumerable<string> permissions, IEnumerable<string> origins)
    {
        lock (s_lock)
        {
            GrantRecord record = Load(extensionId);
            foreach (string permission in permissions)
                record.Permissions.RemoveAll(item =>
                    string.Equals(item, permission, StringComparison.OrdinalIgnoreCase));
            foreach (string origin in origins)
                record.Origins.RemoveAll(item =>
                    string.Equals(item, origin, StringComparison.OrdinalIgnoreCase));
            Save(extensionId, record);
            return (record.Permissions, record.Origins);
        }
    }

    private static bool OriginCovers(string declared, string requested)
    {
        if (declared.Equals(requested, StringComparison.OrdinalIgnoreCase)) return true;
        if (declared == "http://*/*") return requested.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        if (declared == "https://*/*") return requested.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static GrantRecord Load(string extensionId)
    {
        try
        {
            string path = PermissionsPath(extensionId);
            if (!File.Exists(path)) return new GrantRecord();
            return JsonSerializer.Deserialize<GrantRecord>(File.ReadAllText(path)) ?? new GrantRecord();
        }
        catch (Exception)
        {
            return new GrantRecord();
        }
    }

    private static void Save(string extensionId, GrantRecord record)
    {
        try
        {
            string path = PermissionsPath(extensionId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(record));
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("permissions-error", extensionId, ex.Message);
        }
    }

    private static string PermissionsPath(string extensionId)
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Skew", "ExtensionData", extensionId, "permissions.json");
}
