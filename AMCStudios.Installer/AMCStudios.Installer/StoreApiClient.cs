using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AMCStudios.Installer
{
    public static class StoreApi
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
            var client = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            })
            {
                Timeout = TimeSpan.FromSeconds(45)
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AMCStore/1.0");
            return client;
        }

        public static async Task<List<StoreMod>> GetModsAsync(string search, string sort, string category = "")
        {
            var sb = new StringBuilder($"api/mods?limit=200&sort={Uri.EscapeDataString(sort ?? "popular")}");
            if (!string.IsNullOrWhiteSpace(search))
            {
                sb.Append("&search=").Append(Uri.EscapeDataString(search.Trim()));
            }
            if (!string.IsNullOrWhiteSpace(category) && category != "all")
            {
                sb.Append("&category=").Append(Uri.EscapeDataString(category));
            }
            using var resp = await Http.GetAsync(ApiConfig.Url(sb.ToString())).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonConvert.DeserializeObject<ModsResponse>(json)?.Mods ?? new List<StoreMod>();
        }

        public static async Task<StoreMod> GetModAsync(string id)
        {
            using var resp = await Http.GetAsync(ApiConfig.Url($"api/mods/{id}")).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonConvert.DeserializeObject<StoreMod>(json);
        }

        public static async Task<ReactionResponse> ReactAsync(string id, bool like, bool undo)
        {
            var payload = JsonConvert.SerializeObject(new { client_id = AppPrefs.Instance.ClientId, undo });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var verb = like ? "like" : "dislike";
            using var resp = await Http.PostAsync(ApiConfig.Url($"api/mods/{id}/{verb}"), content).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonConvert.DeserializeObject<ReactionResponse>(json) ?? new ReactionResponse();
        }

        public static async Task<System.Collections.Generic.List<string>> GetAnnouncementsAsync()
        {
            using var resp = await Http.GetAsync(ApiConfig.Url("api/announcements")).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonConvert.DeserializeObject<AnnouncementsResponse>(json)?.Announcements ?? new System.Collections.Generic.List<string>();
        }

        public static async Task DownloadFileAsync(string url, string destinationPath, IProgress<double> progress, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (AppPrefs.Instance.IsSignedIn)
                {
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
                }

                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    throw new InvalidOperationException("Sign in to download mods - your session has expired.");
                }
                if ((int)resp.StatusCode == 429)
                {
                    throw new InvalidOperationException("Too many downloads too fast - this account was flagged as a bot and banned.");
                }
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;
                await using (var source = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                await using (var target = File.Create(destinationPath))
                {
                    var buffer = new byte[81920];
                    long readTotal = 0;
                    int bytesRead;
                    while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer, 0, bytesRead, ct).ConfigureAwait(false);
                        readTotal += bytesRead;
                        if (total > 0)
                        {
                            progress?.Report(readTotal * 100.0 / total.Value);
                        }
                    }
                }
            }
        }

        public static Task<byte[]> GetBytesAsync(string url)
        {
            return Http.GetByteArrayAsync(url);
        }

        public static async Task<bool> PingAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var resp = await Http.GetAsync(ApiConfig.Url("api/health"), cts.Token).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public static async Task<(string Version, string Url)> GetLatestVersionAsync()
        {
            using var resp = await Http.GetAsync(ApiConfig.Url("api/version")).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var d = JsonConvert.DeserializeObject<VersionResponse>(json);
            return (d?.Version ?? "", d?.DownloadUrl ?? "");
        }

        public static async Task<List<ChangelogVersion>> GetChangelogAsync()
        {
            try
            {
                using var resp = await Http.GetAsync(ApiConfig.Url("api/changelog")).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<ChangelogResponse>(json)?.Versions ?? new List<ChangelogVersion>();
            }
            catch
            {
                return new List<ChangelogVersion>();
            }
        }

        public static async Task<NotificationsResponse> GetNotificationsAsync(double since, string installedVersion)
        {
            var builder = $"api/notifications?since={since.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            if (!string.IsNullOrWhiteSpace(installedVersion))
            {
                builder += "&installed=" + Uri.EscapeDataString(installedVersion);
            }
            using var req = new HttpRequestMessage(HttpMethod.Get, ApiConfig.Url(builder));
            if (AppPrefs.Instance.IsSignedIn)
            {
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
            }
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonConvert.DeserializeObject<NotificationsResponse>(json) ?? new NotificationsResponse();
        }

        public static void Logout()
        {
            if (string.IsNullOrEmpty(AppPrefs.Instance.SessionToken)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var msg = new HttpRequestMessage(HttpMethod.Post, ApiConfig.Url("api/logout"));
                    msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
                    await Http.SendAsync(msg).ConfigureAwait(false);
                }
                catch { }
            });
        }

        public static void Track(string kind)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var payload = JsonConvert.SerializeObject(new { client_id = AppPrefs.Instance.ClientId });
                    var content = new StringContent(payload, Encoding.UTF8, "application/json");
                    using var resp = await Http.PostAsync(ApiConfig.Url($"api/track/{kind}"), content).ConfigureAwait(false);
                }
                catch { }
            });
        }

        private static string AuthJson(string username, string password)
        {
            return JsonConvert.SerializeObject(new { username, password });
        }

        public static async Task<AuthResponse> RegisterAsync(string username, string password)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiConfig.Url("api/auth/register"))
            { Content = new StringContent(AuthJson(username, password), Encoding.UTF8, "application/json") };
            req.Headers.Add("X-Device-Id", AppPrefs.Instance.ClientId);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonConvert.DeserializeObject<AuthResponse>(json) ?? new AuthResponse();
        }

        public static async Task<AuthResponse> LoginAsync(string username, string password)
        {
            var content = new StringContent(AuthJson(username, password), Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(ApiConfig.Url("api/auth/login"), content).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonConvert.DeserializeObject<AuthResponse>(json) ?? new AuthResponse();
        }

        public static async Task<UserResponse> GetMeAsync()
        {
            if (!AppPrefs.Instance.IsSignedIn) return new UserResponse();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, ApiConfig.Url("api/auth/me"));
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return new UserResponse();
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<UserResponse>(json) ?? new UserResponse();
            }
            catch
            {
                return new UserResponse();
            }
        }

        public static async Task<UserResponse> GetUserAsync(string name)
        {
            using var resp = await Http.GetAsync(ApiConfig.Url($"api/users/{Uri.EscapeDataString(name)}")).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonConvert.DeserializeObject<UserResponse>(json) ?? new UserResponse();
        }

        public static async Task<List<StoreMod>> GetUserModsAsync(string name)
        {
            using var resp = await Http.GetAsync(ApiConfig.Url($"api/users/{Uri.EscapeDataString(name)}/mods")).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonConvert.DeserializeObject<ModsResponse>(json)?.Mods ?? new List<StoreMod>();
        }

        public static async Task<CommentsResponse> GetCommentsAsync(string modId)
        {
            try
            {
                using var resp = await Http.GetAsync(ApiConfig.Url($"api/mods/{modId}/comments")).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<CommentsResponse>(json) ?? new CommentsResponse();
            }
            catch
            {
                return new CommentsResponse();
            }
        }

        public static async Task<bool> PostCommentAsync(string modId, string text)
        {
            if (!AppPrefs.Instance.IsSignedIn) return false;
            var payload = JsonConvert.SerializeObject(new { text });
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiConfig.Url($"api/mods/{modId}/comments"))
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }

        public static async Task<bool> ToggleSubscribeServerAsync(string modId, bool undo)
        {
            if (!AppPrefs.Instance.IsSignedIn) return false;
            var payload = JsonConvert.SerializeObject(new { undo });
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiConfig.Url($"api/mods/{modId}/subscribe"))
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }

        public static async Task<bool> ReportModInstalledAsync(string modId)
        {
            if (!AppPrefs.Instance.IsSignedIn || string.IsNullOrWhiteSpace(modId)) return false;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, ApiConfig.Url($"api/mods/{Uri.EscapeDataString(modId)}/installed"))
                { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public static async Task<(bool Ok, string Error)> UpdateProfileAsync(string bio, string gradA, string gradB)
        {
            if (!AppPrefs.Instance.IsSignedIn) return (false, "Sign in first.");
            var payload = JsonConvert.SerializeObject(new { bio, grad_a = gradA, grad_b = gradB });
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiConfig.Url("api/users/me/profile"))
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            dynamic result = JsonConvert.DeserializeObject(json) ?? new Newtonsoft.Json.Linq.JObject();
            return (resp.IsSuccessStatusCode, (string)result.error ?? "");
        }

        public static async Task<(bool Ok, string Error)> UploadAvatarAsync(string imagePath)
        {
            if (!AppPrefs.Instance.IsSignedIn) return (false, "Sign in first.");
            if (!File.Exists(imagePath)) return (false, "Image file not found.");
            if (new FileInfo(imagePath).Length > 500 * 1024)
            {
                return (false, "Profile pictures must be smaller than 500 KB.");
            }

            using var form = new MultipartFormDataContent();
            form.Add(new StreamContent(File.OpenRead(imagePath)), "avatar", Path.GetFileName(imagePath));
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiConfig.Url("api/users/me/avatar")) { Content = form };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            dynamic result = JsonConvert.DeserializeObject(json) ?? new Newtonsoft.Json.Linq.JObject();
            return (resp.IsSuccessStatusCode, (string)result.error ?? "");
        }

        public static async Task<(bool Ok, string Error)> CommunityUploadAsync(
            string name, string description, string version,
            string zipPath, string iconPath, string thumbnailPath, string configPath = null)
        {
            if (!AppPrefs.Instance.IsSignedIn)
            {
                return (false, "Sign in to upload.");
            }
            if (new FileInfo(zipPath).Length > 5 * 1024 * 1024)
            {
                return (false, "Mod file is larger than the 5 MB community limit.");
            }

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(name), "name");
            form.Add(new StringContent(description ?? ""), "description");
            form.Add(new StringContent(version ?? "1.0.0"), "version");
            form.Add(new StreamContent(File.OpenRead(zipPath)), "modfile", Path.GetFileName(zipPath));
            if (!string.IsNullOrEmpty(configPath))
            {
                var checkedConfig = ValidateConfigFile(configPath);
                if (!checkedConfig.Item1) return (false, checkedConfig.Item2);
                form.Add(new StreamContent(File.OpenRead(configPath)), "config", Path.GetFileName(configPath));
            }
            if (!string.IsNullOrEmpty(iconPath))
            {
                form.Add(new StreamContent(File.OpenRead(iconPath)), "icon", Path.GetFileName(iconPath));
            }
            if (!string.IsNullOrEmpty(thumbnailPath))
            {
                form.Add(new StreamContent(File.OpenRead(thumbnailPath)), "thumbnail", Path.GetFileName(thumbnailPath));
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, ApiConfig.Url("api/community/upload")) { Content = form };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            dynamic result = JsonConvert.DeserializeObject(json) ?? new Newtonsoft.Json.Linq.JObject();
            string error = result.error;
            return (resp.IsSuccessStatusCode, error ?? "");
        }

        public static async Task<(bool Ok, string Error)> OwnerEditModAsync(
            string modId, string name, string description, string version, string thumbnailPath, string configPath = null)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(name ?? ""), "name");
            form.Add(new StringContent(description ?? ""), "description");
            form.Add(new StringContent(version ?? ""), "version");
            if (!string.IsNullOrEmpty(configPath))
            {
                var checkedConfig = ValidateConfigFile(configPath);
                if (!checkedConfig.Item1) return (false, checkedConfig.Item2);
                form.Add(new StreamContent(File.OpenRead(configPath)), "config", Path.GetFileName(configPath));
            }
            if (!string.IsNullOrEmpty(thumbnailPath))
            {
                form.Add(new StreamContent(File.OpenRead(thumbnailPath)), "thumbnail", Path.GetFileName(thumbnailPath));
            }
            return await SendOwnerAsync($"api/mods/{modId}/edit", form).ConfigureAwait(false);
        }

        private const int ConfigMaxBytes = 512 * 1024;

        private static readonly HashSet<string> ConfigExts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".cfg", ".json", ".txt", ".toml", ".yaml", ".yml", ".ini" };

        public static (bool Ok, string Error) ValidateConfigFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return (false, "The config file could not be found.");
            }
            if (new FileInfo(path).Length > ConfigMaxBytes)
            {
                return (false, "Config file is larger than the 512 KB limit.");
            }
            if (!ConfigExts.Contains(Path.GetExtension(path)))
            {
                return (false, "Config file must be .cfg, .json, .txt, .toml, .yaml, .yml or .ini.");
            }
            return (true, "");
        }

        public static async Task<(bool Ok, string Error)> OwnerNewBuildAsync(string modId, string zipPath, string version)
        {
            if (new FileInfo(zipPath).Length > 5 * 1024 * 1024)
            {
                return (false, "Mod file is larger than the 5 MB community limit.");
            }
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(version ?? ""), "version");
            form.Add(new StreamContent(File.OpenRead(zipPath)), "modfile", Path.GetFileName(zipPath));
            return await SendOwnerAsync($"api/mods/{modId}/build", form).ConfigureAwait(false);
        }

        public static async Task<(bool Ok, string Error)> OwnerDeleteModAsync(string modId)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiConfig.Url($"api/mods/{modId}/delete"));
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
            try
            {
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                dynamic result = JsonConvert.DeserializeObject(json) ?? new Newtonsoft.Json.Linq.JObject();
                string error = result.error;
                return (resp.IsSuccessStatusCode, error ?? "");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private static async Task<(bool Ok, string Error)> SendOwnerAsync(string path, MultipartFormDataContent form)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiConfig.Url(path)) { Content = form };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
            try
            {
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                dynamic result = JsonConvert.DeserializeObject(json) ?? new Newtonsoft.Json.Linq.JObject();
                string error = result.error;
                return (resp.IsSuccessStatusCode, error ?? "");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public static async Task<List<CommunityChannel>> GetChannelsAsync()
        {
            try
            {
                using var resp = await Http.GetAsync(ApiConfig.Url("api/community/channels")).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<ChannelsResponse>(json)?.Channels ?? new List<CommunityChannel>();
            }
            catch { return new List<CommunityChannel>(); }
        }

        public static async Task<List<CommunityMessage>> GetChannelMessagesAsync(string channel)
        {
            try
            {
                using var resp = await Http.GetAsync(ApiConfig.Url($"api/community/messages?channel={Uri.EscapeDataString(channel)}")).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<CommunityMessagesResponse>(json)?.Messages ?? new List<CommunityMessage>();
            }
            catch { return new List<CommunityMessage>(); }
        }

        public static async Task<bool> PostChannelMessageAsync(string channel, string text, string kind, Dictionary<string, string> meta = null)
        {
            if (!AppPrefs.Instance.IsSignedIn) return false;
            var payload = JsonConvert.SerializeObject(new { channel, text, kind, meta = meta ?? new Dictionary<string, string>() });
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiConfig.Url("api/community/messages"))
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }

        public static async Task<List<FriendItem>> GetFriendsAsync()
        {
            if (!AppPrefs.Instance.IsSignedIn) return new List<FriendItem>();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, ApiConfig.Url("api/community/friends"));
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return new List<FriendItem>();
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<FriendsResponse>(json)?.Friends ?? new List<FriendItem>();
            }
            catch { return new List<FriendItem>(); }
        }

        public static async Task<List<string>> GetFriendRequestsAsync()
        {
            if (!AppPrefs.Instance.IsSignedIn) return new List<string>();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, ApiConfig.Url("api/community/friends/requests"));
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return new List<string>();
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<FriendRequestsResponse>(json)?.Requests ?? new List<string>();
            }
            catch { return new List<string>(); }
        }

        public static async Task<(bool Ok, string Error)> SendFriendRequestAsync(string username)
        {
            if (!AppPrefs.Instance.IsSignedIn) return (false, "Sign in first.");
            return await PostCommunityActionAsync("api/community/friends/request", username);
        }

        public static async Task<(bool Ok, string Error)> AcceptFriendAsync(string username)
            => await PostCommunityActionAsync("api/community/friends/accept", username);

        public static async Task<(bool Ok, string Error)> DeclineFriendAsync(string username)
            => await PostCommunityActionAsync("api/community/friends/decline", username);

        public static async Task<(bool Ok, string Error)> RemoveFriendAsync(string username)
            => await PostCommunityActionAsync("api/community/friends/remove", username);

        private static async Task<(bool Ok, string Error)> PostCommunityActionAsync(string path, string username)
        {
            var payload = JsonConvert.SerializeObject(new { username });
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiConfig.Url(path))
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
            try
            {
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                dynamic result = JsonConvert.DeserializeObject(json) ?? new Newtonsoft.Json.Linq.JObject();
                string error = result.error;
                return (resp.IsSuccessStatusCode, error ?? "");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<List<DmConversation>> GetDmsAsync()
        {
            if (!AppPrefs.Instance.IsSignedIn) return new List<DmConversation>();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, ApiConfig.Url("api/community/dms"));
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return new List<DmConversation>();
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<DmListResponse>(json)?.Dms ?? new List<DmConversation>();
            }
            catch { return new List<DmConversation>(); }
        }

        public static async Task<List<CommunityMessage>> GetDmMessagesAsync(string username)
        {
            if (!AppPrefs.Instance.IsSignedIn) return new List<CommunityMessage>();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, ApiConfig.Url($"api/community/dms/{Uri.EscapeDataString(username)}"));
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return new List<CommunityMessage>();
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<CommunityMessagesResponse>(json)?.Messages ?? new List<CommunityMessage>();
            }
            catch { return new List<CommunityMessage>(); }
        }

        public static async Task<bool> PostDmAsync(string username, string text, string kind, Dictionary<string, string> meta = null)
        {
            if (!AppPrefs.Instance.IsSignedIn) return false;
            var payload = JsonConvert.SerializeObject(new { text, kind, meta = meta ?? new Dictionary<string, string>() });
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiConfig.Url($"api/community/dms/{Uri.EscapeDataString(username)}"))
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }

        public static async Task<List<UserSearchResult>> SearchUsersAsync(string q)
        {
            try
            {
                using var resp = await Http.GetAsync(ApiConfig.Url($"api/users/search?q={Uri.EscapeDataString(q ?? "")}")).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<UserSearchResponse>(json)?.Users ?? new List<UserSearchResult>();
            }
            catch { return new List<UserSearchResult>(); }
        }

        public static async Task<List<KlipyGif>> KlipyTrendingAsync(int limit = 24)
        {
            try
            {
                using var resp = await Http.GetAsync(ApiConfig.Url($"api/klipy/trending?limit={limit}")).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var result = JsonConvert.DeserializeObject<KlipyResponse>(json);
                return (result != null && result.Ok) ? result.Results : new List<KlipyGif>();
            }
            catch { return new List<KlipyGif>(); }
        }

        public static async Task<(List<KlipyGif> Results, string Error)> KlipySearchAsync(string query, int limit = 24)
        {
            try
            {
                using var resp = await Http.GetAsync(ApiConfig.Url($"api/klipy/search?q={Uri.EscapeDataString(query ?? "")}&limit={limit}")).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var result = JsonConvert.DeserializeObject<KlipyResponse>(json);
                if (result != null && result.Ok) return (result.Results, "");
                return (new List<KlipyGif>(), result?.Error ?? "Couldn't reach the GIF service.");
            }
            catch { return (new List<KlipyGif>(), "No connection to the GIF service."); }
        }

        public static async Task<string> CreateGroupAsync(string name, IEnumerable<string> members)
        {
            if (!AppPrefs.Instance.IsSignedIn) return "";
            var payload = JsonConvert.SerializeObject(new { name, members });
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiConfig.Url("api/community/groups"))
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
            try
            {
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var result = JsonConvert.DeserializeObject<CreateGroupResponse>(json);
                return (resp.IsSuccessStatusCode && result != null && result.Ok) ? (result.GroupId ?? "") : "";
            }
            catch { return ""; }
        }

        public static async Task<List<GroupChat>> GetGroupsAsync()
        {
            if (!AppPrefs.Instance.IsSignedIn) return new List<GroupChat>();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, ApiConfig.Url("api/community/groups"));
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return new List<GroupChat>();
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<GroupListResponse>(json)?.Groups ?? new List<GroupChat>();
            }
            catch { return new List<GroupChat>(); }
        }

        public static async Task<List<CommunityMessage>> GetGroupMessagesAsync(string groupId)
        {
            if (!AppPrefs.Instance.IsSignedIn) return new List<CommunityMessage>();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, ApiConfig.Url($"api/community/groups/{Uri.EscapeDataString(groupId)}/messages"));
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return new List<CommunityMessage>();
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<GroupMessagesResponse>(json)?.Messages ?? new List<CommunityMessage>();
            }
            catch { return new List<CommunityMessage>(); }
        }

        public static async Task<bool> PostGroupMessageAsync(string groupId, string text, string kind, Dictionary<string, string> meta = null)
        {
            if (!AppPrefs.Instance.IsSignedIn) return false;
            var payload = JsonConvert.SerializeObject(new { text, kind, meta = meta ?? new Dictionary<string, string>() });
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiConfig.Url($"api/community/groups/{Uri.EscapeDataString(groupId)}/messages"))
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }

        public static async Task<(bool Ok, string Error)> AddGroupMemberAsync(string groupId, string username)
        {
            if (!AppPrefs.Instance.IsSignedIn) return (false, "Sign in first.");
            var payload = JsonConvert.SerializeObject(new { username });
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiConfig.Url($"api/community/groups/{Uri.EscapeDataString(groupId)}/members"))
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
            try
            {
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                dynamic result = JsonConvert.DeserializeObject(json) ?? new Newtonsoft.Json.Linq.JObject();
                string error = result.error;
                return (resp.IsSuccessStatusCode, error ?? "");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<bool> RenameGroupAsync(string groupId, string name)
        {
            if (!AppPrefs.Instance.IsSignedIn) return false;
            var payload = JsonConvert.SerializeObject(new { name });
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiConfig.Url($"api/community/groups/{Uri.EscapeDataString(groupId)}/rename"))
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppPrefs.Instance.SessionToken);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
    }

    public static class ImageCache
    {
        private static readonly ConcurrentDictionary<string, byte[]> MemoryCache = new ConcurrentDictionary<string, byte[]>();
        private static string _diskDir;

        private static string DiskDir
        {
            get
            {
                if (_diskDir == null)
                {
                    _diskDir = Path.Combine(AppPrefs.DataRoot, "cache");
                    Directory.CreateDirectory(_diskDir);
                }
                return _diskDir;
            }
        }

        public static async Task<ImageSource> LoadFromUrlAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;

            byte[] bytes;
            if (!MemoryCache.TryGetValue(url, out bytes))
            {
                var key = HashUrl(url);
                var diskPath = Path.Combine(DiskDir, key + ".img");
                if (!File.Exists(diskPath))
                {
                    bytes = await StoreApi.GetBytesAsync(url).ConfigureAwait(false);
                    try { File.WriteAllBytes(diskPath, bytes); } catch { }
                }
                else
                {
                    bytes = File.ReadAllBytes(diskPath);
                }
                if (bytes != null && bytes.Length > 0) MemoryCache[url] = bytes;
            }

            return FromBytes(bytes);
        }

        public static ImageSource LoadFromFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                return FromBytes(File.ReadAllBytes(path));
            }
            catch
            {
                return null;
            }
        }

        public static void CopyToDiskCache(string url, string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(url) || !MemoryCache.TryGetValue(url, out var bytes)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                File.WriteAllBytes(destinationPath, bytes);
            }
            catch { }
        }

        private static BitmapImage FromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            var image = new BitmapImage();
            using (var ms = new MemoryStream(bytes))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.StreamSource = ms;
                image.EndInit();
            }
            image.Freeze();
            return image;
        }

        private static string HashUrl(string url)
        {
            using var sha = SHA1.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
            return string.Concat(hash.Select(b => b.ToString("x2")));
        }
    }
}