using Newtonsoft.Json;
using System.IO;

namespace AMCStudios.Installer
{
    public class AppPrefs
    {
        private static AppPrefs _instance;
        public static AppPrefs Instance => _instance ??= Load();

        public static string DataRoot
        {
            get
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AMCStore");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string PrefsFile => Path.Combine(DataRoot, "prefs.json");

        [JsonProperty("client_id")]
        public string ClientId { get; set; } = Guid.NewGuid().ToString("N");

        [JsonProperty("gorilla_tag_path")]
        public string GorillaTagPath { get; set; } = "";

        [JsonProperty("bepinex_plugins_path")]
        public string BepInExPluginsPath { get; set; } = "";

        [JsonProperty("subscribed_ids")]
        public List<string> SubscribedIds { get; set; } = new List<string>();

        [JsonProperty("liked_ids")]
        public List<string> LikedIds { get; set; } = new List<string>();

        [JsonProperty("disliked_ids")]
        public List<string> DislikedIds { get; set; } = new List<string>();

        [JsonProperty("session_token")]
        public string SessionToken { get; set; } = "";

        [JsonProperty("username")]
        public string Username { get; set; } = "";

        [JsonProperty("last_seen_changelog_version")]
        public string LastSeenChangelogVersion { get; set; } = "";

        [JsonIgnore]
        public bool IsSignedIn => !string.IsNullOrEmpty(SessionToken) && !string.IsNullOrEmpty(Username);

        public bool IsSubscribed(string id) => id != null && SubscribedIds.Contains(id);

        public bool HasLiked(string id) => id != null && LikedIds.Contains(id);

        public bool HasDisliked(string id) => id != null && DislikedIds.Contains(id);

        public void SetSubscribed(string id, bool on)
        {
            SubscribedIds.Remove(id);
            if (on) SubscribedIds.Add(id);
            Save();
        }

        public void SetVote(string id, bool like, bool undo)
        {
            if (like)
            {
                DislikedIds.Remove(id);
                LikedIds.Remove(id);
                if (!undo) LikedIds.Add(id);
            }
            else
            {
                LikedIds.Remove(id);
                DislikedIds.Remove(id);
                if (!undo) DislikedIds.Add(id);
            }
            Save();
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(PrefsFile, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch { }
        }

        public static AppPrefs WipeAllLocalData()
        {
            var keepPath = Instance.GorillaTagPath;
            var keepPlugins = Instance.BepInExPluginsPath;
            try { Directory.Delete(DataRoot, true); } catch { }

            var fresh = new AppPrefs
            {
                GorillaTagPath = keepPath,
                BepInExPluginsPath = keepPlugins
            };
            _instance = fresh;
            fresh.Save();
            return fresh;
        }

        private static AppPrefs Load()
        {
            try
            {
                if (File.Exists(PrefsFile))
                {
                    return JsonConvert.DeserializeObject<AppPrefs>(File.ReadAllText(PrefsFile)) ?? new AppPrefs();
                }
                return TryImportLegacySettings();
            }
            catch
            {
                return new AppPrefs();
            }
        }

        private static AppPrefs TryImportLegacySettings()
        {
            try
            {
                var legacy = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AMCStudiosInstaller", "settings.json");
                if (File.Exists(legacy))
                {
                    var old = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(legacy));
                    var prefs = new AppPrefs
                    {
                        GorillaTagPath = old != null && old.ContainsKey("GorillaTagPath") ? old["GorillaTagPath"] : "",
                        BepInExPluginsPath = old != null && old.ContainsKey("BepInExPluginsPath") ? old["BepInExPluginsPath"] : ""
                    };
                    prefs.Save();
                    return prefs;
                }
            }
            catch { }
            return new AppPrefs();
        }
    }
}