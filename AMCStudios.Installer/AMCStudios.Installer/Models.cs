using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Newtonsoft.Json;

namespace AMCStudios.Installer
{
    public class ModsResponse
    {
        [JsonProperty("mods")]
        public List<StoreMod> Mods { get; set; } = new List<StoreMod>();
    }

    public class AnnouncementsResponse
    {
        [JsonProperty("announcements")]
        public List<string> Announcements { get; set; } = new List<string>();
    }

    public class ReactionResponse
    {
        [JsonProperty("ok")]
        public bool Ok { get; set; }

        [JsonProperty("likes")]
        public int Likes { get; set; }

        [JsonProperty("dislikes")]
        public int Dislikes { get; set; }
    }

    public class AuthResponse
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("error")] public string Error { get; set; } = "";
        [JsonProperty("token")] public string Token { get; set; } = "";
        [JsonProperty("user")] public PublicUser User { get; set; }
    }

    public class UserResponse
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("user")] public PublicUser User { get; set; }
    }

    public class CommentsResponse
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("comments")] public List<CommentItem> Comments { get; set; } = new List<CommentItem>();
    }

    public class VersionResponse
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("version")] public string Version { get; set; } = "";
        [JsonProperty("download_url")] public string DownloadUrl { get; set; } = "";
    }

    public class PublicUser
    {
        [JsonProperty("username")] public string Username { get; set; } = "";
        [JsonProperty("bio")] public string Bio { get; set; } = "";
        [JsonProperty("avatar_url")] public string AvatarUrl { get; set; } = "";
        [JsonProperty("grad_a")] public string GradA { get; set; } = "#7B2FFF";
        [JsonProperty("grad_b")] public string GradB { get; set; } = "#2F8DFF";
        [JsonProperty("badges")] public List<string> Badges { get; set; } = new List<string>();
        [JsonProperty("banned")] public bool Banned { get; set; }
        [JsonProperty("joined_ts")] public double JoinedTs { get; set; }
        [JsonProperty("total_downloads")] public int TotalDownloads { get; set; }

        [JsonProperty("mod_count")] public int ModCount { get; set; }
        [JsonProperty("subscriber_count")] public int SubscriberCount { get; set; }
        [JsonProperty("total_likes")] public int TotalLikes { get; set; }

        [JsonIgnore] public string Initial => string.IsNullOrEmpty(Username) ? "?" : Username.Substring(0, 1).ToUpperInvariant();
        [JsonIgnore] public string SubscriberText => $"{SubscriberCount:N0} subscriber{(SubscriberCount == 1 ? "" : "s")}";
        [JsonIgnore] public string JoinedText => JoinedTs > 0 ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(JoinedTs).ToString("MMMM yyyy") : "";

        private ImageSource _avatarImage;
        [JsonIgnore]
        public ImageSource AvatarImage
        {
            get => _avatarImage;
            set { if (!ReferenceEquals(_avatarImage, value)) { _avatarImage = value; OnPropertyChanged(nameof(AvatarImage)); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class CommentItem : INotifyPropertyChanged
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("user")] public string User { get; set; } = "";
        [JsonProperty("text")] public string Text { get; set; } = "";
        [JsonProperty("ts")] public double Ts { get; set; }
        [JsonProperty("badges")] public List<string> Badges { get; set; } = new List<string>();
        [JsonProperty("avatar_url")] public string AvatarUrl { get; set; } = "";

        private ImageSource _avatarImage;
        [JsonIgnore]
        public ImageSource AvatarImage
        {
            get => _avatarImage;
            set { if (!ReferenceEquals(_avatarImage, value)) { _avatarImage = value; OnPropertyChanged(nameof(AvatarImage)); } }
        }

        [JsonIgnore] public string TimeText
        {
            get
            {
                var d = DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Ts);
                if (d.TotalMinutes < 1) return "just now";
                if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
                if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
                return $"{(int)d.TotalDays}d ago";
            }
        }

        [JsonIgnore] public bool HasBadge => Badges.Count > 0;
        [JsonIgnore] public string BadgeIcon
        {
            get
            {
                if (Badges.Contains("ty")) return "💗";
                if (Badges.Contains("owner")) return "⭐";
                if (Badges.Contains("official")) return "✅";
                if (Badges.Contains("popular")) return "✔";
                return "";
            }
        }
        [JsonIgnore] public string TierLabel
        {
            get
            {
                if (Badges.Contains("ty")) return "TY";
                if (Badges.Contains("owner")) return "OWNER";
                if (Badges.Contains("official")) return "OFFICIAL";
                if (Badges.Contains("popular")) return "POPULAR";
                return "";
            }
        }
        [JsonIgnore] public System.Windows.Media.Brush TierBrush
        {
            get
            {
                if (Badges.Contains("ty")) return TyBrush;
                if (Badges.Contains("owner")) return OwnerBrush;
                if (Badges.Contains("official")) return OfficialBrush;
                return PopularBrush;
            }
        }

        private static readonly System.Windows.Media.Brush OwnerBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00));
        private static readonly System.Windows.Media.Brush OfficialBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0xB5, 0xFF));
        private static readonly System.Windows.Media.Brush PopularBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE6, 0xE6, 0xE6));
        private static readonly System.Windows.Media.Brush TyBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x5C, 0xA0));

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class StoreMod : INotifyPropertyChanged
    {
        private string _id = "";
        private string _name = "";
        private string _creator = "";
        private string _uploader = "";
        private bool _verified = true;
        private string _description = "";
        private string _version = "1.0.0";
        private string _thumbnailUrl = "";
        private string _iconUrl = "";
        private string _downloadUrl = "";
        private long _sizeBytes;
        private int _likes;
        private int _dislikes;
        private int _downloads;
        private int _views;
        private int _subscribers;
        private int _commentCount;
        private string _uploadedAt = "";

        [JsonProperty("id")] public string Id { get => _id; set => Set(ref _id, value); }
        [JsonProperty("name")] public string Name { get => _name; set => Set(ref _name, value); }
        [JsonProperty("creator")] public string Creator { get => _creator; set => Set(ref _creator, value); }
        [JsonProperty("uploader")] public string Uploader { get => _uploader; set => Set(ref _uploader, value); }

        [JsonProperty("verified")]
        public bool Verified { get => _verified; set => Set(ref _verified, value); }

        [JsonProperty("description")] public string Description { get => _description; set => Set(ref _description, value); }
        [JsonProperty("version")] public string Version { get => _version; set => Set(ref _version, value); }

        [JsonProperty("thumbnail_url")]
        public string ThumbnailUrl { get => _thumbnailUrl; set => Set(ref _thumbnailUrl, value); }

        [JsonProperty("icon_url")]
        public string IconUrl { get => _iconUrl; set => Set(ref _iconUrl, value); }

        [JsonProperty("download_url")]
        public string DownloadUrl { get => _downloadUrl; set => Set(ref _downloadUrl, value); }

        private string _configUrl = "";
        private string _configName = "";

        [JsonProperty("config_url")]
        public string ConfigUrl { get => _configUrl; set => Set(ref _configUrl, value); }

        [JsonProperty("config_name")]
        public string ConfigName { get => _configName; set => Set(ref _configName, value); }

        [JsonProperty("size_bytes")]
        public long SizeBytes { get => _sizeBytes; set => Set(ref _sizeBytes, value); }

        [JsonProperty("likes")]
        public int Likes { get => _likes; set => Set(ref _likes, value); }

        [JsonProperty("dislikes")]
        public int Dislikes { get => _dislikes; set => Set(ref _dislikes, value); }

        [JsonProperty("downloads")]
        public int Downloads { get => _downloads; set => Set(ref _downloads, value); }

        [JsonProperty("views")]
        public int Views { get => _views; set => Set(ref _views, value); }

        [JsonProperty("subscribers")]
        public int Subscribers { get => _subscribers; set => Set(ref _subscribers, value); }

        [JsonProperty("comment_count")]
        public int CommentCount { get => _commentCount; set => Set(ref _commentCount, value); }

        [JsonIgnore] public string VerifiedText => _verified ? "OFFICIAL" : "UNVERIFIED";

        [JsonProperty("uploaded_at")]
        public string UploadedAt { get => _uploadedAt; set => Set(ref _uploadedAt, value); }

        private ImageSource _thumbnailImage;
        private ImageSource _iconImage;

        [JsonIgnore]
        public ImageSource ThumbnailImage
        {
            get => _thumbnailImage;
            set { if (!ReferenceEquals(_thumbnailImage, value)) { _thumbnailImage = value; Raise(nameof(ThumbnailImage)); } }
        }

        [JsonIgnore]
        public ImageSource IconImage
        {
            get => _iconImage;
            set { if (!ReferenceEquals(_iconImage, value)) { _iconImage = value; Raise(nameof(IconImage)); } }
        }

        private bool _liked;
        private bool _disliked;
        private bool _subscribed;

        [JsonIgnore]
        public bool Liked
        {
            get => _liked;
            set
            {
                if (_liked == value) return;
                _liked = value;
                if (value && _disliked)
                {
                    _disliked = false;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DislikeText)));
                }
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LikeText)));
            }
        }

        [JsonIgnore]
        public bool Disliked
        {
            get => _disliked;
            set
            {
                if (_disliked == value) return;
                _disliked = value;
                if (value && _liked)
                {
                    _liked = false;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LikeText)));
                }
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DislikeText)));
            }
        }

        [JsonIgnore]
        public bool Subscribed
        {
            get => _subscribed;
            set
            {
                if (_subscribed == value) return;
                _subscribed = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SubscribedText)));
            }
        }

        [JsonIgnore] public bool HasConfig => !string.IsNullOrWhiteSpace(ConfigUrl);

        [JsonIgnore] public string ConfigDisplayName =>
            !string.IsNullOrWhiteSpace(ConfigName) ? ConfigName
            : (!string.IsNullOrWhiteSpace(ConfigUrl) ? ConfigUrl.Substring(ConfigUrl.LastIndexOf('/') + 1) : "");

        [JsonIgnore] public string LikeText => _liked ? $"✔ {Likes}" : $"👍 {Likes}";
        [JsonIgnore] public string DislikeText => _disliked ? $"✖ {Dislikes}" : $"👎 {Dislikes}";
        [JsonIgnore] public string DownloadsText => $"⬇ {Downloads:N0}";
        [JsonIgnore] public string ViewsText => $"👁 {Views:N0}";
        [JsonIgnore] public string SubscribersText => $"{Subscribers:N0} subscriber{(Subscribers == 1 ? "" : "s")}";
        [JsonIgnore] public string SubscribedText => _subscribed ? "★ SUBSCRIBED" : "☆ SUBSCRIBE";
        [JsonIgnore] public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant();

        [JsonIgnore] public string UploadedAtText
        {
            get
            {
                if (DateTime.TryParse(UploadedAt, out var dt))
                {
                    return dt.ToString("MMM d, yyyy");
                }
                return UploadedAt;
            }
        }

        [JsonIgnore] public string SizeText => FormatSize(SizeBytes);

        public static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "";
            double mb = bytes / 1048576.0;
            return mb >= 1 ? $"{mb:0.#} MB" : $"{bytes / 1024.0:0} KB";
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            if (name != null) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (!Equals(field, value))
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
                switch (name)
                {
                    case nameof(Likes): PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LikeText))); break;
                    case nameof(Dislikes): PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DislikeText))); break;
                    case nameof(Downloads): PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DownloadsText))); break;
                    case nameof(Views): PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ViewsText))); break;
                    case nameof(Subscribers): PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SubscribersText))); break;
                    case nameof(Name):
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Initial)));
                        break;
                }
            }
        }
    }

    public class InstalledMod : INotifyPropertyChanged
    {
        private string _id = "";
        private string _name = "";
        private string _creator = "";
        private string _version = "";
        private string _iconPath = "";
        private DateTime _installedAtUtc;
        private string _installFolder = "";
        private long _sizeBytes;
        private string _configFileName = "";

        public string Id { get => _id; set => Set(ref _id, value); }
        public string Name { get => _name; set => Set(ref _name, value); }
        public string Creator { get => _creator; set => Set(ref _creator, value); }
        public string Version { get => _version; set => Set(ref _version, value); }
        public string IconPath { get => _iconPath; set { if (Set(ref _iconPath, value)) Raise(nameof(IconImage)); } }
        public DateTime InstalledAtUtc { get => _installedAtUtc; set { if (Set(ref _installedAtUtc, value)) Raise(nameof(InstalledAtText)); } }
        public string InstallFolder { get => _installFolder; set => Set(ref _installFolder, value); }
        public long SizeBytes { get => _sizeBytes; set { if (Set(ref _sizeBytes, value)) Raise(nameof(SizeText)); } }
        public string ConfigFileName { get => _configFileName; set => Set(ref _configFileName, value); }

        [JsonIgnore] public ImageSource IconImage => ImageCache.LoadFromFile(IconPath);
        [JsonIgnore] public string InstalledAtText => InstalledAtUtc.ToLocalTime().ToString("MMM d, yyyy");
        [JsonIgnore] public string SizeText => StoreMod.FormatSize(SizeBytes);

        public event PropertyChangedEventHandler PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        private void Raise(string name)
        {
            if (name != null) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public class CommunityChannel
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("icon")] public string Icon { get; set; } = "";
        [JsonProperty("description")] public string Description { get; set; } = "";
    }

    public class ChannelsResponse
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("channels")] public List<CommunityChannel> Channels { get; set; } = new List<CommunityChannel>();
    }

    public class CommunityMessage : INotifyPropertyChanged
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("user")] public string User { get; set; } = "";
        [JsonProperty("badges")] public List<string> Badges { get; set; } = new List<string>();
        [JsonProperty("avatar_url")] public string AvatarUrl { get; set; } = "";
        [JsonProperty("text")] public string Text { get; set; } = "";
        [JsonProperty("kind")] public string Kind { get; set; } = "text";
        [JsonProperty("meta")] public Dictionary<string, string> Meta { get; set; } = new Dictionary<string, string>();
        [JsonProperty("ts")] public double Ts { get; set; }
        [JsonProperty("channel")] public string Channel { get; set; } = "";

        [JsonIgnore] public bool IsMine => User.Equals(AppPrefs.Instance.Username, StringComparison.OrdinalIgnoreCase);
        [JsonIgnore] public bool IsGif => Kind == "gif";
        [JsonIgnore] public bool IsMedia => Kind == "media";
        [JsonIgnore] public bool HasMedia => IsGif || IsMedia;
        [JsonIgnore] public string GifUrl => IsGif ? (Meta.TryGetValue("gif_url", out var u) ? u : "") : "";
        [JsonIgnore] public Uri GifUri
        {
            get
            {
                var url = IsGif ? (Meta.TryGetValue("gif_url", out var u) ? u : "")
                                : (Meta.TryGetValue("media_url", out var m) ? m : "");
                return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
            }
        }
        [JsonIgnore] public string Initial => string.IsNullOrEmpty(User) ? "?" : User.Substring(0, 1).ToUpperInvariant();

        private ImageSource _avatarImage;
        [JsonIgnore]
        public ImageSource AvatarImage
        {
            get => _avatarImage;
            set { if (!ReferenceEquals(_avatarImage, value)) { _avatarImage = value; OnPropertyChanged(nameof(AvatarImage)); } }
        }

        private ImageSource _gifImage;
        [JsonIgnore]
        public ImageSource GifImage
        {
            get => _gifImage;
            set { if (!ReferenceEquals(_gifImage, value)) { _gifImage = value; OnPropertyChanged(nameof(GifImage)); } }
        }

        [JsonIgnore] public string TimeText
        {
            get
            {
                var d = DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Ts);
                if (d.TotalMinutes < 1) return "just now";
                if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
                if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
                return $"{(int)d.TotalDays}d ago";
            }
        }

        [JsonIgnore] public bool HasBadge => Badges.Count > 0;
        [JsonIgnore] public string BadgeIcon
        {
            get
            {
                if (Badges.Contains("ty")) return "💗";
                if (Badges.Contains("owner")) return "⭐";
                if (Badges.Contains("official")) return "✅";
                if (Badges.Contains("popular")) return "✔";
                return "";
            }
        }
        [JsonIgnore] public string TierLabel
        {
            get
            {
                if (Badges.Contains("ty")) return "TY";
                if (Badges.Contains("owner")) return "OWNER";
                if (Badges.Contains("official")) return "OFFICIAL";
                if (Badges.Contains("popular")) return "POPULAR";
                return "";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class CommunityMessagesResponse
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("messages")] public List<CommunityMessage> Messages { get; set; } = new List<CommunityMessage>();
    }

    public class CommunityActionResponse
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("error")] public string Error { get; set; } = "";
    }

    public class FriendItem : INotifyPropertyChanged
    {
        [JsonProperty("user")] public string User { get; set; } = "";
        [JsonProperty("badges")] public List<string> Badges { get; set; } = new List<string>();
        [JsonProperty("avatar_url")] public string AvatarUrl { get; set; } = "";
        [JsonProperty("online")] public bool Online { get; set; }

        [JsonIgnore] public string Initial => string.IsNullOrEmpty(User) ? "?" : User.Substring(0, 1).ToUpperInvariant();

        private ImageSource _avatarImage;
        [JsonIgnore]
        public ImageSource AvatarImage
        {
            get => _avatarImage;
            set { if (!ReferenceEquals(_avatarImage, value)) { _avatarImage = value; OnPropertyChanged(nameof(AvatarImage)); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class FriendsResponse
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("friends")] public List<FriendItem> Friends { get; set; } = new List<FriendItem>();
    }

    public class SelectableFriend : INotifyPropertyChanged
    {
        [JsonProperty("user")] public string User { get; set; } = "";
        [JsonIgnore] public bool Selected { get; set; }
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class FriendRequestsResponse
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("requests")] public List<string> Requests { get; set; } = new List<string>();
    }

    public class DmConversation : INotifyPropertyChanged
    {
        [JsonProperty("user")] public string User { get; set; } = "";
        [JsonProperty("badges")] public List<string> Badges { get; set; } = new List<string>();
        [JsonProperty("avatar_url")] public string AvatarUrl { get; set; } = "";
        [JsonProperty("last_text")] public string LastText { get; set; } = "";
        [JsonProperty("last_ts")] public double LastTs { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; } = "text";
        [JsonProperty("is_group")] public bool IsGroup { get; set; }
        [JsonProperty("group_id")] public string GroupId { get; set; } = "";
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("member_count")] public int MemberCount { get; set; }

        [JsonIgnore] public string Initial => IsGroup
            ? (string.IsNullOrEmpty(Name) ? "#" : Name.Substring(0, 1).ToUpperInvariant())
            : (string.IsNullOrEmpty(User) ? "?" : User.Substring(0, 1).ToUpperInvariant());
        [JsonIgnore] public string LastPreview => Kind == "gif" ? "[GIF]" : Kind == "media" ? "[IMAGE]" : LastText;
        [JsonIgnore] public string DisplayName => IsGroup ? Name : ("@" + User);
        [JsonIgnore] public string SubLabel => IsGroup
            ? (MemberCount + (MemberCount == 1 ? " member" : " members"))
            : "Direct message";

        private ImageSource _avatarImage;
        [JsonIgnore]
        public ImageSource AvatarImage
        {
            get => _avatarImage;
            set { if (!ReferenceEquals(_avatarImage, value)) { _avatarImage = value; OnPropertyChanged(nameof(AvatarImage)); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class DmListResponse
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("dms")] public List<DmConversation> Dms { get; set; } = new List<DmConversation>();
    }

    public class GroupChat : INotifyPropertyChanged
    {
        [JsonProperty("group_id")] public string GroupId { get; set; } = "";
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("members")] public List<string> Members { get; set; } = new List<string>();
        [JsonProperty("member_count")] public int MemberCount { get; set; }
        [JsonProperty("is_group")] public bool IsGroup { get; set; }
        [JsonProperty("created_by")] public string CreatedBy { get; set; } = "";
        [JsonProperty("created_at")] public double CreatedAt { get; set; }
        [JsonProperty("last_text")] public string LastText { get; set; } = "";
        [JsonProperty("last_sender")] public string LastSender { get; set; } = "";
        [JsonProperty("last_ts")] public double LastTs { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; } = "text";

        [JsonIgnore] public string Initial => string.IsNullOrEmpty(Name) ? "#" : Name.Substring(0, 1).ToUpperInvariant();
        [JsonIgnore] public string LastPreview => Kind == "gif" ? "[GIF]" : Kind == "media" ? "[IMAGE]" : LastText;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class GroupListResponse
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("groups")] public List<GroupChat> Groups { get; set; } = new List<GroupChat>();
    }

    public class GroupMessagesResponse
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("group")] public GroupChat Group { get; set; }
        [JsonProperty("messages")] public List<CommunityMessage> Messages { get; set; } = new List<CommunityMessage>();
    }

    public class CreateGroupResponse
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("group_id")] public string GroupId { get; set; } = "";
        [JsonProperty("error")] public string Error { get; set; } = "";
    }

    public class UserSearchResult
    {
        [JsonProperty("user")] public string User { get; set; } = "";
        [JsonProperty("badges")] public List<string> Badges { get; set; } = new List<string>();
        [JsonProperty("avatar_url")] public string AvatarUrl { get; set; } = "";
        [JsonProperty("bio")] public string Bio { get; set; } = "";
    }

    public class UserSearchResponse
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("users")] public List<UserSearchResult> Users { get; set; } = new List<UserSearchResult>();
    }

    public class ChangelogVersion
    {
        [JsonProperty("version")] public string Version { get; set; } = "";
        [JsonProperty("date")] public string Date { get; set; } = "";
        [JsonProperty("title")] public string Title { get; set; } = "";
        [JsonProperty("notes")] public List<string> Notes { get; set; } = new List<string>();

        [JsonIgnore] public string DateText => string.IsNullOrEmpty(Date) ? "" : ("\u2022  released " + Date);
        [JsonIgnore] public List<string> Lines => Notes ?? new List<string>();
    }

    public class ChangelogResponse
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("versions")] public List<ChangelogVersion> Versions { get; set; } = new List<ChangelogVersion>();
    }

    public class NotificationItem
    {
        [JsonProperty("type")] public string Type { get; set; } = "";
        [JsonProperty("ts")] public double Ts { get; set; }
        [JsonProperty("title")] public string Title { get; set; } = "";
        [JsonProperty("text")] public string Text { get; set; } = "";
        [JsonProperty("text_preview")] public string TextPreview { get; set; } = "";
        [JsonProperty("from")] public string From { get; set; } = "";
        [JsonProperty("version")] public string Version { get; set; } = "";
        [JsonProperty("download_url")] public string DownloadUrl { get; set; } = "";
        [JsonProperty("url")] public string Url { get; set; } = "";
        [JsonProperty("mod_id")] public string ModId { get; set; } = "";
        [JsonProperty("mod_name")] public string ModName { get; set; } = "";
    }

    public class NotificationsResponse
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("server_time")] public double ServerTime { get; set; }
        [JsonProperty("notifications")] public List<NotificationItem> Notifications { get; set; } = new List<NotificationItem>();
    }

    public class KlipyFormat
    {
        [JsonProperty("url")] public string Url { get; set; } = "";
        [JsonProperty("dims")] public int[] Dims { get; set; } = new int[0];

        [JsonIgnore] public int Width => Dims != null && Dims.Length >= 1 ? Dims[0] : 0;
        [JsonIgnore] public int Height => Dims != null && Dims.Length >= 2 ? Dims[1] : 0;
    }

    public class KlipyGif
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("title")] public string Title { get; set; } = "";
        [JsonProperty("formats")] public Dictionary<string, KlipyFormat> Formats { get; set; } = new Dictionary<string, KlipyFormat>();

        public string PickUrl(params string[] keys)
        {
            if (Formats != null)
            {
                foreach (var key in keys)
                {
                    if (Formats.TryGetValue(key, out var f) && f != null)
                    {
                        var url = f.Url ?? "";
                        if (!string.IsNullOrWhiteSpace(url)) return url;
                    }
                }
            }
            return "";
        }

        [JsonIgnore] public Uri PreviewUri
        {
            get
            {
                var url = PickUrl("previewgif", "tinygif", "preview");
                return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
            }
        }

        [JsonIgnore] public string SendUrl => PickUrl("mediumgif", "gif");
        [JsonIgnore] public int PreviewWidth
        {
            get
            {
                foreach (var key in new[] { "previewgif", "tinygif", "preview" })
                {
                    if (Formats.TryGetValue(key, out var f) && f != null && f.Width > 0) return f.Width;
                }
                return 320;
            }
        }
        [JsonIgnore] public int PreviewHeight
        {
            get
            {
                foreach (var key in new[] { "previewgif", "tinygif", "preview" })
                {
                    if (Formats.TryGetValue(key, out var f) && f != null && f.Height > 0) return f.Height;
                }
                return 240;
            }
        }
    }

    public class KlipyResponse
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("results")] public List<KlipyGif> Results { get; set; } = new List<KlipyGif>();
        [JsonProperty("error")] public string Error { get; set; } = "";
    }
}
