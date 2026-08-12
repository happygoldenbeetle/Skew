using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Skew.Models
{
    public class ExtensionStore
    {
        private static readonly ExtensionStore _instance = new();
        public static ExtensionStore Shared => _instance;

        private const string CatalogFile = "extensions.json";

        public ObservableCollection<BrowserExtension> Extensions { get; } = new();

        // A thread-safe snapshot of extensions for background threads (like CEF) to read without COM wrong-thread crashes.
        public IReadOnlyList<BrowserExtension> GetSnapshot()
        {
            lock (_snapshotLock)
            {
                return _snapshot.ToList();
            }
        }
        private readonly object _snapshotLock = new();
        private List<BrowserExtension> _snapshot = new();

        private void UpdateSnapshot()
        {
            lock (_snapshotLock)
            {
                _snapshot = Extensions.ToList();
            }
        }

        public ExtensionStore()
        {
            Extensions.CollectionChanged += (s, e) => UpdateSnapshot();
            Directory.CreateDirectory(GetStoreFolder());
            // Initial load is synchronous enough for list binding, file IO happens async.
            _ = LoadExtensionsAsync();
        }

        private static string GetStoreFolder()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Skew", "Extensions");
        }

        private async Task LoadExtensionsAsync()
        {
            try
            {
                var folder = GetStoreFolder();
                var filePath = Path.Combine(folder, CatalogFile);
                if (!File.Exists(filePath)) return;
                
                var json = await File.ReadAllTextAsync(filePath);
                var items = JsonSerializer.Deserialize<List<BrowserExtension>>(json);
                
                if (items != null)
                {
                    // Run on UI thread if needed, but since we are just adding to an ObservableCollection
                    // before it's heavily bound, we might be fine. Safe to use CoreDispatcher if it throws.
                    foreach (var ext in items)
                    {
                        if (Directory.Exists(ext.Path))
                        {
                            ext.Manifest = ReadManifest(ext.Path);
                            if (ext.IconPath == null && ext.Manifest != null)
                            {
                                ext.IconPath = GetBestIconPath(ext.Manifest, ext.Path);
                            }
                            Extensions.Add(ext);
                        }
                    }
                    UpdateSnapshot();
                }
            }
            catch (Exception)
            {
                // File might not exist yet
            }
        }

        public async Task SaveExtensionsAsync()
        {
            try
            {
                var folder = GetStoreFolder();
                var filePath = Path.Combine(folder, CatalogFile);
                var json = JsonSerializer.Serialize(Extensions);
                await File.WriteAllTextAsync(filePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save extensions: {ex.Message}");
            }
        }

        public async Task<string?> ImportExtensionAsync(string sourceFolder)
        {
            var manifestPath = Path.Combine(sourceFolder, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                return "The selected folder does not contain a manifest.json.";
            }

            var manifest = ReadManifest(sourceFolder);
            if (manifest == null)
            {
                return "Could not parse manifest.json.";
            }

            // Copy to managed directory
            var id = Guid.NewGuid().ToString("N");
            var managedFolder = Path.Combine(GetStoreFolder(), id);
            
            try
            {
                await Task.Run(() => CopyDirectory(sourceFolder, managedFolder));
            }
            catch (Exception ex)
            {
                return $"Failed to copy extension: {ex.Message}";
            }

            var ext = new BrowserExtension
            {
                Id = id,
                Name = string.IsNullOrEmpty(manifest.Name) ? Path.GetFileName(sourceFolder) : manifest.Name,
                Version = manifest.Version,
                Detail = manifest.Description,
                Path = managedFolder,
                IconPath = GetBestIconPath(manifest, managedFolder),
                Enabled = true,
                Manifest = manifest
            };

            Extensions.Add(ext);
            await SaveExtensionsAsync();

            return null; // Success
        }

        public async Task<string?> BeginWebStoreInstallAsync(string idOrUrl)
        {
            var id = ExtractExtensionId(idOrUrl);
            if (string.IsNullOrEmpty(id)) return "Invalid Web Store URL or Extension ID.";

            try
            {
                var crxData = await DownloadCrxAsync(id);
                var zipData = ExtractZipFromCrx(crxData);
                
                var tmpZip = Path.GetTempFileName() + ".zip";
                await File.WriteAllBytesAsync(tmpZip, zipData);
                
                var managedFolder = Path.Combine(GetStoreFolder(), id);
                await Task.Run(() => 
                {
                    if (Directory.Exists(managedFolder)) Directory.Delete(managedFolder, true);
                    System.IO.Compression.ZipFile.ExtractToDirectory(tmpZip, managedFolder);
                    File.Delete(tmpZip);
                });

                var manifest = ReadManifest(managedFolder);
                if (manifest == null)
                {
                    Directory.Delete(managedFolder, true);
                    return "Downloaded extension is missing a valid manifest.json.";
                }

                // Remove old if exists
                var existing = Extensions.FirstOrDefault(x => x.Id == id);
                if (existing != null) Extensions.Remove(existing);

                var ext = new BrowserExtension
                {
                    Id = id,
                    Name = string.IsNullOrEmpty(manifest.Name) ? id : manifest.Name,
                    Version = manifest.Version,
                    Detail = manifest.Description,
                    Path = managedFolder,
                    IconPath = GetBestIconPath(manifest, managedFolder),
                    Enabled = true,
                    Manifest = manifest
                };

                Extensions.Add(ext);
                await SaveExtensionsAsync();
                
                return null;
            }
            catch (Exception ex)
            {
                return $"Failed to install from Web Store: {ex.Message}";
            }
        }

        private static string? ExtractExtensionId(string input)
        {
            input = input.Trim();
            if (input.Length == 32 && IsAlphaOnly(input)) return input.ToLowerInvariant();
            
            try
            {
                if (Uri.TryCreate(input, UriKind.Absolute, out Uri? uri))
                {
                    var segments = uri.AbsolutePath.TrimEnd('/').Split('/');
                    var last = segments[segments.Length - 1];
                    if (last.Length == 32 && IsAlphaOnly(last)) return last.ToLowerInvariant();
                }
            }
            catch {}
            return null;
        }

        private static bool IsAlphaOnly(string s)
        {
            foreach (var c in s)
            {
                if ((c < 'a' || c > 'p') && (c < 'A' || c > 'P'))
                    return false;
            }
            return true;
        }

        private async Task<byte[]> DownloadCrxAsync(string id)
        {
            using var client = new System.Net.Http.HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36");
            var url = $"https://clients2.google.com/service/update2/crx?response=redirect&acceptformat=crx2,crx3&prodversion=148.0&x=id%3D{id}%26installsource%3Dondemand%26uc";
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync();
        }

        private static byte[] ExtractZipFromCrx(byte[] data)
        {
            if (data.Length >= 2 && data[0] == 0x50 && data[1] == 0x4B)
            {
                return data; // Already a raw zip
            }
            if (data.Length < 16 || data[0] != 0x43 || data[1] != 0x72 || data[2] != 0x32 || data[3] != 0x34)
            {
                throw new Exception("Invalid CRX header.");
            }

            int version = BitConverter.ToInt32(data, 4);
            int zipStart = 0;

            if (version == 3)
            {
                int headerLength = BitConverter.ToInt32(data, 8);
                zipStart = 12 + headerLength;
            }
            else if (version == 2)
            {
                int pubKeyLength = BitConverter.ToInt32(data, 8);
                int sigLength = BitConverter.ToInt32(data, 12);
                zipStart = 16 + pubKeyLength + sigLength;
            }
            else
            {
                throw new Exception("Unsupported CRX version.");
            }

            if (zipStart >= data.Length || zipStart < 0) throw new Exception("Invalid CRX format.");
            
            var zipData = new byte[data.Length - zipStart];
            Array.Copy(data, zipStart, zipData, 0, zipData.Length);
            return zipData;
        }

        public async Task RemoveExtensionAsync(BrowserExtension ext)
        {
            Extensions.Remove(ext);
            await SaveExtensionsAsync();

            try
            {
                if (Directory.Exists(ext.Path))
                {
                    Directory.Delete(ext.Path, true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to delete extension folder: {ex.Message}");
            }
        }

        public async Task SetEnabledAsync(BrowserExtension ext, bool enabled)
        {
            ext.Enabled = enabled;
            await SaveExtensionsAsync();
        }

        public static ManifestMeta? ReadManifest(string folderPath)
        {
            try
            {
                var manifestPath = Path.Combine(folderPath, "manifest.json");
                if (!File.Exists(manifestPath)) return null;

                var json = File.ReadAllText(manifestPath);
                return JsonSerializer.Deserialize<ManifestMeta>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch
            {
                return null;
            }
        }

        private static void CopyDirectory(string sourceDir, string destinationDir)
        {
            var dir = new DirectoryInfo(sourceDir);

            if (!dir.Exists)
                throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");

            DirectoryInfo[] dirs = dir.GetDirectories();
            Directory.CreateDirectory(destinationDir);

            foreach (FileInfo file in dir.GetFiles())
            {
                string targetFilePath = Path.Combine(destinationDir, file.Name);
                file.CopyTo(targetFilePath);
            }

            foreach (DirectoryInfo subDir in dirs)
            {
                string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
                CopyDirectory(subDir.FullName, newDestinationDir);
            }
        }

        private string? GetBestIconPath(ManifestMeta manifest, string folderPath)
        {
            if (manifest.Icons == null || manifest.Icons.Count == 0) return null;

            int bestSize = 0;
            string? bestPath = null;
            foreach (var kvp in manifest.Icons)
            {
                if (int.TryParse(kvp.Key, out int size) && size > bestSize)
                {
                    bestSize = size;
                    bestPath = kvp.Value;
                }
            }

            if (bestPath != null)
            {
                var fullPath = Path.Combine(folderPath, bestPath);
                if (File.Exists(fullPath)) return fullPath;
            }
            return null;
        }
    }
}
