using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace MyDynamicIsland
{
    internal sealed class WindowsNotificationWatcher : IDisposable
    {
        // Whether Windows granted this app the right to read notifications.
        // Reporting it back to the island matters because the failure is
        // completely silent otherwise: RequestAccessAsync just returns Denied
        // and every toast is ignored forever with no visible symptom.
        public enum AccessState { Unknown, Allowed, Denied, Failed }

        public AccessState Access { get; private set; } = AccessState.Unknown;

        // What Windows *actually* said when Access came back Denied/Failed -
        // the raw exception text or the returned enum value. Surfaced in the
        // settings status line so "unavailable" stops being a dead end.
        public string AccessDetail { get; private set; } = "";

        // Raised exactly once per Start() call, once the async access request
        // resolves. The form uses it to self-diagnose a dead notification feed.
        public event Action<AccessState>? AccessResolved;

        private UserNotificationListener? listener;
        public event Action<string, string, byte[]>? NotificationReceived;

        private void SetAccess(AccessState state)
        {
            Access = state;
            AccessResolved?.Invoke(state);
        }

        public async void Start()
        {
            try
            {
                listener = UserNotificationListener.Current;
                UserNotificationListenerAccessStatus access = await listener.RequestAccessAsync();
                if (access != UserNotificationListenerAccessStatus.Allowed)
                {
                    AccessDetail = "Windows returned: " + access;
                    SetAccess(AccessState.Denied);
                    return;
                }

                SetAccess(AccessState.Allowed);
                listener.NotificationChanged += Listener_NotificationChanged;
            }
            catch (Exception ex)
            {
                AccessDetail = ex.GetType().Name + ": " + ex.Message;
                listener = null;
                SetAccess(AccessState.Failed);
            }
        }

        private async void Listener_NotificationChanged(
            UserNotificationListener sender,
            UserNotificationChangedEventArgs args)
        {
            if (args.ChangeKind != UserNotificationChangedKind.Added)
                return;

            // Some apps' banners evaporate between "raised" and "read" - the
            // direct by-id fetch throws for exactly those (this was the
            // WhatsApp-shaped hole).
            UserNotification? notification = null;
            try { notification = sender.GetNotification(args.UserNotificationId); }
            catch
            {
                // The record can be milliseconds too young to be readable.
                // One delayed re-fetch catches the fast-vanishing ones; a
                // retry using only APIs the rest of this file already used,
                // because GetNotificationsAsync(NotificationKinds.*) varies
                // with Windows SDK metadata revisions.
                try
                {
                    await Task.Delay(150);
                    notification = sender.GetNotification(args.UserNotificationId);
                }
                catch { }
            }
            if (notification == null) return;

            try
            {
                string appName = "Notification";
                try { appName = notification.AppInfo?.DisplayInfo?.DisplayName ?? appName; }
                catch { }

                // Generic first, then the dynamic template - apps that hand
                // themselves as raw custom toasts (again: WhatsApp) live in
                // the fallback.
                string message = ExtractText(notification, KnownNotificationBindings.ToastGeneric);
                if (string.IsNullOrWhiteSpace(message))
                    message = ExtractText(notification, "ToastDynamic");
                if (string.IsNullOrWhiteSpace(message))
                    message = "New notification";

                // The real sender icon: DisplayInfo.Logo is the same image the
                // Action Center tiles wear. It can legitimately be big; 3 MB
                // and a 128px downscale later keep a rogue toast from eating
                // RAM in a tray app.
                byte[]? logo = await TryReadLogoAsync(notification);

                NotificationReceived?.Invoke(appName, message, logo);
            }
            catch
            {
            }
        }

        private static async Task<byte[]?> TryReadLogoAsync(UserNotification notification)
        {
            // Source 1: the registered app logo (packaged apps ship a real one).
            Windows.ApplicationModel.AppInfo? info = null;
            try { info = notification.AppInfo; } catch { }
            if (info != null)
            {
                try
                {
                    var logoRef = info.DisplayInfo?.GetLogo(new Windows.Foundation.Size(44, 44));
                    if (logoRef != null)
                    {
                        using Windows.Storage.Streams.IRandomAccessStream stream = await logoRef.OpenReadAsync();
                        if (stream.Size > 0 && stream.Size <= 3_000_000)
                        {
                            using var ms = new MemoryStream();
                            stream.AsStream().CopyTo(ms);
                            if (ms.Length > 64) return ms.ToArray();
                        }
                    }
                }
                catch { /* fall through to the exe probe */ }
            }

            // Source 2: classic desktop apps — their identity carries (or is)
            // the exe path, so pull the real file icon off disk. Chrome's
            // icon, Discord's icon, Notepad++'s — whatever the .exe wears.
            string aumid = "";
            try { aumid = info?.AppUserModelId ?? ""; } catch { }
            return IconFromAddress(aumid);
        }

        static byte[]? IconFromAddress(string aumid)
        {
            try
            {
                if (aumid.Length == 0) return null;
                string? exe = null;
                if (aumid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(aumid))
                    exe = aumid;
                else
                {
                    // "Company.App!C:\folder\thing.exe" — the path hides after the bang.
                    int bang = aumid.LastIndexOf('!');
                    string tail = bang >= 0 ? aumid[(bang + 1)..] : aumid;
                    if (tail.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(tail))
                        exe = tail;
                }
                if (exe == null) return null;
                using var ico = Icon.ExtractAssociatedIcon(exe);
                if (ico == null) return null;
                using var bmp = ico.ToBitmap();
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                return ms.Length > 64 ? ms.ToArray() : null;
            }
            catch { return null; }
        }

        private static string ExtractText(UserNotification notification, string bindingTemplate)
        {
            try
            {
                NotificationBinding? binding = notification.Notification.Visual.GetBinding(bindingTemplate);
                if (binding == null) return "";
                return string.Join(" · ", binding.GetTextElements()
                    .Select(element => element.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Take(2) ?? Array.Empty<string>());
            }
            catch { return ""; }
        }

        public void Dispose()
        {
            if (listener != null)
                listener.NotificationChanged -= Listener_NotificationChanged;
            listener = null;
        }
    }
}
