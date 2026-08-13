using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Skew.Models;

public enum ExtensionChangeKind
{
    Loaded,
    Installed,
    Updated,
    Removed,
    Enabled,
    Disabled
}

public sealed class ExtensionStore
{
    private static readonly ExtensionStore _instance = new();
    private static readonly HttpClient DownloadClient = new()
    {
        Timeout = TimeSpan.FromSeconds(90)
    };

    public static ExtensionStore Shared => _instance;

    private const string CatalogFile = "extensions.json";
    private const string CatalogBackupFile = "extensions.json.bak";
    private const int MaxDownloadBytes = 128 * 1024 * 1024;

    private readonly object _snapshotLock = new();
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly Task _loadTask;
    private List<BrowserExtension> _snapshot = new();

    public ObservableCollection<BrowserExtension> Extensions { get; } = new();
    public event Action<BrowserExtension, ExtensionChangeKind>? ExtensionChanged;

    public ExtensionStore()
    {
        Extensions.CollectionChanged += (_, _) => UpdateSnapshot();
        Directory.CreateDirectory(GetStoreFolder());
        _loadTask = LoadExtensionsAsync();
    }

    public IReadOnlyList<BrowserExtension> GetSnapshot()
    {
        lock (_snapshotLock)
            return _snapshot.ToList();
    }

    public async Task SaveExtensionsAsync()
    {
        await _loadTask;
        await _mutationGate.WaitAsync();
        try
        {
            await SaveExtensionsCoreAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save extensions: {ex.Message}");
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<string?> ImportExtensionAsync(string sourceFolder)
    {
        await _loadTask;
        if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
            return "The selected extension folder does not exist.";

        ManifestMeta? sourceManifest = ReadManifest(sourceFolder, out string? manifestError);
        if (sourceManifest is null)
            return manifestError ?? "The selected folder does not contain a valid manifest.json.";

        string? validationError = ValidateManifest(sourceManifest, sourceFolder);
        if (validationError is not null) return validationError;

        string id = ExtensionPackage.StableUnpackedId(sourceFolder, sourceManifest);
        string staging = CreateStagingFolder(id);

        await _mutationGate.WaitAsync();
        try
        {
            await Task.Run(() => CopyDirectory(sourceFolder, staging));
            ManifestMeta manifest = ReadManifest(staging, out manifestError)
                ?? throw new InvalidDataException(manifestError ??
                    "The copied extension has an invalid manifest.json.");
            validationError = ValidateManifest(manifest, staging);
            if (validationError is not null) throw new InvalidDataException(validationError);

            await CommitPreparedExtensionAsync(id, staging, manifest, Path.GetFileName(sourceFolder));
            staging = string.Empty;
            return null;
        }
        catch (Exception ex)
        {
            return $"Failed to import extension: {ex.Message}";
        }
        finally
        {
            DeleteDirectoryBestEffort(staging);
            _mutationGate.Release();
        }
    }

    public async Task<string?> BeginWebStoreInstallAsync(string idOrUrl)
    {
        await _loadTask;
        string? id = ExtractExtensionId(idOrUrl);
        if (id is null) return "Invalid Chrome Web Store URL or extension ID.";

        string staging = CreateStagingFolder(id);
        await _mutationGate.WaitAsync();
        try
        {
            byte[] crx = await DownloadCrxAsync(id);
            byte[] archive = await Task.Run(() => ExtensionPackage.VerifyAndExtractCrx3(crx, id));
            await Task.Run(() => ExtensionPackage.ExtractArchive(archive, staging));

            ManifestMeta manifest = ReadManifest(staging, out string? manifestError)
                ?? throw new InvalidDataException(manifestError ??
                    "The downloaded extension has an invalid manifest.json.");
            string? validationError = ValidateManifest(manifest, staging);
            if (validationError is not null) throw new InvalidDataException(validationError);

            if (!string.IsNullOrWhiteSpace(manifest.Key) &&
                ExtensionPackage.IdFromManifestKey(manifest.Key) is { } manifestId &&
                !string.Equals(manifestId, id, StringComparison.Ordinal))
                throw new InvalidDataException("The manifest key does not match the requested extension ID.");

            await CommitPreparedExtensionAsync(id, staging, manifest, id);
            staging = string.Empty;
            return null;
        }
        catch (Exception ex)
        {
            return $"Failed to install from Chrome Web Store: {ex.Message}";
        }
        finally
        {
            DeleteDirectoryBestEffort(staging);
            _mutationGate.Release();
        }
    }

    public async Task RemoveExtensionAsync(BrowserExtension extension)
    {
        await _loadTask;
        await _mutationGate.WaitAsync();
        try
        {
            int index = Extensions.IndexOf(extension);
            if (index < 0) return;

            Extensions.RemoveAt(index);
            try
            {
                await SaveExtensionsCoreAsync();
            }
            catch
            {
                Extensions.Insert(index, extension);
                throw;
            }

            DeleteDirectoryBestEffort(extension.Path);
            ExtensionChanged?.Invoke(extension, ExtensionChangeKind.Removed);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task SetEnabledAsync(BrowserExtension extension, bool enabled)
    {
        await _loadTask;
        await _mutationGate.WaitAsync();
        try
        {
            bool previous = extension.Enabled;
            extension.Enabled = enabled;
            UpdateSnapshot();
            try
            {
                await SaveExtensionsCoreAsync();
                ExtensionChanged?.Invoke(extension, enabled
                    ? ExtensionChangeKind.Enabled : ExtensionChangeKind.Disabled);
            }
            catch
            {
                extension.Enabled = previous;
                UpdateSnapshot();
                throw;
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public static ManifestMeta? ReadManifest(string folderPath) => ReadManifest(folderPath, out _);

    private static ManifestMeta? ReadManifest(string folderPath, out string? error)
    {
        error = null;
        try
        {
            string manifestPath = Path.Combine(folderPath, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                error = "The extension package does not contain manifest.json.";
                return null;
            }

            var manifest = JsonSerializer.Deserialize<ManifestMeta>(
                File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest is null) return null;

            manifest.Name = ResolveLocalizedString(manifest.Name, manifest.DefaultLocale, folderPath);
            manifest.ShortName = ResolveLocalizedString(manifest.ShortName, manifest.DefaultLocale, folderPath);
            manifest.Description = ResolveLocalizedString(manifest.Description, manifest.DefaultLocale, folderPath);
            return manifest;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to read extension manifest: {ex.Message}");
            error = ex is JsonException jsonException && !string.IsNullOrWhiteSpace(jsonException.Path)
                ? $"The extension manifest has an unsupported value at {jsonException.Path}."
                : $"The extension manifest could not be read: {ex.Message}";
            return null;
        }
    }

    private async Task LoadExtensionsAsync()
    {
        string store = GetStoreFolder();
        string catalog = Path.Combine(store, CatalogFile);
        string backup = Path.Combine(store, CatalogBackupFile);

        List<BrowserExtension>? items = await ReadCatalogAsync(catalog);
        if (items is null && File.Exists(backup))
            items = await ReadCatalogAsync(backup);
        if (items is null) return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BrowserExtension extension in items)
        {
            if (string.IsNullOrWhiteSpace(extension.Id) || !seen.Add(extension.Id) ||
                string.IsNullOrWhiteSpace(extension.Path) || !Directory.Exists(extension.Path))
                continue;

            extension.Manifest = ReadManifest(extension.Path);
            if (extension.Manifest is null) continue;
            extension.Name = extension.Manifest.Name;
            extension.Detail = extension.Manifest.Description;
            extension.Version = extension.Manifest.Version;
            extension.IconPath = GetBestIconPath(extension.Manifest, extension.Path);
            Extensions.Add(extension);
            _ = Task.Run(() => Skew.Cef.ExtensionCompatibilityAnalyzer.AnalyzeAndWrite(extension));
            ExtensionChanged?.Invoke(extension, ExtensionChangeKind.Loaded);
        }
        UpdateSnapshot();
    }

    private static async Task<List<BrowserExtension>?> ReadCatalogAsync(string path)
    {
        if (!File.Exists(path)) return new List<BrowserExtension>();
        try
        {
            return JsonSerializer.Deserialize<List<BrowserExtension>>(await File.ReadAllTextAsync(path));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to read extension catalog {path}: {ex.Message}");
            return null;
        }
    }

    private async Task CommitPreparedExtensionAsync(
        string id, string staging, ManifestMeta manifest, string fallbackName)
    {
        string managed = Path.Combine(GetStoreFolder(), id);
        string backup = Path.Combine(GetStoreFolder(), $".{id}.{Guid.NewGuid():N}.backup");
        BrowserExtension? previous = Extensions.FirstOrDefault(extension =>
            string.Equals(extension.Id, id, StringComparison.OrdinalIgnoreCase));
        int previousIndex = previous is null ? -1 : Extensions.IndexOf(previous);

        var replacement = new BrowserExtension
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(manifest.Name) ? fallbackName : manifest.Name,
            Version = manifest.Version,
            Detail = manifest.Description,
            Path = managed,
            IconPath = GetBestIconPath(manifest, staging),
            Enabled = previous?.Enabled ?? true,
            Pinned = previous?.Pinned ?? false,
            Manifest = manifest
        };

        bool movedOld = false;
        bool movedNew = false;
        bool collectionChanged = false;
        try
        {
            if (Directory.Exists(managed))
            {
                Directory.Move(managed, backup);
                movedOld = true;
            }
            Directory.Move(staging, managed);
            movedNew = true;

            replacement.IconPath = GetBestIconPath(manifest, managed);
            if (previous is not null) Extensions.Remove(previous);
            if (previousIndex >= 0 && previousIndex <= Extensions.Count)
                Extensions.Insert(previousIndex, replacement);
            else
                Extensions.Add(replacement);
            collectionChanged = true;

            await SaveExtensionsCoreAsync();
            _ = Task.Run(() => Skew.Cef.ExtensionCompatibilityAnalyzer.AnalyzeAndWrite(replacement));
            ExtensionChanged?.Invoke(replacement, previous is null
                ? ExtensionChangeKind.Installed : ExtensionChangeKind.Updated);
        }
        catch
        {
            if (collectionChanged)
            {
                Extensions.Remove(replacement);
                if (previous is not null)
                    Extensions.Insert(Math.Clamp(previousIndex, 0, Extensions.Count), previous);
            }
            if (movedNew) DeleteDirectoryBestEffort(managed);
            if (movedOld && Directory.Exists(backup)) Directory.Move(backup, managed);
            throw;
        }
        finally
        {
            DeleteDirectoryBestEffort(backup);
        }
    }

    private async Task SaveExtensionsCoreAsync()
    {
        string folder = GetStoreFolder();
        Directory.CreateDirectory(folder);
        string catalog = Path.Combine(folder, CatalogFile);
        string backup = Path.Combine(folder, CatalogBackupFile);
        string temporary = Path.Combine(folder, $".{CatalogFile}.{Guid.NewGuid():N}.tmp");
        string json = JsonSerializer.Serialize(Extensions);

        try
        {
            await File.WriteAllTextAsync(temporary, json);
            if (File.Exists(catalog)) File.Copy(catalog, backup, overwrite: true);
            File.Move(temporary, catalog, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string? ValidateManifest(ManifestMeta manifest, string root)
    {
        if (manifest.ManifestVersion is not (2 or 3))
            return "Only Manifest V2 and Manifest V3 extensions are supported.";
        if (string.IsNullOrWhiteSpace(manifest.Name))
            return "The extension manifest is missing a name.";
        if (string.IsNullOrWhiteSpace(manifest.Version))
            return "The extension manifest is missing a version.";

        var declaredFiles = new List<string>();
        foreach (ContentScriptMeta script in manifest.ContentScripts)
        {
            if (script.Matches.Count == 0)
                return "A content script is missing its match patterns.";
            declaredFiles.AddRange(script.Js);
            declaredFiles.AddRange(script.Css);
        }
        if (manifest.Background is not null)
        {
            if (!string.IsNullOrWhiteSpace(manifest.Background.ServiceWorker))
                declaredFiles.Add(manifest.Background.ServiceWorker);
            if (!string.IsNullOrWhiteSpace(manifest.Background.Page))
                declaredFiles.Add(manifest.Background.Page);
            if (manifest.Background.Scripts is not null)
                declaredFiles.AddRange(manifest.Background.Scripts);
        }
        if (!string.IsNullOrWhiteSpace(manifest.EffectiveAction?.DefaultPopup))
            declaredFiles.Add(manifest.EffectiveAction.DefaultPopup);

        foreach (string relative in declaredFiles.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (SafePackageFile(root, relative) is null)
                return $"The manifest references a missing or unsafe file: {relative}";
        }
        return null;
    }

    private static string? SafePackageFile(string root, string relative)
    {
        try
        {
            string resolvedRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(root, relative.TrimStart('/', '\\')));
            return candidate.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)
                ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveLocalizedString(string value, string? defaultLocale, string root)
    {
        Match match = Regex.Match(value ?? string.Empty, "^__MSG_(.+)__$", RegexOptions.IgnoreCase);
        if (!match.Success) return value ?? string.Empty;
        string key = match.Groups[1].Value;

        foreach (string locale in new[] { defaultLocale, "en", "en_US" }
            .Where(locale => !string.IsNullOrWhiteSpace(locale))
            .Select(locale => locale!.Replace('-', '_'))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string messages = Path.Combine(root, "_locales", locale, "messages.json");
            if (!File.Exists(messages)) continue;
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(messages));
                if (document.RootElement.TryGetProperty(key, out JsonElement entry) &&
                    entry.TryGetProperty("message", out JsonElement message))
                    return message.GetString() ?? value ?? string.Empty;
            }
            catch (JsonException) { }
        }
        return value ?? string.Empty;
    }

    private static async Task<byte[]> DownloadCrxAsync(string id)
    {
        string productVersion = GetChromiumProductVersion();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://clients2.google.com/service/update2/crx?response=redirect&acceptformat=crx3&prodversion={productVersion}&x=id%3D{id}%26installsource%3Dondemand%26uc");
        request.Headers.UserAgent.ParseAdd(
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{productVersion} Safari/537.36");

        using HttpResponseMessage response = await DownloadClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxDownloadBytes)
            throw new InvalidDataException("The extension download is too large.");

        await using Stream input = await response.Content.ReadAsStreamAsync();
        using var output = new MemoryStream();
        var buffer = new byte[81_920];
        int read;
        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            if (output.Length > MaxDownloadBytes - read)
                throw new InvalidDataException("The extension download is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string GetChromiumProductVersion()
    {
        try
        {
            string cefPath = Path.Combine(AppContext.BaseDirectory, "cef", "libcef.dll");
            string? version = FileVersionInfo.GetVersionInfo(cefPath).ProductVersion;
            Match match = Regex.Match(version ?? string.Empty, @"\bchromium-(\d+\.\d+\.\d+\.\d+)");
            if (match.Success) return match.Groups[1].Value;
        }
        catch { }
        return "148.0.0.0";
    }

    private static string? ExtractExtensionId(string input)
    {
        input = input.Trim();
        if (input.Length == 32 && input.All(character => character is >= 'a' and <= 'p'))
            return input;

        if (!Uri.TryCreate(input, UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Host, "chromewebstore.google.com", StringComparison.OrdinalIgnoreCase))
            return null;
        string candidate = uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;
        return candidate.Length == 32 && candidate.All(character => character is >= 'a' and <= 'p')
            ? candidate : null;
    }

    private static string GetStoreFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skew", "Extensions");

    private static string CreateStagingFolder(string id) => Path.Combine(
        GetStoreFolder(), $".{id}.{Guid.NewGuid():N}.install");

    private static void UpdateSnapshotFrom(ObservableCollection<BrowserExtension> source, object sync, ref List<BrowserExtension> target)
    {
        lock (sync) target = source.ToList();
    }

    private void UpdateSnapshot()
    {
        UpdateSnapshotFrom(Extensions, _snapshotLock, ref _snapshot);

        // Recompile blocking rules whenever the set of extensions changes.
        // Install, remove, enable and disable all pass through here, so the
        // engine never holds rules for an extension that is no longer running.
        try { Skew.Cef.DeclarativeNetRequestEngine.Reload(); }
        catch (Exception) { /* never let rule loading break the store */ }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        foreach (string directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static string? GetBestIconPath(ManifestMeta manifest, string folder)
    {
        if (manifest.Icons is null) return null;
        foreach (var icon in manifest.Icons
            .OrderByDescending(pair => int.TryParse(pair.Key, out int size) ? size : 0))
        {
            string? candidate = SafePackageFile(folder, icon.Value);
            if (candidate is not null) return candidate;
        }
        return null;
    }

    private static void DeleteDirectoryBestEffort(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to clean extension directory {path}: {ex.Message}");
        }
    }
}
