using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AMCStore.Notifier
{

    public class NotifItem
    {
        public string type { get; set; } = "";
        public double ts { get; set; }
        public string title { get; set; } = "";
        public string text { get; set; } = "";
        public string text_preview { get; set; } = "";
        public string from { get; set; } = "";
        public string version { get; set; } = "";
        public string download_url { get; set; } = "";
        public string url { get; set; } = "";
        public string mod_id { get; set; } = "";
        public string mod_name { get; set; } = "";
    }

    public class NotifFeed
    {
        public bool ok { get; set; }
        public double server_time { get; set; }
        public List<NotifItem> notifications { get; set; } = new List<NotifItem>();
    }

    public class ModBrief
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Creator { get; set; } = "";
    }






    public class NotifierWorker : IDisposable
    {
        private static readonly HttpClient Http = new HttpClient(
            new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate })
        { Timeout = TimeSpan.FromSeconds(20) };

        private readonly NotifyIcon _icon;
        private NotifierConfig _cfg;
        private System.Threading.Timer _timer;
        private bool _busy;
        private volatile bool _disposed;
        private volatile bool _updateRunning;
        private const double UpdateRetryCooldownSec = 1800;
        private const double UpdateStuckTimeoutSec = 1800;
        private volatile bool _serverOnline = true;
        private volatile bool _serverDownNotified;
        private volatile bool _serverUpNotified;
        private volatile bool _startupNotified;
        private DateTime _lastStatusNotif = DateTime.MinValue;

        public NotifierWorker(NotifyIcon icon)
        {
            _icon = icon;
            Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AMCStoreNotifier/1.0");
        }

        public void Start()
        {
            _cfg = NotifierConfig.Load();
            _ = RunOnceAsync();
            _timer = new System.Threading.Timer(_ => _ = RunOnceAsync(), null,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        }

        public void Stop()
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }







        public string NotifyTest(string title, string body)
        {
            if (!Program.ToastReady)
            {
                var balloonErr = TryBalloon(title, body);
                return balloonErr == null ? null : $"toast not available; balloon failed: {balloonErr}";
            }

            try
            {
                if (ToastNotifier.TryShow(title, body)) return null;
            }
            catch (Exception ex)
            {
                return $"toast failed: {ex.Message}";
            }

            var err2 = TryBalloon(title, body);
            return err2 == null ? null : $"toast showed nothing and balloon failed: {err2}";
        }

        private string TryBalloon(string title, string body)
        {
            try
            {
                _icon.BalloonTipIcon = ToolTipIcon.Info;
                _icon.BalloonTipTitle = title;
                _icon.BalloonTipText = body;
                _icon.ShowBalloonTip(6000);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private async Task RunOnceAsync()
        {
            if (_busy || _disposed) return;
            _busy = true;
            try
            {

                _cfg = NotifierConfig.Load();

                bool dnd = _cfg.DoNotDisturb;
                string token = "";
                string username = "";



                ResolveSession(ref token, ref username);
                bool signedIn = !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(username);

                ConsumeTestNotification();


                await CheckServerStatusAsync();



                if (!_startupNotified)
                {
                    _startupNotified = true;
                    NotifyStartupSummary();
                }

                var items = new List<NotifItem>();
                var launcherUpdates = new List<NotifItem>();


                if (signedIn)
                {
                    if (!dnd) CheckProfileChanges(token);
                    var feed = await FetchFeedAsync(token, _cfg.LastSeenTs, _cfg.InstalledVersion);
                    if (feed != null && feed.notifications != null)
                    {
                        foreach (var n in feed.notifications)
                        {
                            if (n.type == "launcher_update") launcherUpdates.Add(n);
                            else items.Add(n);
                            if (n.ts > _cfg.LastSeenTs) _cfg.LastSeenTs = n.ts;
                        }
                    }
                }


                if (launcherUpdates.Count == 0)
                {
                    var lu = await CheckLauncherUpdateAsync(_cfg.InstalledVersion);
                    if (lu != null && !string.IsNullOrWhiteSpace(lu.version))
                    {
                        launcherUpdates.Add(lu);
                        if (lu.ts > _cfg.LastSeenTs) _cfg.LastSeenTs = lu.ts;
                    }
                }


                bool launcherDriving = _cfg.UpdateState == "updating" || _cfg.UpdateState == "downloading";
                double nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                if (launcherDriving && !_updateRunning
                    && _cfg.UpdateAttemptTs > 0
                    && (nowSec - _cfg.UpdateAttemptTs) > UpdateStuckTimeoutSec)
                {
                    _cfg.UpdateState = "idle";
                    _cfg.UpdateToVersion = "";
                    _cfg.Save();
                    launcherDriving = false;
                }

                foreach (var lu in launcherUpdates)
                {
                    if (dnd) continue;
                    if (!IsNewer(lu.version ?? "", _cfg.InstalledVersion))
                    {
                        if (NotifyNew(new NotifItem { type = "launcher_banner" },
                                "launcher_banner#" + (lu.version ?? "")))
                        {
                            NotifyUser("AMC Store update", UpdateBanner(lu));
                        }
                        continue;
                    }

                    bool inCooldown = !string.IsNullOrWhiteSpace(_cfg.UpdateAttemptVersion)
                        && _cfg.UpdateAttemptVersion == (lu.version ?? "")
                        && (nowSec - _cfg.UpdateAttemptTs) < UpdateRetryCooldownSec;

                    if (!_updateRunning && !launcherDriving && !inCooldown)
                    {
                        _cfg.UpdateState = "downloading";
                        _cfg.UpdateToVersion = lu.version ?? "";
                        _cfg.UpdateAttemptTs = nowSec;
                        _cfg.UpdateAttemptVersion = lu.version ?? "";
                        _cfg.Save();
                        _updateRunning = true;
                        _ = ApplyLauncherUpdateAsync(lu);
                    }
                    else
                    {
                        if (NotifyNew(new NotifItem { type = "launcher_banner" },
                                "launcher_banner#" + (lu.version ?? "")))
                        {
                            NotifyUser("AMC Store update", UpdateBanner(lu));
                        }
                    }
                }


                bool appOpen = IsStoreRunning();


                bool notifyActivity = !(appOpen && !_cfg.NotifyWhenAppOpen);




                if (!dnd && notifyActivity)
                {
                    CheckModListChanges();
                    CheckInstalledChanges();
                }

                foreach (var n in items)
                {
                    if (dnd) continue;
                    if (!notifyActivity) continue;
                    string sig = Signature(n);
                    if (NotifyNew(n, sig))
                    {

                        NotifyUser(Title(n), Text(n));
                    }
                }

                _cfg.Save();
            }
            catch
            {

            }
            finally
            {
                _busy = false;
            }
        }

        private async Task<NotifFeed> FetchFeedAsync(string token, double since, string installed)
        {
            try
            {
                var q = $"since={since.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                if (!string.IsNullOrWhiteSpace(installed)) q += "&installed=" + Uri.EscapeDataString(installed);
                using var req = new HttpRequestMessage(HttpMethod.Get, ApiConfig.Url("api/notifications?" + q));
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonSerializer.Deserialize<NotifFeed>(json);
            }
            catch { return null; }
        }

        private async Task<NotifItem> CheckLauncherUpdateAsync(string installed)
        {
            if (string.IsNullOrWhiteSpace(installed)) return null;
            try
            {
                using var resp = await Http.GetAsync(ApiConfig.Url("api/version")).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var version = root.TryGetProperty("version", out var v) ? v.GetString() : "";
                var download = root.TryGetProperty("download_url", out var d) ? d.GetString() : "";
                if (string.IsNullOrWhiteSpace(version)) return null;
                if (!IsNewer(version, installed)) return null;
                return new NotifItem
                {
                    type = "launcher_update",
                    ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    version = version,
                    download_url = download ?? "",
                    title = "AMC Store update available",
                    text = "A new build (v" + version + ") is ready to install. Open the store to update."
                };
            }
            catch { return null; }
        }






        private async Task ApplyLauncherUpdateAsync(NotifItem lu)
        {
            try
            {
                _cfg.UpdateState = "downloading";
                _cfg.UpdateToVersion = lu.version ?? "";
                _cfg.Save();

                NotifyUser("AMC Store update",
                    "AMC Store is updating to v" + lu.version + " and will relaunch when it's ready.");

                bool started = await LauncherUpdater.ApplyAsync(
                    lu.version ?? "", lu.download_url ?? "",
                    msg => NotifyUser("AMC Store update",
                        "AMC Store is updating and will relaunch when it's ready.")).ConfigureAwait(false);

                if (!started)
                {

                    ResetUpdateState(idle: true);
                }
                else
                {


                    _cfg.UpdateState = "updating";
                    _cfg.UpdateToVersion = lu.version ?? "";
                    _cfg.Save();
                }
            }
            catch
            {
                ResetUpdateState(idle: true);
            }
            finally
            {
                _updateRunning = false;
            }
        }

        private void ResetUpdateState(bool idle)
        {
            try
            {
                _cfg.UpdateState = idle ? "idle" : "updating";
                if (idle) _cfg.UpdateToVersion = "";
                _cfg.Save();
            }
            catch { }
        }

        private static string UpdateBanner(NotifItem lu)
        {
            var v = string.IsNullOrWhiteSpace(lu.version) ? "" : " (v" + lu.version + ")";
            return "AMC Store is updating" + v + " and will relaunch when it's ready.";
        }

        private static bool IsNewer(string remote, string current)
        {
            int[] Parse(string s)
            {
                var t = (s ?? "").Trim().TrimStart('v', 'V');
                var parts = t.Split('.');
                var r = new List<int>();
                foreach (var p in parts) if (int.TryParse(p, out var n)) r.Add(n);
                return r.ToArray();
            }
            var rr = Parse(remote);
            var cc = Parse(current);
            int max = Math.Max(rr.Length, cc.Length);
            for (int i = 0; i < max; i++)
            {
                int rv = i < rr.Length ? rr[i] : 0;
                int cv = i < cc.Length ? cc[i] : 0;
                if (rv != cv) return rv > cv;
            }
            return false;
        }

        private string Signature(NotifItem n) => n.type switch
        {
            "dm" => "dm#" + n.from + "#" + n.ts,
            "mod_update" => "mod_update#" + (n.mod_id ?? "") + "#" + n.ts,
            "note" => "note#" + (n.ts),
            "launcher_update" => "launcher_update#" + (n.version ?? ""),
            _ => n.type + "#" + n.ts
        };

        private bool NotifyNew(NotifItem n, string sig)
        {
            lock (_cfg)
            {
                if (_cfg.Notified == null) _cfg.Notified = new List<string>();
                if (_cfg.Notified.Contains(sig)) return false;
                _cfg.Notified.Add(sig);
                if (_cfg.Notified.Count > 400) _cfg.Notified.RemoveRange(0, _cfg.Notified.Count - 400);

                return true;
            }
        }

        private static string Title(NotifItem n)
            => string.IsNullOrWhiteSpace(n.title) ? "AMC Store" : n.title;

        private static string Text(NotifItem n)
        {
            var preview = string.IsNullOrWhiteSpace(n.text_preview) ? n.text : n.text_preview;
            if (string.IsNullOrWhiteSpace(preview)) preview = n.text;
            if (preview != null && preview.Length > 180) preview = preview.Substring(0, 177) + "...";
            return string.IsNullOrWhiteSpace(preview) ? "You have a new AMC Store notification." : preview;
        }

        private void NotifyUser(string title, string body)
        {
            try
            {
                if (ToastNotifier.TryShow(title, body)) return;
                ShowBalloon(title, body, ToolTipIcon.Info, 6000);
            }
            catch { }
        }

        private void ConsumeTestNotification()
        {
            if (!_cfg.FlashTestNotification) return;
            _cfg.FlashTestNotification = false;
            _cfg.Save();
            NotifyUser("AMC Store",
                $"Notifications are working. You're signed in as {(_cfg.Username ?? "a guest")}.");
        }

        private void ResolveSession(ref string token, ref string username)
        {
            token = _cfg.SessionToken;
            username = _cfg.Username;
            if (!string.IsNullOrWhiteSpace(token)) return;


            try
            {
                var path = Path.Combine(NotifierConfig.EnvDataDir, "prefs.json");
                if (!File.Exists(path)) return;
                var json = File.ReadAllText(path, Encoding.UTF8);
                using var doc = JsonDocument.Parse(json);
                var tk = (doc.RootElement.TryGetProperty("session_token", out var t) ? t.GetString() : null)
                         ?? (doc.RootElement.TryGetProperty("SessionToken", out var t2) ? t2.GetString() : null);
                var us = (doc.RootElement.TryGetProperty("username", out var u) ? u.GetString() : null)
                         ?? (doc.RootElement.TryGetProperty("Username", out var u2) ? u2.GetString() : null);
                if (!string.IsNullOrWhiteSpace(tk))
                {
                    token = tk;
                    if (string.IsNullOrWhiteSpace(username)) username = us ?? "";

                    _cfg.SessionToken = tk;
                    _cfg.Username = username;
                    _cfg.Save();
                }
            }
            catch { }
        }

        private void NotifyStartupSummary()
        {
            try
            {
                string user = string.IsNullOrWhiteSpace(_cfg.Username) ? "not signed in" : "@" + _cfg.Username;
                string ver = string.IsNullOrWhiteSpace(_cfg.InstalledVersion) ? "unknown" : "v" + _cfg.InstalledVersion;
                string status = _serverOnline ? "online" : "offline";
                NotifyUser("AMC Store notification service",
                    $"Version {ver}\nSigned in as {user}\nServers: {status}");
            }
            catch { }
        }






        private async Task CheckServerStatusAsync()
        {
            try
            {
                using var resp = await Http.GetAsync(ApiConfig.Url("api/health")).ConfigureAwait(false);
                bool onlineNow = resp.IsSuccessStatusCode;
                UpdateServerState(onlineNow);
            }
            catch
            {
                UpdateServerState(false);
            }
        }

        private void UpdateServerState(bool onlineNow)
        {
            if (onlineNow == _serverOnline)
            {

                return;
            }
            _serverOnline = onlineNow;


            if ((DateTime.UtcNow - _lastStatusNotif).TotalSeconds < 20) return;
            _lastStatusNotif = DateTime.UtcNow;

            if (!onlineNow && !_serverDownNotified)
            {
                _serverDownNotified = true;
                _serverUpNotified = false;
                NotifyUser("AMC Store",
                    "The AMC Store server is unreachable. Downloads, logins and chat are temporarily unavailable. We'll let you know when it's back.");
            }
            else if (onlineNow && !_serverUpNotified)
            {
                _serverUpNotified = true;
                _serverDownNotified = false;
                NotifyUser("AMC Store",
                    "The AMC Store server is back online. Logins, downloads and chat are working again.");
            }
        }

        private void CheckModListChanges()
        {
            try
            {
                var mods = FetchModListSync();
                if (mods == null) return;

                var snapshot = _cfg.ModSnapshot ?? new Dictionary<string, string>();
                var current = new Dictionary<string, string>();
                foreach (var m in mods) current[m.Id] = m.Version;

                bool firstRun = snapshot.Count == 0;

                foreach (var m in mods)
                {
                    if (firstRun) continue;
                    if (!snapshot.TryGetValue(m.Id, out var oldVer))
                    {

                        if (NotifyNew(new NotifItem { type = "modlist_new" }, "modlist_new#" + m.Id))
                            NotifyUser("New mod uploaded", $"{m.Name} by @{m.Creator} was added.");
                    }
                    else if (oldVer != m.Version)
                    {

                        if (NotifyNew(new NotifItem { type = "modlist_updated" }, "modlist_updated#" + m.Id + "#" + m.Version))
                            NotifyUser("Mod updated", $"{m.Name} changed (v{oldVer} -> v{m.Version}).");
                    }
                }

                if (!firstRun)
                {
                    foreach (var kvp in snapshot)
                    {
                        if (!current.ContainsKey(kvp.Key))
                        {
                            if (NotifyNew(new NotifItem { type = "modlist_deleted" }, "modlist_deleted#" + kvp.Key))
                                NotifyUser("Mod removed", $"A mod (id {kvp.Key}) was deleted from the store.");
                        }
                    }
                }

                _cfg.ModSnapshot = current;
            }
            catch { }
        }

        private void CheckInstalledChanges()
        {
            try
            {
                var ids = ReadInstalledIds();
                var snapshot = _cfg.InstalledIds ?? new List<string>();
                bool firstRun = snapshot.Count == 0;

                if (!firstRun)
                {
                    foreach (var id in snapshot)
                    {
                        if (!ids.Contains(id))
                        {
                            if (NotifyNew(new NotifItem { type = "uninstall" }, "uninstall#" + id))
                                NotifyUser("Mod uninstalled", $"A mod was uninstalled (id {id}).");
                        }
                    }
                }

                _cfg.InstalledIds = ids;
            }
            catch { }
        }

        private void CheckProfileChanges(string token)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(token)) return;
                using var req = new HttpRequestMessage(HttpMethod.Get, ApiConfig.Url("api/auth/me"));
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = Http.Send(req);
                if (!resp.IsSuccessStatusCode) return;
                var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("user", out var u)) return;

                if (!u.TryGetProperty("username", out var uname)) return;
                string sig = (uname.GetString() ?? "")
                    + "|" + GetStr(u, "bio")
                    + "|" + GetStr(u, "grad_a")
                    + "|" + GetStr(u, "grad_b")
                    + "|" + GetStr(u, "avatar_url");

                bool firstRun = string.IsNullOrEmpty(_cfg.ProfileSnapshot);
                if (!firstRun && _cfg.ProfileSnapshot != sig)
                {
                    if (NotifyNew(new NotifItem { type = "profile" }, "profile#" + sig))
                        NotifyUser("Profile updated", "Your AMC Store profile was updated.");
                }
                _cfg.ProfileSnapshot = sig;
            }
            catch { }
        }

        private List<ModBrief> FetchModListSync()
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, ApiConfig.Url("api/mods"));
                using var resp = Http.Send(req);
                resp.EnsureSuccessStatusCode();
                var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("mods", out var arr)) return new List<ModBrief>();
                var list = new List<ModBrief>();
                foreach (var e in arr.EnumerateArray())
                {
                    var b = new ModBrief
                    {
                        Id = GetStr(e, "id"),
                        Name = GetStr(e, "name"),
                        Version = GetStr(e, "version"),
                        Creator = GetStr(e, "creator")
                    };
                    list.Add(b);
                }
                return list;
            }
            catch { return null; }
        }

        private List<string> ReadInstalledIds()
        {
            try
            {
                var path = Path.Combine(NotifierConfig.EnvDataDir, "installed.json");
                if (!File.Exists(path)) return new List<string>();
                var json = File.ReadAllText(path, Encoding.UTF8);
                using var doc = JsonDocument.Parse(json);
                var list = new List<string>();
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    var id = (e.TryGetProperty("Id", out var p) ? p.GetString()
                             : (e.TryGetProperty("id", out var p2) ? p2.GetString() : ""));
                    if (!string.IsNullOrWhiteSpace(id)) list.Add(id);
                }
                return list;
            }
            catch { return new List<string>(); }
        }

        private static string GetStr(JsonElement e, string key)
        {
            foreach (var k in new[] { key, key == "id" ? "id" : key, key[0].ToString().ToUpperInvariant() + (key.Length > 1 ? key.Substring(1) : "") })
            {
                if (e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString() ?? "";
            }
            return "";
        }

        private void ShowBalloon(string bodyTitle, string body, ToolTipIcon icon, int timeoutMs)
        {
            try
            {
                _icon.BalloonTipIcon = icon;
                _icon.BalloonTipTitle = bodyTitle;
                _icon.BalloonTipText = body;
                _icon.ShowBalloonTip(timeoutMs);
            }
            catch { }
        }


        private static bool IsStoreRunning()
        {
            try
            {
                var procs = Process.GetProcessesByName(
                    System.IO.Path.GetFileNameWithoutExtension(NotifierConfig.ExeName));
                return procs != null && procs.Length > 0;
            }
            catch { return false; }
        }


        public static void LaunchStore()
        {
            try
            {
                var exe = Path.Combine(AppContext.BaseDirectory, NotifierConfig.ExeName);
                if (!File.Exists(exe))
                {

                    exe = Path.Combine(NotifierConfig.EnvDataDir, NotifierConfig.ExeName);
                }
                if (File.Exists(exe))
                {
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
                }
                else
                {
                    MessageBox.Show("Could not find the AMC Store launcher.", "AMC Store", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            Stop();
            try { Http.Dispose(); } catch { }
        }
    }
}
