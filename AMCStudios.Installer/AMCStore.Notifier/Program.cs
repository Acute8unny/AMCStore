using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Toolkit.Uwp.Notifications;

namespace AMCStore.Notifier
{
    internal static class Program
    {
        private static Mutex _mutex;
        private static NotifyIcon _icon;
        private static NotifierWorker _worker;
        private static ToolStripMenuItem _dndItem;
        private static ToolStripMenuItem _appOpenPrefItem;
        private static bool _toastReady;



        public const string AumId = "AMCStudios.AMCStore";

        [STAThread]
        private static void Main()
        {

            bool createdNew;
            _mutex = new Mutex(true, "AMCStore.Notifier.Singleton", out createdNew);
            if (!createdNew) return;



            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Log($"Unhandled exception: {e.ExceptionObject}");
            Application.ThreadException += (s, e) =>
                Log($"UI thread exception: {e.Exception}");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            InitToasts();

            _worker = new NotifierWorker(CreateTrayIcon());

            _worker.Start();

            Application.Run();
        }

        private static readonly object LogLock = new object();
        private static string LogPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AMCStore", "notifier.log");

        public static void Log(string msg)
        {
            try
            {
                lock (LogLock)
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath));
                    System.IO.File.AppendAllText(LogPath,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
                }
            }
            catch { }
        }

        private static void InitToasts()
        {
            try
            {



                var t = new Thread(() =>
                {
                    try
                    {
                        ToastNotificationManagerCompat.CreateToastNotifier();
                        _toastReady = true;
                        Log("Legacy toasts: initialized & ready.");
                    }
                    catch (Exception ex) { _toastReady = false; Log($"Toasts failed to init: {ex}"); }
                });
                t.IsBackground = true;
                t.Start();
            }
            catch (Exception ex) { _toastReady = false; Log($"Toast thread start failed: {ex}"); }
        }

        public static bool ToastReady => _toastReady;

        private static NotifyIcon CreateTrayIcon()
        {
            _icon = new NotifyIcon { Visible = true, Text = "AMC Store Notifications" };


            _icon.Icon = MakeIcon();
            _icon.BalloonTipTitle = "AMC Store";
            _icon.BalloonTipText = "";

            var menu = new ContextMenuStrip();

            menu.Items.Add("Open AMC Store", null, (s, e) => NotifierWorker.LaunchStore());

            _dndItem = new ToolStripMenuItem("Do Not Disturb");
            _dndItem.Click += (s, e) =>
            {
                var cfg = NotifierConfig.Load();
                cfg.DoNotDisturb = !cfg.DoNotDisturb;
                cfg.Save();
                _dndItem.Checked = cfg.DoNotDisturb;
            };
            menu.Items.Add(_dndItem);

            menu.Items.Add("Send test notification", null, (s, e) => SendTestNotification());

            _appOpenPrefItem = new ToolStripMenuItem("Notify when AMC Store is open") { ToolTipText = "Get notifications even while the AMC Store app is open on screen." };
            _appOpenPrefItem.Checked = NotifierConfig.Load().NotifyWhenAppOpen;
            _appOpenPrefItem.Click += (s, e) =>
            {
                var cfg = NotifierConfig.Load();
                cfg.NotifyWhenAppOpen = !cfg.NotifyWhenAppOpen;
                cfg.Save();
                _appOpenPrefItem.Checked = cfg.NotifyWhenAppOpen;
            };
            menu.Items.Add(_appOpenPrefItem);

            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add("Exit", null, (s, e) => Shutdown());

            _icon.ContextMenuStrip = menu;
            _icon.DoubleClick += (s, e) => NotifierWorker.LaunchStore();

            ApplyDndCheck();
            return _icon;
        }

        private static void ApplyDndCheck()
        {
            try
            {
                if (_dndItem != null) _dndItem.Checked = NotifierConfig.Load().DoNotDisturb;
            }
            catch { }
        }

        private static void SendTestNotification()
        {
            try
            {
                if (_worker == null)
                {
                    CopyAndWarn("Test notification failed: notifier worker is not running.");
                    return;
                }

                var error = _worker.NotifyTest("AMC Store",
                    "Test notification \u2728 If you can see this, notifications are working.");
                if (error == null)
                {
                    Log("Manual test notification shown.");
                    return;
                }

                CopyAndWarn($"Test notification failed: {error}");
            }
            catch (Exception ex)
            {
                CopyAndWarn($"Test notification failed: {ex}");
            }
        }

        private static void CopyAndWarn(string message)
        {
            Log(message);
            try
            {
                System.Windows.Forms.Clipboard.SetText(message);
                MessageBox.Show("Test notification failed.\n\nThe error was copied to your clipboard.",
                    "AMC Store", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch
            {
                MessageBox.Show(message, "AMC Store", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static Icon MakeIcon()
        {
            using var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using var grad = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(0, 0, 16, 16), Color.FromArgb(123, 47, 255), Color.FromArgb(47, 141, 255), 45f);
                g.FillEllipse(grad, 0, 0, 15, 15);
                using var f = new Font("Segoe UI", 8f, FontStyle.Bold);
                using var brush = new SolidBrush(Color.White);
                g.DrawString("A", f, brush, 4f, 2f);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        private static void Shutdown()
        {
            try
            {
                _worker?.Stop();
                _worker?.Dispose();
                if (_icon != null) { _icon.Visible = false; _icon.Dispose(); }
            }
            catch { }
            finally
            {
                ApplyDndCheck();
                Application.ExitThread();
            }
        }
    }
}
