using Microsoft.Toolkit.Uwp.Notifications;

namespace AMCStore.Notifier
{
    public static class ToastNotifier
    {
        private static readonly object Gate = new object();

        public static bool TryShow(string title, string body)
        {
            try
            {
                if (!Program.ToastReady) return false;
                lock (Gate)
                {
                    new ToastContentBuilder()
                        .AddText(title)
                        .AddText(body)
                        .Show();
                }
                return true;
            }
            catch { return false; }
        }
    }
}