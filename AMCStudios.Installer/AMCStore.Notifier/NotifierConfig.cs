using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;

namespace AMCStore.Notifier
{







    public class NotifierConfig
    {
        private static readonly object Lock = new object();
        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AMCStore", "notifier.json");

        [JsonPropertyName("session_token")]
        public string SessionToken { get; set; } = "";
        [JsonPropertyName("username")]
        public string Username { get; set; } = "";
        [JsonPropertyName("installed_version")]
        public string InstalledVersion { get; set; } = "";
        [JsonPropertyName("do_not_disturb")]
        public bool DoNotDisturb { get; set; }

        [JsonPropertyName("notify_when_app_open")]
        public bool NotifyWhenAppOpen { get; set; } = true;
        [JsonPropertyName("last_seen_ts")]
        public double LastSeenTs { get; set; }
        [JsonPropertyName("notified")]
        public List<string> Notified { get; set; } = new List<string>();


        [JsonPropertyName("update_state")]
        public string UpdateState { get; set; } = "idle";
        [JsonPropertyName("update_to_version")]
        public string UpdateToVersion { get; set; } = "";
        [JsonPropertyName("update_attempt_ts")]
        public double UpdateAttemptTs { get; set; }
        [JsonPropertyName("update_attempt_version")]
        public string UpdateAttemptVersion { get; set; } = "";


        [JsonPropertyName("flash_test_notification")]
        public bool FlashTestNotification { get; set; }



        [JsonPropertyName("startup_notified")]
        public bool StartupNotified { get; set; }


        [JsonPropertyName("mod_snapshot")]
        public Dictionary<string, string> ModSnapshot { get; set; } = new Dictionary<string, string>();


        [JsonPropertyName("installed_ids")]
        public List<string> InstalledIds { get; set; } = new List<string>();


        [JsonPropertyName("profile_snapshot")]
        public string ProfileSnapshot { get; set; } = "";

        public static string ExeName { get; } = "AMCStudios.Installer.exe";

        public static string EnvDataDir
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AMCStore");
                try { Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        public static NotifierConfig Load()
        {
            lock (Lock)
            {
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var cfg = System.Text.Json.JsonSerializer.Deserialize<NotifierConfig>(
                            File.ReadAllText(FilePath, Encoding.UTF8),
                            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (cfg != null) return cfg;
                    }
                }
                catch { }
                return new NotifierConfig();
            }
        }

        public void Save()
        {
            lock (Lock)
            {
                try
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(this,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(FilePath, json, Encoding.UTF8);
                }
                catch { }
            }
        }
    }
}