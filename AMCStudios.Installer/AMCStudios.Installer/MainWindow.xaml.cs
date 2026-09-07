using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AMCStudios.Installer
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private AppPrefs _prefs = AppPrefs.Instance;

        private bool _introFinished;
        private DispatcherTimer _introFallbackTimer;
        private DispatcherTimer _searchDebounce;
        private DispatcherTimer _heartbeatTimer;

        private readonly Dictionary<string, (PublicUser User, DateTime Fetched)> _userHoverCache =
            new Dictionary<string, (PublicUser, DateTime)>();
        private DispatcherTimer _userHoverTimer;
        private string _hoverUserName = "";

        private CancellationTokenSource _storeCts;
        private CancellationTokenSource _downloadCts;
        private CancellationTokenSource _badgeCts;

        private Grid _pageBeforeProgress;
        private string _busyInstallId;
        private bool _storeEverLoaded;

        private static readonly string[] SortKeys = { "popular", "recent", "rating", "downloads" };

        private string _currentGamePath = "Game folder not set.";
        public string CurrentGamePath
        {
            get => _currentGamePath;
            set { _currentGamePath = value; OnPropertyChanged(); }
        }

        public ObservableCollection<InstalledModRow> InstalledView { get; } = new ObservableCollection<InstalledModRow>();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            StoreList.ItemsSource = new ObservableCollection<StoreMod>();
            ModsList.ItemsSource = InstalledView;

            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _searchDebounce.Tick += (s, e) => { _searchDebounce.Stop(); _ = LoadStoreAsync(); };

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Escape) return;

                if (DialogOverlay.Visibility == Visibility.Visible) return;
                if (FindUserOverlay.Visibility == Visibility.Visible) { FindUserOverlay.Visibility = Visibility.Collapsed; e.Handled = true; return; }
                if (EditProfileOverlay.Visibility == Visibility.Visible) { EditProfileOverlay.Visibility = Visibility.Collapsed; e.Handled = true; return; }
                if (UploadOverlay.Visibility == Visibility.Visible) { CloseUpload(); e.Handled = true; return; }
                if (AccountOverlay.Visibility == Visibility.Visible) { AccountOverlay.Visibility = Visibility.Collapsed; e.Handled = true; return; }
                if (PortfolioOverlay.Visibility == Visibility.Visible) { PortfolioOverlay.Visibility = Visibility.Collapsed; e.Handled = true; return; }
                if (ChangelogOverlay.Visibility == Visibility.Visible) { ChangelogOverlay.Visibility = Visibility.Collapsed; e.Handled = true; return; }
                if (CommentEmojiPopup.IsOpen) { CommentEmojiPopup.IsOpen = false; e.Handled = true; return; }
                if (DetailOverlay.Visibility == Visibility.Visible)
                {
                    CloseModDetail();
                    e.Handled = true;
                }
            };

            _introFallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
            _introFallbackTimer.Tick += (s, e) => FinishIntro();
            _introFallbackTimer.Start();
        }

        private void IntroStoryboard_Completed(object sender, EventArgs e) => FinishIntro();

        private void FinishIntro()
        {
            if (_introFinished) return;
            _introFinished = true;
            try { _introFallbackTimer?.Stop(); } catch { }
            try
            {
                StartupAnimationPage.Visibility = Visibility.Collapsed;
                MainAppGrid.Visibility = Visibility.Visible;
                TopNav.IsEnabled = false;
                NavigateTo(DisclaimerPage, animate: false);
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                try { MainAppGrid.Visibility = Visibility.Visible; NavigateTo(HomePage, false); } catch { }
            }
        }

        private async void DisclaimerAccept_Click(object sender, RoutedEventArgs e)
        {
            TopNav.IsEnabled = true;
            await RunInitAsync();
        }

        private async Task RunInitAsync()
        {
            NavigateTo(LoadingPage);
            try
            {

                string justUpdated = UpdateManager.ConsumeUpdatedFlag();
                if (justUpdated != null) NotifierBridge.SetUpdateState("idle", "");

                StatusText.Text = "Setting things up...";
                SubStatusText.Text = "Locating Gorilla Tag...";

                if (string.IsNullOrWhiteSpace(_prefs.BepInExPluginsPath) || !Directory.Exists(_prefs.BepInExPluginsPath))
                {
                    var gtagPath = await FindGorillaTagPathAsync();
                    if (string.IsNullOrEmpty(gtagPath))
                    {
                        NavigateTo(GameNotFoundPage);
                        return;
                    }

                    var pluginsPath = Path.Combine(gtagPath, "BepInEx", "plugins");
                    if (!Directory.Exists(pluginsPath))
                    {
                        if (!BepInExInstaller.IsInstalled(gtagPath))
                        {
                            StatusText.Text = "Setting up BepInEx...";
                            SubStatusText.Text = "Downloading the mod loader...";
                            var ok = await AutoInstallBepInExAsync(gtagPath, pluginsPath);
                            if (!ok)
                            {
                                NavigateTo(GameNotFoundPage);
                                return;
                            }
                        }
                        else
                        {
                            Directory.CreateDirectory(pluginsPath);
                        }
                    }

                    _prefs.GorillaTagPath = gtagPath;
                    _prefs.BepInExPluginsPath = pluginsPath;
                    _prefs.Save();
                }

                CurrentGamePath = $"Playing from: {_prefs.GorillaTagPath}";

                StatusText.Text = "Connecting to the AMC Store...";
                SubStatusText.Text = "This only takes a second...";

                bool pingOk = await StoreApi.PingAsync();
                if (!pingOk)
                {
                    Toast("Store offline", "Can't reach the AMC Store right now. Your installed mods still work.", ToastType.Warn);
                }
                else
                {
                    StoreApi.Track("launch");
                    if (_heartbeatTimer == null)
                    {
                        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
                        _heartbeatTimer.Tick += (s, e) => StoreApi.Track("ping");
                    }
                    _heartbeatTimer.Start();
                }

                UpdateAccountChipGuest();
                _ = RestoreSessionAsync();

                NotifierBridge.Update();
                NotifierBridge.EnsureNotifierRunning();


                _ = Task.Run(NotifierBridge.FlashTestNotification);

                if (pingOk)
                {
                    StatusText.Text = "Checking for updates...";
                    SubStatusText.Text = "Making sure you have the latest build...";
                    var update = await UpdateManager.CheckForUpdateAsync().ConfigureAwait(true);
                    if (update != null && !IsUpdateInProgress())
                    {
                        var started = await RunAutoUpdateAsync(update).ConfigureAwait(true);
                        if (started) return;
                    }
                }

                StatusText.Text = "Fetching announcements...";
                await LoadAnnouncementsAsync(reportErrors: false);

                NavigateTo(HomePage);

                _ = CheckChangelogBadgeAsync();

                if (justUpdated != null)
                {
                    Toast($"Updated \uD83C\uDF89",
                        $"Welcome to AMC Store v{justUpdated} - you're on the latest build.",
                        ToastType.Success);
                    await ShowChangelogAsync();
                }

                _ = SyncSubscriptionsAsync();
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                var retry = await ShowDialogAsync("Startup problem", ex.Message, buttons: DialogButtons.RetryExit);
                if (retry == true)
                {
                    await RunInitAsync();
                }
                else
                {
                    NavigateTo(HomePage);
                }
            }
        }

        private void NavigateTo(Grid page, bool animate = true)
        {
            foreach (UIElement child in PageContainer.Children)
            {
                if (child is Grid g && !ReferenceEquals(g, page)) g.Visibility = Visibility.Collapsed;
            }
            if (!ReferenceEquals(page, CommunityPage))
            {
                StopChannelPolling();
                StopDmPolling();
            }
            if (_pageBeforeProgress != null && !ReferenceEquals(page, ProgressPage)) _pageBeforeProgress = null;
            if (!ReferenceEquals(page, ProgressPage)) _pageBeforeProgress = page;

            page.Visibility = Visibility.Visible;

            if (animate)
            {
                try
                {
                    var fadeIn = (Storyboard)FindResource("FadeIn");
                    Storyboard.SetTarget(fadeIn, page);
                    fadeIn.Begin(this, true);
                }
                catch { }
            }
        }

        private void SelectNav(ToggleButton active)
        {
            HomeNavButton.IsChecked = ReferenceEquals(active, HomeNavButton);
            StoreNavButton.IsChecked = ReferenceEquals(active, StoreNavButton);
            ModsNavButton.IsChecked = ReferenceEquals(active, ModsNavButton);
            CommunityNavButton.IsChecked = ReferenceEquals(active, CommunityNavButton);
        }

        private void GoToHome_Click(object sender, RoutedEventArgs e)
        {
            SelectNav(HomeNavButton);
            NavigateTo(HomePage);
        }

        private void GoToStore_Click(object sender, RoutedEventArgs e)
        {
            SelectNav(StoreNavButton);
            NavigateTo(StorePage);
            if (!_storeEverLoaded) _ = LoadStoreAsync();
            SearchBox.Focus();
        }

        private void GoToMods_Click(object sender, RoutedEventArgs e)
        {
            SelectNav(ModsNavButton);
            RefreshModsView();
            NavigateTo(ModsPage);
            _ = CheckUpdateBadgesAsync();
        }

        private void GoToCommunity_Click(object sender, RoutedEventArgs e)
        {
            SelectNav(CommunityNavButton);
            NavigateTo(CommunityPage);
            InitializeCommunityAsync();
        }

        private bool _communityInitialized;
        private bool _communityChannelTab = true;
        private string _currentChannel = "general";
        private string _currentDmUser;
        private string _currentGroupId;
        private string _currentGroupName;
        private bool _activeIsGroup;
        private string _findUserMode = "friend";
        private readonly List<string> _communityFriendsCache = new List<string>();
        private DispatcherTimer _channelPollTimer;
        private DispatcherTimer _dmPollTimer;
        private bool _channelPolling;
        private bool _dmPolling;
        private readonly ObservableCollection<CommunityMessage> _channelMessages = new ObservableCollection<CommunityMessage>();

        private static readonly string[] EmojiSet =
        {
            "😀","😁","😂","🤣","😊","😍","🥰","😎","🤩","🥳",
            "😅","😆","😉","🙂","😋","😜","🤪","😇","🤗","🤔",
            "👍","👎","👏","🙌","🙏","💪","🤝","✌️","👌","🤞",
            "❤️","💜","💙","💚","💛","🧡","💯","🔥","✨","⭐",
            "🎉","🎊","🎮","🕹️","👾","🤖","🐵","🧠","💀","👻",
            "😢","😭","😤","😡","🤬","🥺","😳","😱","🤯","😴",
            "🏆","🥇","🥈","🥉","⚽","🏀","🎯","🚀","🛸","⚡",
            "🌮","🍕","🍔","☕","🍺","🥤","💧","🌈","☀️","🌙",
            "❓","❕","❗","✅","❌","⚠️","💬","📢","🔔","🎵"
        };

        private async void InitializeCommunityAsync()
        {
            if (!_communityInitialized)
            {
                _communityInitialized = true;
                _searchDebounce?.Stop();
                await LoadCommunityChannelsAsync();
                _ = LoadCommunityFriendsAsync();
                _ = LoadCommunityDmsAsync();
            }
            ShowCommunitySidebar(_communityChannelTab);
            StartCommunityPolling();
        }

        private async Task LoadCommunityChannelsAsync()
        {
            var channels = await StoreApi.GetChannelsAsync();
            if (channels.Count == 0)
            {
                channels = new List<CommunityChannel>
                {
                    new CommunityChannel { Id = "general", Name = "General", Icon = "💬", Description = "Talk about anything." },
                    new CommunityChannel { Id = "media", Name = "Media", Icon = "🎬", Description = "Share clips, GIFs and links." },
                    new CommunityChannel { Id = "mods", Name = "Mod Sharing", Icon = "📦", Description = "Show off mods and share links." }
                };
            }
            CommunityChannelList.ItemsSource = channels;
            var current = channels.FirstOrDefault(c => c.Id == _currentChannel) ?? channels[0];
            _currentChannel = current.Id;
            SetCommunityChannelHeader(current);
            await LoadChannelMessagesAsync(_currentChannel);
        }

        private void SetCommunityChannelHeader(CommunityChannel c)
        {
            CommunityChatTitle.Text = c.Name;
            CommunityChatDesc.Text = c.Description;
            CommunityChatTypeBadge.Text = "PUBLIC CHANNEL";
            _currentDmUser = null;
            _currentGroupId = null;
            _currentGroupName = null;
            _activeIsGroup = false;
            CommunityGroupActions.Visibility = Visibility.Collapsed;
            CommunityMessagePlaceholder.Text = $"Message #{c.Name.ToLowerInvariant()}  (#channel  or paste a GIF/image link)";
        }

        private void ShowCommunitySidebar(bool channelsTab)
        {
            _communityChannelTab = channelsTab;
            CommunityChannelsView.Visibility = channelsTab ? Visibility.Visible : Visibility.Collapsed;
            CommunityFriendsView.Visibility = !channelsTab && _communityFriendsTab ? Visibility.Visible : Visibility.Collapsed;
            CommunityDmsView.Visibility = !channelsTab && !_communityFriendsTab ? Visibility.Visible : Visibility.Collapsed;

            ChannelsTabButton.Background = channelsTab ? FindResource("AccentGradientBrush") as System.Windows.Media.Brush : null;
            ChannelsTabButton.Foreground = channelsTab ? System.Windows.Media.Brushes.White : (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            FriendsTabButton.Background = _communityFriendsTab ? FindResource("AccentGradientBrush") as System.Windows.Media.Brush : null;
            FriendsTabButton.Foreground = _communityFriendsTab ? System.Windows.Media.Brushes.White : (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            DmsTabButton.Background = (!channelsTab && !_communityFriendsTab) ? FindResource("AccentGradientBrush") as System.Windows.Media.Brush : null;
            DmsTabButton.Foreground = (!channelsTab && !_communityFriendsTab) ? System.Windows.Media.Brushes.White : (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        }

        private bool _communityFriendsTab;

        private void CommunityChannelsTab_Click(object sender, RoutedEventArgs e)
        {
            _communityFriendsTab = false;
            _dmsTabRequested = false;
            ShowCommunitySidebar(true);
        }

        private void CommunityFriendsTab_Click(object sender, RoutedEventArgs e)
        {
            _communityFriendsTab = true;
            _dmsTabRequested = false;
            ShowCommunitySidebar(false);
            _ = LoadCommunityFriendsAsync();
        }

        private void CommunityDmsTab_Click(object sender, RoutedEventArgs e)
        {
            _communityFriendsTab = false;
            _dmsTabRequested = true;
            ShowCommunitySidebar(false);
            _ = LoadCommunityDmsAsync();
        }

        private async void CommunityChannel_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is CommunityChannel c)
            {
                _currentChannel = c.Id;
                _currentDmUser = null;
                _currentGroupId = null;
                _currentGroupName = null;
                _activeIsGroup = false;
                StopDmPolling();
                SetCommunityChannelHeader(c);
                await LoadChannelMessagesAsync(c.Id);
                StartChannelPolling();
            }
        }

        private void StartCommunityPolling()
        {
            if (_currentDmUser != null || _activeIsGroup) StartDmPolling();
            else StartChannelPolling();
        }

        private void StartChannelPolling()
        {
            if (_channelPollTimer != null) return;
            _channelPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _channelPollTimer.Tick += async (s, e) => await PollChannelOnceAsync();
            _channelPollTimer.Start();
        }

        private void StopChannelPolling()
        {
            _channelPollTimer?.Stop();
            _channelPollTimer = null;
        }

        private void StartDmPolling()
        {
            if (_dmPollTimer != null) return;
            _dmPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _dmPollTimer.Tick += async (s, e) => await PollDmOnceAsync();
            _dmPollTimer.Start();
        }

        private void StopDmPolling()
        {
            _dmPollTimer?.Stop();
            _dmPollTimer = null;
        }

        private async Task PollChannelOnceAsync()
        {
            if (_channelPolling || _currentDmUser != null) return;
            _channelPolling = true;
            try
            {
                if (!string.IsNullOrEmpty(_currentChannel))
                    await RefreshChannelMessagesAsync(_currentChannel, silent: true);
            }
            finally { _channelPolling = false; }
        }

        private async Task PollDmOnceAsync()
        {
            if (_dmPolling) return;
            bool hasThread = !string.IsNullOrEmpty(_currentDmUser) || _activeIsGroup;
            if (!hasThread) return;
            _dmPolling = true;
            try
            {
                await RefreshActiveThreadAsync(silent: true);
            }
            finally { _dmPolling = false; }
        }

        private async Task RefreshActiveThreadAsync(bool silent)
        {
            if (_activeIsGroup && !string.IsNullOrEmpty(_currentGroupId))
                await RefreshGroupMessagesAsync(_currentGroupId, silent);
            else if (!string.IsNullOrEmpty(_currentDmUser))
                await RefreshDmMessagesAsync(_currentDmUser, silent);
            else
                await RefreshChannelMessagesAsync(_currentChannel, silent);
        }

        private async Task LoadChannelMessagesAsync(string channel)
        {
            var msgs = await StoreApi.GetChannelMessagesAsync(channel);
            _channelMessages.Clear();
            foreach (var m in msgs) _channelMessages.Add(m);
            CommunityMessagesList.ItemsSource = _channelMessages;
            CommunityNoMessagesText.Visibility = _channelMessages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CommunityMessagesScroller.ScrollToEnd();
            _ = LoadMessageAvatarsAsync(msgs);
            _ = LoadMessageGifsAsync(msgs);
        }

        private async Task RefreshChannelMessagesAsync(string channel, bool silent)
        {
            var msgs = await StoreApi.GetChannelMessagesAsync(channel);
            bool wasAtBottom = IsScrolledToBottom();
            bool changed = false;
            var known = new HashSet<string>();
            foreach (var m in _channelMessages)
            {
                if (!string.IsNullOrEmpty(m.Id)) known.Add(m.Id);
            }
            foreach (var m in msgs)
            {
                if (!string.IsNullOrEmpty(m.Id) && known.Contains(m.Id)) continue;
                _channelMessages.Add(m);
                changed = true;
                if (!string.IsNullOrEmpty(m.Id)) known.Add(m.Id);
            }
            if (changed)
            {
                CommunityNoMessagesText.Visibility = _channelMessages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (_channelMessages.Count > 0 && wasAtBottom) CommunityMessagesScroller.ScrollToEnd();
            }
            _ = LoadMessageAvatarsAsync(msgs);
            _ = LoadMessageGifsAsync(msgs);
        }

        private bool IsScrolledToBottom()
        {
            try
            {
                var sv = CommunityMessagesScroller;
                return !(sv.ScrollableHeight - sv.VerticalOffset > 120);
            }
            catch { return true; }
        }

        private async Task LoadMessageAvatarsAsync(IEnumerable<CommunityMessage> msgs)
        {
            foreach (var m in msgs)
            {
                if (!string.IsNullOrWhiteSpace(m.AvatarUrl) && m.AvatarImage == null)
                {
                    _ = LoadAvatarIntoAsync(m.AvatarUrl, img =>
                    {
                        if (m.AvatarImage == null) m.AvatarImage = img;
                    });
                }
            }
            await Task.CompletedTask;
        }

        private Task LoadMessageGifsAsync(IEnumerable<CommunityMessage> msgs)
        {

            return Task.CompletedTask;
        }

        private void CommunityMessageInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter) _ = SendCommunityMessageAsync();
        }

        private void CommunityMessageInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (CommunityMessagePlaceholder == null) return;
            CommunityMessagePlaceholder.Visibility = CommunityMessageInput.Text.Length == 0
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void CommunitySendMessage_Click(object sender, RoutedEventArgs e)
            => await SendCommunityMessageAsync();

        private async Task SendCommunityMessageAsync()
        {
            var raw = CommunityMessageInput.Text ?? "";
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) return;
            if (!_prefs.IsSignedIn)
            {
                Toast("Account required", "Sign in to chat with the community.", ToastType.Info);
                OpenAccountOverlay(registerMode: false);
                return;
            }

            CommunityMessageInput.Text = "";
            CommunityMessagePlaceholder.Visibility = Visibility.Visible;

            if (trimmed.StartsWith("#"))
            {
                var target = trimmed.TrimStart('#').Split(' ')[0].ToLowerInvariant();
                var channels = CommunityChannelList.ItemsSource as System.Collections.IEnumerable;
                if (channels != null)
                {
                    foreach (CommunityChannel c in channels)
                    {
                        if (c.Id == target || c.Name.ToLowerInvariant() == target)
                        {
                            _currentChannel = c.Id;
                            SetCommunityChannelHeader(c);
                            await LoadChannelMessagesAsync(c.Id);
                            StartChannelPolling();
                            return;
                        }
                    }
                }
                Toast("Unknown channel", $"No channel called \"{target}\".", ToastType.Warn);
                return;
            }

            var isGif = trimmed.StartsWith("gif ", StringComparison.OrdinalIgnoreCase)
                        || trimmed.StartsWith("!g", StringComparison.OrdinalIgnoreCase);
            string kind = "text";
            var meta = new Dictionary<string, string>();

            if (isGif)
            {
                var url = trimmed.Length > 4 ? trimmed.Substring(4).Trim() : "";
                if (string.IsNullOrEmpty(url)) TryExtractEmbeddableMedia(trimmed, out url, out _);
                if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    Toast("Need a GIF link", "Type 'gif <url>' or paste a link to a GIF.", ToastType.Warn);
                    CommunityMessageInput.Text = raw;
                    return;
                }
                kind = "gif";
                meta["gif_url"] = url;
                trimmed = url;
            }
            else if (TryExtractEmbeddableMedia(trimmed, out var embedUrl, out var embedKind))
            {
                kind = embedKind;
                if (kind == "gif") meta["gif_url"] = embedUrl;
                else meta["media_url"] = embedUrl;
            }
            else if (_currentChannel == "media" && Regex.IsMatch(trimmed, @"https?://\S+"))
            {
                kind = "media";
                meta["media_url"] = Regex.Match(trimmed, @"https?://\S+").Value.TrimEnd('.', ')', ']', '}', '>');
            }

            var ok = await PostChatMessageAsync(trimmed, kind, meta);
            if (!ok)
            {
                CommunityMessageInput.Text = raw;
                Toast("Couldn't send", "Check your connection / sign-in, or you're sending too fast.", ToastType.Warn);
            }
        }

        private async Task<bool> PostChatMessageAsync(string text, string kind, Dictionary<string, string> meta)
        {
            bool ok;
            if (_activeIsGroup && !string.IsNullOrEmpty(_currentGroupId))
            {
                ok = await StoreApi.PostGroupMessageAsync(_currentGroupId, text, kind, meta);
            }
            else if (_currentDmUser != null)
            {
                ok = await StoreApi.PostDmAsync(_currentDmUser, text, kind, meta);
            }
            else
            {
                ok = await StoreApi.PostChannelMessageAsync(_currentChannel, text, kind, meta);
            }

            if (ok)
            {
                await RefreshActiveThreadAsync(silent: false);
                _ = LoadCommunityDmsAsync();
            }
            return ok;
        }

        private static bool TryExtractEmbeddableMedia(string text, out string url, out string kind)
        {
            url = "";
            kind = "text";
            var m = Regex.Match(text ?? "", @"https?://[^\s]+");
            if (!m.Success) return false;
            var candidate = m.Value.TrimEnd(',', ')', ']', '}', '>', '.', '!', '?', ';', '\'', '"', '\u201D', '\u2019');
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return false;
            var host = uri.Host.ToLowerInvariant();
            var ext = System.IO.Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();

            if (ext == ".gif"
                || host == "media.tenor.com" || host.EndsWith(".tenor.com")
                || host == "media.giphy.com" || host == "i.giphy.com" || host == "media.giphy-media.com"
                || host.EndsWith(".giphy.com"))
            {
                if (host == "tenor.com" || host == "giphy.com") return false;
                kind = "gif";
                url = candidate;
                return true;
            }

            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".webp" || ext == ".bmp" || ext == ".avif"
                || host == "i.imgur.com" || host == "i.redd.it" || host == "cdn.discordapp.com"
                || host == "media.discordapp.net" || host == "i.ytimg.com")
            {
                kind = "media";
                url = candidate;
                return true;
            }

            return false;
        }

        private bool _emojiBuilt;
        private bool _commentEmojiBuilt;

        private void CommunityEmojiToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!_emojiBuilt)
            {
                _emojiBuilt = true;
                foreach (var emoji in EmojiSet)
                {
                    var b = new System.Windows.Controls.Button
                    {
                        Content = emoji,
                        FontSize = 18,
                        FontFamily = new System.Windows.Media.FontFamily("Segoe UI Emoji"),
                        Padding = new Thickness(6, 3, 6, 3),
                        Margin = new Thickness(2),
                        Style = (Style)FindResource("PillButton")
                    };
                    b.Click += (s, args) =>
                    {
                        CommunityMessageInput.Text += emoji;
                        CommunityMessageInput.Focus();
                        CommunityMessageInput.CaretIndex = CommunityMessageInput.Text.Length;
                        CommunityMessagePlaceholder.Visibility = Visibility.Collapsed;
                    };
                    CommunityEmojiPanel.Children.Add(b);
                }
            }
            CommunityEmojiPopup.IsOpen = !CommunityEmojiPopup.IsOpen;
            if (CommunityEmojiPopup.IsOpen) ShowCommunityPickerTab("emoji");
        }

        private void CommentEmojiToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!_commentEmojiBuilt)
            {
                _commentEmojiBuilt = true;
                foreach (var emoji in EmojiSet)
                {
                    var b = new System.Windows.Controls.Button
                    {
                        Content = emoji,
                        FontSize = 18,
                        FontFamily = new System.Windows.Media.FontFamily("Segoe UI Emoji"),
                        Padding = new Thickness(6, 3, 6, 3),
                        Margin = new Thickness(2),
                        Style = (Style)FindResource("PillButton")
                    };
                    b.Click += (s, args) =>
                    {
                        CommentInput.Text += emoji;
                        CommentInput.Focus();
                        CommentInput.CaretIndex = CommentInput.Text.Length;
                        CommentPlaceholder.Visibility = Visibility.Collapsed;
                    };
                    CommentEmojiPanel.Children.Add(b);
                }
            }
            CommentEmojiPopup.IsOpen = !CommentEmojiPopup.IsOpen;
        }

        private void CommunityOpenGifPicker_Click(object sender, RoutedEventArgs e)
        {
            if (CommunityEmojiPopup.IsOpen && _pickerTab == "gif")
            {
                CommunityEmojiPopup.IsOpen = false;
                return;
            }
            ShowCommunityPickerTab("gif");
            CommunityEmojiPopup.IsOpen = true;
            _ = LoadCommunityGifsAsync(CommunityGifSearchBox.Text);
        }

        private string _pickerTab = "emoji";
        private int _gifRequestSeq;
        private DispatcherTimer _gifDebounce;

        private void CommunityPickerTab_Click(object sender, RoutedEventArgs e)
        {
            var tag = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrEmpty(tag)) return;
            ShowCommunityPickerTab(tag);
            if (tag == "gif") _ = LoadCommunityGifsAsync(CommunityGifSearchBox.Text);
        }

        private void ShowCommunityPickerTab(string tab)
        {
            _pickerTab = tab;
            var isGif = tab == "gif";
            CommunityEmojiTabButton.FontWeight = isGif ? FontWeights.Normal : FontWeights.Bold;
            CommunityGifTabButton.FontWeight = isGif ? FontWeights.Bold : FontWeights.Normal;
            CommunityEmojiPanel.Visibility = isGif ? Visibility.Collapsed : Visibility.Visible;
            CommunityGifSearchRow.Visibility = isGif ? Visibility.Visible : Visibility.Collapsed;
            CommunityGifList.Visibility = isGif ? Visibility.Visible : Visibility.Collapsed;
            CommunityGifLoading.Visibility = Visibility.Collapsed;
            if (!isGif) CommunityGifErrorText.Visibility = Visibility.Collapsed;
        }

        private void CommunityGifSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (CommunityGifSearchPlaceholder == null) return;
            CommunityGifSearchPlaceholder.Visibility = CommunityGifSearchBox.Text.Length == 0
                ? Visibility.Visible : Visibility.Collapsed;
            if (_gifDebounce == null)
            {
                _gifDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
                _gifDebounce.Tick += (s, a) =>
                {
                    _gifDebounce.Stop();
                    if (_pickerTab == "gif") _ = LoadCommunityGifsAsync(CommunityGifSearchBox.Text);
                };
            }
            _gifDebounce.Stop();
            _gifDebounce.Start();
        }

        private async Task LoadCommunityGifsAsync(string query)
        {
            var seq = ++_gifRequestSeq;
            CommunityGifLoading.Visibility = Visibility.Visible;
            CommunityGifErrorText.Visibility = Visibility.Collapsed;
            var trimmed = (query ?? "").Trim();

            List<KlipyGif> results;
            string error = "";
            if (string.IsNullOrEmpty(trimmed))
            {
                results = await StoreApi.KlipyTrendingAsync();
            }
            else
            {
                var r = await StoreApi.KlipySearchAsync(trimmed);
                results = r.Results;
                error = r.Error;
            }

            if (seq != _gifRequestSeq || !CommunityEmojiPopup.IsOpen) return;
            CommunityGifLoading.Visibility = Visibility.Collapsed;
            if (results.Count > 0)
            {
                CommunityGifList.ItemsSource = results;
                CommunityGifErrorText.Visibility = Visibility.Collapsed;
            }
            else
            {
                CommunityGifList.ItemsSource = null;
                CommunityGifErrorText.Text = string.IsNullOrEmpty(error) ? "No GIFs found - try another search." : error;
                CommunityGifErrorText.Visibility = Visibility.Visible;
            }
        }

        private async void CommunityGifItem_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is KlipyGif gif)) return;
            var url = gif.SendUrl;
            if (string.IsNullOrEmpty(url))
            {
                Toast("Oops", "That GIF couldn't be loaded - try another one.", ToastType.Warn);
                return;
            }
            if (!_prefs.IsSignedIn)
            {
                Toast("Account required", "Sign in to send GIFs.", ToastType.Info);
                CommunityEmojiPopup.IsOpen = false;
                OpenAccountOverlay(registerMode: false);
                return;
            }
            CommunityEmojiPopup.IsOpen = false;
            var meta = new Dictionary<string, string> { ["gif_url"] = url };
            var ok = await PostChatMessageAsync(url, "gif", meta);
            if (!ok)
            {
                Toast("Couldn't send", "Check your connection / sign-in, or you're sending too fast.", ToastType.Warn);
            }
        }

        private async void ChatGif_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is CommunityMessage m && !string.IsNullOrWhiteSpace(m.GifUrl))
            {
                await OpenLinkAsync(m.GifUrl);
            }
        }

        private async Task OpenLinkAsync(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { await Task.CompletedTask; }
        }

        private void UserName_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is object dc)) return;
            var name = ResolveHoverName(dc);
            if (string.IsNullOrEmpty(name)
                || name.Equals(_prefs.Username, StringComparison.OrdinalIgnoreCase)) return;
            _hoverUserName = name;
            _userHoverTimer?.Stop();
            if (_userHoverTimer == null)
            {
                _userHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(380) };
                _userHoverTimer.Tick += async (s, a) => await ShowUserCardAsync();
            }
            _userHoverTimer.Start();
        }

        private void UserName_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _userHoverTimer?.Stop();
            _hoverUserName = "";
            UserHoverCard.Visibility = Visibility.Collapsed;
        }

        private static string ResolveHoverName(object dc)
        {
            switch (dc)
            {
                case CommunityMessage cm when !string.IsNullOrWhiteSpace(cm.User): return cm.User;
                case CommentItem ci when !string.IsNullOrWhiteSpace(ci.User): return ci.User;
                case FriendItem fi when !string.IsNullOrWhiteSpace(fi.User): return fi.User;
                case DmConversation d when !d.IsGroup && !string.IsNullOrWhiteSpace(d.User): return d.User;
                case UserSearchResult ur when !string.IsNullOrWhiteSpace(ur.User): return ur.User;
                default: return "";
            }
        }

        private async Task ShowUserCardAsync()
        {
            var name = _hoverUserName;
            if (string.IsNullOrEmpty(name)) return;
            _userHoverTimer?.Stop();

            PublicUser user;
            if (_userHoverCache.TryGetValue(name, out var hit)
                && (DateTime.UtcNow - hit.Fetched).TotalMinutes < 5)
            {
                user = hit.User;
            }
            else
            {
                try
                {
                    var resp = await StoreApi.GetUserAsync(name);
                    if (resp?.User == null) return;
                    user = resp.User;
                }
                catch { return; }
                _userHoverCache[name] = (user, DateTime.UtcNow);
            }

            if (_hoverUserName != name || name.Equals(_prefs.Username, StringComparison.OrdinalIgnoreCase)) return;

            UserHoverName.Text = "@" + user.Username;
            UserHoverName.Foreground = BadgeVisual.UsernameBrush(user.Badges)
                ?? (FindResource("TextPrimaryBrush") as System.Windows.Media.Brush
                    ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White));
            UserHoverBadges.Content = BadgeVisual.BuildIconRow(user.Badges);
            UserHoverInitial.Text = user.Initial;
            UserHoverAvatarImage.Source = null;
            if (!string.IsNullOrWhiteSpace(user.AvatarUrl))
            {
                _ = LoadAvatarIntoAsync(user.AvatarUrl, img =>
                {
                    if (_hoverUserName == user.Username) UserHoverAvatarImage.Source = img;
                });
            }
            UserHoverBio.Text = user.Bio ?? "";
            UserHoverBio.Visibility = string.IsNullOrWhiteSpace(user.Bio) ? Visibility.Collapsed : Visibility.Visible;
            UserHoverJoined.Text = string.IsNullOrEmpty(user.JoinedText) ? "" : ("Member since " + user.JoinedText);

            try
            {
                UserHoverGradA.Color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(user.GradA);
                UserHoverGradB.Color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(user.GradB);
            }
            catch { }

            PositionUserCard();
            UserHoverCard.Visibility = Visibility.Visible;
        }

        private void PositionUserCard()
        {
            var pos = Mouse.GetPosition(this);
            double x = pos.X + 16;
            double y = pos.Y + 16;
            if (x + UserHoverCard.Width > ActualWidth) x = Math.Max(0, ActualWidth - UserHoverCard.Width - 8);
            if (y + 130 > ActualHeight) y = Math.Max(0, pos.Y - 130 - 8);
            UserHoverCard.Margin = new Thickness(Math.Max(0, x), Math.Max(0, y), 0, 0);
        }

        private async void CommunityRefresh_Click(object sender, RoutedEventArgs e)
        {
            await RefreshActiveThreadAsync(silent: false);
            _ = LoadCommunityDmsAsync();
            _ = LoadCommunityFriendsAsync();
        }

        private async void CommunityOpenAddFriend_Click(object sender, RoutedEventArgs e)
        {
            if (!_prefs.IsSignedIn)
            {
                Toast("Account required", "Sign in to add friends and send DMs.", ToastType.Info);
                OpenAccountOverlay(registerMode: false);
                return;
            }
            _findUserMode = _dmsTabRequested ? "dm" : "friend";
            _dmsTabRequested = false;
            FindUserTitle.Text = _findUserMode == "friend" ? "Find a friend" : "Start a DM";
            FindUserModeHint.Text = _findUserMode == "friend"
                ? "Search for someone to add as a friend."
                : "Search for someone to send a direct message to.";
            FindUserStatusText.Text = "";
            FindUserResultsList.ItemsSource = null;
            FindUserBox.Clear();
            FindUserOverlay.Visibility = Visibility.Visible;
            FindUserBox.Focus();
        }

        private bool _dmsTabRequested;
        private const string GroupModeCreate = "create";
        private const string GroupModeAdd = "add";
        private string _groupActionMode = GroupModeCreate;

        private async void CommunityNewGroup_Click(object sender, RoutedEventArgs e)
        {
            if (!_prefs.IsSignedIn)
            {
                Toast("Account required", "Sign in to create groups.", ToastType.Info);
                OpenAccountOverlay(registerMode: false);
                return;
            }
            _groupActionMode = GroupModeCreate;
            await ShowGroupPickerAsync(name: "New group",
                hint: "Pick friends and name your group. Everyone can add more friends later.",
                createLabel: "Create group");
        }

        private async void CommunityGroupAddMember_Click(object sender, RoutedEventArgs e)
        {
            if (!_prefs.IsSignedIn) { Toast("Account required", "Sign in to add members.", ToastType.Info); return; }
            if (string.IsNullOrEmpty(_currentGroupId)) return;
            _groupActionMode = GroupModeAdd;
            await ShowGroupPickerAsync(name: "Add members",
                hint: "Tick the friends to add to this group.",
                createLabel: "Add to group");
        }

        private async Task ShowGroupPickerAsync(string name, string hint, string createLabel)
        {
            List<SelectableFriend> friends = new List<SelectableFriend>();
            try
            {
                var list = await StoreApi.GetFriendsAsync();
                foreach (var f in list) friends.Add(new SelectableFriend { User = f.User });
            }
            catch { }
            NewGroupFriendsList.ItemsSource = friends;
            NewGroupNameBox.Clear();
            NewGroupStatusText.Text = friends.Count == 0
                ? "You have no friends yet. Add some friends first."
                : "Tick the friends you want.";
            bool isCreate = _groupActionMode == GroupModeCreate;
            NewGroupNameRow.Visibility = isCreate ? Visibility.Visible : Visibility.Collapsed;
            NewGroupNameLabel.Visibility = isCreate ? Visibility.Visible : Visibility.Collapsed;
            NewGroupNameBox.Visibility = isCreate ? Visibility.Visible : Visibility.Collapsed;
            CommunityGroupHintText.Text = hint;
            NewGroupOverlayTitle.Text = name;
            NewGroupCreateButton.Content = createLabel;
            NewGroupOverlay.Visibility = Visibility.Visible;
        }

        private void NewGroupName_TextChanged(object sender, TextChangedEventArgs e)
        {
            NewGroupStatusText.Text = "";
            if (NewGroupNameBox.Text.Length > 60) NewGroupNameBox.Text = NewGroupNameBox.Text.Substring(0, 60);
        }

        private async void NewGroupCreate_Click(object sender, RoutedEventArgs e)
        {
            var selected = new List<string>();
            if (NewGroupFriendsList.ItemsSource is IEnumerable<SelectableFriend> list)
                foreach (var f in list)
                    if (f.Selected) selected.Add(f.User);

            if (_groupActionMode == GroupModeAdd)
            {
                if (selected.Count == 0) { NewGroupStatusText.Text = "Pick at least one friend."; return; }
                bool allOk = true; string err = "";
                foreach (var u in selected)
                {
                    var r = await StoreApi.AddGroupMemberAsync(_currentGroupId, u);
                    if (!r.Ok) { allOk = false; err = r.Error; break; }
                }
                if (!allOk) { NewGroupStatusText.Text = err ?? "Could not add member."; return; }
                NewGroupOverlay.Visibility = Visibility.Collapsed;
                Toast("Members added", "Friends added to the group.", ToastType.Success);
                _ = LoadCommunityDmsAsync();
                return;
            }

            var name = NewGroupNameBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(name)) { NewGroupStatusText.Text = "Give the group a name."; return; }
            if (selected.Count == 0) { NewGroupStatusText.Text = "Pick at least one friend."; return; }

            var gid = await StoreApi.CreateGroupAsync(name, selected);
            if (string.IsNullOrEmpty(gid))
            {
                NewGroupStatusText.Text = "Couldn't create the group. Check your connection / sign-in.";
                return;
            }
            NewGroupOverlay.Visibility = Visibility.Collapsed;
            Toast("Group created", $"'{name}' is ready.", ToastType.Success);
            _ = LoadCommunityDmsAsync();
            OpenGroupThread(gid, name);
        }

        private void NewGroupCancel_Click(object sender, RoutedEventArgs e)
        {
            NewGroupOverlay.Visibility = Visibility.Collapsed;
        }

        private async void CommunityGroupRename_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentGroupId)) return;
            var newName = await PromptInputAsync("Rename group", "Enter a new group name:");
            if (string.IsNullOrWhiteSpace(newName)) return;
            bool ok = await StoreApi.RenameGroupAsync(_currentGroupId, newName);
            if (!ok) { Toast("Rename failed", "Could not rename the group.", ToastType.Warn); return; }
            _currentGroupName = newName;
            CommunityChatTitle.Text = newName;
            _ = LoadCommunityDmsAsync();
        }

        private Task<string> PromptInputAsync(string title, string message)
        {
            _promptTcs?.TrySetResult(null);
            var tcs = _promptTcs = new TaskCompletionSource<string>();
            DialogEmoji.Text = "✏️";
            DialogTitle.Text = title;
            DialogMessage.Text = message;
            DialogInputBox.Text = "";
            DialogInputBox.Visibility = Visibility.Visible;
            DialogButtonsPanel.Children.Clear();

            void AddButton(string label, bool result, bool primary)
            {
                var btn = new System.Windows.Controls.Button
                {
                    Content = label,
                    Style = (Style)FindResource(primary ? "PrimaryButton" : "SecondaryButton"),
                    Margin = new Thickness(6, 0, 6, 0),
                    MinWidth = primary ? 150 : 110
                };
                btn.Click += (s, args) =>
                {
                    string val = result && !string.IsNullOrWhiteSpace(DialogInputBox.Text)
                        ? DialogInputBox.Text.Trim()
                        : null;
                    DialogOverlay.Visibility = Visibility.Collapsed;
                    DialogInputBox.Visibility = Visibility.Collapsed;
                    tcs.TrySetResult(val);
                };
                DialogButtonsPanel.Children.Add(btn);
            }
            AddButton("OK", true, true);
            AddButton("Cancel", false, false);
            DialogOverlay.Visibility = Visibility.Visible;
            DialogInputBox.Focus();

            return tcs.Task;
        }

        private async void FindUser_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (FindUserPlaceholder == null) return;
            FindUserPlaceholder.Visibility = FindUserBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            await Task.Delay(300);
            await SearchFindUsersAsync();
        }

        private async void FindUser_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter) await SearchFindUsersAsync();
        }

        private async Task SearchFindUsersAsync()
        {
            var q = FindUserBox.Text?.Trim() ?? "";
            if (q.Length < 1)
            {
                FindUserResultsList.ItemsSource = null;
                FindUserStatusText.Text = "";
                return;
            }
            var results = await StoreApi.SearchUsersAsync(q);
            FindUserResultsList.ItemsSource = results;
            FindUserStatusText.Text = results.Count == 0 ? "No players found." : "";
        }

        private async void FindUserAction_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is UserSearchResult r)
            {
                if (string.Equals(r.User, _prefs.Username, StringComparison.OrdinalIgnoreCase))
                {
                    FindUserStatusText.Text = "That's you!";
                    return;
                }
                if (_findUserMode == "friend")
                {
                    var (ok, error) = await StoreApi.SendFriendRequestAsync(r.User);
                    if (ok) Toast("Request sent", $"Friend request sent to @{r.User}.", ToastType.Success);
                    else FindUserStatusText.Text = error ?? "Could not send request.";
                }
                else
                {
                    FindUserOverlay.Visibility = Visibility.Collapsed;
                    OpenDmThread(r.User);
                }
            }
        }

        private void FindUserClose_Click(object sender, RoutedEventArgs e)
            => FindUserOverlay.Visibility = Visibility.Collapsed;

        private async void CommunityAddFriend_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            var name = CommunityAddFriendBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(name)) return;
            var (ok, error) = await StoreApi.SendFriendRequestAsync(name);
            if (ok)
            {
                Toast("Request sent", $"Friend request sent to @{name}.", ToastType.Success);
                CommunityAddFriendBox.Clear();
            }
            else
            {
                Toast("Request failed", error ?? "Could not send request.", ToastType.Warn);
            }
        }

        private async void CommunityAcceptFriend_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is string name)) return;
            var (ok, error) = await StoreApi.AcceptFriendAsync(name);
            if (ok)
            {
                Toast("Friend added", $"@{name} is now your friend.", ToastType.Success);
                await LoadCommunityFriendsAsync();
                await LoadCommunityDmsAsync();
            }
            else Toast("Failed", error ?? "Could not accept.", ToastType.Warn);
        }

        private async void CommunityDeclineFriend_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is string name)) return;
            await StoreApi.DeclineFriendAsync(name);
            await LoadCommunityFriendsAsync();
        }

        private async void CommunityRemoveFriend_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is FriendItem f)) return;
            await StoreApi.RemoveFriendAsync(f.User);
            Toast("Friend removed", $"@{f.User} was removed from your friends.", ToastType.Info);
            await LoadCommunityFriendsAsync();
            await LoadCommunityDmsAsync();
        }

        private async void CommunityFriend_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is FriendItem f)
            {
                OpenDmThread(f.User);
            }
        }

        private async void CommunityDmConvo_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DmConversation c)
            {
                if (c.IsGroup) OpenGroupThread(c.GroupId, c.Name);
                else OpenDmThread(c.User);
            }
        }

        private async Task LoadCommunityFriendsAsync()
        {
            var friends = await StoreApi.GetFriendsAsync();
            CommunityFriendsList.ItemsSource = friends;
            CommunityNoFriendsText.Visibility = friends.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _communityFriendsCache.Clear();
            foreach (var f in friends) _communityFriendsCache.Add(f.User);
            foreach (var f in friends)
            {
                if (!string.IsNullOrWhiteSpace(f.AvatarUrl))
                    _ = LoadAvatarIntoAsync(f.AvatarUrl, img => { if (f.AvatarImage == null) f.AvatarImage = img; });
            }
            var reqs = await StoreApi.GetFriendRequestsAsync();
            CommunityFriendRequestsList.ItemsSource = reqs;
            foreach (var border in FindVisualChildren<Border>(CommunityFriendRequestsList))
            {
                border.IsHitTestVisible = true;
            }
        }

        private async Task LoadCommunityDmsAsync()
        {
            var dms = await StoreApi.GetDmsAsync();
            CommunityDmList.ItemsSource = dms;
            CommunityNoDmsText.Visibility = dms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var c in dms)
            {
                if (!string.IsNullOrWhiteSpace(c.AvatarUrl))
                    _ = LoadAvatarIntoAsync(c.AvatarUrl, img => { if (c.AvatarImage == null) c.AvatarImage = img; });
            }
        }

        private async void OpenDmThread(string otherUser)
        {
            _dmsTabRequested = true;
            _activeIsGroup = false;
            _currentDmUser = otherUser;
            _currentGroupId = null;
            _currentGroupName = null;
            StopChannelPolling();
            CommunityChatTitle.Text = "@" + otherUser;
            CommunityChatDesc.Text = "Direct message";
            CommunityChatTypeBadge.Text = "PRIVATE DM";
            CommunityMessagePlaceholder.Text = $"Message @{otherUser}  (#channel to go back to a public channel)";
            CommunityGroupActions.Visibility = Visibility.Collapsed;
            ShowCommunitySidebar(false);
            await RefreshDmMessagesAsync(otherUser, silent: false);
            StartDmPolling();
        }

        private async void OpenGroupThread(string groupId, string groupName)
        {
            _dmsTabRequested = true;
            _activeIsGroup = true;
            _currentDmUser = null;
            _currentGroupId = groupId;
            _currentGroupName = groupName;
            StopChannelPolling();
            CommunityChatTitle.Text = groupName;
            CommunityChatDesc.Text = "Group message";
            CommunityChatTypeBadge.Text = "GROUP";
            CommunityMessagePlaceholder.Text = $"Message {groupName}  (#channel to go back to a public channel)";
            CommunityGroupActions.Visibility = Visibility.Visible;
            ShowCommunitySidebar(false);
            await RefreshGroupMessagesAsync(groupId, silent: false);
            StartDmPolling();
        }

        private async Task RefreshGroupMessagesAsync(string groupId, bool silent)
        {
            var msgs = await StoreApi.GetGroupMessagesAsync(groupId);
            CommunityMessagesList.ItemsSource = msgs;
            CommunityNoMessagesText.Visibility = msgs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (msgs.Count > 0) CommunityMessagesScroller.ScrollToEnd();
            _ = LoadMessageAvatarsAsync(msgs);
            _ = LoadMessageGifsAsync(msgs);
        }

        private async Task RefreshDmMessagesAsync(string otherUser, bool silent)
        {
            var msgs = await StoreApi.GetDmMessagesAsync(otherUser);
            CommunityMessagesList.ItemsSource = msgs;
            CommunityNoMessagesText.Visibility = msgs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (msgs.Count > 0) CommunityMessagesScroller.ScrollToEnd();
            _ = LoadMessageAvatarsAsync(msgs);
            _ = LoadMessageGifsAsync(msgs);
        }

        private async void RefreshAnnouncements_Click(object sender, RoutedEventArgs e)
        {
            await LoadAnnouncementsAsync(reportErrors: true);
        }

        private async Task LoadAnnouncementsAsync(bool reportErrors)
        {
            try
            {
                var list = await StoreApi.GetAnnouncementsAsync();
                AnnouncementsItemsControl.ItemsSource = list.Count > 0
                    ? list
                    : new List<string>
                    {
                        "Welcome to the AMC Store! Every mod here is free and open source.",
                        "If you have questions or concerns about malware, contact @acutebunny on Discord."
                    };
                if (reportErrors) Toast("Refreshed", "Announcements updated.", ToastType.Success);
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                AnnouncementsItemsControl.ItemsSource = new List<string>
                {
                    "Couldn't load announcements from the store.",
                    "All mods on the AMC Store are free and open source.",
                    "Questions about malware? Contact @acutebunny on Discord."
                };
                if (reportErrors) Toast("Offline", "Could not reach the AMC Store API.", ToastType.Warn);
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchPlaceholder == null) return;
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                ? Visibility.Visible : Visibility.Collapsed;

            _searchDebounce?.Stop();
            _searchDebounce?.Start();
        }

        private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _searchDebounce?.Stop();
                _ = LoadStoreAsync();
            }
        }

        private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_introFinished || SearchBox == null) return;
            _searchDebounce?.Stop();
            _ = LoadStoreAsync();
        }

        private void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_introFinished || SearchBox == null) return;
            _searchDebounce?.Stop();
            _ = LoadStoreAsync();
        }

        private static readonly string[] CategoryKeys = { "", "official", "unverified" };

        private void StoreRefresh_Click(object sender, RoutedEventArgs e) => _ = LoadStoreAsync();
        private void StoreRetry_Click(object sender, RoutedEventArgs e) => _ = LoadStoreAsync();

        private async Task LoadStoreAsync()
        {
            _storeCts?.Cancel();
            var cts = _storeCts = new CancellationTokenSource();
            var sort = SortKeys[Math.Max(0, Math.Min(SortCombo.SelectedIndex, SortKeys.Length - 1))];
            var category = CategoryKeys[Math.Max(0, Math.Min(CategoryCombo.SelectedIndex, CategoryKeys.Length - 1))];
            var search = SearchBox.Text ?? "";

            SetStoreStatus("\uD83D\uDCE1", "Loading mods...", showRetry: false);

            try
            {
                var mods = await StoreApi.GetModsAsync(search, sort, category).ConfigureAwait(true);
                if (cts.IsCancellationRequested) return;

                foreach (var m in mods)
                {
                    m.Liked = _prefs.HasLiked(m.Id);
                    m.Disliked = _prefs.HasDisliked(m.Id);
                    m.Subscribed = _prefs.IsSubscribed(m.Id);
                }

                ((ObservableCollection<StoreMod>)StoreList.ItemsSource).ResetWith(mods);
                _storeEverLoaded = true;

                HideStoreStatus();

                if (mods.Count == 0)
                {
                    SetStoreStatus("\uD83D\uDD0E",
                        string.IsNullOrWhiteSpace(search) ? "The store is empty for now." : $"No mods match \"{search}\".",
                        showRetry: false);
                }
                else
                {
                    _ = LoadCardImagesAsync(mods, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {

            }
            catch (Exception ex)
            {
                LogCrash(ex);
                if (cts.IsCancellationRequested) return;
                ((ObservableCollection<StoreMod>)StoreList.ItemsSource)?.ClearIfPossible();
                SetStoreStatus("\uD83D\uDCE1", "Couldn't reach the AMC Store.\nCheck your internet connection.", showRetry: true);
            }
        }

        private async Task LoadCardImagesAsync(IEnumerable<StoreMod> mods, CancellationToken ct)
        {
            foreach (var mod in mods)
            {
                if (ct.IsCancellationRequested) return;
                var url = !string.IsNullOrWhiteSpace(mod.ThumbnailUrl) ? mod.ThumbnailUrl : mod.IconUrl;
                if (string.IsNullOrWhiteSpace(url)) continue;
                try
                {
                    mod.ThumbnailImage = await ImageCache.LoadFromUrlAsync(url).ConfigureAwait(true);
                    if (ct.IsCancellationRequested) return;
                }
                catch { }
            }
        }

        private void SetStoreStatus(string emoji, string message, bool showRetry)
        {
            StoreList.Visibility = Visibility.Collapsed;
            StoreStatusPanel.Visibility = Visibility.Visible;
            StoreStatusEmoji.Text = emoji;
            StoreStatusText.Text = message;
            StoreRetryButton.Visibility = showRetry ? Visibility.Visible : Visibility.Collapsed;
        }

        private void HideStoreStatus()
        {
            StoreStatusPanel.Visibility = Visibility.Collapsed;
            StoreList.Visibility = Visibility.Visible;
        }

        private async void StoreDownload_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is StoreMod mod)
            {
                await InstallFlowAsync(mod, silent: false);
            }
        }

        private async void StoreLike_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is StoreMod mod)
            {
                await ReactFlowAsync(mod, like: true);
            }
        }

        private async void StoreDislike_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is StoreMod mod)
            {
                await ReactFlowAsync(mod, like: false);
            }
        }

        private async void StoreSubscribe_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is StoreMod mod)) return;

            var subscribing = !mod.Subscribed;
            if (subscribing && !await EnsureAuthenticatedAsync())
            {
                mod.Subscribed = false;
                return;
            }

            mod.Subscribed = subscribing;
            _prefs.SetSubscribed(mod.Id, subscribing);
            _ = StoreApi.ToggleSubscribeServerAsync(mod.Id, undo: !subscribing);

            if (subscribing)
            {
                Toast("Subscribed", $"{mod.Name} will now auto-download and auto-update.", ToastType.Success);
                await InstallFlowAsync(mod, silent: false);
            }
            else
            {
                Toast("Unsubscribed", $"{mod.Name} won't be auto-updated anymore.", ToastType.Info);
            }
        }

        private async Task ReactFlowAsync(StoreMod mod, bool like)
        {
            if (!await EnsureAuthenticatedAsync())
            {

                mod.Liked = _prefs.HasLiked(mod.Id);
                mod.Disliked = _prefs.HasDisliked(mod.Id);
                return;
            }

            var wasActive = like ? mod.Liked : mod.Disliked;
            var hadOther = like ? mod.Disliked : mod.Liked;

            if (like) { mod.Liked = !wasActive; if (mod.Liked) mod.Disliked = false; }
            else { mod.Disliked = !wasActive; if (mod.Disliked) mod.Liked = false; }

            try
            {
                if (hadOther)
                {
                    await StoreApi.ReactAsync(mod.Id, !like, undo: true).ConfigureAwait(true);
                }
                var resp = await StoreApi.ReactAsync(mod.Id, like, wasActive).ConfigureAwait(true);
                mod.Likes = resp.Likes > 0 ? resp.Likes : mod.Likes;
                mod.Dislikes = resp.Dislikes > 0 ? resp.Dislikes : mod.Dislikes;
                _prefs.SetVote(mod.Id, like, wasActive);
            }
            catch (Exception ex)
            {
                LogCrash(ex);

                mod.Liked = _prefs.HasLiked(mod.Id);
                mod.Disliked = _prefs.HasDisliked(mod.Id);
                Toast("Vote failed", "Could not reach the AMC Store.", ToastType.Error);
            }
        }

        private async Task InstallFlowAsync(StoreMod mod, bool silent)
        {
            if (mod == null || string.IsNullOrWhiteSpace(mod.Id)) return;

            if (!_prefs.IsSignedIn)
            {
                if (silent) return;
                if (!await EnsureAuthenticatedAsync()) return;
            }

            if (_busyInstallId != null)
            {
                if (!silent) Toast("Busy", "Another download is already running.", ToastType.Info);
                return;
            }

            if (string.IsNullOrWhiteSpace(_prefs.BepInExPluginsPath) || !Directory.Exists(_prefs.BepInExPluginsPath))
            {
                NavigateTo(GameNotFoundPage);
                SelectNav(null);
                return;
            }

            if (!silent && IsGorillaTagRunning())
            {
                var close = await ShowDialogAsync("Gorilla Tag is running",
                    "Gorilla Tag should be closed while installing mods.\nClose it now?",
                    emoji: "\uD83C\uDFAE", buttons: DialogButtons.YesNo);
                if (close != true) return;
                TryKillGorillaTag();
            }

            if (!mod.Verified && !silent)
            {
                var proceed = await ShowDialogAsync(
                    "Unverified mod",
                    $"\"{mod.Name}\" is an unverified community upload.\n\n" +
                    "It has passed our automated scans, but no AMC Studios team member has reviewed it yet. " +
                    "Malicious mods can get your account banned.\n\nInstall anyway?",
                    emoji: "\u26A0\uFE0F", buttons: DialogButtons.YesNo);
                if (proceed != true) return;
            }

            _busyInstallId = mod.Id;
            _downloadCts = new CancellationTokenSource();
            var backPage = _pageBeforeProgress ?? HomePage;

            IProgress<double> progress = null;
            if (!silent)
            {
                ProgressCancelButton.Visibility = Visibility.Visible;
                ProgressStatusText.Text = $"Downloading {mod.Name}";
                ProgressSubStatusText.Text = "Preparing download...";
                ProgressPercentText.Text = "  0%";
                MainProgressBar.Value = 0;
                NavigateTo(ProgressPage);

                var lastPct = -1;
                var lastUiUpdate = DateTime.MinValue;
                var installShown = false;
                progress = new Progress<double>(p =>
                {
                    var pct = (int)Math.Floor(p);
                    if (pct > 100) pct = 100;
                    if (pct == lastPct) return;
                    if (pct < 100 && (DateTime.UtcNow - lastUiUpdate).TotalMilliseconds < 120) return;

                    lastPct = pct;
                    lastUiUpdate = DateTime.UtcNow;
                    MainProgressBar.Value = pct;
                    ProgressPercentText.Text = $"{pct,3:0}%";

                    if (pct >= 100 && !installShown)
                    {
                        installShown = true;
                        ProgressSubStatusText.Text = "Installing...";
                    }
                    else if (pct < 100)
                    {
                        ProgressSubStatusText.Text = "Downloading...";
                    }
                });
            }

            try
            {
                var entry = await ModInstaller.InstallAsync(mod, _prefs.BepInExPluginsPath, progress, _downloadCts.Token);
                InstalledModsManager.Upsert(entry);
                RefreshModsView();
                _ = StoreApi.ReportModInstalledAsync(mod.Id);
                Toast("Installed \u2705", $"{mod.Name} v{entry.Version} installed successfully.", ToastType.Success);
            }
            catch (OperationCanceledException)
            {
                Toast("Canceled", $"Download of {mod.Name} was canceled.", ToastType.Info);
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                await ShowDialogAsync("Install failed",
                    $"Could not install \"{mod.Name}\".\n\n{ex.Message}", emoji: "\u26A0\uFE0F");
            }
            finally
            {
                _busyInstallId = null;
                if (!silent) NavigateTo(backPage);
                if (ReferenceEquals(backPage, ModsPage)) RefreshModsView();
            }
        }

        private async void ProgressCancel_Click(object sender, RoutedEventArgs e)
        {
            try { _downloadCts?.Cancel(); } catch { }
            await Task.CompletedTask;
        }

        private async Task<bool> AutoInstallBepInExAsync(string gtagPath, string pluginsPath)
        {
            _downloadCts = new CancellationTokenSource();
            var backPage = _pageBeforeProgress ?? LoadingPage;

            ProgressCancelButton.Visibility = Visibility.Visible;
            ProgressStatusText.Text = "Installing BepInEx 5";
            ProgressSubStatusText.Text = "Downloading BepInEx...";
            ProgressPercentText.Text = "  0%";
            MainProgressBar.Value = 0;
            NavigateTo(ProgressPage);

            var lastPct = -1;
            var finishingShown = false;
            var progress = new Progress<double>(p =>
            {
                var pct = (int)Math.Floor(p);
                if (pct > 100) pct = 100;
                if (pct == lastPct) return;
                lastPct = pct;
                MainProgressBar.Value = pct;
                ProgressPercentText.Text = $"{pct,3:0}%";
                if (pct >= 100 && !finishingShown)
                {
                    finishingShown = true;
                    ProgressSubStatusText.Text = "Finishing up...";
                }
            });

            try
            {
                var result = await BepInExInstaller.InstallAsync(gtagPath, progress, _downloadCts.Token);
                if (!result.Ok)
                {
                    await ShowDialogAsync("BepInEx install failed",
                        $"Could not set up BepInEx.\n\n{result.Error}", emoji: "\u26A0\uFE0F");
                    return false;
                }
                Toast("BepInEx installed \u2705",
                    "BepInEx 5 was installed into your Gorilla Tag folder.", ToastType.Success);
                return true;
            }
            catch (OperationCanceledException)
            {
                Toast("Canceled", "BepInEx install was canceled.", ToastType.Info);
                return false;
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                await ShowDialogAsync("BepInEx install failed",
                    $"Could not set up BepInEx.\n\n{ex.Message}", emoji: "\u26A0\uFE0F");
                return false;
            }
            finally
            {
                if (!ReferenceEquals(backPage, ProgressPage)) NavigateTo(backPage);
            }
        }

        private async void ModRowUninstall_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is InstalledModRow row)) return;

            var ok = await ShowDialogAsync("Uninstall mod",
                $"Remove \"{row.Entry.Name}\" from Gorilla Tag?\n\nIts own folder gets deleted - your game files are untouched.",
                emoji: "\uD83D\uDDD1\uFE0F", buttons: DialogButtons.YesNo);
            if (ok != true) return;

            try
            {
                ModInstaller.Uninstall(row.Entry, _prefs.BepInExPluginsPath);
                InstalledModsManager.Remove(row.Entry.Id);
                _prefs.SetSubscribed(row.Entry.Id, false);
                RefreshModsView();
                Toast("Removed", $"{row.Entry.Name} has been uninstalled.", ToastType.Success);
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                Toast("Failed", $"Could not uninstall: {ex.Message}", ToastType.Error);
            }
        }

        private async void ModRowUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is InstalledModRow row)) return;
            try
            {
                SetBusyRow(row, true);
                var remote = await StoreApi.GetModAsync(row.Entry.Id);
                if (remote == null) throw new InvalidOperationException("This mod no longer exists on the store.");
                row.UpdateAvailable = false;
                await InstallFlowAsync(remote, silent: false);
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                Toast("Update failed", ex.Message, ToastType.Error);
            }
            finally
            {
                SetBusyRow(row, false);
            }
        }

        private async void ModRowEdit_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is InstalledModRow row)) return;
            await OpenManageForModAsync(new StoreMod { Id = row.Entry.Id, Name = row.Entry.Name });
        }

        private void ModRowFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is InstalledModRow row)) return;
            try
            {
                if (Directory.Exists(row.Entry.InstallFolder))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{row.Entry.InstallFolder}\"") { UseShellExecute = true });
                }
                else
                {
                    Toast("Missing folder", "This mod's folder no longer exists. Uninstall and reinstall it.", ToastType.Warn);
                }
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                Toast("Failed", $"Could not open folder: {ex.Message}", ToastType.Error);
            }
        }

        private async void ModRowIcon_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is InstalledModRow row)) return;
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Pick an icon for " + row.Entry.Name,
                    Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.gif|All files|*.*"
                };
                if (dlg.ShowDialog(this) != true) return;

                var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext)) ext = ".png";
                var dest = InstalledModsManager.IconPathFor(row.Entry.Id, ext);
                File.Copy(dlg.FileName, dest, overwrite: true);

                row.Entry.IconPath = dest;
                InstalledModsManager.Upsert(row.Entry);
                RefreshModsView();
                Toast("Icon updated", $"{row.Entry.Name}'s icon was changed.", ToastType.Success);
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                Toast("Icon failed", $"Could not set icon: {ex.Message}", ToastType.Error);
            }
        }

        private async void ModsRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshModsView();
            await CheckUpdateBadgesAsync();
            Toast("Refreshed", "Your library is up to date with the store.", ToastType.Info);
        }

        private void RefreshModsView()
        {
            var entries = InstalledModsManager.Load().OrderByDescending(m => m.InstalledAtUtc).ToList();
            InstalledView.Clear();
            foreach (var entry in entries) InstalledView.Add(new InstalledModRow(entry));
            ModsEmptyPanel.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ModsList.Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SetBusyRow(InstalledModRow row, bool busy)
        {

            if (row == null) return;
            foreach (var button in FindVisualChildren<System.Windows.Controls.Button>(ModsList))
            {
                if (ReferenceEquals((button.DataContext as InstalledModRow)?.Entry, row.Entry))
                {
                    button.IsEnabled = !busy;
                }
            }
        }

        private async Task CheckUpdateBadgesAsync()
        {
            _badgeCts?.Cancel();
            var cts = _badgeCts = new CancellationTokenSource();

            foreach (var row in InstalledView)
            {
                if (cts.IsCancellationRequested) return;
                try
                {
                    var remote = await StoreApi.GetModAsync(row.Entry.Id);
                    if (cts.IsCancellationRequested) return;
                    if (remote != null)
                    {
                        row.UpdateAvailable =
                            !string.Equals(NormalizeVersion(remote.Version), NormalizeVersion(row.Entry.Version), StringComparison.OrdinalIgnoreCase);
                        row.CanEdit = _prefs.IsSignedIn &&
                                      !string.IsNullOrEmpty(remote.Uploader) &&
                                      string.Equals(remote.Uploader, _prefs.Username, StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch
                {

                }
            }
        }

        private static string NormalizeVersion(string v) =>
            (v ?? "").Trim().TrimStart('v', 'V');

        private async Task SyncSubscriptionsAsync()
        {
            var ids = _prefs.SubscribedIds.ToList();
            if (ids.Count == 0) return;

            int installedCount = 0, updatedCount = 0, failedCount = 0;
            bool gameRunning = IsGorillaTagRunning();

            foreach (var id in ids)
            {
                try
                {
                    var remote = await StoreApi.GetModAsync(id);
                    if (remote == null) continue;

                    var local = InstalledModsManager.Get(id);
                    var needsInstall = local == null ||
                                       !Directory.Exists(local.InstallFolder) ||
                                       !string.Equals(NormalizeVersion(local.Version), NormalizeVersion(remote.Version), StringComparison.OrdinalIgnoreCase);
                    if (!needsInstall) continue;

                    if (gameRunning)
                    {
                        if (local == null) Toast("Waiting", $"Close Gorilla Tag so \"{remote.Name}\" can install.", ToastType.Info);
                        continue;
                    }

                    await InstallFlowAsync(remote, silent: true);
                    if (local == null) installedCount++; else updatedCount++;
                }
                catch
                {
                    failedCount++;
                }
            }

            if (installedCount + updatedCount + failedCount > 0)
            {
                var parts = new List<string>();
                if (installedCount > 0) parts.Add($"{installedCount} installed");
                if (updatedCount > 0) parts.Add($"{updatedCount} updated");
                if (failedCount > 0) parts.Add($"{failedCount} failed");
                Toast("Subscriptions", string.Join(", ", parts) + ".", failedCount > 0 ? ToastType.Warn : ToastType.Success);
                RefreshModsView();
            }
        }

        private async Task<string> FindGorillaTagPathAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    string steamPath = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
                    if (string.IsNullOrEmpty(steamPath)) return null;

                    var libraries = new List<string> { steamPath };
                    var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdf))
                    {
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\""))
                        {
                            libraries.Add(Regex.Unescape(m.Groups[1].Value));
                        }
                    }

                    return libraries.Select(f => Path.Combine(f, "steamapps", "common", "Gorilla Tag"))
                                    .FirstOrDefault(Directory.Exists);
                }
                catch
                {
                    return null;
                }
            });
        }

        private async void ChangeFolderButton_Click(object sender, RoutedEventArgs e) => await PromptAndSetGamePathAsync();
        private async void SelectFolderButton_Click(object sender, RoutedEventArgs e) => await PromptAndSetGamePathAsync();
        private async void InstallBepInExButton_Click(object sender, RoutedEventArgs e) => await DownloadAndInstallBepInExFlowAsync();

        private async Task DownloadAndInstallBepInExFlowAsync()
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select your Gorilla Tag folder (the folder that contains the game)",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            var selected = dialog.SelectedPath;
            var plugins = BepInExInstaller.PluginsPathFor(selected);

            if (BepInExInstaller.IsInstalled(selected))
            {
                Directory.CreateDirectory(plugins);
                _prefs.GorillaTagPath = selected;
                _prefs.BepInExPluginsPath = plugins;
                _prefs.Save();
                CurrentGamePath = $"Playing from: {selected}";
                Toast("BepInEx found", "BepInEx is already installed in that folder.", ToastType.Success);
                if (TopNav.IsEnabled) NavigateTo(HomePage);
                return;
            }

            StatusText.Text = "Setting up BepInEx...";
            SubStatusText.Text = "Downloading the mod loader...";
            var ok = await AutoInstallBepInExAsync(selected, plugins);
            if (!ok) return;

            _prefs.GorillaTagPath = selected;
            _prefs.BepInExPluginsPath = plugins;
            _prefs.Save();
            CurrentGamePath = $"Playing from: {selected}";
            if (TopNav.IsEnabled) NavigateTo(HomePage);
        }

        private async Task PromptAndSetGamePathAsync()
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select your main Gorilla Tag folder (the folder that contains the game)",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            var selected = dialog.SelectedPath;
            var plugins = Path.Combine(selected, "BepInEx", "plugins");

            if (!Directory.Exists(plugins))
            {
                if (!BepInExInstaller.IsInstalled(selected))
                {
                    var install = await ShowDialogAsync("BepInEx not installed",
                        "BepInEx isn't installed in that folder yet.\n\n" +
                        "Download and install BepInEx 5 automatically?",
                        emoji: "\uD83E\uDDE9", buttons: DialogButtons.YesNo);
                    if (install != true)
                    {
                        await ShowDialogAsync("Invalid folder",
                            "Select the main Gorilla Tag game folder (the folder that contains the game).",
                            emoji: "\uD83D\uDCC2");
                        return;
                    }
                    var ok = await AutoInstallBepInExAsync(selected, plugins);
                    if (!ok) return;
                }
                else
                {
                    Directory.CreateDirectory(plugins);
                }
            }

            _prefs.GorillaTagPath = selected;
            _prefs.BepInExPluginsPath = plugins;
            _prefs.Save();
            CurrentGamePath = $"Playing from: {_prefs.GorillaTagPath}";
            Toast("Folder saved", "Gorilla Tag location updated.", ToastType.Success);

            if (TopNav.IsEnabled) NavigateTo(_pageBeforeProgress ?? HomePage);
        }

        private enum DialogButtons { Ok, YesNo, RetryExit }

        private TaskCompletionSource<bool> _dialogTcs;
        private TaskCompletionSource<string> _promptTcs;

        private Task<bool> ShowDialogAsync(string title, string message, string emoji = null, DialogButtons buttons = DialogButtons.Ok)
        {
            _dialogTcs?.TrySetResult(false);
            var tcs = _dialogTcs = new TaskCompletionSource<bool>();

            DialogEmoji.Text = emoji ?? (buttons == DialogButtons.Ok ? "\u2139\uFE0F" : "\u2753");
            DialogTitle.Text = title;
            DialogMessage.Text = message;
            DialogButtonsPanel.Children.Clear();

            void AddButton(string label, bool result, bool primary)
            {
                var btn = new System.Windows.Controls.Button
                {
                    Content = label,
                    Style = (Style)FindResource(primary ? "PrimaryButton" : "SecondaryButton"),
                    Margin = new Thickness(6, 0, 6, 0),
                    MinWidth = primary ? 150 : 110
                };
                btn.Click += (s, args) =>
                {
                    DialogOverlay.Visibility = Visibility.Collapsed;
                    tcs.TrySetResult(result);
                };
                DialogButtonsPanel.Children.Add(btn);
            }

            switch (buttons)
            {
                case DialogButtons.YesNo:
                    AddButton("Yes", true, true);
                    AddButton("No", false, false);
                    break;
                case DialogButtons.RetryExit:
                    AddButton("Retry", true, true);
                    AddButton("Continue Anyway", false, false);
                    break;
                default:
                    AddButton("OK", true, true);
                    break;
            }

            DialogOverlay.Visibility = Visibility.Visible;
            return tcs.Task;
        }

        private enum ToastType { Info, Success, Warn, Error }

        private void Toast(string title, string message, ToastType type)
        {
            try
            {
                while (ToastHost.Children.Count >= 4) ToastHost.Children.RemoveAt(0);

                var borderBrush = type switch
                {
                    ToastType.Success => FindResource("SuccessBrush"),
                    ToastType.Warn => FindResource("WarnBrush"),
                    ToastType.Error => FindResource("DangerBrush"),
                    _ => FindResource("AccentBlueBrush")
                };

                var toast = new Border
                {
                    Background = (System.Windows.Media.Brush)FindResource("ContentBackgroundBrush"),
                    BorderBrush = (System.Windows.Media.Brush)borderBrush,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(14, 10, 16, 10),
                    Margin = new Thickness(0, 8, 0, 0),
                    MaxWidth = 340,
                    Opacity = 0,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        ShadowDepth = 1,
                        BlurRadius = 14,
                        Opacity = 0.45
                    }
                };

                var stack = new StackPanel();
                stack.Children.Add(new TextBlock
                {
                    Text = title,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 13,
                    Foreground = System.Windows.Media.Brushes.White
                });
                stack.Children.Add(new TextBlock
                {
                    Text = message,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                    Margin = new Thickness(0, 2, 0, 0)
                });
                toast.Child = stack;
                ToastHost.Children.Add(toast);

                var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220));
                toast.BeginAnimation(OpacityProperty, fadeIn);

                var dismiss = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                dismiss.Tick += (s, e) =>
                {
                    dismiss.Stop();
                    var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
                    fadeOut.Completed += (s2, e2) => ToastHost.Children.Remove(toast);
                    toast.BeginAnimation(OpacityProperty, fadeOut);
                };
                dismiss.Start();
            }
            catch
            {

            }
        }

        private static bool IsGorillaTagRunning() =>
            Process.GetProcessesByName("Gorilla Tag").Length > 0;

        private static void TryKillGorillaTag()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("Gorilla Tag")) p.Kill();
                Thread.Sleep(800);
            }
            catch { }
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) yield break;
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T typed) yield return typed;
                foreach (var sub in FindVisualChildren<T>(child)) yield return sub;
            }
        }

        private static void LogCrash(Exception ex)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppPrefs.DataRoot, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
            }
            catch { }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (e.ButtonState == MouseButtonState.Pressed) DragMove();
            }
            catch { }
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e) => System.Windows.Application.Current.Shutdown();

        public void ShowCrashToast(string message) =>
            Toast("Something went wrong", message, ToastType.Error);

        private void RootClipGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            const double radius = 15;
            RootClipGrid.Clip = new System.Windows.Media.RectangleGeometry(
                new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), radius, radius);
        }

        private void RoundCorners_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is FrameworkElement el && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                double radius = 12;
                if (el.Tag is string s && double.TryParse(s, out var r)) radius = r;
                el.Clip = new System.Windows.Media.RectangleGeometry(
                    new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), radius, radius);
            }
        }

        private StoreMod _detailMod;

        private void StoreCard_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is StoreMod mod)
            {
                OpenModDetail(mod);
            }
        }

        private void OpenModDetail(StoreMod mod)
        {
            _detailMod = mod;
            DetailOverlay.DataContext = mod;

            DetailName.Text = string.IsNullOrWhiteSpace(mod.Name) ? "Unnamed Mod" : mod.Name;
            DetailInitial.Text = mod.Initial;
            DetailImage.Source = mod.ThumbnailImage;

            DetailVersionChip.Text = "v" + NormalizeVersion(mod.Version);
            DetailDateChip.Text = string.IsNullOrEmpty(mod.UploadedAtText) ? "" : "\uD83D\uDCC5  " + mod.UploadedAtText;
            DetailSizeChip.Text = string.IsNullOrEmpty(mod.SizeText) ? "" : "\uD83D\uDCBE  " + mod.SizeText;
            DetailDownloadsChip.Text = "\u2B07  " + mod.Downloads.ToString("N0") + " downloads";
            DetailViewsChip.Text = mod.Views > 0 ? "\uD83D\uDC41  " + mod.Views.ToString("N0") + " views" : "";
            DetailSubsChip.Text = mod.Subscribers > 0
                ? "\uD83D\uDD14  " + mod.Subscribers.ToString("N0") + (mod.Subscribers == 1 ? " subscriber" : " subscribers")
                : "";

            DetailVerifiedBadge.Visibility = mod.Verified ? Visibility.Visible : Visibility.Collapsed;
            DetailUnverifiedBadge.Visibility = !mod.Verified && !string.IsNullOrEmpty(mod.Uploader)
                ? Visibility.Visible : Visibility.Collapsed;
            DetailWarningBanner.Visibility = mod.Verified ? Visibility.Collapsed : Visibility.Visible;

            if (!string.IsNullOrWhiteSpace(mod.Uploader))
                DetailCreator.Text = $"{(string.IsNullOrWhiteSpace(mod.Creator) ? "unknown" : mod.Creator)}  (@{mod.Uploader})";
            else
                DetailCreator.Text = string.IsNullOrWhiteSpace(mod.Creator) ? "unknown" : mod.Creator;

            DetailDescriptionText.Text = string.IsNullOrWhiteSpace(mod.Description)
                ? "No description provided."
                : mod.Description;

            bool isOwner = _prefs.IsSignedIn &&
                           !string.IsNullOrEmpty(mod.Uploader) &&
                           string.Equals(mod.Uploader, _prefs.Username, StringComparison.OrdinalIgnoreCase);
            DetailManageButton.Visibility = isOwner ? Visibility.Visible : Visibility.Collapsed;

            UpdateDetailCounts();
            _ = LoadCommentsAsync(mod.Id);
            if (DetailOverlay.Visibility != Visibility.Visible)
            {
                DetailOverlay.Visibility = Visibility.Visible;
                try
                {
                    var fadeIn = (Storyboard)FindResource("FadeIn");
                    Storyboard.SetTarget(fadeIn, DetailOverlay);
                    fadeIn.Begin(this, true);
                }
                catch { }
            }
        }

        private void UpdateDetailCounts()
        {
            if (_detailMod == null) return;
            DetailLikesText.Text = _detailMod.LikeText;
            DetailDislikesText.Text = _detailMod.DislikeText;
            DetailDownloadsChip.Text = "\u2B07  " + _detailMod.Downloads.ToString("N0") + " downloads";
            DetailViewsChip.Text = _detailMod.Views > 0 ? "\uD83D\uDC41  " + _detailMod.Views.ToString("N0") + " views" : "";
        }

        private void DetailBack_Click(object sender, RoutedEventArgs e) => CloseModDetail();

        private void CloseModDetail()
        {
            _detailMod = null;
            DetailOverlay.DataContext = null;
            DetailOverlay.Visibility = Visibility.Collapsed;
            CommentEmojiPopup.IsOpen = false;
        }

        private async void DetailDownload_Click(object sender, RoutedEventArgs e)
        {
            var mod = _detailMod;
            CloseModDetail();
            if (mod != null) await InstallFlowAsync(mod, silent: false);
        }

        private async void DetailLike_Click(object sender, RoutedEventArgs e)
        {
            if (_detailMod == null) return;
            await ReactFlowAsync(_detailMod, like: true);
            UpdateDetailCounts();
        }

        private async void DetailDislike_Click(object sender, RoutedEventArgs e)
        {
            if (_detailMod == null) return;
            await ReactFlowAsync(_detailMod, like: false);
            UpdateDetailCounts();
        }

        private async void DetailSubscribe_Click(object sender, RoutedEventArgs e)
        {
            var mod = _detailMod;
            if (mod == null) return;

            var subscribing = !mod.Subscribed;
            if (subscribing && !await EnsureAuthenticatedAsync())
            {
                mod.Subscribed = false;
                return;
            }

            mod.Subscribed = subscribing;
            _prefs.SetSubscribed(mod.Id, subscribing);
            _ = StoreApi.ToggleSubscribeServerAsync(mod.Id, undo: !subscribing);

            if (subscribing)
            {
                Toast("Subscribed", $"{mod.Name} will now auto-download and auto-update.", ToastType.Success);
                CloseModDetail();
                await InstallFlowAsync(mod, silent: false);
            }
            else
            {
                Toast("Unsubscribed", $"{mod.Name} won't be auto-updated anymore.", ToastType.Info);
            }
        }

        private bool IsUpdateInProgress()
        {
            try
            {
                var state = NotifierBridge.GetUpdateState();
                return state == "updating" || state == "downloading";
            }
            catch { return false; }
        }

        private async Task<bool> RunAutoUpdateAsync(UpdateManager.PendingUpdate update)
        {
            TopNav.IsEnabled = false;
            ProgressCancelButton.Visibility = Visibility.Collapsed;
            ProgressStatusText.Text = "Updating AMC Store";
            ProgressSubStatusText.Text = $"A new version (v{update.Version}) is available.";
            ProgressPercentText.Text = "  0%";
            MainProgressBar.Value = 0;
            NavigateTo(ProgressPage, animate: false);

            var progress = new Progress<double>(p =>
            {
                var pct = Math.Min(100, (int)Math.Floor(p));
                MainProgressBar.Value = pct;
                ProgressPercentText.Text = $"{pct,3:0}%";
            });

            NotifierBridge.SetUpdateState("updating", update.Version);
            NotifierBridge.MarkUpdateAttempt(update.Version);

            bool started = await UpdateManager.ApplyAsync(
                update,
                progress,
                status => { try { ProgressStatusText.Text = status; } catch { } },
                sub => { try { ProgressSubStatusText.Text = sub; } catch { } }).ConfigureAwait(true);

            if (!started)
            {

                NotifierBridge.SetUpdateState("idle", "");
                TopNav.IsEnabled = true;
                ProgressCancelButton.Visibility = Visibility.Visible;
                Toast("Update failed", $"Could not install v{update.Version}. The store will still work.", ToastType.Warn);
                return false;
            }

            await Task.Delay(600).ConfigureAwait(true);
            System.Windows.Application.Current.Shutdown();
            Environment.Exit(0);
            return true;
        }

        private bool _accountRegisterMode;

        private async Task RestoreSessionAsync()
        {
            try
            {
                var me = await StoreApi.GetMeAsync().ConfigureAwait(true);
                if (me?.User != null && !string.IsNullOrEmpty(me.User.Username))
                {
                    ApplySignedIn(me.User);
                }
                else if (_prefs.IsSignedIn)
                {

                    _prefs.SessionToken = "";
                    _prefs.Username = "";
                    _prefs.Save();
                }
            }
            catch { }
        }

        private void ApplySignedIn(PublicUser user)
        {
            AccountChipName.Text = "@" + user.Username;
            AccountChipInitial.Text = user.Initial;
            AccountChipAvatar.Source = null;
            _ = LoadAvatarIntoAsync(user.AvatarUrl, img => AccountChipAvatar.Source = img);
            UpdateAccountTabUi();
            NotifierBridge.Update();
        }

        private void UpdateAccountChipGuest()
        {
            AccountChipName.Text = "Sign In";
            AccountChipInitial.Text = "?";
            AccountChipAvatar.Source = null;
        }

        private static async Task LoadAvatarIntoAsync(string url, Action<System.Windows.Media.ImageSource> apply)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                var img = await ImageCache.LoadFromUrlAsync(url).ConfigureAwait(true);
                apply(img);
            }
            catch { }
        }

        private async void AccountChip_Click(object sender, MouseButtonEventArgs e)
        {
            if (_prefs.IsSignedIn)
            {
                await OpenPortfolioAsync(_prefs.Username);
                return;
            }
            OpenAccountOverlay(registerMode: false);
        }

        private void OpenAccountOverlay(bool registerMode)
        {
            _accountRegisterMode = registerMode;
            AccountErrorText.Text = "";
            RegUsernameBox.Clear();
            RegPasswordBox.Clear();
            RegPassword2Box.Clear();
            LoginUsernameBox.Clear();
            LoginPasswordBox.Clear();
            UpdateAccountTabUi();
            AccountOverlay.Visibility = Visibility.Visible;
            (registerMode ? (System.Windows.Controls.Control)RegUsernameBox : LoginUsernameBox).Focus();
        }

        private void UpdateAccountTabUi()
        {
            bool reg = _accountRegisterMode;

            RegisterFields.Visibility = reg ? Visibility.Visible : Visibility.Collapsed;
            LoginFields.Visibility = reg ? Visibility.Collapsed : Visibility.Visible;

            LoginTabButton.Background = reg ? null : FindResource("AccentGradientBrush") as System.Windows.Media.Brush;
            RegisterTabButton.Background = reg ? FindResource("AccentGradientBrush") as System.Windows.Media.Brush : null;
            LoginTabButton.Foreground = reg
                ? (System.Windows.Media.Brush)FindResource("TextSecondaryBrush")
                : System.Windows.Media.Brushes.White;
            RegisterTabButton.Foreground = reg
                ? System.Windows.Media.Brushes.White
                : (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");

            AccountActionButton.Content = reg ? "Create Account" : "Sign In";

            RegUsernamePlaceholder.Visibility = RegUsernameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            RegPass1Placeholder.Visibility = RegPasswordBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            RegPass2Placeholder.Visibility = RegPassword2Box.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            LoginUserPlaceholder.Visibility = LoginUsernameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            LoginPassPlaceholder.Visibility = LoginPasswordBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void LoginTab_Click(object sender, RoutedEventArgs e)
        {
            _accountRegisterMode = false;
            UpdateAccountTabUi();
        }

        private void RegUsername_TextChanged(object sender, TextChangedEventArgs e)
            => RegUsernamePlaceholder.Visibility = RegUsernameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void LoginUser_TextChanged(object sender, TextChangedEventArgs e)
            => LoginUserPlaceholder.Visibility = LoginUsernameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void RegPass1_Changed(object sender, RoutedEventArgs e)
            => RegPass1Placeholder.Visibility = RegPasswordBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void RegPass2_Changed(object sender, RoutedEventArgs e)
            => RegPass2Placeholder.Visibility = RegPassword2Box.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void LoginPass_Changed(object sender, RoutedEventArgs e)
            => LoginPassPlaceholder.Visibility = LoginPasswordBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void RegisterTab_Click(object sender, RoutedEventArgs e)
        {
            _accountRegisterMode = true;
            UpdateAccountTabUi();
        }

        private void AccountClose_Click(object sender, RoutedEventArgs e)
            => AccountOverlay.Visibility = Visibility.Collapsed;

        private async void AccountAction_Click(object sender, RoutedEventArgs e) => await SubmitAccountAsync();

        private async Task SubmitAccountAsync()
        {
            var username = (_accountRegisterMode ? RegUsernameBox.Text : LoginUsernameBox.Text)?.Trim() ?? "";
            var password = _accountRegisterMode ? RegPasswordBox.Password : LoginPasswordBox.Password;

            if (username.Length < 2 || username.Length > 20 || !Regex.IsMatch(username, @"^[A-Za-z0-9_]+$"))
            {
                AccountErrorText.Text = "Usernames are 2-20 characters - letters, numbers and underscores only.";
                return;
            }
            if (password.Length < 4)
            {
                AccountErrorText.Text = "Please use a password of at least 4 characters.";
                return;
            }
            if (_accountRegisterMode && password != RegPassword2Box.Password)
            {
                AccountErrorText.Text = "The two passwords do not match.";
                return;
            }

            AccountActionButton.IsEnabled = false;
            AccountErrorText.Text = "";
            try
            {
                var resp = _accountRegisterMode
                    ? await StoreApi.RegisterAsync(username, password).ConfigureAwait(true)
                    : await StoreApi.LoginAsync(username, password).ConfigureAwait(true);

                if (!resp.Ok || string.IsNullOrEmpty(resp.Token))
                {
                    AccountErrorText.Text = string.IsNullOrWhiteSpace(resp.Error)
                        ? "Could not reach the AMC Store. Try again."
                        : resp.Error.Replace(". ", ".\n");
                    return;
                }

                _prefs.SessionToken = resp.Token;
                _prefs.Username = username;
                _prefs.Save();
                ApplySignedIn(resp.User ?? new PublicUser { Username = username });
                AccountOverlay.Visibility = Visibility.Collapsed;
                Toast($"Welcome{(_accountRegisterMode ? "" : " back")} \uD83D\uDC4B",
                    $"Signed in as @{username}. You can like, comment, subscribe and upload now.",
                    ToastType.Success);
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                AccountErrorText.Text = "Network error - check your internet connection and try again.";
            }
            finally
            {
                AccountActionButton.IsEnabled = true;
            }
        }

        private async Task<bool> EnsureAuthenticatedAsync()
        {
            if (_prefs.IsSignedIn) return true;
            Toast("Account required", "Sign in or create a free account to do that.", ToastType.Info);
            OpenAccountOverlay(registerMode: false);
            return false;
        }

        private async Task LoadCommentsAsync(string modId)
        {
            if (string.IsNullOrEmpty(modId)) return;
            try
            {
                var resp = await StoreApi.GetCommentsAsync(modId).ConfigureAwait(true);

                if (_detailMod == null || _detailMod.Id != modId) return;

                foreach (var c in resp.Comments)
                {
                    c.AvatarImage = null;
                    var url = c.AvatarUrl;
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        _ = LoadAvatarIntoAsync(url, img =>
                        {
                            if (!ReferenceEquals(c.AvatarImage, img)) c.AvatarImage = img;
                        });
                    }
                }

                CommentsList.ItemsSource = resp.Comments;
                DetailCommentCount.Text = resp.Comments.Count == 0 ? "" : $"\u2022  {resp.Comments.Count}";
                NoCommentsText.Visibility = resp.Comments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        private async void CommentInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter) await PostCommentFlowAsync();
        }

        private void CommentInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (CommentPlaceholder == null) return;
            CommentPlaceholder.Visibility = CommentInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void CommentPost_Click(object sender, RoutedEventArgs e) => await PostCommentFlowAsync();

        private async Task PostCommentFlowAsync()
        {
            if (_detailMod == null) return;
            var text = CommentInput.Text?.Trim() ?? "";

            if (text.Length == 0) return;
            if (!await EnsureAuthenticatedAsync()) return;

            CommentInput.Text = "";
            var ok = await StoreApi.PostCommentAsync(_detailMod.Id, text).ConfigureAwait(true);
            if (ok)
            {
                await LoadCommentsAsync(_detailMod.Id);
            }
            else
            {
                CommentInput.Text = text;
                Toast("Couldn't post", "You may be commenting too fast, or your session expired - sign in again.", ToastType.Warn);
            }
        }

        private async Task OpenPortfolioAsync(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return;
            try
            {
                var resp = await StoreApi.GetUserAsync(username).ConfigureAwait(true);
                if (resp?.User == null)
                {
                    Toast("Not found", $"No AMC Store user called \"{username}\".", ToastType.Warn);
                    return;
                }
                var user = resp.User;

                PortfolioUsername.Text = "@" + user.Username;
                var tierBrush = BadgeVisual.UsernameBrush(user.Badges);
                PortfolioUsername.Foreground = tierBrush ?? (System.Windows.Media.SolidColorBrush)FindResource("TextPrimaryBrush");
                PortfolioInitial.Text = user.Initial;
                PortfolioAvatarImage.Source = null;
                _ = LoadAvatarIntoAsync(user.AvatarUrl, img => PortfolioAvatarImage.Source = img);
                PortfolioSubsText.Text = user.SubscriberText;
                PortfolioJoinedText.Text = string.IsNullOrEmpty(user.JoinedText) ? "" : "\u2022  joined " + user.JoinedText;
                PortfolioBioText.Text = string.IsNullOrWhiteSpace(user.Bio) ? "This player hasn't written a bio yet." : user.Bio;
                PortfolioModsStat.Text = $"{user.ModCount} mod{(user.ModCount == 1 ? "" : "s")}";
                PortfolioLikesStat.Text = $"\uD83D\uDC4D {user.TotalLikes:N0} likes earned";
                PortfolioDownloadsStat.Text = $"\u2B07 {user.TotalDownloads:N0} downloads";

                try
                {
                    PortfolioGradA.Color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                        string.IsNullOrWhiteSpace(user.GradA) ? "#7B2FFF" : user.GradA);
                    PortfolioGradB.Color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                        string.IsNullOrWhiteSpace(user.GradB) ? "#2F8DFF" : user.GradB);
                }
                catch
                {
                    PortfolioGradA.Color = System.Windows.Media.Color.FromRgb(0x7B, 0x2F, 0xFF);
                    PortfolioGradB.Color = System.Windows.Media.Color.FromRgb(0x2F, 0x8D, 0xFF);
                }

                BuildBadgeHost(PortfolioBadgeHost, user.Badges);

                bool isSelf = _prefs.IsSignedIn &&
                              string.Equals(_prefs.Username, user.Username, StringComparison.OrdinalIgnoreCase);
                EditProfileButton.Visibility = isSelf ? Visibility.Visible : Visibility.Collapsed;
                PortfolioDangerZone.Visibility = isSelf ? Visibility.Visible : Visibility.Collapsed;
                if (isSelf)
                {
                    PortfolioNotifRow.Visibility = Visibility.Visible;
                    DoNotDisturbToggle.IsChecked = NotifierBridge.GetDoNotDisturb();
                }
                else
                {
                    PortfolioNotifRow.Visibility = Visibility.Collapsed;
                }

                PortfolioNoModsText.Visibility = Visibility.Collapsed;
                PortfolioModList.ItemsSource = new ObservableCollection<StoreMod>();

                var mods = await StoreApi.GetUserModsAsync(username).ConfigureAwait(true);
                if (PortfolioUsername.Text != "@" + user.Username) return;

                foreach (var m in mods)
                {
                    m.Liked = _prefs.HasLiked(m.Id);
                    m.Disliked = _prefs.HasDisliked(m.Id);
                    m.Subscribed = _prefs.IsSubscribed(m.Id);
                }
                ((ObservableCollection<StoreMod>)PortfolioModList.ItemsSource).ResetWith(mods);
                PortfolioNoModsText.Visibility = mods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                _ = LoadCardImagesAsync(mods, new CancellationToken());

                PortfolioOverlay.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                Toast("Offline", "Could not load that profile.", ToastType.Warn);
            }
        }

        private void BuildBadgeHost(ContentControl host, List<string> badges)
        {
            host.Content = null;
            if (badges == null || badges.Count == 0) return;

            var row = BadgeVisual.BuildIconRow(badges);
            if (row == null) return;

            var border = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 2, 7, 2),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center
            };
            var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            panel.Children.Add(row);
            border.Child = panel;
            host.Content = border;
        }

        private void PortfolioBack_Click(object sender, RoutedEventArgs e)
            => PortfolioOverlay.Visibility = Visibility.Collapsed;

        private async void SignOut_Click(object sender, RoutedEventArgs e)
        {
            var yes = await ShowDialogAsync("Sign out",
                $"Forget @{_prefs.Username} on this computer?\n\nYour uploads, likes and comments stay on the AMC Store - you can sign back in anytime.",
                emoji: "\uD83D\uDE4B", buttons: DialogButtons.YesNo);
            if (yes != true) return;

            StoreApi.Logout();
            SignOutLocally();
            Toast("Signed out", "This computer no longer remembers your account.", ToastType.Success);
        }

        private async void FactoryWipe_Click(object sender, RoutedEventArgs e)
        {
            var yes = await ShowDialogAsync("Factory wipe",
                "Erase ALL AMC Store data from this computer?\n\n" +
                "\u2022 your signed-in account\n" +
                "\u2022 votes, subscriptions and this device's ID\n" +
                "\u2022 cached icons, logs and update flags\n\n" +
                "Your uploads stay on the AMC Store. Mods already installed into Gorilla Tag are not touched.\n" +
                "Your Gorilla Tag folder setting is kept.",
                emoji: "\u26A0\uFE0F", buttons: DialogButtons.YesNo);
            if (yes != true) return;

            var sure = await ShowDialogAsync("Are you sure?",
                "Last chance - this cannot be undone. Wipe this PC clean now?",
                emoji: "\u26A0\uFE0F", buttons: DialogButtons.YesNo);
            if (sure != true) return;

            StoreApi.Logout();
            _prefs = AppPrefs.WipeAllLocalData();
            UpdateAccountChipGuest();
            PortfolioOverlay.Visibility = Visibility.Collapsed;
            Toast("Wiped clean \uD83E\uDDF9", "All AMC Store data was removed from this computer.", ToastType.Success);
        }

        private void SignOutLocally()
        {
            _prefs.SessionToken = "";
            _prefs.Username = "";
            _prefs.Save();
            UpdateAccountChipGuest();
            PortfolioOverlay.Visibility = Visibility.Collapsed;
            NotifierBridge.Update();
        }

        private void DoNotDisturbToggle_Changed(object sender, RoutedEventArgs e)
        {
            bool on = DoNotDisturbToggle?.IsChecked == true;
            NotifierBridge.SetDoNotDisturb(on);
            Toast(on ? "Do Not Disturb on \uD83D\uDD15" : "Do Not Disturb off \uD83D\uDD14",
                on ? "Background notifications are paused." : "Background notifications are back on.",
                on ? ToastType.Info : ToastType.Success);
        }

        private async void OpenChangelog_Click(object sender, RoutedEventArgs e)
            => await ShowChangelogAsync();

        private async Task ShowChangelogAsync()
        {
            ChangelogOverlay.Visibility = Visibility.Visible;
            ChangelogAppVersion.Text = "you're on " + AppMeta.CurrentVersion;
            ChangelogList.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<ChangelogVersion>();
            try
            {
                var list = await StoreApi.GetChangelogAsync();
                if (list != null && list.Count > 0)
                {
                    ChangelogList.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<ChangelogVersion>(list);

                    var latest = list[0]?.Version ?? "";
                    if (!string.IsNullOrEmpty(latest))
                    {
                        _prefs.LastSeenChangelogVersion = latest;
                        _prefs.Save();
                        ChangelogNewBadge.Visibility = Visibility.Collapsed;
                    }
                }
            }
            catch
            {
                Toast("Offline", "Couldn't load the change log. Check your connection.", ToastType.Warn);
            }
        }

        private async Task CheckChangelogBadgeAsync()
        {
            try
            {
                var list = await StoreApi.GetChangelogAsync();
                if (list == null || list.Count == 0) return;

                var latest = list[0]?.Version ?? "";
                var seen = _prefs.LastSeenChangelogVersion ?? "";

                if (!string.IsNullOrEmpty(latest) && !string.Equals(latest, seen, StringComparison.OrdinalIgnoreCase))
                {
                    ChangelogNewBadge.Visibility = Visibility.Visible;
                }
                else
                {
                    ChangelogNewBadge.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
        }

        private void ChangelogClose_Click(object sender, RoutedEventArgs e)
            => ChangelogOverlay.Visibility = Visibility.Collapsed;

        private void ChangelogOverlay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.Source is Grid)
                ChangelogOverlay.Visibility = Visibility.Collapsed;
        }

        private async void Avatar_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return;

            string username = null;
            if (sender is FrameworkElement fe)
            {
                username = fe.DataContext switch
                {
                    CommentItem c => c.User,
                    StoreMod m => !string.IsNullOrEmpty(m.Uploader) ? m.Uploader : null,
                    PublicUser u => u.Username,
                    _ => null
                };
            }

            if (string.IsNullOrWhiteSpace(username)) return;
            DetailOverlay.Visibility = Visibility.Collapsed;
            await OpenPortfolioAsync(username);
        }

        private async void DetailCreator_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2 || _detailMod == null) return;
            var uploader = string.IsNullOrWhiteSpace(_detailMod.Uploader)
                ? _detailMod.Creator
                : _detailMod.Uploader;
            await OpenPortfolioAsync(uploader);
        }

        private string _uploadZipPath;
        private string _uploadThumbPath;
        private string _uploadConfigPath;

        private async void UploadOpen_Click(object sender, RoutedEventArgs e)
        {
            if (!await EnsureAuthenticatedAsync()) return;
            OpenUploadOverlay();
        }

        private void OpenUploadOverlay()
        {
            UploadNameBox.Clear();
            UploadVersionBox.Text = "1.0.0";
            UploadDescBox.Clear();
            UploadErrorText.Text = "";
            PickZipButton.Content = "\uD83D\uDCCE  Choose mod file (.zip)";
            PickThumbButton.Content = "\uD83D\uDDBC Thumbnail";
            PickConfigButton.Content = "\u2699  Add recommended config (.cfg)";
            _uploadZipPath = null;
            _uploadThumbPath = null;
            _uploadConfigPath = null;
            UploadDescPlaceholder.Visibility = Visibility.Visible;
            UploadOverlay.Visibility = Visibility.Visible;
            UploadNameBox.Focus();
        }

        private void CloseUpload() => UploadOverlay.Visibility = Visibility.Collapsed;

        private void UploadClose_Click(object sender, RoutedEventArgs e) => CloseUpload();

        private void UploadDesc_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (UploadDescPlaceholder == null) return;
            UploadDescPlaceholder.Visibility = UploadDescBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UploadName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (UploadNamePlaceholder == null) return;
            UploadNamePlaceholder.Visibility = UploadNameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PickZip_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose your mod file",
                Filter = "Mod archive (*.zip)|*.zip"
            };
            if (dlg.ShowDialog(this) != true) return;

            if (new FileInfo(dlg.FileName).Length > 5 * 1024 * 1024)
            {
                UploadErrorText.Text = "That file is larger than the 5 MB community limit.";
                _uploadZipPath = null;
                PickZipButton.Content = "\uD83D\uDCCE  Choose mod file (.zip)";
                return;
            }

            _uploadZipPath = dlg.FileName;
            UploadErrorText.Text = "";
            PickZipButton.Content = "\uD83D\uDCC1  " + Path.GetFileName(dlg.FileName);
        }

        private void PickThumb_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a thumbnail image",
                Filter = "Images (*.png;*.jpg;*.jpeg;*.webp;*.gif)|*.png;*.jpg;*.jpeg;*.webp;*.gif"
            };
            if (dlg.ShowDialog(this) != true) return;

            _uploadThumbPath = dlg.FileName;
            PickThumbButton.Content = "\uD83D\uDDBC " + Path.GetFileName(dlg.FileName);
        }

        private void PickConfig_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose the recommended config file",
                Filter = "Config files (*.cfg;*.json;*.txt;*.toml;*.yaml;*.yml;*.ini)|*.cfg;*.json;*.txt;*.toml;*.yaml;*.yml;*.ini|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;

            var check = StoreApi.ValidateConfigFile(dlg.FileName);
            if (!check.Ok)
            {
                UploadErrorText.Text = check.Error;
                return;
            }

            _uploadConfigPath = dlg.FileName;
            UploadErrorText.Text = "";
            PickConfigButton.Content = "\u2699  " + Path.GetFileName(dlg.FileName);
        }

        private async void UploadSubmit_Click(object sender, RoutedEventArgs e)
        {
            var name = UploadNameBox.Text?.Trim() ?? "";
            var desc = UploadDescBox.Text?.Trim() ?? "";
            var version = NormalizeVersion(UploadVersionBox.Text);

            if (name.Length < 3) { UploadErrorText.Text = "Give your mod a name (at least 3 characters)."; return; }
            if (string.IsNullOrWhiteSpace(version)) { UploadErrorText.Text = "Enter a version like 1.0.0."; return; }
            if (desc.Length == 0) { UploadErrorText.Text = "Add a short description so players know what your mod does."; return; }
            if (string.IsNullOrEmpty(_uploadZipPath)) { UploadErrorText.Text = "Choose your .zip mod file first."; return; }

            UploadSubmitButton.IsEnabled = false;
            UploadErrorText.Text = "";
            try
            {
                var (ok, error) = await StoreApi.CommunityUploadAsync(
                    name, desc, version, _uploadZipPath, iconPath: null, thumbnailPath: _uploadThumbPath,
                    configPath: _uploadConfigPath).ConfigureAwait(true);

                if (!ok)
                {
                    UploadErrorText.Text = string.IsNullOrWhiteSpace(error)
                        ? "Upload failed - check your connection and try again."
                        : error.Replace(". ", ".\n");
                    return;
                }

                CloseUpload();
                Toast("Published \uD83C\uDF89",
                    $"{name} is live under Unverified mods. It becomes Official once a team member reviews it.",
                    ToastType.Success);
                _searchDebounce?.Stop();
                _ = LoadStoreAsync();
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                UploadErrorText.Text = "Upload failed - check your internet connection.";
            }
            finally
            {
                UploadSubmitButton.IsEnabled = true;
            }
        }

        private StoreMod _manageMod;
        private string _manThumbPath;
        private string _manZipPath;
        private string _manConfigPath;

        private async void ManageOpen_Click(object sender, RoutedEventArgs e)
            => await OpenManageForModAsync(_detailMod);

        private async Task OpenManageForModAsync(StoreMod mod)
        {
            if (mod == null) return;
            if (!await EnsureAuthenticatedAsync()) return;

            if (!string.Equals(mod.Uploader, _prefs.Username, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var remote = await StoreApi.GetModAsync(mod.Id).ConfigureAwait(true);
                    if (remote != null) mod = remote;
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(mod.Uploader) &&
                !string.Equals(mod.Uploader, _prefs.Username, StringComparison.OrdinalIgnoreCase))
            {
                Toast("Not yours", "You can only manage mods you uploaded.", ToastType.Warn);
                return;
            }

            _manageMod = mod;
            _manThumbPath = null;
            _manZipPath = null;
            _manConfigPath = null;
            ManageModName.Text = mod.Name ?? "mod";
            ManageNameBox.Text = mod.Name ?? "";
            ManageVersionBox.Text = NormalizeVersion(mod.Version);
            ManageDescBox.Text = mod.Description ?? "";
            ManPickThumbButton.Content = "\uD83D\uDDBC  Replace thumbnail (optional)";
            ManPickZipButton.Content = "\uD83D\uDCC1  Choose new build (.zip)";
            ManPickConfigButton.Content = mod.HasConfig
                ? "\u2699  Replace config: " + mod.ConfigDisplayName
                : "\u2699  Add recommended config (.cfg)";
            ManageErrorText.Text = "";
            ManageOverlay.Visibility = Visibility.Visible;
        }

        private void ManageClose_Click(object sender, RoutedEventArgs e) => ManageOverlay.Visibility = Visibility.Collapsed;

        private void ManPickThumb_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a new thumbnail",
                Filter = "Images (*.png;*.jpg;*.jpeg;*.webp;*.gif)|*.png;*.jpg;*.jpeg;*.webp;*.gif"
            };
            if (dlg.ShowDialog(this) != true) return;
            if (new FileInfo(dlg.FileName).Length > 500 * 1024)
            {
                ManageErrorText.Text = "Thumbnail must be under 500 KB.";
                return;
            }
            _manThumbPath = dlg.FileName;
            ManageErrorText.Text = "";
            ManPickThumbButton.Content = "\uD83D\uDDBC  " + Path.GetFileName(dlg.FileName);
        }

        private void ManPickConfig_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose the recommended config file",
                Filter = "Config files (*.cfg;*.json;*.txt;*.toml;*.yaml;*.yml;*.ini)|*.cfg;*.json;*.txt;*.toml;*.yaml;*.yml;*.ini|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;

            var check = StoreApi.ValidateConfigFile(dlg.FileName);
            if (!check.Ok)
            {
                ManageErrorText.Text = check.Error;
                return;
            }

            _manConfigPath = dlg.FileName;
            ManageErrorText.Text = "";
            ManPickConfigButton.Content = "\u2699  " + Path.GetFileName(dlg.FileName);
        }

        private async void ManageSaveDetails_Click(object sender, RoutedEventArgs e)
        {
            if (_manageMod == null) return;
            var name = ManageNameBox.Text?.Trim() ?? "";
            var version = NormalizeVersion(ManageVersionBox.Text);
            var desc = ManageDescBox.Text?.Trim() ?? "";

            if (name.Length < 3) { ManageErrorText.Text = "Mod name needs at least 3 characters."; return; }
            if (string.IsNullOrWhiteSpace(version)) { ManageErrorText.Text = "Enter a version like 1.0.0."; return; }

            ManageDeleteButton.IsEnabled = false;
            ManageErrorText.Text = "";
            try
            {
                var (ok, error) = await StoreApi.OwnerEditModAsync(_manageMod.Id, name, desc, version, _manThumbPath, _manConfigPath).ConfigureAwait(true);
                if (!ok)
                {
                    ManageErrorText.Text = string.IsNullOrWhiteSpace(error) ? "Could not save - try again." : error.Replace(". ", ".\n");
                    return;
                }

                _manageMod.Name = name;
                _manageMod.Version = version;
                _manageMod.Description = desc;
                DetailName.Text = name;
                DetailDescriptionText.Text = string.IsNullOrWhiteSpace(desc) ? "No description provided." : desc;
                DetailVersionChip.Text = "v" + version;

                CloseDetailAndRefreshStore();
                Toast("Saved \u2705", $"{name} was updated.", ToastType.Success);
                ManageOverlay.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                ManageErrorText.Text = "Could not save - check your connection.";
            }
            finally
            {
                ManageDeleteButton.IsEnabled = true;
            }
        }

        private void ManPickZip_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose the new build",
                Filter = "Mod archive (*.zip)|*.zip"
            };
            if (dlg.ShowDialog(this) != true) return;
            if (new FileInfo(dlg.FileName).Length > 5 * 1024 * 1024)
            {
                ManageErrorText.Text = "That file is larger than the 5 MB community limit.";
                return;
            }
            _manZipPath = dlg.FileName;
            ManageErrorText.Text = "";
            ManPickZipButton.Content = "\uD83D\uDCC1  " + Path.GetFileName(dlg.FileName);
        }

        private async void ManageUploadBuild_Click(object sender, RoutedEventArgs e)
        {
            if (_manageMod == null) return;
            if (string.IsNullOrEmpty(_manZipPath))
            {
                ManageErrorText.Text = "Choose the new .zip build first.";
                return;
            }
            var version = NormalizeVersion(ManageVersionBox.Text);
            if (string.IsNullOrWhiteSpace(version)) { ManageErrorText.Text = "Enter a version like 1.0.1."; return; }

            ManageDeleteButton.IsEnabled = false;
            ManageErrorText.Text = "";
            try
            {
                var (ok, error) = await StoreApi.OwnerNewBuildAsync(_manageMod.Id, _manZipPath, version).ConfigureAwait(true);
                if (!ok)
                {
                    ManageErrorText.Text = string.IsNullOrWhiteSpace(error) ? "Build upload failed - try again." : error.Replace(". ", ".\n");
                    return;
                }

                _manageMod.Version = version;
                DetailVersionChip.Text = "v" + version;
                CloseDetailAndRefreshStore();
                Toast("Build published \uD83C\uDF89",
                    $"v{version} is live. Subscribers get it automatically.", ToastType.Success);
                ManageOverlay.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                ManageErrorText.Text = "Build upload failed - check your connection.";
            }
            finally
            {
                ManageDeleteButton.IsEnabled = true;
            }
        }

        private async void ManageDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_manageMod == null) return;
            var yes = await ShowDialogAsync("Delete mod",
                $"Permanently delete \"{_manageMod.Name}\"?\n\nThe download and images are removed from the store for everyone. This cannot be undone.",
                emoji: "\uD83D\uDDD1\uFE0F", buttons: DialogButtons.YesNo);
            if (yes != true) return;

            try
            {
                var (ok, error) = await StoreApi.OwnerDeleteModAsync(_manageMod.Id).ConfigureAwait(true);
                if (!ok)
                {
                    ManageErrorText.Text = string.IsNullOrWhiteSpace(error) ? "Delete failed - try again." : error;
                    return;
                }
                CloseDetailAndRefreshStore();
                Toast("Deleted", $"{_manageMod.Name} was removed from the store.", ToastType.Success);
                ManageOverlay.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                ManageErrorText.Text = "Delete failed - check your connection.";
            }
        }

        private void CloseDetailAndRefreshStore()
        {
            DetailOverlay.Visibility = Visibility.Collapsed;
            _searchDebounce?.Stop();
            _ = LoadStoreAsync();
        }

        private string _editAvatarPath;
        private System.Windows.Media.Imaging.BitmapImage _editAvatarSource;
        private bool _cropDragging;
        private System.Windows.Point _cropDragStart;
        private double _cropStartTranslateX;
        private double _cropStartTranslateY;

        private void EditProfileOpen_Click(object sender, RoutedEventArgs e)
        {
            if (!_prefs.IsSignedIn) return;

            EditProfileErrorText.Text = "";
            _editAvatarPath = null;
            _editAvatarSource = null;
            EditAvatarImage.Source = AccountChipAvatar.Source;
            AvatarCropImage.Source = null;
            AvatarCropPanel.Visibility = Visibility.Collapsed;
            AvatarZoomSlider.Value = 100;
            AvatarCropScale.ScaleX = 1;
            AvatarCropScale.ScaleY = 1;
            AvatarCropTranslate.X = 0;
            AvatarCropTranslate.Y = 0;

            var name = _prefs.Username ?? "";
            EditAvatarInitial.Text = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
            _ = FillEditProfileAsync();
            EditProfileOverlay.Visibility = Visibility.Visible;
        }

        private async Task FillEditProfileAsync()
        {
            try
            {
                var resp = await StoreApi.GetUserAsync(_prefs.Username).ConfigureAwait(true);
                if (resp?.User == null || !EditProfileOverlay.IsVisible) return;

                if (EditBioBox.Text.Length == 0) EditBioBox.Text = resp.User.Bio ?? "";
                EditGradABox.Text = string.IsNullOrWhiteSpace(resp.User.GradA) ? "#7B2FFF" : resp.User.GradA;
                EditGradBBox.Text = string.IsNullOrWhiteSpace(resp.User.GradB) ? "#2F8DFF" : resp.User.GradB;
                UpdateGradSwatches();
            }
            catch { }
        }

        private void EditBio_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (EditBioPlaceholder == null) return;
            EditBioPlaceholder.Visibility = EditBioBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void GradPreview_TextChanged(object sender, TextChangedEventArgs e)
        {

            if (EditGradABox == null || EditGradBBox == null) return;
            UpdateGradSwatches();
        }

        private void UpdateGradSwatches()
        {

            if (GradASwatch == null || GradBSwatch == null ||
                EditGradABox == null || EditGradBBox == null) return;

            try { GradASwatch.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(EditGradABox.Text)); }
            catch { }
            try { GradBSwatch.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(EditGradBBox.Text)); }
            catch { }
        }

        private void GradASwatch_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            PickGradientColor(EditGradABox);
        }

        private void GradBSwatch_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            PickGradientColor(EditGradBBox);
        }

        private void PickGradientColor(System.Windows.Controls.TextBox box)
        {
            using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true };
            try
            {
                var current = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(box.Text);
                dlg.Color = System.Drawing.Color.FromArgb(current.A, current.R, current.G, current.B);
            }
            catch { }
            var owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var result = owner != IntPtr.Zero
                ? dlg.ShowDialog(new OwnerWindow(owner))
                : dlg.ShowDialog();
            if (result != System.Windows.Forms.DialogResult.OK) return;
            var c = dlg.Color;
            box.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        private sealed class OwnerWindow : System.Windows.Forms.IWin32Window
        {
            public OwnerWindow(IntPtr handle) => Handle = handle;
            public IntPtr Handle { get; }
        }

        private void EditAvatarPick_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a profile picture",
                Filter = "Images (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp"
            };
            if (dlg.ShowDialog(this) != true) return;

            if (new FileInfo(dlg.FileName).Length > 500 * 1024)
            {
                EditProfileErrorText.Text = "That image is larger than the 500 KB limit - pick a smaller one.";
                return;
            }

            _editAvatarPath = dlg.FileName;
            EditProfileErrorText.Text = "";
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(dlg.FileName);
                bmp.EndInit();
                bmp.Freeze();
                _editAvatarSource = bmp;
                EditAvatarImage.Source = bmp;
                AvatarCropImage.Source = bmp;
                AvatarZoomSlider.Value = 100;
                AvatarCropScale.ScaleX = 1;
                AvatarCropScale.ScaleY = 1;
                AvatarCropTranslate.X = 0;
                AvatarCropTranslate.Y = 0;
                _cropDragging = false;
                _ = AvatarCropHost.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                    new Action(() => AvatarCropPanel.Visibility = Visibility.Visible));
            }
            catch
            {
                _editAvatarPath = null;
                _editAvatarSource = null;
                EditProfileErrorText.Text = "That image couldn't be opened - try a different one.";
            }
        }

        private void AvatarZoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (AvatarCropScale == null || _editAvatarSource == null) return;
            var zoom = AvatarZoomSlider.Value / 100.0;
            var old = AvatarCropScale.ScaleX;
            if (Math.Abs(old - zoom) < 0.001) return;
            var hostW = AvatarCropHost.ActualWidth > 0 ? AvatarCropHost.ActualWidth : 190;
            var hostH = AvatarCropHost.ActualHeight > 0 ? AvatarCropHost.ActualHeight : 190;
            var factor = zoom / old;
            if (!_cropDragging)
            {
                AvatarCropTranslate.X = hostW / 2 - (hostW / 2 - AvatarCropTranslate.X) * factor;
                AvatarCropTranslate.Y = hostH / 2 - (hostH / 2 - AvatarCropTranslate.Y) * factor;
            }
            AvatarCropScale.ScaleX = zoom;
            AvatarCropScale.ScaleY = zoom;
            ClampCropTranslate();
        }

        private void AvatarCrop_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_editAvatarSource == null) return;
            _cropDragging = true;
            _cropDragStart = e.GetPosition(AvatarCropHost);
            _cropStartTranslateX = AvatarCropTranslate.X;
            _cropStartTranslateY = AvatarCropTranslate.Y;
            AvatarCropHost.CaptureMouse();
            e.Handled = true;
        }

        private void AvatarCrop_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_cropDragging) return;
            var pos = e.GetPosition(AvatarCropHost);
            AvatarCropTranslate.X = _cropStartTranslateX + (pos.X - _cropDragStart.X);
            AvatarCropTranslate.Y = _cropStartTranslateY + (pos.Y - _cropDragStart.Y);
            ClampCropTranslate();
        }

        private void AvatarCrop_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_cropDragging)
            {
                _cropDragging = false;
                AvatarCropHost.ReleaseMouseCapture();
            }
        }

        private void AvatarCrop_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_cropDragging)
            {
                _cropDragging = false;
                AvatarCropHost.ReleaseMouseCapture();
            }
        }

        private void AvatarCrop_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (_editAvatarSource == null) return;
            var factor = e.Delta > 0 ? 1.12 : (1.0 / 1.12);
            var zoom = Math.Max(0.5, Math.Min(4.0, AvatarCropScale.ScaleX * factor));
            AvatarZoomSlider.Value = zoom * 100;
            e.Handled = true;
        }

        private void AvatarCropReset_Click(object sender, RoutedEventArgs e)
        {
            AvatarZoomSlider.Value = 100;
            AvatarCropScale.ScaleX = 1;
            AvatarCropScale.ScaleY = 1;
            AvatarCropTranslate.X = 0;
            AvatarCropTranslate.Y = 0;
        }

        private double CropDrawWidth
        {
            get
            {
                if (_editAvatarSource == null) return 0;
                var hostW = AvatarCropHost.ActualWidth > 0 ? AvatarCropHost.ActualWidth : 190;
                var fill = hostW / Math.Max(1, _editAvatarSource.PixelWidth);
                return Math.Max(1, _editAvatarSource.PixelWidth) * fill * AvatarCropScale.ScaleX;
            }
        }

        private double CropDrawHeight
        {
            get
            {
                if (_editAvatarSource == null) return 0;
                var hostH = AvatarCropHost.ActualHeight > 0 ? AvatarCropHost.ActualHeight : 190;
                var fill = hostH / Math.Max(1, _editAvatarSource.PixelHeight);
                return Math.Max(1, _editAvatarSource.PixelHeight) * fill * AvatarCropScale.ScaleY;
            }
        }

        private void ClampCropTranslate()
        {
            if (_editAvatarSource == null) return;
            var hostW = AvatarCropHost.ActualWidth > 0 ? AvatarCropHost.ActualWidth : 190;
            var hostH = AvatarCropHost.ActualHeight > 0 ? AvatarCropHost.ActualHeight : 190;
            var w = CropDrawWidth;
            var h = CropDrawHeight;
            var tx = AvatarCropTranslate.X;
            var ty = AvatarCropTranslate.Y;
            AvatarCropTranslate.X = Math.Min(0, Math.Max(hostW - w, tx));
            AvatarCropTranslate.Y = Math.Min(0, Math.Max(hostH - h, ty));
        }

        private string RenderAvatarCrop()
        {
            int size = 256;
            while (true)
            {
                try
                {
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        size, size, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(AvatarCropImage);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    var path = Path.Combine(Path.GetTempPath(), $"amc_avatar_{Guid.NewGuid():N}.png");
                    using (var fs = new FileStream(path, FileMode.Create)) encoder.Save(fs);
                    if (new FileInfo(path).Length <= 500 * 1024) return path;
                    try { File.Delete(path); } catch { }
                }
                catch { return null; }
                if (size <= 128) return null;
                size = size * 3 / 4;
            }
        }

        private void EditProfileClose_Click(object sender, RoutedEventArgs e)
            => EditProfileOverlay.Visibility = Visibility.Collapsed;

        private async void EditProfileSave_Click(object sender, RoutedEventArgs e)
        {
            var bio = EditBioBox.Text?.Trim() ?? "";
            var gradA = EditGradABox.Text?.Trim() ?? "";
            var gradB = EditGradBBox.Text?.Trim() ?? "";

            foreach (var g in new[] { gradA, gradB })
            {
                if (!Regex.IsMatch(g, @"^#[0-9A-Fa-f]{6}$"))
                {
                    EditProfileErrorText.Text = "Theme colors must be hex codes like #7B2FFF.";
                    return;
                }
            }

            EditProfileSaveButton.IsEnabled = false;
            EditProfileErrorText.Text = "";
            try
            {
                var (ok, error) = await StoreApi.UpdateProfileAsync(bio, gradA, gradB).ConfigureAwait(true);
                if (!ok)
                {
                    EditProfileErrorText.Text = string.IsNullOrWhiteSpace(error)
                        ? "Could not save your profile - check your connection."
                        : error.Replace(". ", ".\n");
                    return;
                }

                if (!string.IsNullOrEmpty(_editAvatarPath) && _editAvatarSource != null)
                {
                    var png = RenderAvatarCrop();
                    if (string.IsNullOrEmpty(png))
                    {
                        EditProfileErrorText.Text = "Couldn't prepare your picture - try picking it again.";
                        return;
                    }
                    var (avOk, avError) = await StoreApi.UploadAvatarAsync(png).ConfigureAwait(true);
                    try { File.Delete(png); } catch { }
                    if (!avOk)
                    {
                        EditProfileErrorText.Text = string.IsNullOrWhiteSpace(avError)
                            ? "Profile saved, but the picture upload failed."
                            : avError.Replace(". ", ".\n");
                        return;
                    }
                    _editAvatarPath = null;
                    _editAvatarSource = null;
                }

                EditProfileOverlay.Visibility = Visibility.Collapsed;
                Toast("Profile updated", "Your new look is live.", ToastType.Success);

                await OpenPortfolioAsync(_prefs.Username);
                _ = RestoreSessionAsync();
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                EditProfileErrorText.Text = "Network error - your profile was not saved.";
            }
            finally
            {
                EditProfileSaveButton.IsEnabled = true;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public class InstalledModRow : INotifyPropertyChanged
    {
        private bool _updateAvailable;
        public InstalledMod Entry { get; }

        public InstalledModRow(InstalledMod entry)
        {
            Entry = entry;
            entry.PropertyChanged += (s, e) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(e.PropertyName));
        }

        public string Name => Entry.Name;
        public string Version => Entry.Version;
        public string Creator => Entry.Creator;
        public string InstalledAtText => Entry.InstalledAtText;
        public string SizeText => Entry.SizeText;
        public string Initial => string.IsNullOrEmpty(Entry.Name) ? "?" : Entry.Name.Substring(0, 1).ToUpperInvariant();
        public System.Windows.Media.ImageSource IconImage => Entry.IconImage;

        public bool UpdateAvailable
        {
            get => _updateAvailable;
            set
            {
                if (_updateAvailable == value) return;
                _updateAvailable = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdateAvailable)));
            }
        }

        public bool CanEdit
        {
            get => _canEdit;
            set
            {
                if (_canEdit == value) return;
                _canEdit = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanEdit)));
            }
        }
        private bool _canEdit;

        public event PropertyChangedEventHandler PropertyChanged;
    }

    internal static class CollectionExtensions
    {
        public static void ResetWith<T>(this ObservableCollection<T> target, IEnumerable<T> items)
        {
            target.Clear();
            foreach (var item in items) target.Add(item);
        }

        public static void ClearIfPossible<T>(this ObservableCollection<T> target) => target?.Clear();
    }
}
