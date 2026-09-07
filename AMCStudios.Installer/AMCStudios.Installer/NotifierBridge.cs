using System;
using System.IO;
using System.Text;

namespace AMCStudios.Installer
{
    public static class NotifierBridge
    {
        private static readonly object Lock = new object();

        private static string FilePath => Path.Combine(AppPrefs.DataRoot, "notifier.json");
        private static string ExeName => "AMCStore.Notifier.exe";

        public static void Update()
        {
            lock (Lock)
            {
                try
                {
                    var data = ReadExisting();
                    data["session_token"] = AppPrefs.Instance.SessionToken;
                    data["username"] = AppPrefs.Instance.Username;
                    data["installed_version"] = AppMeta.CurrentVersion;
                    if (!data.ContainsKey("do_not_disturb")) data["do_not_disturb"] = false;
                    if (!data.ContainsKey("last_seen_ts")) data["last_seen_ts"] = 0.0;
                    if (!data.ContainsKey("notified")) data["notified"] = new object[0];
                    Write(data);
                }
                catch { }
            }
        }

        public static void SetUpdateState(string state, string toVersion)
        {
            lock (Lock)
            {
                try
                {
                    var data = ReadExisting();
                    data["update_state"] = state;
                    data["update_to_version"] = toVersion ?? "";
                    Write(data);
                }
                catch { }
            }
        }

        public static void MarkUpdateAttempt(string toVersion)
        {
            lock (Lock)
            {
                try
                {
                    var data = ReadExisting();
                    data["update_attempt_ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    data["update_attempt_version"] = toVersion ?? "";
                    Write(data);
                }
                catch { }
            }
        }

        public static string GetUpdateState()
        {
            try
            {
                var data = ReadExisting();
                return data.TryGetValue("update_state", out var v) ? (v?.ToString() ?? "idle") : "idle";
            }
            catch { return "idle"; }
        }

        public static void SetDoNotDisturb(bool on)
        {
            lock (Lock)
            {
                try
                {
                    var data = ReadExisting();
                    data["do_not_disturb"] = on;
                    Write(data);
                }
                catch { }
            }
        }

        public static bool GetDoNotDisturb()
        {
            try
            {
                var data = ReadExisting();
                return data.TryGetValue("do_not_disturb", out var v)
                    && (v is bool b ? b : v?.ToString() == "true");
            }
            catch { return false; }
        }

        public static void FlashTestNotification()
        {
            lock (Lock)
            {
                try
                {
                    var data = ReadExisting();
                    data["flash_test_notification"] = true;
                    Write(data);
                }
                catch { }
            }
        }

        public static string EnsureNotifierRunning()
        {
            try
            {
                string exe = Path.Combine(AppContext.BaseDirectory, ExeName);
                if (!File.Exists(exe))
                {
                    exe = Path.Combine(Path.GetDirectoryName(typeof(NotifierBridge).Assembly.Location) ?? "", ExeName);
                }
                if (!File.Exists(exe)) return null;

                var name = ExeName;
                var running = System.Diagnostics.Process.GetProcessesByName(
                    System.IO.Path.GetFileNameWithoutExtension(name));
                if (running != null && running.Length > 0) return exe;

                var psi = new System.Diagnostics.ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe)
                };
                System.Diagnostics.Process.Start(psi);
                return exe;
            }
            catch { return null; }
        }

        private static System.Collections.Generic.Dictionary<string, object> ReadExisting()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath, Encoding.UTF8);
                    var parsed = Newtonsoft.Json.Linq.JObject.Parse(json);



                    string Str(params string[] keys)
                    {
                        foreach (var k in keys)
                            if (parsed[k]?.Type == Newtonsoft.Json.Linq.JTokenType.String)
                                return (string)parsed[k];
                        return "";
                    }
                    bool Bool(params string[] keys)
                    {
                        foreach (var k in keys)
                            if (parsed[k]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean)
                                return (bool)parsed[k];
                        return false;
                    }
                    double Num(params string[] keys)
                    {
                        foreach (var k in keys)
                            if (parsed[k]?.Type == Newtonsoft.Json.Linq.JTokenType.Float ||
                                parsed[k]?.Type == Newtonsoft.Json.Linq.JTokenType.Integer)
                                return (double)parsed[k];
                        return 0.0;
                    }

                    return new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["session_token"] = Str("session_token", "SessionToken"),
                        ["username"] = Str("username", "Username"),
                        ["installed_version"] = Str("installed_version", "InstalledVersion"),
                        ["do_not_disturb"] = Bool("do_not_disturb", "DoNotDisturb"),
                        ["notify_when_app_open"] = Bool("notify_when_app_open", "NotifyWhenAppOpen"),
                        ["last_seen_ts"] = Num("last_seen_ts", "LastSeenTs"),
                        ["notified"] = parsed["notified"] ?? parsed["Notified"] ?? new Newtonsoft.Json.Linq.JArray(),
                        ["update_state"] = Str("update_state", "UpdateState"),
                        ["update_to_version"] = Str("update_to_version", "UpdateToVersion"),
                        ["update_attempt_ts"] = Num("update_attempt_ts", "UpdateAttemptTs"),
                        ["update_attempt_version"] = Str("update_attempt_version", "UpdateAttemptVersion"),
                        ["flash_test_notification"] = Bool("flash_test_notification", "FlashTestNotification"),
                    };
                }
            }
            catch { }
            return new System.Collections.Generic.Dictionary<string, object>
            {
                ["session_token"] = "",
                ["username"] = "",
                ["installed_version"] = "",
                ["do_not_disturb"] = false,
                ["notify_when_app_open"] = true,
                ["last_seen_ts"] = 0.0,
                ["notified"] = new Newtonsoft.Json.Linq.JArray(),
                ["update_state"] = "idle",
                ["update_to_version"] = "",
                ["update_attempt_ts"] = 0.0,
                ["update_attempt_version"] = "",
                ["flash_test_notification"] = false,
            };
        }

        private static void Write(System.Collections.Generic.Dictionary<string, object> data)
        {




            Newtonsoft.Json.Linq.JObject obj;
            try
            {
                obj = File.Exists(FilePath)
                    ? Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(FilePath, Encoding.UTF8))
                    : new Newtonsoft.Json.Linq.JObject();
            }
            catch { obj = new Newtonsoft.Json.Linq.JObject(); }

            obj["session_token"] = data["session_token"]?.ToString() ?? "";
            obj["username"] = data["username"]?.ToString() ?? "";
            obj["installed_version"] = data["installed_version"]?.ToString() ?? "";
            obj["do_not_disturb"] = data.ContainsKey("do_not_disturb") && (data["do_not_disturb"] is bool b ? b : (data["do_not_disturb"]?.ToString() == "true"));
            obj["last_seen_ts"] = data["last_seen_ts"] is double d ? d : 0.0;
            obj["notified"] = (Newtonsoft.Json.Linq.JToken)data["notified"] ?? new Newtonsoft.Json.Linq.JArray();
            obj["update_state"] = data.ContainsKey("update_state") ? (data["update_state"]?.ToString() ?? "idle") : "idle";
            obj["update_to_version"] = data.ContainsKey("update_to_version") ? (data["update_to_version"]?.ToString() ?? "") : "";
            obj["update_attempt_ts"] = data.ContainsKey("update_attempt_ts") && data["update_attempt_ts"] is double ats ? ats : 0.0;
            obj["update_attempt_version"] = data.ContainsKey("update_attempt_version") ? (data["update_attempt_version"]?.ToString() ?? "") : "";
            obj["flash_test_notification"] = data.ContainsKey("flash_test_notification") && (data["flash_test_notification"] is bool fb ? fb : (data["flash_test_notification"]?.ToString() == "true"));
            File.WriteAllText(FilePath, obj.ToString(Newtonsoft.Json.Formatting.Indented), Encoding.UTF8);
        }
    }
}
