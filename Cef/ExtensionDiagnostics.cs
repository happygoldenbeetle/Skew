using System.Text;

namespace Skew.Cef;

internal static class ExtensionDiagnostics
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private static readonly object Sync = new();
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Skew", "Logs");
    internal static readonly string LogPath = Path.Combine(Folder, "extensions.log");

    internal static void Reset()
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(LogPath,
                    $"{DateTimeOffset.Now:O} [diagnostics] Extension diagnostics started.{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch { }
        }
    }

    internal static void Write(string category, string extensionId, string message)
    {
        string safeId = string.IsNullOrWhiteSpace(extensionId) ? "unknown" : extensionId;
        string safeMessage = (message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
                    File.Move(LogPath, LogPath + ".old", overwrite: true);
                File.AppendAllText(LogPath,
                    $"{DateTimeOffset.Now:O} [{category}] [{safeId}] {safeMessage}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch { }
        }
    }

    internal static string ExtensionIdFromUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, SkewSchemes.ExtensionScheme, StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        return uri.Host;
    }
}
