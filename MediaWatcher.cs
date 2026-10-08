using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Runtime.Versioning;
using Windows.ApplicationModel;
using Windows.Media.Control;

namespace MyDynamicIsland
{
    internal class MediaInfo
    {
        public string Title = "";
        public string Artist = "";
        public string Aumid = "";
        public Bitmap? Thumbnail;
    }

    internal class MediaWatcher
    {
        private GlobalSystemMediaTransportControlsSessionManager? manager;
        private GlobalSystemMediaTransportControlsSession? currentSession;

        public event Action<MediaInfo>? MediaChanged;
        public event Action<TimeSpan, TimeSpan, bool>? TimelineChanged; // position, duration, isPlaying

        // Raised whenever the set of live media sessions changes (a second
        // player starts, a tab closes...). The island uses it to redraw the
        // artwork tap target's meaning (switch player vs open app) and the
        // shuffle/launch control on the spot.
        public event Action? SessionsChanged;

        public async void Start()
        {
            try
            {
                manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                manager.CurrentSessionChanged += Manager_CurrentSessionChanged;
                manager.SessionsChanged += Manager_SessionsChanged;
                Hook();
            }
            catch
            {
                // No system media session support on this build/config —
                // the watcher just stays idle instead of taking the app down.
            }
        }

        // Unhooks everything this watcher subscribed to. Call this from the
        // form's FormClosing handler — otherwise the manager and whatever
        // session was last active keep a reference to this object alive.
        public void Stop()
        {
            if (manager != null)
            {
                manager.CurrentSessionChanged -= Manager_CurrentSessionChanged;
                manager.SessionsChanged -= Manager_SessionsChanged;
            }

            if (currentSession != null)
            {
                currentSession.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                currentSession.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
                currentSession.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
            }
        }

        private void Manager_CurrentSessionChanged(
            GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
            => Hook();

        private void Manager_SessionsChanged(
            GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
            => SessionsChanged?.Invoke();

        // Every GMTC session, but only the ones that actually mean something:
        // browsers register a session for each open media tab, half of them
        // dead. Keeping Playing/Paused/Stopped cuts the list down to real
        // players (Spotify + the YouTube tab that's actually loaded).
        private List<GlobalSystemMediaTransportControlsSession> LiveSessions()
        {
            var live = new List<GlobalSystemMediaTransportControlsSession>();
            try
            {
                var sessions = manager?.GetSessions();
                if (sessions == null) return live;
                foreach (var s in sessions)
                {
                    try
                    {
                        var status = s.GetPlaybackInfo().PlaybackStatus;
                        if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ||
                            status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused ||
                            status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped)
                            live.Add(s);
                    }
                    catch { }
                }
            }
            catch { }
            return live;
        }

        // 0/1/2... live players. The artwork tap and the shuffle slot change
        // meaning based on this number.
        public int ActiveSessionCount => LiveSessions().Count;

        // Re-point every subscription at a different session. This is the
        // version-portable way to "switch players": the API setter
        // manager.CurrentSession only exists in newer Windows projections,
        // whereas unhook-and-reattach works on every build we support. If the
        // user later drives the OTHER player with the keyboard media keys,
        // CurrentSessionChanged fires and Hook() re-bases to it automatically.
        private void Attach(GlobalSystemMediaTransportControlsSession? target)
        {
            if (currentSession != null)
            {
                currentSession.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                currentSession.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
                currentSession.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
            }

            currentSession = target;
            if (currentSession == null) return;

            currentSession.MediaPropertiesChanged += Session_MediaPropertiesChanged;
            currentSession.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
            currentSession.PlaybackInfoChanged += Session_PlaybackInfoChanged;

            _ = LoadCurrent(currentSession);
            PublishTimeline(currentSession);
        }

        // Hand the island's pointer to the next live player (Spotify <-> YouTube...).
        // Signature kept async-shaped so the form's call site is unchanged.
        public Task SwitchToNextSessionAsync()
        {
            try
            {
                var live = LiveSessions();
                if (live.Count < 2) return Task.CompletedTask;

                int idx = 0;
                if (currentSession != null)
                {
                    for (int i = 0; i < live.Count; i++)
                    {
                        try
                        {
                            if (live[i].SourceAppUserModelId == currentSession.SourceAppUserModelId)
                            {
                                idx = i;
                                break;
                            }
                        }
                        catch { }
                    }
                }
                Attach(live[(idx + 1) % live.Count]);
            }
            catch { }
            return Task.CompletedTask;
        }

        private void Hook()
        {
            if (currentSession != null)
            {
                currentSession.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                currentSession.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
                currentSession.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
            }

            currentSession = manager?.GetCurrentSession();
            if (currentSession == null) return;

            currentSession.MediaPropertiesChanged += Session_MediaPropertiesChanged;
            currentSession.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
            currentSession.PlaybackInfoChanged += Session_PlaybackInfoChanged;

            _ = LoadCurrent(currentSession);
            PublishTimeline(currentSession);
        }

        private async void Session_MediaPropertiesChanged(
            GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
            => await PublishMediaInfo(sender);

        private void Session_TimelinePropertiesChanged(
            GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
            => PublishTimeline(sender);

        private void Session_PlaybackInfoChanged(
            GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
            => PublishTimeline(sender);

        private async Task LoadCurrent(GlobalSystemMediaTransportControlsSession session) => await PublishMediaInfo(session);

        private async Task PublishMediaInfo(GlobalSystemMediaTransportControlsSession session)
        {
            var props = await session.TryGetMediaPropertiesAsync();

            Bitmap? thumb = null;
            if (props.Thumbnail != null)
            {
                try
                {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    using var netStream = stream.AsStream();
                    using var loaded = new Bitmap(netStream);
                    thumb = new Bitmap(loaded);
                }
                catch { thumb = null; }
            }

            MediaChanged?.Invoke(new MediaInfo
            {
                Title = props.Title ?? "",
                Artist = props.Artist ?? "",
                Aumid = session.SourceAppUserModelId ?? "",
                Thumbnail = thumb
            });
        }

        private void PublishTimeline(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                var timeline = session.GetTimelineProperties();
                var playback = session.GetPlaybackInfo();
                bool playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var duration = timeline.EndTime - timeline.StartTime;
                TimelineChanged?.Invoke(timeline.Position, duration, playing);
            }
            catch
            {
                // Some sources (notably a lot of browser tabs) don't report a timeline at all.
            }
        }

        public async Task NextAsync() { if (currentSession != null) await currentSession.TrySkipNextAsync(); }
        public async Task PreviousAsync() { if (currentSession != null) await currentSession.TrySkipPreviousAsync(); }
        public async Task SetShuffleAsync(bool shuffleOn) { if (currentSession != null) await currentSession.TryChangeShuffleActiveAsync(shuffleOn); }
        public async Task SetRepeatAsync(bool repeatOn)
        {
            if (currentSession == null) return;
            try
            {
                await currentSession.TryChangeAutoRepeatModeAsync(
                    repeatOn
                        ? Windows.Media.MediaPlaybackAutoRepeatMode.Track
                        : Windows.Media.MediaPlaybackAutoRepeatMode.None);
            }
            catch { }
        }
        public async Task TogglePlayPauseAsync() { if (currentSession != null) await currentSession.TryTogglePlayPauseAsync(); }

        // Seeks to an absolute position in the current track. Used by the
        // draggable timeline in Form1. Silently no-ops if the active source
        // doesn't support scrubbing (some browser tabs / apps don't).
        public async Task SeekAsync(TimeSpan position)
        {
            if (currentSession == null) return;
            try
            {
                if (position < TimeSpan.Zero) position = TimeSpan.Zero;
                await currentSession.TryChangePlaybackPositionAsync(position.Ticks);
            }
            catch
            {
                // Source doesn't support seeking — ignore.
            }
        }

        // Resolves the icon of whatever app is currently playing media.
        // Reliable for packaged/Store apps (Spotify, Movies & TV). Classic desktop
        // apps and browser tabs often report an AUMID that doesn't resolve here —
        // in that case this just returns null and the badge stays hidden.
        public static async Task<Bitmap?> GetAppIconAsync(string aumid)
        {
            if (string.IsNullOrWhiteSpace(aumid)) return null;

            // AppInfo.GetFromAppUserModelId requires Windows 10 build 19041+.
            // Keep this helper callable from the app on older supported builds,
            // but never invoke the newer API there.
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                return null;

            try
            {
                var appInfo = AppInfo.GetFromAppUserModelId(aumid);
                if (appInfo == null) return null;

                var logoStreamRef = appInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(32, 32));
                using var stream = await logoStreamRef.OpenReadAsync();
                using var netStream = stream.AsStream();
                using var loaded = new Bitmap(netStream);
                var bmp = new Bitmap(loaded);

                // A lot of tile logos ship with a flat background plate baked
                // into the image. If all four corners are the same solid
                // color, treat that as the plate and key it out so only the
                // glyph is left.
                var corner = bmp.GetPixel(0, 0);
                if (corner.A == 255 &&
                    ColorsClose(corner, bmp.GetPixel(bmp.Width - 1, 0)) &&
                    ColorsClose(corner, bmp.GetPixel(0, bmp.Height - 1)) &&
                    ColorsClose(corner, bmp.GetPixel(bmp.Width - 1, bmp.Height - 1)))
                {
                    bmp.MakeTransparent(corner);
                }

                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static bool ColorsClose(Color a, Color b)
            => Math.Abs(a.R - b.R) < 12 && Math.Abs(a.G - b.G) < 12 && Math.Abs(a.B - b.B) < 12;
    }
}
