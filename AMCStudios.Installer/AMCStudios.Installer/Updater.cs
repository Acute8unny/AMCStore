using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace AMCStudios.Installer
{
    public static class AppMeta
    {
        public static string CurrentVersion
        {
            get
            {
                try
                {
                    var v = typeof(AppMeta).Assembly.GetName().Version;
                    return v != null ? v.ToString(3) : "2.0.0";
                }
                catch { return "2.0.0"; }
            }
        }
    }

    internal static class UpdateManager
    {
        private const string ExeName = "AMCStudios.Installer.exe";

        public sealed class PendingUpdate
        {
            public string Version { get; init; } = "";
            public string DownloadUrl { get; init; } = "";
        }

        public static async Task<PendingUpdate> CheckForUpdateAsync()
        {
            try
            {
                var (version, url) = await StoreApi.GetLatestVersionAsync().ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(url)) return null;
                if (!IsNewerVersion(version, AppMeta.CurrentVersion)) return null;
                return new PendingUpdate { Version = version, DownloadUrl = url };
            }
            catch
            {
                return null;
            }
        }

        public static bool IsNewerVersion(string remote, string current)
        {
            int[] Parse(string s) => (s ?? "").Trim().TrimStart('v', 'V')
                .Split('.')
                .Select(p => int.TryParse(p, out var n) ? n : 0)
                .ToArray();

            var r = Parse(remote);
            var l = Parse(current);
            for (int i = 0; i < Math.Max(r.Length, l.Length); i++)
            {
                int rv = i < r.Length ? r[i] : 0;
                int lv = i < l.Length ? l[i] : 0;
                if (rv != lv) return rv > lv;
            }
            return false;
        }

        public static string FlagPath => Path.Combine(AppPrefs.DataRoot, "update.flag");

        public static string ConsumeUpdatedFlag()
        {
            try
            {
                if (!File.Exists(FlagPath)) return null;
                var ver = File.ReadAllText(FlagPath).Trim();
                File.Delete(FlagPath);
                return string.IsNullOrWhiteSpace(ver) ? null : ver.TrimStart('v', 'V');
            }
            catch { return null; }
        }

        public static async Task<bool> ApplyAsync(PendingUpdate update, IProgress<double> progress,
            Action<string> statusText, Action<string> subStatusText)
        {
            var tempZip = Path.Combine(Path.GetTempPath(), "AMCStore_update.zip");
            var extractDir = Path.Combine(Path.GetTempPath(), "AMCStoreUpdate");
            try
            {
                subStatusText?.Invoke($"Downloading v{update.Version}...");
                await StoreApi.DownloadFileAsync(update.DownloadUrl, tempZip, progress, System.Threading.CancellationToken.None)
                    .ConfigureAwait(true);

                subStatusText?.Invoke("Preparing files...");
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(tempZip, extractDir, true);

                var rootEntries = Directory.GetFileSystemEntries(extractDir);
                if (rootEntries.Length == 1 && Directory.Exists(rootEntries[0]))
                {
                    var inner = rootEntries[0];
                    foreach (var entry in Directory.GetFileSystemEntries(inner))
                        Directory.Move(entry, Path.Combine(extractDir, Path.GetFileName(entry)));
                    Directory.Delete(inner, true);
                }

                var newExe = Directory.EnumerateFiles(extractDir, ExeName, SearchOption.AllDirectories).FirstOrDefault();
                if (newExe == null) throw new InvalidOperationException($"{ExeName} was missing inside the update package.");

                var destDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
                var relExe = Path.GetRelativePath(extractDir, newExe);
                var psPath = Path.Combine(Path.GetTempPath(), "AMCStore_apply_update.ps1");
                WriteUpdaterScript(psPath, extractDir, destDir, relExe, update.Version);

                statusText?.Invoke("Restarting...");
                subStatusText?.Invoke("Applying v" + update.Version + " - the store will reopen in a moment.");

                bool needElevation = !DirectoryIsWritable(destDir);
                if (!StartPowerShell(psPath, needElevation)) return false;
                return true;
            }
            catch (Exception ex)
            {
                LogCrashSafe(ex);
                return false;
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
        }

        private static void WriteUpdaterScript(string path, string src, string dest, string relExe, string version)
        {
            string q(string s) => "'" + (s ?? "").Replace("'", "''") + "'";
            var exeName = ExeName;
            var exeBase = Path.GetFileNameWithoutExtension(exeName);
            var flag = FlagPath;
            var verArg = q("v" + version);

            var script =
                "$ErrorActionPreference = 'SilentlyContinue'\r\n" +
                $"$src  = {q(src)}\r\n" +
                $"$dest = {q(dest)}\r\n" +
                $"$rel  = {q(relExe)}\r\n" +
                $"$flag = {q(flag)}\r\n" +
                $"$ver  = {verArg}\r\n" +
                $"$exeName = {q(exeName)}\r\n" +
                $"$exeBase = {q(exeBase)}\r\n" +
                "$notifierBase = 'AMCStore.Notifier'\r\n" +
                "\r\n" +
                "# 1) stop the companion notifier too (it runs from the same folder and\r\n" +
                "#    would otherwise lock its own exe and break the copy). The relocated\r\n" +
                "#    launcher restarts it on startup.\r\n" +
                "Get-Process -Name $notifierBase -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue\r\n" +
                "\r\n" +
                "# 2) wait for the launcher process to exit (max ~30s)\r\n" +
                "for ($i = 0; $i -lt 60; $i++) {\r\n" +
                "  if (-not (Get-Process -Name $exeBase -ErrorAction SilentlyContinue)) { break }\r\n" +
                "  Start-Sleep -Milliseconds 500\r\n" +
                "}\r\n" +
                "\r\n" +
                "# 3) force-kill if it is still alive\r\n" +
                "Get-Process -Name $exeBase -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue\r\n" +
                "\r\n" +
                "# 4) give the OS a beat to fully release file locks\r\n" +
                "Start-Sleep -Seconds 1\r\n" +
                "\r\n" +
                "# 5) copy the new files over with retry (a file can stay briefly locked)\r\n" +
                "$copied = $false\r\n" +
                "for ($i = 0; $i -lt 40; $i++) {\r\n" +
                "  try {\r\n" +
                "    Copy-Item -Path (Join-Path $src '*') -Destination $dest -Recurse -Force -ErrorAction Stop\r\n" +
                "    $copied = $true\r\n" +
                "    break\r\n" +
                "  } catch {\r\n" +
                "    Start-Sleep -Milliseconds 500\r\n" +
                "  }\r\n" +
                "}\r\n" +
                "if (-not $copied) {\r\n" +
                "  $fallback = Join-Path $dest $rel\r\n" +
                "  if (Test-Path -LiteralPath $fallback) {\r\n" +
                "    Start-Process -FilePath $fallback -WorkingDirectory $dest\r\n" +
                "  } elseif (Test-Path -LiteralPath (Join-Path $dest $exeName)) {\r\n" +
                "    Start-Process -FilePath (Join-Path $dest $exeName) -WorkingDirectory $dest\r\n" +
                "  }\r\n" +
                "  exit 2\r\n" +
                "}\r\n" +
                "\r\n" +
                "# 6) write the update flag so the launcher shows the welcome-back toast\r\n" +
                "try { [System.IO.File]::WriteAllText($flag, $ver) } catch {}\r\n" +
                "\r\n" +
                "# 7) relaunch the new launcher from the install folder (it restarts the notifier)\r\n" +
                "$target = Join-Path $dest $rel\r\n" +
                "if (Test-Path -LiteralPath $target) {\r\n" +
                "  Start-Process -FilePath $target -WorkingDirectory $dest\r\n" +
                "}\r\n" +
                "\r\n" +
                "# 8) remove the notifier copy of this script so future updates start fresh\r\n" +
                "Start-Sleep -Seconds 2\r\n" +
                "Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue\r\n";

            System.IO.File.WriteAllText(path, script);
        }

        private static bool DirectoryIsWritable(string dir)
        {
            try
            {
                var probe = Path.Combine(dir, $"amc_write_test_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        private static bool StartPowerShell(string psPath, bool elevated)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{psPath}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Verb = elevated ? "runas" : null
            };
            try
            {
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void LogCrashSafe(Exception ex)
        {
            try { File.AppendAllText(Path.Combine(AppPrefs.DataRoot, "crash.log"),
                $"{DateTime.UtcNow:O} updater: {ex}\r\n"); } catch { }
        }
    }
}
