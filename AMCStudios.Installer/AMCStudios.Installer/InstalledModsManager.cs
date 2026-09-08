using Newtonsoft.Json;
using System.IO;
using System.IO.Compression;

namespace AMCStudios.Installer
{
    public static class InstalledModsManager
    {
        private static string LibraryFile => Path.Combine(AppPrefs.DataRoot, "installed.json");

        private static readonly object IoLock = new object();

        public static List<InstalledMod> Load()
        {
            lock (IoLock)
            {
                try
                {
                    if (File.Exists(LibraryFile))
                    {
                        return JsonConvert.DeserializeObject<List<InstalledMod>>(File.ReadAllText(LibraryFile))
                               ?? new List<InstalledMod>();
                    }
                }
                catch { }
                return new List<InstalledMod>();
            }
        }

        public static void Save(List<InstalledMod> mods)
        {
            lock (IoLock)
            {
                try
                {
                    File.WriteAllText(LibraryFile, JsonConvert.SerializeObject(mods, Formatting.Indented));
                }
                catch { }
            }
        }

        public static InstalledMod Get(string id) => Load().FirstOrDefault(m => m.Id == id);

        public static void Upsert(InstalledMod entry)
        {
            var all = Load();
            all.RemoveAll(m => m.Id == entry.Id);
            all.Add(entry);
            Save(all);
        }

        public static void Remove(string id)
        {
            var all = Load();
            all.RemoveAll(m => m.Id == id);
            Save(all);
        }

        public static string IconPathFor(string id, string ext = ".png")
        {
            var iconsDir = Path.Combine(AppPrefs.DataRoot, "icons");
            Directory.CreateDirectory(iconsDir);
            return Path.Combine(iconsDir, id + ext);
        }
    }

    public static class ModInstaller
    {
        public static string InstallRootFor(string pluginsPath, string modId) =>
            Path.Combine(pluginsPath, "AMCStore", modId ?? "unknown");

        public static string ConfigFolderFor(string pluginsPath)
        {
            var bepinexRoot = Path.GetDirectoryName(Path.GetFullPath(pluginsPath));
            return Path.Combine(string.IsNullOrEmpty(bepinexRoot) ? pluginsPath : bepinexRoot, "config");
        }

        public static async Task<InstalledMod> InstallAsync(
            StoreMod mod,
            string pluginsPath,
            IProgress<double> progress,
            CancellationToken ct)
        {
            if (mod == null) throw new ArgumentNullException(nameof(mod));
            if (string.IsNullOrWhiteSpace(pluginsPath) || !Directory.Exists(pluginsPath))
            {
                throw new InvalidOperationException("Gorilla Tag plugins folder is not set.");
            }

            Directory.CreateDirectory(Path.Combine(pluginsPath, "AMCStore"));
            var installFolder = InstallRootFor(pluginsPath, mod.Id);

            var tempZip = Path.Combine(Path.GetTempPath(), $"amcstore_{Guid.NewGuid():N}.zip");
            try
            {
                var url = ApiConfig.Url($"api/mods/{mod.Id}/download?client_id={Uri.EscapeDataString(AppPrefs.Instance.ClientId)}").ToString();

                await StoreApi.DownloadFileAsync(url, tempZip, progress, ct).ConfigureAwait(false);

                if (Directory.Exists(installFolder))
                {
                    Directory.Delete(installFolder, true);
                }
                Directory.CreateDirectory(installFolder);

                ZipFile.ExtractToDirectory(tempZip, installFolder, true);

                File.WriteAllText(Path.Combine(installFolder, $"{mod.Id}.version"), mod.Version ?? "1.0.0");

                string configFileName = "";
                if (!string.IsNullOrWhiteSpace(mod.ConfigUrl))
                {
                    configFileName = await InstallConfigAsync(mod, pluginsPath, ct).ConfigureAwait(false);
                }

                long size = Directory.EnumerateFiles(installFolder, "*", SearchOption.AllDirectories)
                                     .Sum(f => new FileInfo(f).Length);

                var iconSourceUrl = !string.IsNullOrWhiteSpace(mod.IconUrl) ? mod.IconUrl : mod.ThumbnailUrl;
                var iconPath = "";
                if (!string.IsNullOrWhiteSpace(iconSourceUrl))
                {
                    try
                    {
                        await ImageCache.LoadFromUrlAsync(iconSourceUrl).ConfigureAwait(false);
                        iconPath = InstalledModsManager.IconPathFor(mod.Id, Path.GetExtension(new Uri(iconSourceUrl).AbsolutePath));
                        if (!iconPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                            !iconPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) &&
                            !iconPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) &&
                            !iconPath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                        {
                            iconPath += ".png";
                        }
                        ImageCache.CopyToDiskCache(iconSourceUrl, iconPath);
                    }
                    catch { }
                }

                return new InstalledMod
                {
                    Id = mod.Id,
                    Name = mod.Name,
                    Creator = mod.Creator,
                    Version = mod.Version ?? "1.0.0",
                    InstalledAtUtc = DateTime.UtcNow,
                    InstallFolder = installFolder,
                    SizeBytes = size,
                    IconPath = iconPath,
                    ConfigFileName = configFileName
                };
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
        }

        private static async Task<string> InstallConfigAsync(StoreMod mod, string pluginsPath, CancellationToken ct)
        {
            var name = !string.IsNullOrWhiteSpace(mod.ConfigName)
                ? mod.ConfigName
                : mod.ConfigUrl.Substring(mod.ConfigUrl.LastIndexOf('/') + 1);
            name = Path.GetFileName(name);
            if (string.IsNullOrWhiteSpace(name)) return "";

            var configFolder = ConfigFolderFor(pluginsPath);
            Directory.CreateDirectory(configFolder);
            var destination = Path.Combine(configFolder, name);
            var temp = Path.Combine(Path.GetTempPath(), $"amcstore_cfg_{Guid.NewGuid():N}");
            try
            {
                await StoreApi.DownloadFileAsync(mod.ConfigUrl, temp, null, ct).ConfigureAwait(false);
                if (File.Exists(temp))
                {
                    File.Copy(temp, destination, true);
                    return name;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
            return "";
        }

        public static bool Uninstall(InstalledMod entry, string pluginsPath = null)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.InstallFolder)) return false;

            var root = Path.GetFullPath(entry.InstallFolder);
            bool removed = false;
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
                removed = true;
            }
            else if (entry.InstallFolder.Contains($"{Path.DirectorySeparatorChar}AMCStore{Path.DirectorySeparatorChar}") == false
                     && Directory.Exists(root))
            {
                Directory.Delete(root, true);
                removed = true;
            }

            if (!string.IsNullOrWhiteSpace(entry.ConfigFileName) && !string.IsNullOrWhiteSpace(pluginsPath))
            {
                var configFile = Path.Combine(ConfigFolderFor(pluginsPath), Path.GetFileName(entry.ConfigFileName));
                try
                {
                    if (File.Exists(configFile)) File.Delete(configFile);
                }
                catch { }
            }

            try
            {
                var amcRoot = Path.GetDirectoryName(root);
                if (amcRoot != null && Path.GetFileName(amcRoot) == "AMCStore" &&
                    !Directory.EnumerateFileSystemEntries(amcRoot).Any())
                {
                    Directory.Delete(amcRoot, true);
                }
            }
            catch { }

            return removed;
        }
    }

    public static class BepInExInstaller
    {
        public const string Version = "5.4.23.3";

        public const string DownloadUrl =
            "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.3/BepInEx_win_x64_5.4.23.3.zip";

        public static string PluginsPathFor(string gameRoot) => Path.Combine(gameRoot, "BepInEx", "plugins");

        public static bool IsInstalled(string gameRoot)
        {
            if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot)) return false;
            return Directory.Exists(Path.Combine(gameRoot, "BepInEx")) &&
                   File.Exists(Path.Combine(gameRoot, "winhttp.dll"));
        }

        public static async Task<(bool Ok, string Error)> InstallAsync(
            string gameRoot, IProgress<double> progress, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
            {
                return (false, "The Gorilla Tag folder doesn't exist.");
            }

            var tempZip = Path.Combine(Path.GetTempPath(), $"bepinex_{Guid.NewGuid():N}.zip");
            try
            {
                await StoreApi.DownloadFileAsync(DownloadUrl, tempZip, progress, ct).ConfigureAwait(false);
                progress?.Report(95);

                ZipFile.ExtractToDirectory(tempZip, gameRoot, overwriteFiles: true);

                if (!File.Exists(Path.Combine(gameRoot, "winhttp.dll")))
                {
                    return (false, "BepInEx was downloaded, but the install files are missing.");
                }

                Directory.CreateDirectory(PluginsPathFor(gameRoot));
                progress?.Report(100);
                return (true, "");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return (false, ex.Message); }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
        }
    }
}