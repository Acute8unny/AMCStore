using System.Diagnostics;
using System.IO.Compression;

namespace AMCStore.Notifier
{
    internal static class LauncherUpdater
    {
        private const string ExeName = "AMCStudios.Installer.exe";

        public static string LauncherPath()
        {
            var exe = Path.Combine(AppContext.BaseDirectory, ExeName);
            if (File.Exists(exe)) return exe;
            exe = Path.Combine(NotifierConfig.EnvDataDir, ExeName);
            return File.Exists(exe) ? exe : null;
        }

        public static string InstallDir()
        {
            var exe = LauncherPath();
            if (exe == null) return null;
            var dir = Path.GetDirectoryName(exe);
            try
            {
                if (DirectoryIsWritable(dir)) return dir;
            }
            catch { }
            return dir;
        }

        public static bool DirectoryIsWritable(string dir)
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

        public static async Task<bool> ApplyAsync(string version, string downloadUrl, Action<string> progressText)
        {
            var tempZip = Path.Combine(Path.GetTempPath(), "AMCStore_update.zip");
            var extractDir = Path.Combine(Path.GetTempPath(), "AMCStoreUpdate");
            try
            {
                progressText?.Invoke($"Downloading v{version}...");
                await DownloadFileAsync(downloadUrl, tempZip, CancellationToken.None).ConfigureAwait(false);

                progressText?.Invoke("Preparing files...");
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

                var destDir = InstallDir();
                if (string.IsNullOrEmpty(destDir)) throw new InvalidOperationException("Could not locate the AMC Store install folder.");

                var relExe = Path.GetRelativePath(extractDir, newExe);
                var psPath = Path.Combine(Path.GetTempPath(), "AMCStore_apply_update.ps1");
                WriteUpdaterScript(psPath, extractDir, destDir, relExe, version);

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

        private static async Task DownloadFileAsync(string url, string filePath, CancellationToken ct)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AMCStoreNotifier/1.0");
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var dst = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await src.CopyToAsync(dst, 81920, ct).ConfigureAwait(false);
        }

        private static void WriteUpdaterScript(string path, string src, string dest, string relExe, string version)
        {
            string q(string s) => "'" + (s ?? "").Replace("'", "''") + "'";
            var exeBase = Path.GetFileNameWithoutExtension(ExeName);
            var flag = Path.Combine(NotifierConfig.EnvDataDir, "update.flag");

            var script =
                "$ErrorActionPreference = 'SilentlyContinue'\r\n" +
                $"$src  = {q(src)}\r\n" +
                $"$dest = {q(dest)}\r\n" +
                $"$rel  = {q(relExe)}\r\n" +
                $"$flag = {q(flag)}\r\n" +
                $"$ver  = {q("v" + version)}\r\n" +
                $"$exeBase = {q(exeBase)}\r\n" +
                $"$exeName = {q(ExeName)}\r\n" +
                "$notifierBase = 'AMCStore.Notifier'\r\n" +
                "\r\n" +
                "# 1) stop the companion notifier too (it runs from the same folder and\r\n" +
                "#    would otherwise lock its own exe and break the copy). The relocated\r\n" +
                "#    launcher restarts it on startup.\r\n" +
                "Get-Process -Name $notifierBase -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue\r\n" +
                "Start-Sleep -Milliseconds 700\r\n" +
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
                "# 5) copy the new files over with retry\r\n" +
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
                "# 7) relaunch the new launcher from the install folder\r\n" +
                "$target = Join-Path $dest $rel\r\n" +
                "if (Test-Path -LiteralPath $target) {\r\n" +
                "  Start-Process -FilePath $target -WorkingDirectory $dest\r\n" +
                "}\r\n" +
                "\r\n" +
                "# 8) remove this script so future updates start fresh\r\n" +
                "Start-Sleep -Seconds 2\r\n" +
                "Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue\r\n";

            System.IO.File.WriteAllText(path, script);
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
            catch { return false; }
        }

        private static void LogCrashSafe(Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(NotifierConfig.EnvDataDir, "crash.log"),
                    $"{DateTime.UtcNow:O} notifier-updater: {ex}\r\n");
            }
            catch { }
        }
    }
}