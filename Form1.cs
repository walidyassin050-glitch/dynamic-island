using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MyDynamicIsland
{
    public partial class Form1 : Form
    {
        private readonly AppSettings appSettings;
        private bool mediaEventsEnabled;
        private bool networkEventsEnabled;
        private bool notificationEventsEnabled;
        private bool brightnessEventsEnabled;
        public event Action? SettingsRequested;

        // Your existing fields...
        private readonly Font fullTitleFont = new Font("Segoe UI", 10F, FontStyle.Bold);
        private readonly Font fullArtistFont = new Font("Segoe UI", 9F, FontStyle.Regular);
        private readonly Font fullTimeFont = new Font("Segoe UI", 7.5F, FontStyle.Regular);
        private readonly Font fullOutputFont = new Font("Segoe UI", 8F, FontStyle.Regular);

        private MMDevice? device;
        private readonly LyricService lyrics = new();

        // The acoustic layer of word sync: fed by the same loopback capture
        // that drives the waveform, it lets the actual sung attacks move the
        // glow a word at most in either direction of the time-proportional
        // estimate. YouTube Music's per-word times (LyricService) override it
        // entirely when they arrive. Both are invisible when absent.
        private readonly WordAligner voice = new();

        // The lyric slot sits in the text column between the artist row (ends
        // y=74) and the timeline (y=108): one line, marquee-scrolled if it is
        // longer than the column, because a clipped word out of a lyric is worse
        // than a late one and nothing else may be moved to make room.
        private const int LyricRowTop = 78;
        private const int LyricRowHeight = 20;
        private int lyricShownIndex = -1;
        private int lyricPreviousIndex = -1;
        private float lyricMarqueeOffset = 0f;
        private float lyricPreviousMarqueeOffset = 0f;
        // Half the old pace: a scrolling lyric should be followable at
        // reading speed, not at speed-reading speed. (The active word glows
        // in sync regardless of scroll — see DrawKaraokeLyric.)
        private const float LyricMarqueeSpeed = 0.55f;
        private const double LyricTransitionMs = 420d;
        private DateTime lyricTransitionStartedAt = DateTime.MinValue;
        // the line-level engine's only state: 0 = words showing, 1 = note
        private float lyricNotePresence = 1f;
        private MediaWatcher mediaWatcher = new MediaWatcher();
        private System.Windows.Forms.Timer animationTimer = new();

        private NotifyIcon trayIcon = new NotifyIcon();
        private ContextMenuStrip? islandMenu;
        private Screen anchorScreen = Screen.PrimaryScreen ?? Screen.AllScreens[0];

        // *** SIZE TUNING *** — logical pixel sizes for each island state.
        // WinForms auto-scaling is disabled in the designer so these values
        // remain stable regardless of the Windows display scaling setting.
        // Rest and hover share one width. PeekTopRowHeight preserves the compact
        // media row while hover grows downward to reveal the title.
        private const int PeekTopRowHeight = 32;
        private readonly Size restingSize = new(283, 46);
        private readonly Size peekSize = new(283, 72);
        private readonly Size fullSize = new(478, 172);
        private Size targetSize;
        private Size sizeAnimationStart;
        private DateTime sizeAnimationStartedAt = DateTime.MinValue;
        private double sizeAnimationDurationMs;
        private bool sizeAnimationRunning;
        private Size lastAppliedWindowSize = Size.Empty;
        private bool syncingWindowSize;

        // Tracks whether the plain collapsed timeline was visible last tick,
        // so we can detect the moment it reappears and kick off the reveal
        // sweep in DrawProgressOutline.
        private bool progressBarWasVisible = false;

        private DateTime holdUntil = DateTime.MinValue;
        private bool holding = false;
        private enum TransientKind { Volume, Notification, Network, Battery, Device, Brightness, DeviceAudio, Download }
        private TransientKind transientKind = TransientKind.Volume;
        private string transientTitle = "";
        private string transientDetail = "";
        private DateTime transientStartedAt = DateTime.MinValue;
        private DateTime volumeStripUntil = DateTime.MinValue;
        private bool volumeStripWasShowing;
        private bool volumeStripClosing;
        // A default-output change legitimately moves the master level (each
        // endpoint owns a different volume) - without this window, plugging in
        // earbuds makes the volume tile instantly overwrite the earbud card.
        private DateTime volumeSuppressUntil = DateTime.MinValue;
        private bool ShowVolumeStrip => DateTime.Now < volumeStripUntil;

        // --- Volume HUD state ---
        // The endpoint-volume notification (NAudio's OnVolumeNotification) only
        // reliably fires for mute toggles on many devices, which is why plain
        // slider changes never showed the popup. A light 250ms poll of the
        // real master volume catches every change (mixer, hotkeys, hardware).
        private WindowsNotificationWatcher.AccessState notificationAccess
            = WindowsNotificationWatcher.AccessState.Unknown;
        private string notificationAccessDetail = "";

        // Windows quietly demotes topmost layered windows after fullscreen
        // apps/games open and close. Counter drives a gentle re-pin about
        // once a second (see AnimationTick) - no focus stealing, no cost.
        private int topmostReassertTick = 0;

        private readonly System.Windows.Forms.Timer volumePollTimer = new();
        private float lastPolledVolume = -1f;   // -1 = no baseline yet
        private bool lastPolledMute = false;

        // Earbud truth: WM_DEVICECHANGE is hit-or-miss for bluetooth, but the
        // DEFAULT RENDER ENDPOINT swaps the instant buds connect or die - so
        // poll that every 2s and announce real transitions.
        private readonly System.Windows.Forms.Timer audioPollTimer = new();
        private string? lastDefaultRenderId;

        // NLA's NetworkAvailabilityChanged only fires when internet-as-a-whole
        // flips; toggling wifi while another adapter breathes sends nothing.
        // A state check riding the 5s battery timer catches the toggle the
        // user actually performed.
        private bool? knownNetworkUp;
        // Tweened bar fill — chases the real level so the bar "moves
        // according to the change" instead of snapping.
        private float volumeShownLevel = 0f;
        private const int VolumeHudMs = 1800;

        // Brightness HUD state - the same icon+bar control, sun variant.
        private float brightnessTargetLevel = -1f;
        private float brightnessShownLevel = -1f;
        private bool BrightnessHudActive => holding && transientKind == TransientKind.Brightness;

        private bool VolumeHudActive =>
            (holding && transientKind == TransientKind.Volume) || ShowVolumeStrip;

        // --- "Hello" welcome shown on launch (laptop boot) ---
        private DateTime welcomeStartAt = DateTime.MinValue;
        private DateTime welcomeUntil = DateTime.MinValue;
        private bool ShowWelcome => DateTime.Now < welcomeUntil;
        // Card springs open (~0.65s) -> white ink is written on with a soft
        // pen edge from ~0.3s to ~1.7s (the video's timing) -> held ~1s as
        // requested -> 0.5s fade -> the spring collapses it.
        private const double WelcomeTotalMs = 3200;
        // The greeting uses the exact same silhouette as the fully expanded
        // media card - iPhone-boot moment, big quiet card, one logo, no extras.
        // NOTE: values mirror fullSize below - C# forbids field initializers
        // referencing other instance fields, so if you ever resize the card,
        // resize this to match.
        private readonly Size welcomeSize = new Size(478, 172);

        private bool hovering = false;
        private bool clicked = false;

        // Auto-collapse
        private DateTime? collapseAt = null;
        private const double CollapseDelaySeconds = 0.65;

        // --- Auto-hide ---
        // Once the island has been ignored for the configured stretch, it
        // glides up past the top of the screen, leaving a 2px sliver of glass
        // as an anchor. Reaching the edge above its slot — or any event that
        // wants the stage (popup, announcement, welcome) — glides it back.
        private float autoHideOffset = 0f;        // px raised, 0 = fully shown
        private int appliedHideOffset = 0;        // last offset actually on screen
        private DateTime notchLastActive = DateTime.Now;
        private const int AutoHideSliverPx = 2;

        // Anything that means "someone is using it / it has something to say".
        private bool NotchBusy =>
            hovering || clicked || holding || draggingProgress ||
            ShowWelcome || ShowVolumeStrip || BrightnessHudActive || ShowAnnouncement ||
            (islandMenu != null && islandMenu.Visible);

        // A generous catch zone at the screen edge over the notch's slot, so
        // the pointer can summon the island before it can possibly touch the
        // sliver itself.
        private Rectangle GetWakeRect()
        {
            Rectangle screen = anchorScreen.WorkingArea;
            int x = screen.Left + (screen.Width - Width) / 2;
            return new Rectangle(x - 14, screen.Top - 12, Width + 28, 26);
        }

        private void UpdateAutoHide()
        {
            if (!appSettings.AutoHide)
            {
                GlideNotchTo(0f);
                return;
            }

            if (NotchBusy || GetWakeRect().Contains(Cursor.Position))
                notchLastActive = DateTime.Now;

            int seconds = Math.Clamp(appSettings.AutoHideSeconds, 1, 600);
            bool idle = (DateTime.Now - notchLastActive).TotalSeconds >= seconds;

            // The expanded card and every popup are user-visible commitments:
            // hiding only ever engages while the island sits at its resting
            // pill — and it leaves a clickable sliver behind, never a ghost.
            float target = (idle && !NotchBusy)
                ? Math.Max(0, Height - AutoHideSliverPx)
                : 0f;
            GlideNotchTo(target);
        }

        private void GlideNotchTo(float targetOffset)
        {
            autoHideOffset += (targetOffset - autoHideOffset) * 0.2f;
            if (Math.Abs(targetOffset - autoHideOffset) < 0.6f) autoHideOffset = targetOffset;
            if (autoHideOffset < 0f) autoHideOffset = 0f;

            int want = (int)Math.Round(autoHideOffset);
            if (want == appliedHideOffset) return;

            appliedHideOffset = want;
            RepositionWindow();
        }

        // Controls
        private Label statusLabel = new Label();
        private Label textLabel = new Label();
        private PictureBox albumArtBox = new PictureBox();
        private PictureBox appIconBadge = new PictureBox();

        // Media info
        private string mediaText = "";
        private Color albumAccentColor = Color.FromArgb(104, 182, 211);
        private string mediaArtist = "";
        private Rectangle mediaTextRect = Rectangle.Empty;
        private Rectangle artistRect = Rectangle.Empty;
        private Rectangle outputRect = Rectangle.Empty;
        private Rectangle timeRect = Rectangle.Empty;
        private float peekMarqueeOffset = 0f;
        private float fullMarqueeOffset = 0f;
        private bool peekTextOverflows = false;
        private bool fullTextOverflows = false;
        private int peekTextWidth = 100;
        private int fullTextWidth = 100;
        private const float MarqueeSpeed = 0.35f;
        private const int MarqueeGap = 500;
        private const int MaxPeekTextWidth = 5;
        private const int Spacing = 10;
        private const int FullArtSize = 58;
        private const int FullBottomRowHeight = 4000;
        private const int FullControlGap = 14;
        private int controlSeparatorY = -1;

        // Progress bar
        private bool draggingProgress = false;
        private bool suppressClickToggle = false;
        private const double RestOpacity = 0.55;
        private Button prevButton = new Button();
        private Button playPauseButton = new Button();
        private Button nextButton = new Button();
        private Button shuffleButton = new Button();
        private Button repeatButton = new Button();
        private Button favoriteButton = new Button();
        private int lastBtnIconSize = -1;
        private bool repeatOn = false;
        private string audioOutputName = "";

        // --- "Playing Next" queue panel (replaces the like button) ---
        // Windows' GlobalSystemMediaTransportControlsSession API (what
        // MediaWatcher is built on) doesn't expose a source app's real
        // playback queue for arbitrary apps, so there's no OS-level "next up"
        // list to read the way the reference video's macOS widget can. This
        // keeps the same open/close card behavior and layout, backed by a
        // simple in-memory list you can wire up to a real source later
        // (e.g. Spotify's Web API) via SetQueue()/OnQueueItemRemoved.
        private sealed class QueueItem
        {
            public string Title = "";
            public string Subtitle = "";
            public Bitmap? Art;

            // Carried through from Spotify so a tap can start exactly this
            // track; empty for rows fed by SetQueue's display-only overload.
            public string Uri = "";
        }

        // Whether the "Playing Next" extension is open under the media card
        // — driven by the card's queue button, as it always was. The
        // closing flag keeps the panel painted while the card shrinks back.
        private bool queueOpen = false;
        private bool queuePanelClosing;
        private readonly List<QueueItem> queueItems = new();
        // The old drawer's window height; the queue PAGE fits five rows
        // (QueueMaxCardRows) and this floor now only anchors the empty-state
        // scroll math.
        // Geometry the real list uses at this width: a 40px cover, the title and
        // artist breathing to the left of a right-aligned duration.
        private const int QueueRowHeight = 54;
        private const int QueueHeaderHeight = 42;
        private const int QueueMaxVisibleRows = 3;
        private const int QueueArtSize = 40;
        private const int QueueRightWidth = 46;

        // The empty panel has three jobs now: hold whatever rows do exist, say
        // precisely why the missing ones are missing, and when the fix is
        // something you can do from there, offer it as a button.
        private SpotifyQueueStatus queueStatus = SpotifyQueueStatus.NotConnected;
        private string queueStatusDetail = "";

        // One press should be enough, so while the island is talking to Spotify
        // the panel shows a quiet "checking" line and refuses to stack a second
        // handshake on top of the first.
        private bool queueCheckRunning = false;

        // A 4s poll over a link that occasionally takes longer than that would
        // interleave two list replacements; this makes late ticks skip.
        private bool queueFetchInFlight = false;

        // The list is longer than the card window, so the wheel moves it instead
        // of the panel growing to fit every track.
        //
        // Measured in pixels rather than rows. One notch per row sent the whole
        // list past twice over, so nothing stayed readable long enough to choose;
        // three notches now advance one row, which is slow enough to stop on any
        // line and still reaches the end of a long queue quickly.
        private float queueScrollPx = 0f;
        private const float QueueScrollNotch = 18f;   // a third of QueueRowHeight
        private int hoveredQueueRow = -1;

        // --- Notification presentation: two tiers, like the island itself ---
        // Compact is a Live-Activity strip the pill's own height: glyph, one line
        // of the message, how long ago. Clicking it drops the iOS-style banner
        // underneath, which repeats the strip as its header and adds the app tile
        // plus the message at full width. The header staying put is what makes
        // the growth read as one object opening rather than a new popup.
        private bool notificationExpanded = false;
        private const int NotifCompactHeight = 42;
        private const int NotifBannerBodyHeight = 104;
        private const int NotifReadMs = 9000;      // extra reading time once opened

        // Deliberately the resting pill's own width, not a wider tile. This
        // popup is an annotation on the island, so opening it must not change
        // the silhouette's width at all — only the text inside it changes. A
        // 340px strip next to a 46px dot measured wider than the thing it was
        // reporting on, which is what read as "the collapsed island got big".
        private int NotifWidth => restingSize.Width;

        private Size NotificationSize =>
            new Size(NotifWidth, notificationExpanded
                ? NotifCompactHeight + NotifBannerBodyHeight
                : NotifCompactHeight);

        // Every text popup, not just Windows toasts: network, battery and
        // device events are the same object wearing different glyphs, and a
        // two-line battery message deserves the same room to open into.
        // toasts run their own card now; the strip/banner system stays for
        // the plain activity popups (network, power, devices)
        private bool NotificationShowing =>
            holding && transientKind is TransientKind.Network
                or TransientKind.Battery or TransientKind.Device;

        private string NotificationWhen
        {
            get
            {
                double secs = (DateTime.Now - transientStartedAt).TotalSeconds;
                if (secs < 45) return "now";
                double mins = secs / 60.0;
                return mins < 60 ? ((int)mins) + "m" : ((int)(mins / 60.0)) + "h";
            }
        }

        // A tap has to be felt before Spotify's own state comes back around: the
        // row paints a "Starting" tag that is retired by the next list that no
        // longer contains the track, or by this deadline if nothing ever does
        // (a refused write, a device that went away).
        private string queueStartedUri = "";
        private DateTime queueStartedAt = DateTime.MinValue;
        private const int QueueStartGraceMs = 5000;

        private bool QueueStartPending =>
            queueStartedUri.Length > 0 &&
            (DateTime.Now - queueStartedAt).TotalMilliseconds < QueueStartGraceMs;

        // The status line only exists in the empty panel, so a tap that fails
        // while the list is on screen would otherwise be silent — the exact
        // failure mode this whole panel was rebuilt to remove. The row that was
        // tapped carries the reason instead, for as long as it is worth reading.
        private string queueTapNoteUri = "";
        private string queueTapNoteText = "";
        private DateTime queueTapNoteAt = DateTime.MinValue;
        private const int QueueTapNoteMs = 6000;

        // Notes are per row, not per panel, because the row is the thing the note
        // is about. They fade rather than needing a dismiss control.
        private bool QueueTapNoteOn(string uri) =>
            uri.Length > 0 && uri == queueTapNoteUri &&
            (DateTime.Now - queueTapNoteAt).TotalMilliseconds < QueueTapNoteMs;

        private void NoteQueueRow(string uri, string text)
        {
            queueTapNoteUri = uri;
            queueTapNoteText = text;
            queueTapNoteAt = DateTime.Now;
        }

        // Display-only rows: anything that can populate the list but not be
        // tapped into playback (the Uri stays empty and the row hides its glyph).
        public void SetQueue(IEnumerable<(string title, string subtitle, Bitmap? art)> items)
        {
            ApplyQueueRows(items.Select(i => new QueueItem
            {
                Title = i.title,
                Subtitle = i.subtitle,
                Art = i.art,
            }));
        }

        // Spotify's own rows, with the uri and duration the panel needs to look
        // and behave like the real list.
        private void SetQueue(IReadOnlyList<SpotifyQueueService.SpotifyQueueTrack> tracks)
        {
            ApplyQueueRows(tracks.Select(t => new QueueItem
            {
                Title = t.Title,
                // "Artist — Album", the same order the client prints.
                Subtitle = t.Album.Length > 0 && t.Artist.Length > 0
                    ? t.Artist + "  —  " + t.Album
                    : t.Artist.Length > 0 ? t.Artist : t.Album,
                Art = t.Art,
                Uri = t.Uri,
            }));
        }

        private void ApplyQueueRows(IEnumerable<QueueItem> rows)
        {
            void Apply()
            {
                foreach (var existing in queueItems)
                    existing.Art?.Dispose();

                queueItems.Clear();
                queueItems.AddRange(rows);

                // The track leaving the queue is the only proof that a tap
                // landed, so the tag survives refreshes that still list it and
                // dies the moment it is gone.
                if (queueStartedUri.Length > 0 &&
                    !queueItems.Any(i => i.Uri == queueStartedUri))
                {
                    queueStartedUri = "";
                }

                // A window past the end of a shortened list would paint nothing.
                queueScrollPx = Math.Clamp(queueScrollPx, 0f, QueueScrollMaxPx);

                if (queueTapNoteUri.Length > 0 &&
                    !queueItems.Any(i => i.Uri == queueTapNoteUri))
                {
                    queueTapNoteUri = "";
                }

                RefreshVisual();
            }

            if (InvokeRequired) Invoke(new Action(Apply));
            else Apply();
        }

        // --- "Playing Next" extension geometry ---
        // The list extends the media card downward, exactly the drawer it
        // always was: full card on top, header + rows below it, the window
        // ending flush with the card bottom.
        private const int QueueMaxCardRows = 3;

        // panelTop for the header/rows block: the bottom edge of the media
        // card the list extends.
        private int QueuePanelTopY() => fullSize.Height;

        // The list window runs from under the header to the card's bottom
        // edge, lifted by the volume strip whenever one stacks below it.
        private bool QueueStripBelow =>
            clicked && queueOpen && ShowVolumeStrip;

        private int QueueListBottom()
        {
            int bottom = Height;
            if (QueueStripBelow) bottom -= VolumeStripHeight;
            return bottom;
        }

        // Rows that fit the window as CURRENTLY drawn — Height animates while
        // the extension opens, so rows slide into view instead of a clipped
        // list waiting for the card to finish growing.
        private int QueueWindowRows()
        {
            if (queueItems.Count == 0) return QueueMaxVisibleRows;
            int usable = QueueListBottom() - (QueuePanelTopY() + QueueHeaderHeight);
            return Math.Max(1, Math.Min(QueueMaxCardRows, usable / QueueRowHeight));
        }

        // The panel is a fixed-height window even when the list holds sixty
        // tracks, which is what keeps the card from resizing every time the
        // queue advances.
        private int QueueVisibleRows => Math.Min(queueItems.Count, QueueWindowRows());

        // How far the window may travel before the last row sits at the bottom.
        private float QueueScrollMaxPx =>
            Math.Max(0f, (queueItems.Count - QueueVisibleRows) * QueueRowHeight);

        // The empty state reserves room for the status line, plus a second
        // row when there is a button under it.
        private int QueueEmptyBodyHeight => ShowQueueConnectButton ? 20 + 6 + 30 + 8 : 20;

        // Total extended-card height while the queue is open: base media card
        // plus header block and however many rows the list actually earns
        // space for.
        private int QueuePageTargetHeight() => queueItems.Count > 0
            ? fullSize.Height + QueueHeaderHeight
                + Math.Min(QueueMaxCardRows, queueItems.Count) * QueueRowHeight
            : fullSize.Height + QueueHeaderHeight + QueueEmptyBodyHeight;

        private int QueueCardHeight() => QueuePageTargetHeight();


        private float lastStatusFontSize = -1f;
        private readonly object audioMeterLock = new object();

        private WasapiLoopbackCapture? loopbackCapture;
        private readonly float[] waveformLevels = new float[9];
        // Per-band captured/smoothed loopback levels, so each bar reacts to a
        // different slice of the live audio instead of one shared peak value.
        private readonly float[] loopbackBandPeaks = new float[9];
        private readonly float[] audioBandLevels = new float[9];
        private float wavePhase = 0f;

        // Instant tactile feedback for control clicks: which button (if any)
        // was just pressed, and when, so it can flash immediately instead of
        // waiting for the real (async, OS round-trip) state change to land.
        private int pressedControl = -1;
        private DateTime pressedAt = DateTime.MinValue;
        private const int PressFlashMs = 160;

        private bool lowBatteryAlerted = false;
        private bool batteryStateInitialized = false;
        private bool previousChargingState = false;
        private string sourceAppName = "";

        private string lastAumid = "";
        private bool shuffleOn = false;
        private bool lastIconIsPlaying = false;

        // Timeline
        private TimeSpan basePosition = TimeSpan.Zero;
        private TimeSpan trackDuration = TimeSpan.Zero;
        private DateTime basePositionStamp = DateTime.Now;
        private bool isPlaying = false;

        // Background tint
        private Color expandedTint = Color.FromArgb(28, 28, 30);

        // Progress rect
        private Rectangle progressRect = Rectangle.Empty;
        private float progressFraction = 0f;
        private bool showProgress = false;

        // Declare islandRectangle at class level
        private Rectangle islandRectangle = Rectangle.Empty;

        // --- Expanded-card pages ---
        // The bubble that used to sit beside the notch is gone. The card
        // now carries three pages: Media (the full card, with the "Playing
        // Next" extension still a part of it — opened by the queue button,
        // exactly like before), Search (the typed question card), and Today
        // (weather + agenda expanded, clock + battery collapsed). The pill
        // rests on the active page, a click opens it, and the wheel — or a
        // tap on the fixed indicator dots along the card's right edge —
        // moves between pages.
        private enum IslandMode { Media, Search, Today }
        private IslandMode activeMode = IslandMode.Media;
        private int pageIndex = 0;

        private int PageCount() => 3;

        private IslandMode PageAt(int index) => index switch
        {
            0 => IslandMode.Media,
            1 => IslandMode.Search,
            _ => IslandMode.Today,
        };

        // Wheel physics for page turns: a single notch turns one page — the
        // threshold sits BELOW one notch (120 units) so a lone deliberate
        // tick always crosses it. One notch never turned one page for free
        // though: a fast flick packs several events into moments, so each
        // turn enforces a short beat before the next one may fire. The beat
        // is direction-symmetric on purpose — an earlier version also banked
        // progress against a quiet-period timer, and slow single-tick
        // scrolling never accumulated enough to cross, which read as
        // "the wheel only works one way".
        private float pageTurnAccum = 0f;
        private DateTime pageLastTurnAt = DateTime.MinValue;
        private const int PageTurnTicks = 100;          // under one 120-unit notch
        private const double PageTurnHoldMs = 120;      // beat between turns

        // The single funnel every page change goes through (wheel, indicator
        // tap, tray menu). The queue extension belongs to the media page:
        // turning away from Media closes the extension — and its Spotify
        // polling — behind you.
        private void SetPage(int index, bool expand = true)
        {
            index = Math.Clamp(index, 0, PageCount() - 1);
            pageTurnAccum = 0f;
            if (index == pageIndex && activeMode == PageAt(index))
            {
                // Already parked on this page — the only work left is what
                // "navigate here" still means: open the card if it was closed,
                // and stop any idle-collapse countdown if it was open.
                if (expand)
                {
                    clicked = true;
                    collapseAt = null;
                    holding = false;
                    if (activeMode == IslandMode.Search)
                        BeginInvoke(new Action(() => Focus()));
                    RefreshVisual();
                }
                return;
            }

            pageIndex = index;
            activeMode = PageAt(index);

            CloseQueueExtension();

            // The question card opens blank every time; keys must land on
            // the form the moment it appears.
            if (activeMode == IslandMode.Search)
            {
                askText = "";
                if (expand) BeginInvoke(new Action(() => Focus()));
            }

            if (expand)
            {
                clicked = true;
                collapseAt = null;
                holding = false;
            }
            modeSwitchAt = DateTime.Now;
            RefreshVisual();
        }

        // Shared by paging away and by Esc: the extension retracts (kept
        // painted for the frames of its own shrink), polling stops, and a
        // handshake finishing in the background is left alone to clear its
        // own latch — wiping it mid-flight would mute the next Connect press.
        private void CloseQueueExtension()
        {
            if (!queueOpen) return;
            queueOpen = false;
            queuePanelClosing = true;
            queuePollTimer.Stop();
            if (spotifyQueue.Status != SpotifyQueueStatus.Connecting)
                queueCheckRunning = false;
        }

        // Drives the fade+pop transition in DrawFadedPop whenever the page
        // flips (see SetPage).
        private DateTime modeSwitchAt = DateTime.MinValue;
        private const double ModeSwitchMs = 220;

        // --- Three-tier state model ---
        // Tier 1 "Pill": restingSize, ambient only (clock / mini waveform),
        // with the song-progress ring traced around its own border.
        // Tier 2 "Peek": peekSize, shown on hover AND briefly auto-announced
        // when a new track starts — art + title + the same progress ring
        // traced around the pill's border, animating left-to-right.
        // Tier 3 "Card": fullSize, the fully interactive view — shown only
        // on click, never just from hovering.
        private DateTime announceUntil = DateTime.MinValue;
        private bool ShowAnnouncement => DateTime.Now < announceUntil;
        private const double AnnounceMs = 3000;
        private string lastAnnouncedTrackKey = "";
        private bool everGotTrack = false;

        // The card belongs to whichever page is active. The media page owns
        // the hover peek and auto-announce — the track row is its resting
        // half-measure, on no other page.
        private bool CardShowing => clicked && overlay == OverlayKind.None;
        private bool PeekShowing => activeMode == IslandMode.Media
            && !clicked && (hovering || ShowAnnouncement) && overlay == OverlayKind.None;

        // --- Weather (Open-Meteo + ip-api.com — both free, no API key) ---
        // --- Spotify "Playing Next" queue ---
        // Real data from Spotify's Web API over an OAuth 2.0 PKCE handshake:
        // one browser approval, then access+refresh tokens kept in this exe's
        // AppData (DPAPI, current user) and renewed in the background.
        private readonly SpotifyQueueService spotifyQueue = new SpotifyQueueService();
        private readonly System.Windows.Forms.Timer queuePollTimer = new();

        private System.Windows.Forms.Timer batteryTimer = new();
        private readonly WindowsNotificationWatcher notificationWatcher = new();

        // iOS-style toast card: real sender icon + title + wrapped text on an
        // ask-card-sized pill. toastAppIcon is 44x44, owned by the island.
        private Bitmap? toastAppIcon;
        private bool ToastHud => holding && transientKind == TransientKind.Notification;
        private DateTime lastDeviceChangeAt = DateTime.MinValue;
        private IntPtr brightnessNotificationHandle = IntPtr.Zero;
        private float batteryPercent = 1f;   // 0..1
        private bool batteryCharging = false;
        private bool hasBattery = true;

        // With the side bubble deleted the pill IS the whole window; the page
        // indicator lives inside the card's right edge instead. Today stays
        // reachable on battery-less desktops — it is a page now, not a dot.
        private int PillWidth => Math.Max(1, Width);

        private Size GetTotalSize(Size pillSize) => pillSize;

        // --- Third Live Activity: "Today" panel ---
        // Wide three-column card (weather / clock+date / agenda), matching the
        // reference video's notch-widget. Weather and agenda are placeholders
        // until wired to a real source — call SetWeather()/SetAgenda() to feed
        // live data the same way SetQueue() feeds the queue panel.
        private sealed class AgendaItem
        {
            public string Title = "";
            public string TimeRange = "";
            public Color Dot = Color.FromArgb(104, 182, 211);
        }

        private readonly List<AgendaItem> agendaItems = new();
        private string weatherCity = "";
        private string weatherCondition = "";
        private string weatherTemp = "";
        private string weatherIconKind = "sun";
        private bool weatherIsDay = true;

        // Today pill sizes: at rest EXACTLY the normal pill (260x50, same as
        // media/search). On hover it grows sideways by the room the full
        // readout needs — and it learns: the painter measures the REAL
        // strings at the real DPI every frame and reports the exact width
        // they occupy back here (todayNeededPx), so if anything ever needs
        // more room, the pill simply grows into it on the next frame. The
        // percentage can no longer be cut, by construction.
        private bool todayBattery => hasBattery && appSettings.BatteryActivity;
        private int todayNeededPx;         // feedback from the painter
        private Size GetTodayCompactSize() => restingSize;

        private Size GetTodayWideSize()
        {
            if (!todayBattery) return restingSize;
            var timeFont = CachedFont("Segoe UI", 10F, FontStyle.Bold);
            var pctFont = CachedFont("Segoe UI", 11F, FontStyle.Bold);
            int timeW = TextRenderer.MeasureText("00:00 PM", timeFont,
                new Size(900, 40), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            int pctW = TextRenderer.MeasureText("100%", pctFont,
                new Size(900, 40), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            int needed = 16 + timeW + 10 + pctW + 18 + 10 + 26 + 16;
            if (todayNeededPx > 0)
                needed = Math.Max(needed, todayNeededPx);
            return new Size(Math.Max(restingSize.Width + 44, needed), restingSize.Height);
        }

        // Live Today-page data: agenda + weather (see TodayServices.cs). The
        // feed self-throttles; the 5 s battery tick just knocks. agendaNote
        // carries "why is this empty" when the calendar store says no.
        private TodayDataFeed? todayFeed;
        private string agendaNote = "";

        // Rotating event-dot hues, same family as the island's accents.
        private static readonly Color[] AgendaPalette =
        {
            Color.FromArgb(104, 182, 211), Color.FromArgb(232, 190, 96),
            Color.FromArgb(150, 190, 140), Color.FromArgb(200, 140, 220),
        };

        private readonly Size todaySize = new Size(460, 190);

        public void SetWeather(string city, string condition, string temp, string iconKind = "sun",
            bool isDay = true)
        {
            void Apply()
            {
                weatherCity = city;
                weatherCondition = condition;
                weatherTemp = temp;
                weatherIconKind = iconKind;
                weatherIsDay = isDay;
                RefreshVisual();
            }
            if (InvokeRequired) Invoke(new Action(Apply));
            else Apply();
        }

        public void SetAgenda(IEnumerable<(string title, string timeRange, Color dot)> items)
        {
            void Apply()
            {
                agendaItems.Clear();
                foreach (var i in items)
                    agendaItems.Add(new AgendaItem { Title = i.title, TimeRange = i.timeRange, Dot = i.dot });
                RefreshVisual();
            }
            if (InvokeRequired) Invoke(new Action(Apply));
            else Apply();
        }

        // --- Transient overlay activities: incoming call / message reply ---
        // These behave like the existing mute popup (a temporary card that
        // takes over the pill) rather than a page in the card's cycle,
        // since they're triggered by an external event.
        private enum OverlayKind { None, Call, Reply, Ask }
        private OverlayKind overlay = OverlayKind.None;

        private readonly Size callSize = new Size(360, 96);
        private readonly Size replySize = new Size(380, 130);
        private readonly Size askSize = new Size(380, 100);
        // Self-drawn input text: a layered (UpdateLayeredWindow) window cannot
        // host a real TextBox at all - child controls never composite onto it.
        // (The old replyBox suffered the same silent invisibility.) So the
        // ask card renders its text + blinking caret itself and eats keystrokes
        // through the form's KeyPress.
        private string askText = "";

        private string callerName = "";
        private Bitmap? callerAvatar;
        private Action? onCallAccept;
        private Action? onCallDecline;

        private string replySender = "";
        private string replyPreview = "";
        private Bitmap? replyAvatar;
        private Action<string>? onReplySend;
        private TextBox replyBox = new TextBox();

        // Shows a FaceTime-style incoming call card. Safe to call from a
        // background thread. Persists until accepted, declined, or replaced.
        public void ShowIncomingCall(string name, Bitmap? avatar, Action onAccept, Action onDecline)
        {
            void Apply()
            {
                callerName = name;
                callerAvatar = avatar;
                onCallAccept = onAccept;
                onCallDecline = onDecline;
                overlay = OverlayKind.Call;
                clicked = true;
                collapseAt = null;
                replyBox.Visible = false;
                RefreshVisual();
            }
            if (InvokeRequired) Invoke(new Action(Apply));
            else Apply();
        }

        // Shows an inline message-reply card with a real editable text field,
        // mirroring the Messages Live Activity from the reference video.
        public void ShowMessageReply(string sender, Bitmap? avatar, string preview, Action<string> onSend)
        {
            void Apply()
            {
                replySender = sender;
                replyAvatar = avatar;
                replyPreview = preview;
                onReplySend = onSend;
                overlay = OverlayKind.Reply;
                clicked = true;
                collapseAt = null;
                replyBox.Text = "";
                replyBox.Visible = true;
                RefreshVisual();
                BeginInvoke(new Action(() => replyBox.Focus()));
            }
            if (InvokeRequired) Invoke(new Action(Apply));
            else Apply();
        }

        private void DismissOverlay()
        {
            overlay = OverlayKind.None;
            replyBox.Visible = false;
            clicked = false;
            RefreshVisual();
        }

        public Form1(AppSettings? settings = null)
        {
            appSettings = settings ?? AppSettings.Load();
            spotifyQueue.Configure(appSettings);
            InitializeComponent();

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            // Only used by the OnPaint fallback path if EnableLayeredStyle
            // fails for some reason — the layered path itself renders to a
            // transparent ARGB bitmap and ignores BackColor entirely.
            BackColor = Color.Black;

            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            UpdateStyles();

            RefreshBattery(); // sets hasBattery before the first layout pass

            Size = GetTotalSize(restingSize);
            targetSize = restingSize;
            sizeAnimationStart = restingSize;
            WireDownloads();
            BrowserDownloads.Start();

            StartPosition = FormStartPosition.Manual;
            anchorScreen = Screen.FromPoint(Cursor.Position);
            RepositionWindow();

            // Handle must exist before GetWindowLong/SetWindowLong can touch
            // the window's extended style, so force creation here rather
            // than waiting for it to happen implicitly on first Show().
            if (!IsHandleCreated) _ = Handle;
            EnableLayeredStyle();
            RenderLayered(); // first frame, replaces the old Region-based clip

            // Initialize controls...
            // (your existing control setup code here)
            // ...

            SetupReplyBox();

            MouseClick += Form1_MouseClick;
            MouseMove += Form1_MouseMove;
            MouseDown += Form1_MouseDown;
            MouseUp += Form1_MouseUp;
            KeyDown += Form1_KeyDown;
            KeyPress += Form1_KeyPressAsk;

            SetupTrayIcon();
            SetupMuteListener();
            SetupLoopbackCapture();
            RefreshAudioOutputName();

            mediaWatcher.MediaChanged += OnMediaInfoChanged;
            mediaWatcher.TimelineChanged += OnTimelineChanged;
            // Session roster changes (second player starts/stops) repaint the
            // badges and flip the shuffle-slot meaning on the fly.
            mediaWatcher.SessionsChanged += () =>
            {
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke(new Action(RefreshVisual));
            };

            batteryTimer.Interval = 5000;
            batteryTimer.Tick += (s, e) => { RefreshBattery(); CheckNetworkTransition(); todayFeed?.Tick(); };
            batteryTimer.Start();

            notificationWatcher.NotificationReceived += NotificationReceived;
            notificationWatcher.AccessResolved += OnNotificationAccessResolved;

            // Catches every master-volume / mute change, whatever produced it
            // (mixer slider, hotkeys, hardware dials) — see fields above.
            volumePollTimer.Interval = 250;
            volumePollTimer.Tick += VolumePollTick;
            volumePollTimer.Start();

            audioPollTimer.Interval = 2000;
            audioPollTimer.Tick += AudioDefaultPollTick;
            audioPollTimer.Start();

            // iPhone-boot-style "Hello" when the island first starts (i.e. at
            // Windows launch via the startup entry): expand, greet, collapse.
            if (appSettings.WelcomeGreeting)
            {
                welcomeStartAt = DateTime.Now;
                welcomeUntil = welcomeStartAt.AddMilliseconds(WelcomeTotalMs);
            }

            ApplySettings();

            // Today-page live data: agenda every 5 minutes, weather every 15
            // (both throttled inside the feed; the battery tick just knocks).
            // One immediate pass so the panel is never blank merely because a
            // timer had not fired yet. SetWeather/SetAgenda marshal back to
            // this thread themselves.
            todayFeed = new TodayDataFeed(
                () => (appSettings.WeatherCity, appSettings.AllowAutoLocation),
                wx => SetWeather(wx.City, wx.Condition, wx.Temp, wx.IconKind, wx.IsDay),
                (status, items) =>
                {
                    agendaNote = status switch
                    {
                        AgendaStatus.NoAccess => "Calendar blocked: run the packaged build",
                        AgendaStatus.NoCalendars => "No calendar account connected",
                        AgendaStatus.Failed => "Calendar unavailable",
                        _ => "",
                    };
                    SetAgenda(items.Select((it, i) =>
                        (it.Title, it.TimeRange, AgendaPalette[i % AgendaPalette.Length])));
                    RefreshVisual();
                });
            todayFeed.Tick();

            // Queue polling only runs while the "Playing Next" extension is
            // open (see ToggleQueuePanel), so it's created here but not
            // started.
            queuePollTimer.Interval = 4000;
            queuePollTimer.Tick += (s, e) => _ = RefreshSpotifyQueueAsync();

            // A login finishing in the background would otherwise stay invisible
            // until the next tick, which reads as the button not working.
            spotifyQueue.StatusChanged += OnSpotifyQueueStatusChanged;

            animationTimer.Interval = 16; // 60 fps — smooth size/marquee tweens
            animationTimer.Tick += AnimationTick;
            animationTimer.Start();

            // Start hover detection timer
            var hoverTimer = new System.Windows.Forms.Timer { Interval = 50 };
            hoverTimer.Tick += (s, e) => UpdateHoverState();
            hoverTimer.Start();

            FormClosing += Form1_FormClosing;

            // Initialize islandRectangle with some default or calculated value
            islandRectangle = new Rectangle(0, 0, Width, Height);
        }

        // --- Your existing methods ---

        // Real WinForms TextBox overlaid on the drawn reply card — owner-drawn
        // pills can't host a native caret/IME themselves, so this is the one
        // bit of actual UI floating on top of the painted content, positioned
        // in DrawReplyOverlay each frame while visible.
        private void SetupReplyBox()
        {
            replyBox.BorderStyle = BorderStyle.None;
            replyBox.BackColor = Color.FromArgb(40, 40, 44);
            replyBox.ForeColor = Color.White;
            replyBox.Font = new Font("Segoe UI", 9.5F, FontStyle.Regular);
            replyBox.Visible = false;
            replyBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    SendReply();
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    DismissOverlay();
                }
            };
            Controls.Add(replyBox);
            replyBox.BringToFront();
        }

        private void SendReply()
        {
            string text = replyBox.Text;
            var callback = onReplySend;
            DismissOverlay();
            if (!string.IsNullOrWhiteSpace(text))
                callback?.Invoke(text);
        }

        // The form itself is the input: the ask card renders its own text and
        // caret (see DrawAskOverlay), because a layered window physically
        // cannot composite a child TextBox on top of its bitmap.
        public void ShowAsk()
        {
            void Apply()
            {
                overlay = OverlayKind.Ask;
                clicked = false;
                collapseAt = null;
                welcomeUntil = DateTime.MinValue; // an ask beats the greeting
                replyBox.Visible = false;
                askText = "";
                Focus(); // keystrokes must land on the form itself
                RefreshVisual();
            }
            if (InvokeRequired) BeginInvoke(new Action(Apply));
            else Apply();
        }

        // The form's own keystroke feed while the ask card is open: printable
        // characters append, backspace deletes, Enter submits. (Esc already
        // routes through Form1_KeyDown -> DismissOverlay.)
        private void Form1_KeyPressAsk(object? sender, KeyPressEventArgs e)
        {
            // The ask overlay or the Search page's card — both eat keystrokes
            // the same way, and neither can host a real TextBox (a layered
            // window never composites child controls).
            bool askPage = activeMode == IslandMode.Search && clicked &&
                overlay == OverlayKind.None;
            if (overlay != OverlayKind.Ask && !askPage) return;

            if (e.KeyChar == (char)Keys.Enter)
            {
                e.Handled = true;
                SubmitAsk();
                return;
            }
            if (e.KeyChar == (char)Keys.Back)
            {
                e.Handled = true;
                if (askText.Length > 0) askText = askText[..^1];
                RefreshVisual();
                return;
            }
            if (!char.IsControl(e.KeyChar))
            {
                e.Handled = true;
                if (askText.Length < 220) askText += e.KeyChar;
                RefreshVisual();
            }
        }

        private void SubmitAsk()
        {
            string query = askText.Trim();
            if (overlay == OverlayKind.Ask)
            {
                DismissOverlay();
            }
            else
            {
                // Search-page question: the browser (or at least the
                // clipboard) carries the words now, so the card has done its
                // job — wipe the field and collapse back to the pill.
                askText = "";
                clicked = false;
                collapseAt = null;
                SetPage(0, expand: false);
                RefreshVisual();
            }
            if (query.Length == 0) return;

            try
            {
                string template = string.IsNullOrWhiteSpace(appSettings.AiAskUrl)
                    ? "https://www.google.com/search?q={query}&udm=50"
                    : appSettings.AiAskUrl;

                // Park it on the clipboard first: assistants that refuse
                // query URLs (ChatGPT/Claude/Gemini open their app and ignore
                // params) can still be driven - and with the auto-send below
                // they usually drive themselves.
                try { Clipboard.SetText(query); } catch { }

                bool autoPasteSend = !template.Contains("google.com",
                    StringComparison.OrdinalIgnoreCase);
                string url = template.Replace("{query}", Uri.EscapeDataString(query));
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

                if (autoPasteSend) WaitForBrowserThenSend();
            }
            catch
            {
                // Bad template or no browser association - the words are at
                // least on the clipboard, so nothing is lost.
            }
        }

        // Poll until the freshly-opened browser window is the foreground (up
        // to ~4s), then Ctrl+V + Enter to the agent's own page via SendKeys.
        // If focus never lands somewhere new, nothing is typed anywhere.
        private void WaitForBrowserThenSend()
        {
            IntPtr was = GetForegroundWindow();
            _ = Task.Run(() =>
            {
                for (int i = 0; i < 34; i++)
                {
                    Thread.Sleep(120);
                    if (IsDisposed) return;
                    if (GetForegroundWindow() == was) continue;
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                SendKeys.SendWait("^v");
                                Thread.Sleep(150);
                                SendKeys.SendWait("{ENTER}");
                            }
                            catch { }
                        }));
                    }
                    catch { }
                    return;
                }
            });
        }

        // Ask / Search card. When opened as a PAGE (askSize 100px tall) the
        // typed question sits centered and large with a live caret right after
        // the last character — the way the real island grows into search. Over
        // the ask OVERLAY (also 100px) it reads the same. Provider mark bottom
        // left, key line bottom right.
        private void DrawAskOverlay(Graphics g)
        {
            int w = PillWidth;
            int h = Height;
            bool pageMode = activeMode == IslandMode.Search && clicked;

            var inputFont = CachedFont("Segoe UI", pageMode ? 15F : 11F, FontStyle.Regular);
            int textTop = pageMode ? (h - 40) / 2 : 14;
            int textH = pageMode ? 34 : 26;

            // The well only for the small overlay; the page IS the field.
            if (!pageMode)
            {
                var well = new Rectangle(20, 40, Math.Max(40, w - 40), 30);
                using (var wb = new SolidBrush(Color.FromArgb(40, 40, 44)))
                    FillRoundedRectangle(g, wb, well, 9);
            }

            int leftPad = pageMode ? 24 : 30;
            int avail = Math.Max(20, w - leftPad * 2);
            var rowRect = new Rectangle(leftPad, textTop, w - leftPad * 2, textH);

            if (askText.Length == 0)
            {
                DrawTextAt(g, "Ask anything...", pageMode ? 14F : 10.5F, FontStyle.Regular,
                    Color.FromArgb(118, 118, 126), rowRect, true);
            }
            else
            {
                Size full = TextRenderer.MeasureText(g, askText, inputFont,
                    new Size(int.MaxValue, textH),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                int textW = Math.Min(full.Width, avail);
                // left-align when it fits; scroll the tail into view when not;
                // centre the whole visible run when pageMode and it fits.
                int x;
                if (full.Width <= avail)
                    x = pageMode ? leftPad + (avail - full.Width) / 2 : leftPad;
                else
                    x = leftPad + (avail - full.Width);
                bool rtl = ContainsRtl(askText);

                var st = g.Save();
                g.IntersectClip(new Rectangle(leftPad, textTop, avail, textH));
                TextRenderer.DrawText(g, askText, inputFont,
                    new Rectangle(x, textTop, full.Width + 8, textH), Color.White,
                    TextFormatFlags_VERTICAL(rtl));

                // Live caret right after the last visible glyph.
                if (DateTime.Now.Millisecond < 500)
                {
                    int caretX = Math.Min(leftPad + avail,
                        (full.Width <= avail ? x + full.Width : leftPad + avail) + 2);
                    using var caret = new SolidBrush(Color.FromArgb(220, 104, 182, 211));
                    g.FillRectangle(caret, caretX, textTop + 5, 2, textH - 10);
                }
                g.Restore(st);
            }

            var markRect = new Rectangle(20, h - 24, 14, 14);
            DrawAiProviderMark(g, appSettings.AiAskUrl, markRect);
            DrawTextAt(g, AgentDisplayName(appSettings.AiAskUrl), 7.8F, FontStyle.Regular,
                Color.FromArgb(140, 140, 148),
                new Rectangle(markRect.Right + 5, h - 26, 120, 16), false);
            DrawTextAt(g, "Enter asks  ·  Esc closes", 7.8F, FontStyle.Regular,
                Color.FromArgb(118, 118, 126),
                new Rectangle(w / 2, h - 26, Math.Max(20, w / 2 - 20), 16), false, rightAlign: true);
        }

        static TextFormatFlags TextFormatFlags_VERTICAL(bool rtl)
        {
            var f = TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            if (rtl) f |= TextFormatFlags.RightToLeft | TextFormatFlags.Right;
            return f;
        }

        private static string AgentDisplayName(string? url)
        {
            url = url ?? "";
            if (url.Contains("chatgpt")) return "ChatGPT";
            if (url.Contains("claude")) return "Claude";
            if (url.Contains("gemini")) return "Gemini";
            if (url.Contains("udm=50")) return "Google AI";
            return "Ask anything";
        }

        // Small vector brand marks for the ask card: Claude's asterisk,
        // Gemini's four-point star, ChatGPT's hexagon ring, Google's blue-G
        // circle. Chosen by the live AIAskUrl, so the tray's "Ask with..."
        // switch is instantly visible in the notch.
        private static void DrawAiProviderMark(Graphics g, string? url, Rectangle area)
        {
            float cx = area.X + area.Width / 2f, cy = area.Y + area.Height / 2f;
            float r = area.Width / 2f - 1.5f;
            url = url ?? "";
            using var pen = new Pen(Color.White, 1.8f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

            if (url.Contains("claude"))
            {
                for (int i = 0; i < 3; i++)
                {
                    double a = Math.PI / 2.0 * i / 3.0 + 0.5236; // 30/90/150 deg
                    g.DrawLine(pen,
                        cx - (float)Math.Cos(a) * r, cy - (float)Math.Sin(a) * r,
                        cx + (float)Math.Cos(a) * r, cy + (float)Math.Sin(a) * r);
                }
                return;
            }
            if (url.Contains("gemini"))
            {
                using var b = new SolidBrush(Color.White);
                g.FillPolygon(b, new[]
                {
                    new PointF(cx, cy - r), new PointF(cx + r * 0.30f, cy - r * 0.30f),
                    new PointF(cx + r, cy), new PointF(cx + r * 0.30f, cy + r * 0.30f),
                    new PointF(cx, cy + r), new PointF(cx - r * 0.30f, cy + r * 0.30f),
                    new PointF(cx - r, cy), new PointF(cx - r * 0.30f, cy - r * 0.30f),
                });
                return;
            }
            if (url.Contains("chatgpt"))
            {
                var pts = new PointF[6];
                for (int i = 0; i < 6; i++)
                {
                    double a = -Math.PI / 2 + i * Math.PI / 3.0;
                    pts[i] = new PointF(cx + (float)Math.Cos(a) * r, cy + (float)Math.Sin(a) * r);
                }
                using (var path = new GraphicsPath())
                {
                    path.AddPolygon(pts);
                    g.DrawPath(pen, path);
                }
                using var dot = new SolidBrush(Color.White);
                g.FillEllipse(dot, cx - r * 0.16f, cy - r * 0.16f, r * 0.32f, r * 0.32f);
                return;
            }
            using (var circle = new Pen(Color.FromArgb(66, 133, 244), 1.9f))
                g.DrawEllipse(circle, area.X, area.Y, area.Width - 2, area.Height - 2);
            var gf = CachedFont("Segoe UI", 9.5F, FontStyle.Bold);
            TextRenderer.DrawText(g, "G", gf, area, Color.FromArgb(66, 133, 244),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // Agent choices for "Ask anything"; {query} takes the typed words.
        // Google AI Mode is the one that answers straight from a query URL;
        // the others open their app with the words already on the clipboard.
        private static readonly (string Name, string Template)[] AiPresets =
        {
            ("Google AI Mode", "https://www.google.com/search?q={query}&udm=50"),
            ("ChatGPT", "https://chatgpt.com/?q={query}"),
            ("Gemini", "https://gemini.google.com/?q={query}"),
            ("Claude", "https://claude.ai/new?q={query}"),
        };

        private void SetupTrayIcon()
        {
            trayIcon.Icon = CreateTrayIcon();
            trayIcon.Text = "My Dynamic Island";
            trayIcon.Visible = true;

            islandMenu = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(30, 30, 32),
                ForeColor = Color.White,
                ShowImageMargin = false
            };

            // The old mode list drove the beside-notch bubble; the bubble is
            // gone, so these now jump the expanded card to a page (and open
            // it when it was closed). The queue is not here — it is the media
            // page's extension, opened by the button on the card itself.
            var mediaItem = new ToolStripMenuItem("Now Playing");
            mediaItem.Click += (_, _) => SetPage(0);
            islandMenu.Items.Add(mediaItem);

            var searchMenuItem = new ToolStripMenuItem("Search");
            searchMenuItem.Click += (_, _) => SetPage(1);
            islandMenu.Items.Add(searchMenuItem);

            var todayMenuItem = new ToolStripMenuItem("Today");
            todayMenuItem.Click += (_, _) => SetPage(2);
            islandMenu.Items.Add(todayMenuItem);

            var displayMenu = new ToolStripMenuItem("Move to display");
            for (int i = 0; i < Screen.AllScreens.Length; i++)
            {
                Screen screen = Screen.AllScreens[i];
                string label = screen.Primary ? $"Display {i + 1} (Primary)" : $"Display {i + 1}";
                var item = new ToolStripMenuItem(label);
                item.Click += (_, _) =>
                {
                    anchorScreen = screen;
                    RepositionWindow();
                    RenderLayered();
                };
                displayMenu.DropDownItems.Add(item);
            }

            islandMenu.Items.Add(new ToolStripSeparator());
            islandMenu.Items.Add(displayMenu);
            islandMenu.Items.Add(new ToolStripSeparator());

            var askItem = new ToolStripMenuItem("Ask anything (AI)");
            askItem.Click += (_, _) => ShowAsk();
            islandMenu.Items.Add(askItem);

            // Which agent "Ask anything" answers through. The typed words
            // always land on the clipboard too, so even the assistants that
            // refuse query links (ChatGPT/Claude/Gemini) open one Ctrl+V
            // away from an answer.
            var withMenu = new ToolStripMenuItem("Ask with...");
            foreach (var (aiName, template) in AiPresets)
            {
                string capturedName = aiName;
                string capturedTemplate = template;
                var aiItem = new ToolStripMenuItem(capturedName)
                {
                    Tag = capturedTemplate,
                    Checked = (appSettings.AiAskUrl ?? "").Contains(capturedTemplate,
                        StringComparison.Ordinal)
                };
                aiItem.Click += (_, _) =>
                {
                    appSettings.AiAskUrl = capturedTemplate;
                    appSettings.Save();
                    foreach (ToolStripItem child in withMenu.DropDownItems)
                        if (child is ToolStripMenuItem mi)
                            mi.Checked = string.Equals((string?)mi.Tag, capturedTemplate,
                                StringComparison.Ordinal);
                };
                withMenu.DropDownItems.Add(aiItem);
            }
            islandMenu.Items.Add(withMenu);

            var settingsItem = new ToolStripMenuItem("Open settings");
            settingsItem.Click += (_, _) => SettingsRequested?.Invoke();
            islandMenu.Items.Add(settingsItem);

            // A stale token can also be fixed from here, which matters because
            // the queue panel is only reachable through the card.
            var spotifyItem = new ToolStripMenuItem("Reconnect Spotify account");
            spotifyItem.Click += (_, _) => _ = ReconnectSpotifyAsync();
            islandMenu.Items.Add(spotifyItem);

            // The way to hand the island to a different Spotify account: the
            // protected token file alone would keep silently refreshing the old
            // one, and Connect without this just renews it.
            var spotifySignOut = new ToolStripMenuItem("Sign out of Spotify")
            {
                Visible = spotifyQueue.IsConfigured
            };
            spotifySignOut.Click += (_, _) => spotifyQueue.Disconnect();
            islandMenu.Items.Add(spotifySignOut);
            var startupItem = new ToolStripMenuItem("Launch at Windows startup")
            {
                Checked = StartupManager.IsEnabled(),
                CheckOnClick = true
            };
            startupItem.CheckedChanged += (_, _) => StartupManager.SetEnabled(startupItem.Checked);
            islandMenu.Items.Add(startupItem);
            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (s, e) => Close();
            islandMenu.Items.Add(exitItem);
            trayIcon.ContextMenuStrip = islandMenu;
            trayIcon.DoubleClick += (_, _) => SettingsRequested?.Invoke();
        }

        private Icon CreateTrayIcon()
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var brush = new SolidBrush(Color.White);
                g.FillEllipse(brush, 4, 10, 24, 12);
            }

            IntPtr hIcon = bmp.GetHicon();
            try
            {
                using var handleIcon = Icon.FromHandle(hIcon);
                return (Icon)handleIcon.Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private void RepositionWindow()
        {
            Rectangle screen = anchorScreen.WorkingArea;
            Location = new Point(
                screen.Left + (screen.Width - Width) / 2,
                screen.Top - appliedHideOffset);
        }

        private void ApplyExactWindowSize(Size totalSize)
        {
            Rectangle screen = anchorScreen.WorkingArea;
            int x = screen.Left + (screen.Width - totalSize.Width) / 2;
            int y = screen.Top - appliedHideOffset;

            // Keep WinForms' managed bounds and the real layered HWND in sync.
            // SetWindowPos bypasses any late AutoScale/layout pass that tries to
            // restore the old 260x72 shell after lyrics/notification callbacks.
            ClientSize = totalSize;
            bool nativeApplied = !IsHandleCreated || SetWindowPos(
                Handle, new IntPtr(-1), x, y, totalSize.Width, totalSize.Height,
                0x0010 | 0x0040); // SWP_NOACTIVATE | SWP_SHOWWINDOW

            lastAppliedWindowSize = totalSize;
        }

        private Size CurrentPillSize()
            => new Size(Math.Max(1, PillWidth), Math.Max(1, Height));

        private void RetargetSizeAnimation(Size desiredSize)
        {
            if (desiredSize == targetSize) return;

            sizeAnimationStart = CurrentPillSize();
            targetSize = desiredSize;
            sizeAnimationStartedAt = DateTime.Now;

            int distance = Math.Max(
                Math.Abs(targetSize.Width - sizeAnimationStart.Width),
                Math.Abs(targetSize.Height - sizeAnimationStart.Height));
            sizeAnimationDurationMs = Math.Clamp(150d + distance * 0.65d, 190d, 360d);
            sizeAnimationRunning = sizeAnimationStart != targetSize;
        }

        private void UpdateSizeAnimation()
        {
            bool volumeStripShowing = ShowVolumeStrip;
            if (volumeStripWasShowing && !volumeStripShowing)
                volumeStripClosing = true;
            else if (volumeStripShowing)
                volumeStripClosing = false;
            volumeStripWasShowing = volumeStripShowing;

            RetargetSizeAnimation(DecideTargetSize());

            if (!sizeAnimationRunning)
            {
                Size exact = GetTotalSize(targetSize);
                if (lastAppliedWindowSize != exact)
                    ApplyExactWindowSize(exact);
                return;
            }

            double elapsed = (DateTime.Now - sizeAnimationStartedAt).TotalMilliseconds;
            float progress = (float)Math.Clamp(elapsed / sizeAnimationDurationMs, 0d, 1d);
            float eased = progress < 0.5f
                ? 4f * progress * progress * progress
                : 1f - MathF.Pow(-2f * progress + 2f, 3f) / 2f;

            var frame = new Size(
                (int)Math.Round(sizeAnimationStart.Width +
                    (targetSize.Width - sizeAnimationStart.Width) * eased),
                (int)Math.Round(sizeAnimationStart.Height +
                    (targetSize.Height - sizeAnimationStart.Height) * eased));

            if (progress >= 1f)
            {
                frame = targetSize;
                sizeAnimationRunning = false;
                if (!queueOpen)
                    queuePanelClosing = false;
                if (!ShowVolumeStrip)
                    volumeStripClosing = false;
            }

            Size totalFrame = GetTotalSize(frame);
            if (lastAppliedWindowSize != totalFrame)
                ApplyExactWindowSize(totalFrame);
        }

        // Keep only one RoundedRegion method
        private Region RoundedRegion(Rectangle bounds, int radius)
        {
            int r = Math.Max(1, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
            using var path = RoundedRectangle(bounds, r);
            return new Region(path);
        }

        private static void FillLiquidGlass(Graphics g, GraphicsPath path, Rectangle bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            using (var fill = new SolidBrush(Color.Black))
                g.FillPath(fill, path);
        }

        // A desktop notch rather than a floating phone pill: the narrow top
        // edge is attached to the screen and the shoulders flow outward into
        // the wider card before rounding around the bottom.
        private static GraphicsPath AttachedNotchPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            float left = rect.Left;
            float top = rect.Top;
            float right = rect.Right;
            float bottom = rect.Bottom;
            float shoulder = Math.Clamp(rect.Width * 0.045f, 9f, 18f);
            float shoulderY = Math.Clamp(rect.Height * 0.2f, 11f, 27f);
            float r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2f);

            path.StartFigure();
            path.AddLine(left + shoulder, top, right - shoulder, top);
            path.AddBezier(
                right - shoulder, top,
                right - shoulder * 0.35f, top,
                right, top + shoulderY * 0.38f,
                right, top + shoulderY);
            path.AddLine(right, top + shoulderY, right, bottom - r);
            path.AddArc(right - r * 2, bottom - r * 2, r * 2, r * 2, 0, 90);
            path.AddLine(right - r, bottom, left + r, bottom);
            path.AddArc(left, bottom - r * 2, r * 2, r * 2, 90, 90);
            path.AddLine(left, bottom - r, left, top + shoulderY);
            path.AddBezier(
                left, top + shoulderY,
                left, top + shoulderY * 0.38f,
                left + shoulder * 0.35f, top,
                left + shoulder, top);
            path.CloseFigure();
            return path;
        }

        // "Squircle" corner: a superellipse (x/r)^n + (y/r)^n = 1 instead of a
        // quarter-circle arc. iOS/macOS corners are continuous-curvature, not
        // circular — this is what makes the pill read as one seamless shape
        // (like the reference video) rather than a rounded-rect with visible
        // arc-to-edge seams. n=4 approximates that continuity closely.
        private const double SquircleExponent = 4.0;
        private const int SquircleSamplesPerCorner = 20;

        private static GraphicsPath RoundedRectangle(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int r = Math.Max(1, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2));

            // Below ~6px a superellipse and a circle are visually identical,
            // so skip the extra sampling cost for small glyph-sized corners.
            // Likewise, a fully round square (an avatar) should stay a true
            // circle rather than the slightly-squared-off squircle silhouette.
            bool isFullCircle = rect.Width == rect.Height && r * 2 >= rect.Width;
            if (r <= 6 || isFullCircle)
            {
                int d = r * 2;
                path.AddArc(rect.X, rect.Y, d, d, 180, 90);
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }

            var pts = new List<PointF>((SquircleSamplesPerCorner + 1) * 4);

            // Walk all four corners going clockwise from top-right, each as a
            // quarter-superellipse around its own center, so the whole pill
            // is one continuous outline with no seams between corner and edge.
            AddSquircleCorner(pts, rect.Right - r, rect.Y + r, r, startDeg: -90);   // top-right
            AddSquircleCorner(pts, rect.Right - r, rect.Bottom - r, r, startDeg: 0);    // bottom-right
            AddSquircleCorner(pts, rect.X + r, rect.Bottom - r, r, startDeg: 90);   // bottom-left
            AddSquircleCorner(pts, rect.X + r, rect.Y + r, r, startDeg: 180);  // top-left

            path.AddPolygon(pts.ToArray());
            return path;
        }

        // Appends one quarter-superellipse arc (a "squircle corner") centered
        // at (cx,cy), sweeping 90 degrees clockwise starting at startDeg.
        private static void AddSquircleCorner(List<PointF> pts, float cx, float cy, int r, double startDeg)
        {
            double startRad = startDeg * Math.PI / 180.0;
            for (int i = 0; i <= SquircleSamplesPerCorner; i++)
            {
                double t = startRad + (Math.PI / 2) * (i / (double)SquircleSamplesPerCorner);
                double cosT = Math.Cos(t), sinT = Math.Sin(t);
                double x = r * Math.Pow(Math.Abs(cosT), 2.0 / SquircleExponent) * Math.Sign(cosT);
                double y = r * Math.Pow(Math.Abs(sinT), 2.0 / SquircleExponent) * Math.Sign(sinT);
                pts.Add(new PointF(cx + (float)x, cy + (float)y));
            }
        }

        // The pill's shape: a short flat run at the very top (flush against
        // the screen edge) that flows into the corner via a gentle bend —
        // not a hard 90° angle — before the sides go straight down into the
        // bigger squircle-rounded bottom corners. Wing geometry ported directly
        // from the standalone SVG tuner (len=70 rise=31 angle=-29 b1=100 b2=100
        // offset=5 drop=25), mirrored programmatically so left/right are
        // pixel-symmetric regardless of parameter values, and sealed so the
        // wing's return curve terminates exactly at the top-edge/side-wall
        // corner point (no gap/sliver between wing and body).
        private static GraphicsPath TopFlatRoundedRectangle(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int r = Math.Max(1, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2));

            if (r <= 6)
            {
                int d = r * 2;
                path.AddLine(rect.X, rect.Y, rect.Right, rect.Y);
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }

            // --- Wing tuner parameters (from the SVG tuner, tuned at 640x300
            // reference canvas with pill top=80/bottom=230/left=130/right=490,
            // i.e. a 360x150 pill). Scaled below to the actual rect/radius. ---
            const float tunerLen = 70f;
            const float tunerRise = 31f;
            const float tunerAngleDeg = -29f;
            const float tunerB1 = 1.0f;   // 100/100
            const float tunerB2 = 1.0f;   // 100/100
            const float tunerOffset = 5f;
            const float tunerDrop = 25f;
            const float tunerPillHeight = 150f; // 230 - 80

            float scale = rect.Height / tunerPillHeight;
            float len = tunerLen * scale;
            float rise = tunerRise * scale;
            float dropPx = tunerDrop * scale;
            float tipYAdj = -tunerOffset * scale;

            float top = rect.Y, left = rect.X, right = rect.Right, bottom = rect.Bottom;
            float centerX = (left + right) / 2f;

            double ang = tunerAngleDeg * Math.PI / 180.0;
            float dx = (float)Math.Cos(ang) * len;
            float dy = (float)Math.Sin(ang) * len;

            // --- Compute the LEFT wing only ---
            float lBaseX = left, lBaseY = top + dropPx;
            float lTipX = lBaseX - dx, lTipY = top - rise - dy + tipYAdj;
            float lC1x = lBaseX - dx * tunerB1 * 0.25f, lC1y = lBaseY - (lBaseY - top) * tunerB1;
            float lC2x = lTipX + dx * (1 - tunerB2), lC2y = lTipY + (lBaseY - lTipY) * (1 - tunerB2) * 0.5f;

            var lStart = new PointF(lBaseX, lBaseY + 6 * scale);
            var lMid1 = new PointF(lTipX + (lC2x - lTipX) * 0.3f, lTipY + 10 * scale);
            var lTip = new PointF(lTipX, lTipY);
            var lMid2 = new PointF(lBaseX - dx * 0.2f, top + (lBaseY - top) * 0.15f);
            var lEnd = new PointF(left, top);
            var lC1 = new PointF(lC1x, lC1y);
            var lC2 = new PointF(lC2x, lC2y);

            // --- RIGHT wing = mirror image of the left wing across centerX ---
            // Guarantees pixel-symmetric sides regardless of parameter values.
            PointF Mirror(PointF p) => new PointF(2 * centerX - p.X, p.Y);
            var rStart = Mirror(lStart);
            var rC1 = Mirror(lC1);
            var rMid1 = Mirror(lMid1);
            var rTip = Mirror(lTip);
            var rC2 = Mirror(lC2);
            var rMid2 = Mirror(lMid2);
            var rEnd = Mirror(lEnd);

            // --- Build the sealed outline: left wing -> top edge -> right wing
            // (mirrored, walked tip->base) -> right wall -> bottom squircle
            // corners -> left wall -> close. ---
            path.AddBezier(lStart, lC1, lMid1, lTip);
            path.AddBezier(lTip, lC2, lMid2, lEnd);

            path.AddLine(lEnd, rEnd);

            path.AddBezier(rEnd, rMid2, rMid1, rTip);
            path.AddBezier(rTip, rC1, rStart, rStart);

            // Right wall + existing bottom-right squircle corner.
            path.AddLine(rStart, new PointF(right, bottom - r));
            var bottomRightPts = new List<PointF>();
            AddSquircleCorner(bottomRightPts, right - r, bottom - r, r, startDeg: 0);
            for (int i = 0; i < bottomRightPts.Count - 1; i++)
                path.AddLine(bottomRightPts[i], bottomRightPts[i + 1]);

            // Bottom-left squircle corner + left wall.
            var bottomLeftPts = new List<PointF>();
            AddSquircleCorner(bottomLeftPts, left + r, bottom - r, r, startDeg: 90);
            for (int i = 0; i < bottomLeftPts.Count - 1; i++)
                path.AddLine(bottomLeftPts[i], bottomLeftPts[i + 1]);
            path.AddLine(bottomLeftPts[bottomLeftPts.Count - 1], lStart);

            path.CloseFigure();
            return path;
        }

        private void RefreshBattery()
        {
            try
            {
                var status = SystemInformation.PowerStatus;
                hasBattery = status.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery;
                batteryPercent = status.BatteryLifePercent;
                batteryCharging = status.PowerLineStatus == PowerLineStatus.Online && hasBattery;

                if (appSettings.BatteryActivity && batteryStateInitialized &&
                    batteryCharging != previousChargingState && IsHandleCreated)
                {
                    ShowSystemActivity(
                        "Power",
                        batteryCharging ? $"Charging · {batteryPercent:P0}" : $"On battery · {batteryPercent:P0}",
                        TransientKind.Battery,
                        2400);
                }

                if (hasBattery && !batteryCharging && batteryPercent <= 0.20f)
                {
                    if (appSettings.LowBatteryAlerts && !lowBatteryAlerted && IsHandleCreated)
                    {
                        lowBatteryAlerted = true;
                        ShowSystemActivity("Low Battery", $"{batteryPercent:P0} remaining", TransientKind.Battery, 4000);
                    }
                }
                else if (batteryCharging || batteryPercent >= 0.25f)
                {
                    lowBatteryAlerted = false;
                }

                previousChargingState = batteryCharging;
                batteryStateInitialized = true;
            }
            catch
            {
                hasBattery = false;
            }

            if (IsHandleCreated)
                RefreshVisual();
        }

        private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
        {
            if (!appSettings.NetworkChanges) return;
            void Apply()
            {
                bool wifi = e.IsAvailable && NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                    n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                    n.OperationalStatus == OperationalStatus.Up);
                ShowSystemActivity(
                    e.IsAvailable ? (wifi ? "Wi-Fi Connected" : "Network Connected") : "Network Offline",
                    e.IsAvailable ? "Internet access is available" : "Check your connection",
                    TransientKind.Network,
                    2800);
            }

            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke((Action)Apply); else Apply();
        }

        private void NotificationReceived(string appName, string message, byte[]? logoPng)
        {
            if (!appSettings.WindowsNotifications) return;

            // icon swap happens on the UI thread with the popup itself;
            // the previous sender's bitmap dies the moment a new one lands
            Bitmap? nextIcon = null;
            if (logoPng != null && logoPng.Length > 64)
            {
                try
                {
                    using var ms = new MemoryStream(logoPng);
                    using var raw = Image.FromStream(ms);
                    nextIcon = new Bitmap(raw, new Size(44, 44));
                }
                catch { nextIcon = null; }
            }

            void Apply()
            {
                var old = toastAppIcon;
                toastAppIcon = nextIcon;
                old?.Dispose();
                ShowSystemActivity(
                    string.IsNullOrWhiteSpace(appName) ? "Notification" : appName,
                    message,
                    TransientKind.Notification,
                    5200);
            }

            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke((Action)Apply); else Apply();
        }

        private void ShowSystemActivity(string title, string detail, TransientKind kind, int durationMs)
        {
            // Same-card dedupe: the wifi poll, NLA event, DBT broadcast and
            // the default-audio poll can all describe ONE physical moment.
            string key = title + "|" + detail;
            if (holding && transientTitle + "|" + transientDetail == key &&
                (DateTime.Now - transientStartedAt).TotalMilliseconds < 1400) return;

            notificationExpanded = false;
            transientKind = kind;
            transientTitle = title;
            transientDetail = detail;
            transientStartedAt = DateTime.Now;
            holding = true;
            holdUntil = DateTime.Now.AddMilliseconds(durationMs);
            collapseAt = null;
            RefreshVisual();
        }

        public void ApplySettings()
        {
            // Battery is no longer its own page — the Today pill carries the
            // readout — so the setting only gates what the collapsed pill
            // shows and the alerts. The clamp just keeps the index honest.
            pageIndex = Math.Clamp(pageIndex, 0, PageCount() - 1);

            if (appSettings.MediaActivity && !mediaEventsEnabled)
            {
                mediaWatcher.Start();
                mediaEventsEnabled = true;
            }
            else if (!appSettings.MediaActivity && mediaEventsEnabled)
            {
                mediaWatcher.Stop();
                mediaEventsEnabled = false;
                mediaText = "";
                mediaArtist = "";
                albumArtBox.Image?.Dispose();
                albumArtBox.Image = null;
            }

            if (appSettings.NetworkChanges && !networkEventsEnabled)
            {
                NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
                networkEventsEnabled = true;
            }
            else if (!appSettings.NetworkChanges && networkEventsEnabled)
            {
                NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
                networkEventsEnabled = false;
            }

            if (appSettings.WindowsNotifications && !notificationEventsEnabled)
            {
                notificationWatcher.Start();
                notificationEventsEnabled = true;
            }
            else if (!appSettings.WindowsNotifications && notificationEventsEnabled)
            {
                notificationWatcher.Dispose();
                notificationEventsEnabled = false;
            }

            if (appSettings.BrightnessChanges && !brightnessEventsEnabled)
            {
                RegisterBrightnessNotifications();
                brightnessEventsEnabled = brightnessNotificationHandle != IntPtr.Zero;
            }
            else if (!appSettings.BrightnessChanges && brightnessEventsEnabled)
            {
                UnregisterPowerSettingNotification(brightnessNotificationHandle);
                brightnessNotificationHandle = IntPtr.Zero;
                brightnessEventsEnabled = false;
            }

            RefreshVisual();
        }

        // --- Public surface for the settings window (SettingsForm) ---
        public AppSettings Settings => appSettings;

        // Copies dialog values into the live settings instance, persists first,
        // then re-runs ApplySettings so every watcher starts/stops immediately.
        public void ApplyNewSettings(AppSettings fresh)
        {
            appSettings.MediaActivity = fresh.MediaActivity;
            appSettings.BatteryActivity = fresh.BatteryActivity;
            appSettings.LowBatteryAlerts = fresh.LowBatteryAlerts;
            appSettings.WindowsNotifications = fresh.WindowsNotifications;
            appSettings.NetworkChanges = fresh.NetworkChanges;
            appSettings.DeviceChanges = fresh.DeviceChanges;
            appSettings.BrightnessChanges = fresh.BrightnessChanges;
            appSettings.WelcomeGreeting = fresh.WelcomeGreeting;
            appSettings.AutoHide = fresh.AutoHide;
            appSettings.AutoHideSeconds = Math.Clamp(fresh.AutoHideSeconds, 1, 600);
            // Turning the feature on (or tuning the clock) should never hide
            // the island instantly out from under whoever just clicked Save.
            notchLastActive = DateTime.Now;
            appSettings.SpotifyClientId = (fresh.SpotifyClientId ?? "").Trim();
            appSettings.SpotifyRedirectUri = string.IsNullOrWhiteSpace(fresh.SpotifyRedirectUri)
                ? SpotifyQueueService.DefaultRedirect : fresh.SpotifyRedirectUri.Trim();
            // Picks up a freshly pasted Client ID and re-reads the protected
            // token file it owns — no restart needed.
            spotifyQueue.Configure(appSettings);
            if (!string.IsNullOrWhiteSpace(fresh.AiAskUrl))
                appSettings.AiAskUrl = fresh.AiAskUrl;
            appSettings.WeatherCity = (fresh.WeatherCity ?? "").Trim();
            appSettings.AllowAutoLocation = fresh.AllowAutoLocation;
            appSettings.Save();
            // A changed city must reflect within seconds, not at the next
            // quarter-hour refresh; emptying it re-reads via IP on the next
            // tick too.
            todayFeed?.InvalidateWeather();
            ApplySettings();
        }

        // Records whatever Windows answered our notification-access request so
        // the settings window can show it as a status line. Deliberately does
        // NOT pop up an island card on failure anymore: a plain .exe can never
        // be granted Windows' notification-reader permission (the API requires
        // package identity), so repeating "access denied" on every launch was
        // noise, not a fixable problem.
        private void OnNotificationAccessResolved(WindowsNotificationWatcher.AccessState state)
        {
            notificationAccess = state;
            notificationAccessDetail = notificationWatcher.AccessDetail;
        }

        // Human-readable access verdict for the settings dialog. When Windows
        // said something specific, the dialog now quotes it instead of a
        // shrug - "unavailable in this build" was near-useless forensics.
        public string NotificationAccessText => notificationAccess switch
        {
            WindowsNotificationWatcher.AccessState.Allowed =>
                "Windows access: granted",
            WindowsNotificationWatcher.AccessState.Denied =>
                "Windows access: denied ("
                    + (string.IsNullOrWhiteSpace(notificationAccessDetail) ? "user consent declined" : notificationAccessDetail) + ")",
            WindowsNotificationWatcher.AccessState.Failed =>
                // The honest portable-exe sentence: Windows grants the
                // notification listener only to installed (packaged) apps, so
                // this is not a malfunction to fix but a door by design.
                "unavailable in the portable build — Windows gives live-notification "
                + "access only to installed apps; every other feature works",
            _ => "Windows access: not checked yet",
        };

        // The settings dialog prints this under the field you just typed into,
        // instead of a "possible?: yes" that says nothing about what it found.
        // What the settings window prints under the field you just typed into:
        // the saved Client ID's own problem if it has one, otherwise whether the
        // token behind it is live.
        public string SpotifyStatusText
        {
            get
            {
                if (!spotifyQueue.IsConfigured)
                    return "Not set up yet — a Client ID (or SPOTIFY_ACCESS_TOKEN) is needed.";

                string why = appSettings.SpotifyCredentialProblem();
                if (why.Length > 0) return "Credentials: " + why;

                return spotifyQueue.Status switch
                {
                    SpotifyQueueStatus.Ok => "Signed in; the token renews by itself.",
                    SpotifyQueueStatus.PremiumRequired => "Signed in, but Spotify gates the " +
                        "up-next list behind Premium.",
                    SpotifyQueueStatus.Connecting => "Sign-in in progress...",
                    _ => "Credentials look usable; not signed in yet.",
                };
            }
        }

        // Pulls the live Spotify queue while the panel is open. This path never
        // raises a browser: a 4s timer that decided you "should" log in would
        // spawn a tab every four seconds. Sign-in only starts from something you
        // press — the panel's Connect button or the tray's reconnect entry.
        private async Task RefreshSpotifyQueueAsync()
        {
            if (!queueOpen || queueFetchInFlight) return;

            queueFetchInFlight = true;
            try
            {
                var items = await spotifyQueue.GetQueueAsync();
                if (items == null)
                {
                    // No answer at all. Keep the rows already on screen — a
                    // momentary network blip should not blank a list you were
                    // reading — and let the status line carry the reason.
                    SetQueueStatus(spotifyQueue.Status, spotifyQueue.StatusDetail);
                    return;
                }

                SetQueue(items);
                SetQueueStatus(SpotifyQueueStatus.Ok, "");
            }
            catch
            {
                // Network hiccup or an expired session: the next tick retries.
                SetQueueStatus(spotifyQueue.Status, spotifyQueue.StatusDetail);
            }
            finally
            {
                queueFetchInFlight = false;
            }
        }

        // Only touches the status line, never the rows, so the list and the
        // explanation for what is missing from it can change independently.
        private void SetQueueStatus(SpotifyQueueStatus status, string detail)
        {
            void Apply()
            {
                queueStatus = status;
                queueStatusDetail = detail ?? "";
                RefreshVisual();
            }

            if (InvokeRequired) BeginInvoke(new Action(Apply));
            else Apply();
        }

        // Raised by the service when a background token refresh lands or a
        // browser handshake finishes — i.e. exactly when the panel should redraw
        // rather than sit until the next poll.
        private void OnSpotifyQueueStatusChanged()
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (queueCheckRunning) return;
                    SetQueueStatus(spotifyQueue.Status, spotifyQueue.StatusDetail);
                    if (queueOpen && spotifyQueue.Status == SpotifyQueueStatus.Ok)
                        _ = RefreshSpotifyQueueAsync();
                }));
            }
            catch
            {
            }
        }

        // Skips the cached token on purpose. Spotify's refresh call can only
        // re-issue scopes that were already granted, so a token minted before
        // queue control existed has to go through the browser again — pressing
        // Reconnect must never look like it did nothing.
        private async Task ReconnectSpotifyAsync()
        {
            if (queueCheckRunning) return;
            queueCheckRunning = true;
            SetQueueStatus(SpotifyQueueStatus.Connecting, "Re-approving in the browser...");

            try
            {
                if (await spotifyQueue.ReauthorizeAsync())
                    await RefreshSpotifyQueueAsync();
                else
                    SetQueueStatus(spotifyQueue.Status, spotifyQueue.StatusDetail);
            }
            catch
            {
            }
            finally
            {
                queueCheckRunning = false;
            }
        }

        // Toggling the "Playing Next" button opens/closes the extension under
        // the media card AND starts/stops polling Spotify for it, kicking off
        // login the first time it's opened if we don't have a session yet.
        private void ToggleQueuePanel()
        {
            if (!queueOpen)
            {
                queueOpen = true;
                queuePanelClosing = false;
                queueScrollPx = 0f;
                queuePollTimer.Start();
                _ = EnsureSpotifyLoginAndRefreshAsync();
            }
            else
            {
                CloseQueueExtension();
            }
            hoveredQueueRow = -1;
        }

        // The interactive path, and the only place allowed to open a browser.
        // Guarded so a double press cannot queue two handshakes.
        private async Task EnsureSpotifyLoginAndRefreshAsync()
        {
            if (queueCheckRunning) return;
            queueCheckRunning = true;
            SetQueueStatus(SpotifyQueueStatus.Connecting, "Contacting Spotify...");

            try
            {
                if (!spotifyQueue.IsConfigured)
                {
                    // The panel shows this plus an "Open settings" button, so no
                    // popup is stacked on top of the card you are already reading.
                    SetQueueStatus(SpotifyQueueStatus.NoClientConfigured,
                        "developer.spotify.com/dashboard -> Create app, add the redirect, " +
                        "paste the Client ID in Settings.");
                    return;
                }

                // A scope-gated failure is not fixed by another silent handshake,
                // so once Spotify has said insufficient_scope, pressing the button
                // means "re-approve" and goes straight to the browser.
                bool signedIn = queueStatus == SpotifyQueueStatus.ReapprovalNeeded
                    ? await spotifyQueue.ReauthorizeAsync()
                    : await spotifyQueue.ConnectAsync();

                if (!signedIn)
                {
                    SetQueueStatus(spotifyQueue.Status, spotifyQueue.StatusDetail);
                    return;
                }

                await RefreshSpotifyQueueAsync();
            }
            catch
            {
            }
            finally
            {
                queueCheckRunning = false;
            }
        }

        // The panel's single button. Its label is the action, because "Connect"
        // is wrong on a machine where credentials are missing entirely.
        // The states where a press can actually change the outcome. Premium and
        // "nothing playing" are deliberately absent: no button fixes those, and a
        // control that cannot work is worse than a sentence that says so.
        private bool QueueNeedsAttention =>
            !queueCheckRunning &&
            queueStatus is SpotifyQueueStatus.NoClientConfigured or
                SpotifyQueueStatus.NotConnected or
                SpotifyQueueStatus.ConnectionDeclined or
                SpotifyQueueStatus.RejectedBySpotify or
                SpotifyQueueStatus.ReapprovalNeeded or
                SpotifyQueueStatus.NetworkError;

        private bool ShowQueueConnectButton => QueueNeedsAttention;

        // "Reconnect" rather than "Retry" wherever a fresh approval is what the
        // state actually needs, because pressing a button called Retry must not
        // silently do nothing twice in a row.
        private string QueueConnectLabel =>
            queueStatus == SpotifyQueueStatus.NoClientConfigured ? "Open settings"
            : queueStatus is SpotifyQueueStatus.NotConnected
                or SpotifyQueueStatus.ReapprovalNeeded ? "Reconnect Spotify"
            : "Retry Spotify";

        private void UpdateAudioWaveform()
        {
            wavePhase += 0.22f;

            float capturedTotal;
            lock (audioMeterLock)
            {
                capturedTotal = 0f;
                for (int i = 0; i < waveformLevels.Length; i++)
                {
                    // Smooth each band toward its latest captured peak so bars
                    // rise and fall with the actual audio instead of snapping.
                    audioBandLevels[i] = audioBandLevels[i] * 0.5f + loopbackBandPeaks[i] * 0.5f;
                    capturedTotal += loopbackBandPeaks[i];
                    loopbackBandPeaks[i] *= 0.25f; // consumed this frame — decay for the next one
                }
            }

            if (!isPlaying)
            {
                // Ease every bar down to a calm, static baseline when nothing is playing.
                for (int i = 0; i < waveformLevels.Length; i++)
                    waveformLevels[i] = Math.Max(0.06f, waveformLevels[i] * 0.85f);
                RefreshVisual();
                return;
            }

            bool hasSignal = capturedTotal > 0.015f;

            for (int i = 0; i < waveformLevels.Length; i++)
            {
                if (hasSignal)
                {
                    // Real audio: each bar reflects its own captured band level.
                    float level = 0.12f + audioBandLevels[i] * 2.6f;
                    waveformLevels[i] = Math.Clamp(level, 0.1f, 1f);
                }
                else
                {
                    // Loopback capture isn't producing a signal (it can fail
                    // silently on some devices/VMs) — fall back to a gentle
                    // synthetic wave so the meter still reads as "alive".
                    float phase = wavePhase + i * 0.85f;
                    float pulse = 0.5f + 0.5f * (float)Math.Sin(phase);
                    waveformLevels[i] = Math.Clamp(0.2f + pulse * 0.5f, 0.16f, 1f);
                }
            }

            RefreshVisual();
        }

        private Size DecideTargetSize()
        {
            if (overlay == OverlayKind.Call)
                return callSize;
            if (overlay == OverlayKind.Reply)
                return replySize;
            if (overlay == OverlayKind.Ask)
                return askSize;

            // Greeting wins even over transient popups at launch (see PaintFrame
            // for the same ordering rule) - the brightness broadcast Windows
            // fires on boot used to steal the first seconds of the hello.
            if (ShowWelcome)
                return welcomeSize;

            if (holding)
            {
                if (transientKind == TransientKind.Download)
                    return downloadSize;

                if (transientKind == TransientKind.Notification)
                    return askSize;   // the iOS card speaks ask-page dimensions

                if (transientKind == TransientKind.Volume || transientKind == TransientKind.Brightness)
                    // One shared slider-tile silhouette for volume & brightness.
                    // Once a 356x84 slab — now close to the footprint of the
                    // card's own volume strip, so it reads as a sibling of the
                    // island's parts instead of a billboard over them.
                    return new Size(330, 62);

                // Text notifications open narrow and expand downward on click.
                return NotificationShowing ? NotificationSize
                    : new Size(Math.Max(340, peekSize.Width), peekSize.Height);
            }

            // Expanded card: exactly one page visible at a time, sized to fit
            // what that page shows. The wheel and the right-edge indicator
            // are how you move; the window itself morphs with the page.
            if (clicked)
            {
                if (activeMode == IslandMode.Search)
                    return askSize;
                if (activeMode == IslandMode.Today)
                    return todaySize;

                // The media page: base card, the queue extension when it's
                // open, and the volume strip when that's up too.
                int strip = ShowVolumeStrip ? VolumeStripHeight : 0;
                int body = queueOpen ? QueueCardHeight() : fullSize.Height;
                return new Size(fullSize.Width, body + strip);
            }

            // Glance states. The media page reveals the track on hover or
            // right after a change; Search and Today answer a hover with
            // their pill-sized content at the resting width — exactly what
            // the old dot bubble volunteered, now for free and without ever
            // changing the silhouette.
            if (activeMode == IslandMode.Today)
                return (hovering || ShowAnnouncement)
                    ? GetTodayWideSize()      // hover: pill widens, % appears
                    : GetTodayCompactSize();  // rest: time + capsule only

            // Search answers a hover with its pill glyph, never expansion.
            if (hovering || ShowAnnouncement)
            {
                if (activeMode == IslandMode.Search)
                    return restingSize;
                return GetPeekSize();
            }

            return restingSize;
        }

        // Layout constants for the Sneak Peek's pinned top row + the
        // marquee row that grows in below it.
        // *** MARQUEE TEXT BOUNDS ***
        // Pick one of these pairs (or set your own two numbers) for where
        // the Sneak Peek title starts and ends:
        //   A) Snug, edge-to-edge (current default):   Left = 6,   Right = 6
        //   B) Aligned to the icon row above it:        Left = 7,   Right = 34
        //      (7 lines up with the album art's left edge; 34 keeps clear
        //      of the waveform glyph sitting above on the right)
        //   C) Wide, minimal padding:                   Left = 4,   Right = 4
        // Option B — aligned to the icon row above (7 lines up with the
        // album art's left edge; 34 keeps clear of the waveform glyph
        // sitting above on the right).
        private const int PeekTextRightMargin = 34;

        private Size GetPeekSize() => peekSize;

        private void Form1_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                islandMenu?.Show(Cursor.Position);
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            // Any real interaction means the user wants the island itself —
            // retire the launch greeting immediately, then handle the click
            // normally below.
            if (ShowWelcome)
                welcomeUntil = DateTime.MinValue;

            if (overlay == OverlayKind.Call)
            {
                HandleCallOverlayClick(e.Location);
                return;
            }
            if (overlay == OverlayKind.Reply)
            {
                HandleReplyOverlayClick(e.Location);
                return;
            }
            if (overlay == OverlayKind.Ask)
            {
                // The TextBox itself swallows clicks inside it; anywhere else
                // on the card = "never mind".
                DismissOverlay();
                return;
            }

            // Right-edge page indicator, while the card is open: tapping a
            // dot jumps to that page. (This is where the old bubble's job
            // went — but only when the dots are actually on screen, so a
            // popup covering the card never leaves phantom targets behind.)
            if (CardShowing && !holding)
            {
                for (int i = 0; i < PageCount(); i++)
                {
                    if (GetIndicatorHitRect(i).Contains(e.Location))
                    {
                        SetPage(i);
                        return;
                    }
                }
            }

            if (suppressClickToggle)
            {
                suppressClickToggle = false;
                return;
            }

            // The volume tile is a live control while it's up: speaker icon =
            // mute, track tap = set level. Brightness shows the same control
            // READ-ONLY (Windows gives no API to set brightness by percentage
            // on every panel), but clicks on it are swallowed all the same so
            // a HUD tap can never accidentally expand the card behind it.
            // A text notification is a two-tier object: the strip opens into the
            // banner, and pressing it again closes the banner back down. Handled
            // before the card toggle so a press can never expand the media card
            // underneath a popup the user is still reading.
            if (NotificationShowing)
            {
                notificationExpanded = !notificationExpanded;

                // Opening buys reading time. Closing hands the clock straight
                // back: an unconditional short hold, because the remaining
                // window from before the expand would leave the banner sitting
                // there for seconds the user has already finished reading.
                holdUntil = DateTime.Now.AddMilliseconds(notificationExpanded
                    ? Math.Max(NotifReadMs, (holdUntil - DateTime.Now).TotalMilliseconds)
                    : 1500);

                RefreshVisual();
                return;
            }

            if (DownloadHudActive)
            {
                DownloadCardClick(e.Location);
                RefreshVisual();
                return;
            }

            if (ToastHud)
            {
                holding = false;
                RefreshVisual();
                return;
            }

            if (holding && (transientKind == TransientKind.Volume ||
                            transientKind == TransientKind.Brightness))
            {
                if (transientKind == TransientKind.Volume)
                {
                    if (GetHudIconRect().Contains(e.Location))
                    {
                        ToggleMute();
                        ShowVolumeHud(); // keep the tile alive while it's being used
                    }
                    else if (GetHudTrackRect().Contains(e.Location))
                        SetVolumeFromPoint(e.Location.X, GetHudTrackCoreRect());
                }
                return;
            }

            if (!clicked && !appSettings.MediaActivity && activeMode == IslandMode.Media)
            {
                SettingsRequested?.Invoke();
                return;
            }
            // Tapping the artwork opens the app the media is playing from,
            // instead of expanding/collapsing the card. Works in the resting
            // pill and the peek alike — both draw the same left-edge thumbnail.
            if (activeMode == IslandMode.Media && !clicked
                && !string.IsNullOrWhiteSpace(sourceAppName)
                && GetCollapsedArtHitRect().Contains(e.Location))
            {
                TapArtwork();
                return;
            }
            // Expanded page content: the indicator handled above owns the
            // right strip; HandleExpandedClick keeps its hit-testing to the
            // active page so a tap in dead space collapses the card.
            if (CardShowing && HandleExpandedClick(e.Location))
                return;
            clicked = !clicked;
            collapseAt = null;
            if (clicked && activeMode == IslandMode.Search)
            {
                // Opening the question card from the collapsed lens: start it
                // blank and put the caret in it, so typing works immediately.
                askText = "";
                BeginInvoke(new Action(() => Focus()));
            }
            RefreshVisual();
        }

        private void Form1_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Escape) return;

            if (overlay != OverlayKind.None)
            {
                DismissOverlay();
            }
            else if (DownloadHudActive)
            {
                downloadDismissedPhase = IslandDownloads.Current?.Phase ?? "";
                holding = false;
                RefreshVisual();
            }
            else if (ToastHud)
            {
                holding = false;
                RefreshVisual();
            }
            else if (NotificationShowing && notificationExpanded)
            {
                // One press per layer: Esc closes the banner, a second closes
                // the notification itself.
                notificationExpanded = false;
                holdUntil = DateTime.Now.AddMilliseconds(1500);
                RefreshVisual();
            }
            else
            {
                clicked = false;
                collapseAt = null;

                // Esc off the media card takes the extension with it — an
                // off-screen list has nothing to refresh.
                CloseQueueExtension();

                hoveredQueueRow = -1;
                RefreshVisual();
            }

            e.Handled = true;
        }

        private bool HandleExpandedClick(Point point)
        {
            if (activeMode == IslandMode.Today)
            {
                // The weather block's refresh button is this page's only
                // control; it pulls a new reading (which re-geocodes too, so
                // a freshly typed city shows without waiting 15 minutes).
                if (GetWeatherRefreshRect().Contains(point))
                {
                    FlashControl(5);
                    todayFeed?.InvalidateWeather();
                    todayFeed?.Tick();
                    return true;
                }
                return false; // anywhere else — tap falls through to collapse
            }

            // Search card: a tap on it is how you aim your typing at it, not
            // how you close it. Esc (or Enter) leaves; a stray click stays,
            // like clicking into any text field.
            if (activeMode == IslandMode.Search)
            {
                BeginInvoke(new Action(() => Focus()));
                return true;
            }

            // Volume strip live at the card's foot: icon mutes, track sets.
            // With the extension open it rides up under the list, so its
            // top edge is where the extension ends.
            if (ShowVolumeStrip)
            {
                int stripTop = queueOpen ? QueueCardHeight() : fullSize.Height;
                if (HandleVolumeStripClick(point, stripTop))
                    return true;
            }

            // Same clamp as DrawExpandedMedia's controlsY, so hit-testing
            // matches the drawn positions (the media card's controls are
            // pinned to the base-card height).
            int y = Math.Min(Height, fullSize.Height) - 25;
            int center = PillWidth / 2;
            int spread = Math.Clamp((PillWidth - 170) / 2, 26, 72);

            // Big album art in the card: tapping it switches to the next
            // live player (or opens the app when there's only one).
            // Same rect DrawExpandedMedia paints into.
            if (new Rectangle(20, 24, 72, 72).Contains(point) &&
                !string.IsNullOrWhiteSpace(sourceAppName))
            {
                TapArtwork();
                return true;
            }

            if (new Rectangle(14, y - 24, 55, 48).Contains(point))
            {
                ToggleQueuePanel();
                FlashControl(0);
                return true;
            }

            if (queueOpen && HandleQueueClick(point))
                return true;

            if (new Rectangle(center - spread - 24, y - 24, 48, 48).Contains(point))
            {
                FlashControl(1);
                _ = mediaWatcher.PreviousAsync();
                return true;
            }
            if (new Rectangle(center - 28, y - 28, 56, 56).Contains(point))
            {
                // Flip immediately instead of waiting for the OS media session
                // to round-trip back a PlaybackInfoChanged event — that hop can
                // take a noticeable moment, which is what was reading as a long
                // delay on the button. The real event still corrects this if
                // the source app disagrees (e.g. it refuses to toggle).
                isPlaying = !isPlaying;
                basePosition = GetCurrentPosition();
                basePositionStamp = DateTime.Now;
                FlashControl(2);
                _ = mediaWatcher.TogglePlayPauseAsync();
                return true;
            }
            if (new Rectangle(center + spread - 24, y - 24, 48, 48).Contains(point))
            {
                FlashControl(3);
                _ = mediaWatcher.NextAsync();
                return true;
            }
            if (new Rectangle(PillWidth - 82, y - 24, 55, 48).Contains(point))
            {
                if (mediaWatcher.ActiveSessionCount >= 2)
                {
                    // Second live player appeared: this slot is now "open the
                    // playing app", and shuffle keeps its real state for when
                    // only one player is left.
                    FlashControl(4);
                    OpenSourceApp();
                    return true;
                }

                // Flip immediately for instant feedback, same pattern as the
                // play/pause button — the real toggle still round-trips
                // through the OS session async.
                shuffleOn = !shuffleOn;
                FlashControl(4);
                _ = mediaWatcher.SetShuffleAsync(shuffleOn);
                return true;
            }

            Rectangle progressArea = GetProgressHitArea();
            if (progressArea.Contains(point) &&
                trackDuration > TimeSpan.Zero)
            {
                float fraction = Math.Clamp(
                    (point.X - progressArea.Left) / (float)progressArea.Width, 0f, 1f);
                _ = mediaWatcher.SeekAsync(TimeSpan.FromSeconds(
                    trackDuration.TotalSeconds * fraction));
                return true;
            }
            return false;
        }

        // Shared by both card pages' hit-testing mirrors of DrawVolumeStrip's
        // geometry, so mute/set land exactly where they look.
        private bool HandleVolumeStripClick(Point point, int stripTop)
        {
            if (point.Y < stripTop || point.Y > stripTop + VolumeStripHeight) return false;

            hoveredQueueRow = -1; // a strip press is not a row press
            Rectangle sIcon = GetStripIconRect(stripTop);
            sIcon.Inflate(6, 6);
            Rectangle sTrack = GetStripTrackRect(PillWidth, stripTop);
            sTrack.Inflate(0, 12);

            if (sIcon.Contains(point)) ToggleMute();
            else if (sTrack.Contains(point)) SetVolumeFromPoint(point.X, sTrack);
            return true; // the strip owns its row, always
        }

        // Marks a control as just-pressed so DrawExpandedMedia can give it an
        // immediate visual flash, independent of how long the real action
        // (which may involve an OS round-trip) takes to actually complete.
        private void FlashControl(int controlIndex)
        {
            pressedControl = controlIndex;
            pressedAt = DateTime.Now;
            RefreshVisual();
        }

        private void Form1_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !CardShowing || trackDuration <= TimeSpan.Zero)
                return;

            // The invisible timeline of the Media page must not steal a drag
            // that starts over the open queue list.
            if (activeMode != IslandMode.Media)
                return;

            Rectangle hitArea = GetProgressHitArea();
            if (!hitArea.Contains(e.Location)) return;

            draggingProgress = true;
            suppressClickToggle = true;
            Capture = true;
            PreviewSeek(e.X);
        }

        private void Form1_MouseUp(object sender, MouseEventArgs e)
        {
            if (!draggingProgress) return;

            draggingProgress = false;
            Capture = false;
            PreviewSeek(e.X);
            _ = mediaWatcher.SeekAsync(basePosition);
        }

        private void Form1_MouseMove(object sender, MouseEventArgs e)
        {
            UpdateHoverState();
            if (draggingProgress)
                PreviewSeek(e.X);
        }

        private Rectangle GetProgressHitArea()
        {
            Rectangle track = GetProgressTrackRect();
            return new Rectangle(track.X, track.Y - 12, track.Width, 28);
        }

        private Rectangle GetProgressTrackRect()
        {
            const int edgePadding = 18;
            const int timeWidth = 40;
            const int timeGap = 9;
            int left = edgePadding + timeWidth + timeGap;
            int width = Math.Max(40, PillWidth - left * 2);
            return new Rectangle(left, 108, width, 4);
        }

        private void PreviewSeek(int x)
        {
            Rectangle area = GetProgressHitArea();
            float fraction = Math.Clamp((x - area.Left) / (float)area.Width, 0f, 1f);
            basePosition = TimeSpan.FromSeconds(trackDuration.TotalSeconds * fraction);
            basePositionStamp = DateTime.Now;
            RefreshVisual();
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            animationTimer.Stop();
            volumePollTimer.Stop();
            audioPollTimer.Stop();
            batteryTimer.Stop();
            queuePollTimer.Stop();
            spotifyQueue.StatusChanged -= OnSpotifyQueueStatusChanged;
            NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
            notificationWatcher.NotificationReceived -= NotificationReceived;
            notificationWatcher.Dispose();
            if (brightnessNotificationHandle != IntPtr.Zero)
            {
                UnregisterPowerSettingNotification(brightnessNotificationHandle);
                brightnessNotificationHandle = IntPtr.Zero;
            }
            trayIcon.Visible = false;
            trayIcon.Dispose();
            islandMenu?.Dispose();
            mediaWatcher.Stop();
            spotifyQueue.Dispose();
            lyrics.Clear();   // drops this track's lines; the shared HttpClient stays
            toastAppIcon?.Dispose();

            foreach (var item in queueItems)
                item.Art?.Dispose();
            albumArtBox.Image?.Dispose();
            appIconBadge.Image?.Dispose();
            callerAvatar?.Dispose();
            replyAvatar?.Dispose();
            fullTitleFont.Dispose();
            fullArtistFont.Dispose();
            fullTimeFont.Dispose();
            fullOutputFont.Dispose();

            frameBuffer?.Dispose();
            frameBuffer = null;

            if (device != null)
            {
                try { device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeChanged; }
                catch { }
            }

            try
            {
                loopbackCapture?.StopRecording();
                loopbackCapture?.Dispose();
            }
            catch { }
        }

        private void SetupMuteListener()
        {
            RefreshDefaultAudioDevice(false);
        }

        private void RefreshDefaultAudioDevice(bool announce)
        {
            string previousName = audioOutputName;
            try
            {
                if (device != null)
                {
                    try { device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeChanged; } catch { }
                    device.Dispose();
                    device = null;
                }

                using var enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                device.AudioEndpointVolume.OnVolumeNotification += OnVolumeChanged;
                RefreshAudioOutputName();
                try { lastDefaultRenderId = device.ID; } catch { }

                // The first ~1.5 s after an endpoint swap, volume deltas are
                // the SWITCH talking, not the user - re-learn the new device's
                // baseline silently instead of popping over the device card.
                volumeSuppressUntil = DateTime.Now.AddMilliseconds(1500);
                lastPolledVolume = -1f;

                if (announce && !string.IsNullOrWhiteSpace(audioOutputName) &&
                    !string.Equals(previousName, audioOutputName, StringComparison.OrdinalIgnoreCase))
                {
                    string lowered = audioOutputName.ToLowerInvariant();
                    bool headphones = lowered.Contains("headphone") || lowered.Contains("earbud") ||
                        lowered.Contains("airpod") || lowered.Contains("buds") ||
                        lowered.Contains("galaxy buds") || lowered.Contains("pixelbud") ||
                        lowered.Contains("sony") || lowered.Contains("bose") ||
                        lowered.Contains("jbl") || lowered.Contains("sennheiser");
                    if (headphones)
                    {
                        // AirPods-style: the DEVICE'S name is the headline,
                        // "Connected"/"Disconnected" is the kicker, and the
                        // earbud glyph spins in behind it.
                        ShowSystemActivity(audioOutputName, "Connected", TransientKind.DeviceAudio, 3400);
                    }
                    else
                    {
                        ShowSystemActivity("Audio Connected", audioOutputName,
                            TransientKind.Device, 3200);
                    }
                }
            }
            catch
            {
                device = null;
                audioOutputName = "";
            }
        }

        private const int WM_DEVICECHANGE = 0x0219;
        private const int WM_POWERBROADCAST = 0x0218;
        private const int PBT_POWERSETTINGCHANGE = 0x8013;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        private const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0;
        private static readonly Guid VideoBrightnessSetting =
            new Guid("8FFEE2C6-2D01-46BE-ADB9-398ADDC5B4FF");

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PowerBroadcastSetting
        {
            public Guid PowerSetting;
            public uint DataLength;
            public byte Data;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterPowerSettingNotification(
            IntPtr recipient, ref Guid powerSettingGuid, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

        private void RegisterBrightnessNotifications()
        {
            if (brightnessNotificationHandle != IntPtr.Zero) return;
            try
            {
                Guid setting = VideoBrightnessSetting;
                brightnessNotificationHandle = RegisterPowerSettingNotification(
                    Handle, ref setting, DEVICE_NOTIFY_WINDOW_HANDLE);
            }
            catch
            {
                brightnessNotificationHandle = IntPtr.Zero;
            }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (appSettings.BrightnessChanges && m.Msg == WM_POWERBROADCAST &&
                m.WParam.ToInt32() == PBT_POWERSETTINGCHANGE &&
                m.LParam != IntPtr.Zero && IsHandleCreated && !IsDisposed)
            {
                try
                {
                    var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(m.LParam);
                    if (setting.PowerSetting == VideoBrightnessSetting && setting.DataLength >= 1)
                    {
                        // Percentage in, sun-slider HUD out - the same
                        // icon+bar control as volume, no text anywhere.
                        float lvl = Math.Clamp(setting.Data / 100f, 0f, 1f);
                        brightnessTargetLevel = lvl;
                        if (brightnessShownLevel < 0f) brightnessShownLevel = lvl; // snap the first time
                        ShowSystemActivity("Brightness", "", TransientKind.Brightness, 1900);
                    }
                }
                catch
                {
                }
                return;
            }

            if (!appSettings.DeviceChanges || m.Msg != WM_DEVICECHANGE || !IsHandleCreated || IsDisposed) return;

            int change = m.WParam.ToInt32();
            if (change != DBT_DEVICEARRIVAL && change != DBT_DEVICEREMOVECOMPLETE) return;
            if ((DateTime.Now - lastDeviceChangeAt).TotalMilliseconds < 900) return;
            lastDeviceChangeAt = DateTime.Now;

            BeginInvoke(new Action(async () =>
            {
                await Task.Delay(650);
                string before = audioOutputName;
                RefreshDefaultAudioDevice(change == DBT_DEVICEARRIVAL);
                if (change == DBT_DEVICEREMOVECOMPLETE && string.Equals(before, audioOutputName, StringComparison.OrdinalIgnoreCase))
                    ShowSystemActivity("Device Disconnected", "A Windows device was removed", TransientKind.Device, 2600);
                else if (change == DBT_DEVICEARRIVAL && string.Equals(before, audioOutputName, StringComparison.OrdinalIgnoreCase))
                    ShowSystemActivity("Device Connected", "Windows detected a new device", TransientKind.Device, 2600);
            }));
        }

        private void OnVolumeChanged(AudioVolumeNotificationData data)
        {
            // Fires reliably for mute toggles on practically every device.
            // Plain volume changes reach us through VolumePollTick instead.
            textLabel.Text = data.Muted ? "Muted" : "Unmuted";
            if (InvokeRequired) Invoke(new Action(ShowVolumeHud));
            else ShowVolumeHud();
        }

        // The volume HUD — shown for mute AND plain level changes. If the card
        // is already expanded, it grows in as a strip at the bottom; otherwise
        // the pill swells into the wider/taller volume tile. Both spring back
        // down automatically when the HUD times out.
        private void ShowVolumeHud()
        {
            // A tile already on screen means the user is USING it - refresh
            // that freely. The suppression window and any other system
            // activity mid-flight only gate NEW volume pops.
            bool alreadyShowingVolume = holding && transientKind == TransientKind.Volume;
            if (!alreadyShowingVolume)
            {
                if (DateTime.Now < volumeSuppressUntil) { lastPolledVolume = -1f; return; }
                if (holding) return; // another system activity owns the notch right now
            }

            transientKind = TransientKind.Volume;

            if (CardShowing)
            {
                // Whichever page is open, the strip grows under it — the card
                // never swaps out from under a volume change in progress.
                volumeStripUntil = DateTime.Now.AddMilliseconds(VolumeHudMs);
                RefreshVisual();
                return;
            }

            holding = true;
            holdUntil = DateTime.Now.AddMilliseconds(VolumeHudMs);
            collapseAt = null;
            RefreshVisual();
        }

        // 250 ms poller: the one thing OnVolumeChanged can't be trusted to
        // deliver is plain slider movement (device-dependent), so we read the
        // real master volume ourselves and announce any delta.
        private void VolumePollTick(object? sender, EventArgs e)
        {
            if (device == null) return;
            try
            {
                float level = device.AudioEndpointVolume.MasterVolumeLevelScalar;
                bool mute = device.AudioEndpointVolume.Mute;

                bool haveBaseline = lastPolledVolume >= 0f;
                bool levelMoved = haveBaseline && Math.Abs(level - lastPolledVolume) > 0.005f;
                bool muteMoved = haveBaseline && mute != lastPolledMute;

                lastPolledVolume = level;
                lastPolledMute = mute;

                if (levelMoved || muteMoved)
                    ShowVolumeHud();
            }
            catch
            {
                // Device swap in flight — RefreshDefaultAudioDevice re-hooks.
                lastPolledVolume = -1f;
            }
        }

        // The reliable "earbuds connected/disconnected" signal: whatever the
        // default render endpoint IS right now, compared to what we last saw.
        // Bluetooth dongles, USB docks, 3.5mm jack - all flip this.
        private void AudioDefaultPollTick(object? sender, EventArgs e)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                string id = endpoint.ID;

                if (lastDefaultRenderId == null) { lastDefaultRenderId = id; return; }
                if (string.Equals(lastDefaultRenderId, id, StringComparison.Ordinal)) return;

                lastDefaultRenderId = id;
                if (!appSettings.DeviceChanges) return;
                RefreshDefaultAudioDevice(true); // announces only if the NAME changed
            }
            catch
            {
                // Transient COM hiccup mid-unplug - the next tick settles it.
            }
        }

        // Wifi on/off the user actually PERFORMED, regardless of whether
        // internet "availability" (NLA) technically flipped.
        private void CheckNetworkTransition()
        {
            if (!appSettings.NetworkChanges) return;
            try
            {
                bool up = false;
                string detailName = "";
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    up = true;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    { detailName = "Wi-Fi"; break; }
                    if (detailName == "" && nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                        detailName = "Ethernet";
                }

                bool first = knownNetworkUp == null;
                bool changed = !first && knownNetworkUp!.Value != up;
                knownNetworkUp = up;
                if (!changed) return;

                ShowSystemActivity(
                    up ? (detailName == "" ? "Network Connected" : detailName + " Connected")
                       : "Network Offline",
                    up ? "Internet access is available" : "Check your connection",
                    TransientKind.Network, 2800);
            }
            catch { }
        }

        // --- Artwork tap → activate the playing app ---
        // Hit rect mirroring DrawCollapsedMediaRow's thumbnail in BOTH the
        // resting pill (icon centred in the full pill height) and the peek
        // (icon pinned inside the row at restingSize.Height) — the pill's
        // left-edge artwork, padded generously.
        private Rectangle GetCollapsedArtHitRect()
        {
            const int iconSize = 28;
            int rowH = Height > restingSize.Height ? restingSize.Height : Height;
            var rect = new Rectangle(14, (rowH - iconSize) / 2, iconSize, iconSize);
            rect.Inflate(8, 8);
            return rect;
        }

        // Best-effort launch/activation of whatever app owns the current
        // media session. Packaged sources (Spotify, Movies & TV, browsers
        // with modern AppUserModelIDs) resolve through AppInfo and start —
        // or simply come to the foreground if already running. Sources whose
        // AUMID doesn't resolve stay put (the same limitation the app-icon
        // badge already quietly lives with).
        private void OpenSourceApp()
        {
            string aumid = sourceAppName;
            if (string.IsNullOrWhiteSpace(aumid)) return;

            try
            {
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                {
                    // Throws for an AUMID the shell doesn't know — that's our
                    // validator. shell:AppsFolder addresses apps by their full
                    // AUMID, which is exactly what the media session reports.
                    var info = Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(aumid);
                    if (info != null)
                    {
                        Process.Start(new ProcessStartInfo(
                            "explorer.exe", $"shell:AppsFolder\\{aumid}")
                        { UseShellExecute = true });
                        return;
                    }
                }

                // Some desktop apps report their exe path as the AUMID.
                if (aumid.IndexOf('\\') >= 0 && File.Exists(aumid))
                    Process.Start(new ProcessStartInfo(aumid) { UseShellExecute = true });
            }
            catch
            {
                // Unresolvable identity — same silent-failure contract as the
                // icon badge; the tap just does nothing rather than erroring.
            }
        }

        // One tap on the artwork: with 2+ live players it hands Windows'
        // current-session pointer to the next one (Spotify <-> YouTube; the
        // island relayouts by itself via CurrentSessionChanged). With a
        // single player it activates/opens that app like it always did.
        private void TapArtwork()
        {
            if (mediaWatcher.ActiveSessionCount >= 2)
            {
                _ = mediaWatcher.SwitchToNextSessionAsync();
                return;
            }
            OpenSourceApp();
        }

        // Control-slot glyph for "open the app that's playing": a mini window
        // frame with an arrow lifting out of it — the universal launch mark,
        // in the same 2px-stroke hand as shuffle/next.
        private static void DrawLaunchAppIcon(Graphics g, Brush brush, int centerX, int centerY)
        {
            using var pen = new Pen(brush, 1.9F)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            g.DrawRectangle(pen, centerX - 11, centerY - 8, 14, 13);
            g.DrawLine(pen, centerX - 2, centerY + 2, centerX + 10, centerY - 10);
            g.DrawLine(pen, centerX + 10, centerY - 10, centerX + 3, centerY - 10);
            g.DrawLine(pen, centerX + 10, centerY - 10, centerX + 10, centerY - 3);
        }

        // Resting pill for the Search page: quiet lens glyph at the art's
        // usual spot + gray prompt text; clicking the pill expands the card
        // straight into the question card (the same page, bigger).
        private void DrawSearchRest(Graphics g)
        {
            const int iconSize = 24;
            var iconRect = new Rectangle(14, (Math.Min(Height, restingSize.Height) - iconSize) / 2,
                iconSize, iconSize);
            DrawMiniLens(g, Color.White, iconRect);

            DrawTextAt(g, "Ask anything...", 9.4F, FontStyle.Regular,
                Color.FromArgb(168, 168, 176),
                new Rectangle(iconRect.Right + 10, 0,
                    Math.Max(20, PillWidth - iconRect.Right - 20), Height),
                false);
        }

        private static void DrawMiniLens(Graphics g, Color color, Rectangle area)
        {
            using var pen = new Pen(color, 1.9F)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            float d = Math.Min(area.Width, area.Height) * 0.62f;
            float glassX = area.X + area.Width * 0.12f;
            float glassY = area.Y + area.Height * 0.12f;
            g.DrawEllipse(pen, glassX, glassY, d, d);
            float hx = glassX + d * 0.82f;
            float hy = glassY + d * 0.82f;
            g.DrawLine(pen, hx, hy, hx + d * 0.42f, hy + d * 0.42f);
        }

        private void ToggleMute()
        {
            if (device == null) return;
            try
            {
                device.AudioEndpointVolume.Mute = !device.AudioEndpointVolume.Mute;
            }
            catch
            {
                // The active output device may disappear while switching devices.
            }
        }

        // --- Volume control geometry contracts -------------------------------
        // Exactly mirrors DrawSliderHud so drawing and clicking can't drift.
        private Rectangle GetHudIconRect()
        {
            var r = new Rectangle(14, (Height - 24) / 2, 24, 24);
            r.Inflate(8, 8);
            return r;
        }

        private Rectangle GetHudTrackCoreRect()
        {
            int trackX = 14 + 24 + 12;
            return new Rectangle(trackX, (Height - 7) / 2,
                Math.Max(30, PillWidth - trackX - 16), 7);
        }

        private Rectangle GetHudTrackRect()
        {
            var r = GetHudTrackCoreRect();
            r.Inflate(0, 12);
            return r;
        }

        private static Rectangle GetStripIconRect(int panelTop)
            => new Rectangle(20, panelTop + (VolumeStripHeight - 18) / 2, 18, 18);

        private static Rectangle GetStripTrackRect(int w, int panelTop)
            => new Rectangle(50, panelTop + (VolumeStripHeight - 8) / 2,
                Math.Max(40, w - 50 - 58), 8);

        private void SetVolumeFromPoint(int x, Rectangle track)
        {
            if (device == null) return;
            try
            {
                float f = Math.Clamp((x - track.X) / (float)Math.Max(1, track.Width), 0f, 1f);
                device.AudioEndpointVolume.MasterVolumeLevelScalar = f;

                // Snap the tween to the tapped spot immediately - the 250 ms
                // poll + OS confirmation will agree with it a moment later.
                volumeShownLevel = f;
                volumeStripUntil = DateTime.Now.AddMilliseconds(VolumeHudMs);
                if (!clicked)
                {
                    holding = true;
                    holdUntil = DateTime.Now.AddMilliseconds(VolumeHudMs);
                }
                RefreshVisual();
            }
            catch
            {
                // Volume is being set by another app mid-tap - drop it.
            }
        }

        private void SetupLoopbackCapture()
        {
            try
            {
                loopbackCapture = new WasapiLoopbackCapture();
                loopbackCapture.DataAvailable += LoopbackCapture_DataAvailable;
                voice.SourceLive = true;
                loopbackCapture.RecordingStopped += (_, __) =>
                {
                    // Optional cleanup...
                };
                loopbackCapture.StartRecording();
            }
            catch
            {
                loopbackCapture = null;
                voice.SourceLive = false;
            }
        }

        private void LoopbackCapture_DataAvailable(object? sender, WaveInEventArgs e)
        {
            try
            {
                WaveFormat format = loopbackCapture?.WaveFormat ?? new WaveFormat(48000, 32, 2);
                int bands = waveformLevels.Length;
                Span<float> bandPeaks = stackalloc float[bands];

                // Slice this chunk of audio across the bars in time order, so
                // each bar reflects a different moment within the ~10-20ms of
                // audio this callback delivers. It's not a real spectrum
                // analyzer, but it makes the bars move independently with the
                // actual track rather than all moving together off one number.
                double env = 0; int envN = 0;
                if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
                {
                    int totalSamples = e.BytesRecorded / 4;
                    int samplesPerBand = Math.Max(1, totalSamples / bands);
                    for (int i = 0; i + 3 < e.BytesRecorded; i += 8) // every other sample
                    {
                        int band = Math.Min(bands - 1, (i / 4) / samplesPerBand);
                        float sample = Math.Abs(BitConverter.ToSingle(e.Buffer, i));
                        if (sample > bandPeaks[band]) bandPeaks[band] = sample;
                        env += sample; envN++;
                    }
                }
                else if (format.BitsPerSample == 16)
                {
                    int totalSamples = e.BytesRecorded / 2;
                    int samplesPerBand = Math.Max(1, totalSamples / bands);
                    for (int i = 0; i + 1 < e.BytesRecorded; i += 4) // every other sample
                    {
                        int band = Math.Min(bands - 1, (i / 2) / samplesPerBand);
                        float sample = Math.Abs(BitConverter.ToInt16(e.Buffer, i) / 32768f);
                        if (sample > bandPeaks[band]) bandPeaks[band] = sample;
                        env += sample; envN++;
                    }
                }
                if (envN > 0) voice.Push(WordAligner.NowMs, env / envN);   // loudness for word sync

                lock (audioMeterLock)
                {
                    for (int b = 0; b < bands; b++)
                    {
                        float peak = Math.Clamp(bandPeaks[b], 0f, 1f);
                        if (peak > loopbackBandPeaks[b])
                            loopbackBandPeaks[b] = peak;
                    }
                }
            }
            catch { }
        }

        private void RefreshAudioOutputName()
        {
            try
            {
                if (device == null)
                {
                    audioOutputName = "";
                    return;
                }
                audioOutputName = device.FriendlyName ?? "";
            }
            catch
            {
                audioOutputName = "";
            }
        }

        private void OnMediaInfoChanged(MediaInfo info)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => ApplyMediaInfo(info)));
            }
            else
            {
                ApplyMediaInfo(info);
            }
        }

        private async void ApplyMediaInfo(MediaInfo info)
        {
            mediaText = string.IsNullOrWhiteSpace(info.Title) ? "No media playing" : info.Title;
            mediaArtist = info.Artist ?? "";

            // New track, new words (and the previous song's line is cleared
            // immediately so it cannot flash under the one that replaced it).
            lyricShownIndex = -1;
            lyricPreviousIndex = -1;
            lyricMarqueeOffset = 0f;
            lyricPreviousMarqueeOffset = 0f;
            lyricTransitionStartedAt = DateTime.MinValue;

            // Duration is deliberately NOT sent. At this instant trackDuration
            // still holds the *previous* song's length (the timeline event lands
            // a moment later), and LRCLIB treats duration as a matching key — a
            // wrong length doesn't widen a search, it steers it onto the wrong
            // recording. Artist + title plus the search fallback is the honest
            // query; a mismatched lyric is worse than a missing one.
            // Lyric display removed by choice — nothing on the card ever
            // reads the service, so the per-track lookup is cancelled too
            // (no silent network traffic for an invisible feature).
            lyrics.Clear();
            sourceAppName = info.Aumid ?? "";
            peekMarqueeOffset = 0f;
            fullMarqueeOffset = 0f;

            // Auto-announce (Tier 2 "Expanded Pill") whenever a genuinely
            // new track starts — not on the very first load (that would just
            // flash on app startup) and not while the user already has the
            // Card open via hover/click.
            string key = mediaText + "|" + mediaArtist;
            bool hasRealTrack = !string.Equals(mediaText, "No media playing", StringComparison.OrdinalIgnoreCase);
            if (hasRealTrack && key != lastAnnouncedTrackKey)
            {
                if (everGotTrack && !CardShowing)
                    announceUntil = DateTime.Now.AddMilliseconds(AnnounceMs);
                lastAnnouncedTrackKey = key;
                everGotTrack = true;
            }

            if (albumArtBox.Image != null)
            {
                var previous = albumArtBox.Image;
                albumArtBox.Image = null;
                previous.Dispose();
            }

            albumArtBox.Image = info.Thumbnail;
            albumArtBox.SizeMode = PictureBoxSizeMode.Zoom;

            albumAccentColor = info.Thumbnail != null
                ? ComputeAverageColor(info.Thumbnail)
                : Color.FromArgb(104, 182, 211);

            if (appIconBadge.Image != null)
            {
                appIconBadge.Image.Dispose();
                appIconBadge.Image = null;
            }

            if (!string.IsNullOrWhiteSpace(sourceAppName))
                appIconBadge.Image = await MediaWatcher.GetAppIconAsync(sourceAppName);

            RefreshVisual();
        }

        private void OnTimelineChanged(TimeSpan position, TimeSpan duration, bool playing)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => SetTimeline(position, duration, playing)));
            }
            else
            {
                SetTimeline(position, duration, playing);
            }
        }

        private void SetTimeline(TimeSpan position, TimeSpan duration, bool playing)
        {
            trackDuration = duration;
            isPlaying = playing;

            if (draggingProgress) return;

            basePosition = position;
            basePositionStamp = DateTime.Now;
        }

        private void AnimationTick(object? sender, EventArgs e)
        {
            UpdateHoverState();

            // Re-pin topmost roughly once a second (60 x 16ms ticks). SetWindowPos
            // with HWND_TOPMOST + NOACTIVATE/NOMOVE/NOSIZE reasserts the z-order
            // slot without ever stealing focus or moving the window - the repair
            // for fullscreen apps' enter/exit quietly demoting a layered window.
            // Exclusive-fullscreen games are a hardware scanout layer above the
            // desktop itself and are the one thing topmost cannot cross.
            if (++topmostReassertTick >= 60)
            {
                topmostReassertTick = 0;
                if (IsHandleCreated && !IsDisposed)
                    SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            }

            if (holding && DateTime.Now >= holdUntil)
            {
                holding = false;
                textLabel.Text = "";
            }

            if (!hovering && collapseAt.HasValue && DateTime.Now >= collapseAt.Value)
            {
                clicked = false;
                collapseAt = null;
            }

            // Volume HUD fill tween — chases the real master level (0 when
            // muted) so the bar visibly grows/moves with each change and the
            // strip drains back when it resets, instead of snapping.
            if (VolumeHudActive)
            {
                float goal = 0f;
                try
                {
                    if (device != null && device.AudioEndpointVolume.Mute != true)
                        goal = device.AudioEndpointVolume.MasterVolumeLevelScalar;
                }
                catch { }
                volumeShownLevel += (goal - volumeShownLevel) * 0.22f;
            }

            if (BrightnessHudActive && brightnessTargetLevel >= 0f)
                brightnessShownLevel += (brightnessTargetLevel - brightnessShownLevel) * 0.22f;

            bool hasTrack = !string.IsNullOrWhiteSpace(mediaText) &&
                !string.Equals(mediaText, "No media playing", StringComparison.OrdinalIgnoreCase);

            if (CardShowing && hasTrack)
            {
                fullMarqueeOffset += 0.45F;
                if (lyrics.HasLyrics)
                    lyricMarqueeOffset += LyricMarqueeSpeed;
            }
            else if (!CardShowing)
            {
                fullMarqueeOffset = 0f;
                lyricMarqueeOffset = 0f;
            }

            if (PeekShowing && hasTrack)
                peekMarqueeOffset += 0.4F;
            else if (!PeekShowing)
                peekMarqueeOffset = 0f;

            UpdateSizeAnimation();

            UpdateAudioWaveform();

            // The timeline sweeps in from empty the moment the plain
            // collapsed pill (with its own timeline) reappears after being
            // hidden by Peek/Card/an overlay — not just left sitting at
            // whatever fraction is "correct" the instant it's visible again.
            bool showingCollapsedTimeline = activeMode == IslandMode.Media
                && !clicked && !holding &&
                overlay == OverlayKind.None && !(hovering || ShowAnnouncement) && hasTrack;
            if (showingCollapsedTimeline && !progressBarWasVisible)
                timelineRevealAt = DateTime.Now;
            progressBarWasVisible = showingCollapsedTimeline;
        }

        // Retained for the welcome ink's small visual pop.
        private static float EaseOutBack(float t)
        {
            const float c1 = 0.55f;
            const float c3 = c1 + 1f;
            float tm1 = t - 1f;
            return 1f + c3 * tm1 * tm1 * tm1 + c1 * tm1 * tm1;
        }

        private void UpdateHoverState()
        {
            // The whole window is the pill now — hover anywhere on it peeks.
            islandRectangle = new Rectangle(0, 0, PillWidth, Height);

            Point cursor = Cursor.Position;
            Point localCursor = PointToClient(cursor);
            bool inside = islandRectangle.Contains(localCursor);

            bool wasHovering = hovering;

            if (inside)
            {
                hovering = true;
                collapseAt = null;
            }
            else
            {
                if (hovering)
                {
                    if (collapseAt == null)
                        collapseAt = DateTime.Now.AddSeconds(CollapseDelaySeconds);
                }
                hovering = false;
            }

            // After the branch, not inside it: a row can only be under the
            // pointer while the card is open, and this must not break the
            // if/else pair above.
            hoveredQueueRow = QueueRowUnderPointer(localCursor);

            if (collapseAt.HasValue && DateTime.Now >= collapseAt.Value)
            {
                if (Size != GetTotalSize(restingSize))
                {
                    clicked = false;
                }
                collapseAt = null;
            }

            if (wasHovering != hovering)
                RefreshVisual();
        }

        // Row index for a Y inside the panel, offset included. Shared by the
        // highlight, the click and the painter, so a row can never light up under
        // the pointer and then start a different track.
        private int QueueRowIndexAt(int y, int panelTop)
        {
            float at = y - (panelTop + QueueHeaderHeight) + queueScrollPx;
            return at < 0f ? -1 : (int)(at / QueueRowHeight);
        }

        // Which queue row the pointer is over, as an index into the full list —
        // so the highlight follows the item and not the slot it happens to be
        // painted into after a wheel scroll. Returns -1 for anywhere else on
        // the island, including the header strip above a closed list.
        private int QueueRowUnderPointer(Point local)
        {
            if (!queueOpen || queueItems.Count == 0) return -1;

            int top = QueuePanelTopY() + QueueHeaderHeight;
            if (local.Y < top || local.Y >= QueueListBottom()) return -1;

            int index = QueueRowIndexAt(local.Y, QueuePanelTopY());
            return index < queueItems.Count ? index : -1;
        }

        // Windows delivers the wheel to the window under the cursor (that is the
        // default "scroll inactive windows" behaviour), so this works without the
        // island ever taking focus — which it must not, being a topmost overlay.
        // While the card is open the wheel is the page turner. Exception that
        // proves the rule: with the queue extension open and the pointer
        // inside the list window, the wheel belongs to the list until it
        // bottoms out, and only the gesture after that flips the page.
        // Anywhere else over the card, one notch turns one page — with a
        // short beat of recovery, so a hard flick can't stream past them all.
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);

            Point p = PointToClient(Cursor.Position);

            if (queueOpen && queueItems.Count > 0 &&
                p.Y >= QueuePanelTopY() + QueueHeaderHeight && p.Y < QueueListBottom())
            {
                float notch = e.Delta < 0 ? QueueScrollNotch : -QueueScrollNotch;
                float moved = Math.Clamp(queueScrollPx + notch, 0f, QueueScrollMaxPx);
                if (Math.Abs(moved - queueScrollPx) >= 0.01f)
                {
                    queueScrollPx = moved;
                    RefreshVisual();
                    return;
                }
            }

            if (!CardShowing || holding || overlay != OverlayKind.None) return;

            int pages = PageCount();
            if (pages < 2) return;

            // A single notch turns a page in either direction; the beat only
            // reined in fast flicks (several events land in moments).
            DateTime now = DateTime.Now;
            int dir = e.Delta < 0 ? 1 : -1;

            int target = Math.Clamp(pageIndex + dir, 0, PageCount() - 1);
            if (target == pageIndex)
            {
                pageTurnAccum = 0f;
                return;
            }
            if ((now - pageLastTurnAt).TotalMilliseconds < PageTurnHoldMs)
            {
                pageTurnAccum = 0f;
                return;
            }

            pageTurnAccum += Math.Abs(e.Delta);
            if (pageTurnAccum < PageTurnTicks) return;

            pageTurnAccum = 0f;
            pageLastTurnAt = now;
            SetPage(target);
        }

        private void SuspendDrawing()
        {
            SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        }

        private void ResumeDrawing()
        {
            SendMessage(Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
            RefreshVisual();
        }

        private const int WM_SETREDRAW = 0x000B;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(
            IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

        // --- Layered window (per-pixel alpha) ---
        // Replaces Region-based hard-edge clipping. A Region only lets a
        // pixel be fully opaque or fully invisible, which is why the
        // progress-outline glow could never actually appear OUTSIDE the
        // pill's silhouette — anything past the Region boundary was simply
        // clipped away regardless of what was drawn there. UpdateLayeredWindow
        // instead composites a full 32bpp ARGB bitmap with true per-pixel
        // alpha, so a soft glow can genuinely fade out past the pill's edge
        // onto the desktop behind it.
        private const int WS_EX_LAYERED = 0x80000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int GWL_EXSTYLE = -20;
        private const int ULW_ALPHA = 0x2;
        private const byte AC_SRC_OVER = 0x0;
        private const byte AC_SRC_ALPHA = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; }
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(
            IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        private bool layeredEnabled = false;

        // A full-window 32bpp bitmap is megabytes, not kilobytes per frame at
        // 60 fps. One is allocated and reused for as long as the size lives,
        // and the composite itself is capped at ~30 fps while the LOGIC
        // (tweens, marquee, hover) keeps ticking at 60 — motion at 30 still
        // reads as smooth; churn at 60 is what was making the machine crawl.
        // Anything genuinely mid-move (size morph, page fade, welcome ink, a
        // scrub drag, the auto-hide glide) pushes every tick, so transitions
        // keep their full-rate silk.
        private Bitmap frameBuffer;
        private DateTime lastRenderAt = DateTime.MinValue;

        private void EnableLayeredStyle()
        {
            int exStyle = GetWindowLong(Handle, GWL_EXSTYLE);
            SetWindowLong(Handle, GWL_EXSTYLE, exStyle | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
            layeredEnabled = true;
        }

        // Renders the whole frame into an off-screen 32bpp ARGB bitmap
        // (via the same drawing methods OnPaint used to call directly) and
        // hands it to UpdateLayeredWindow. This is the layered-window
        // equivalent of what OnPaint + the Region clip used to do together.
        private void RenderLayered()
        {
            if (!layeredEnabled || Width <= 0 || Height <= 0) return;

            // Frame-rate floor for the composite. Logic still ticks at 60;
            // only the expensive UpdateLayeredWindow push is paced, and even
            // that pacing steps aside for anything visibly in motion so no
            // transition ever drops a frame.
            DateTime now = DateTime.Now;
            bool midMotion = sizeAnimationRunning || GetModeSwitchProgress() < 1f ||
                ShowWelcome || draggingProgress || autoHideOffset != appliedHideOffset ||
                volumeStripClosing;
            if (!midMotion && (now - lastRenderAt).TotalMilliseconds < 31) return;
            lastRenderAt = now;

            if (frameBuffer == null || frameBuffer.Width != Width || frameBuffer.Height != Height)
            {
                frameBuffer?.Dispose();
                frameBuffer = new Bitmap(Math.Max(1, Width), Math.Max(1, Height),
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            }
            using (var g = Graphics.FromImage(frameBuffer))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Transparent);

                PaintFrame(g);
            }

            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = frameBuffer.GetHbitmap(Color.FromArgb(0, 0, 0, 0));
            IntPtr oldBitmap = SelectObject(memDc, hBitmap);

            try
            {
                var size = new SIZE { cx = Width, cy = Height };
                var pointSource = new POINT { x = 0, y = 0 };
                var topPos = new POINT { x = Left, y = Top };
                var blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA
                };

                UpdateLayeredWindow(Handle, screenDc, ref topPos, ref size, memDc, ref pointSource, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                SelectObject(memDc, oldBitmap);
                DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // Control.RefreshVisual() is not virtual, so it cannot be overridden —
        // this is a same-signature replacement method instead. A layered
        // window never actually receives WM_PAINT for visible compositing,
        // so the base RefreshVisual() would silently do nothing once layering
        // is on. Every call site in this file was switched from
        // RefreshVisual() to RefreshVisual() to route through RenderLayered.
        private void RefreshVisual()
        {
            if (!syncingWindowSize)
            {
                syncingWindowSize = true;
                try
                {
            UpdateAutoHide();
            UpdateSizeAnimation();
                }
                finally
                {
                    syncingWindowSize = false;
                }
            }

            if (layeredEnabled) RenderLayered();
            else base.Invalidate();
        }

        // Layered windows don't receive normal WM_PAINT compositing, so
        // OnPaint is no longer where drawing happens (see RenderLayered,
        // which builds the ARGB frame and pushes it via UpdateLayeredWindow).
        // OnPaint is kept only as a safety no-op in case anything still
        // triggers it, and PaintFrame holds the actual drawing logic that
        // used to live directly in OnPaint.
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!layeredEnabled)
                PaintFrame(e.Graphics); // fallback if layering failed to enable
        }

        private void PaintFrame(Graphics g)
        {
            Rectangle bounds = new Rectangle(0, 0, Math.Max(1, PillWidth - 1), Math.Max(1, Height - 1));
            // An opened notification banner is the same class of object as the
            // card and the overlays; left out of this test it inherited the
            // pill's 15px radius and its flatter shoulder curve, so a 146px-tall
            // body rendered as a boxy slab with the bottom row clipped. The
            // compact strip stays on the pill radius, because it IS the pill.
            int cornerRadius = DownloadHudActive ? 21
                : clicked || overlay != OverlayKind.None || ShowWelcome ||
                  (NotificationShowing && notificationExpanded)
                    ? Math.Min(28, Height / 2)
                    : Math.Min(12, Height / 2);
            using (var pillPath = AttachedNotchPath(bounds, cornerRadius))
                FillLiquidGlass(g, pillPath, bounds);

            if (overlay == OverlayKind.Call)
            {
                DrawCallOverlay(g);
                return;
            }
            if (overlay == OverlayKind.Reply)
            {
                DrawReplyOverlay(g);
                return;
            }
            if (overlay == OverlayKind.Ask)
            {
                DrawAskOverlay(g);
                return;
            }

            // Boot greeting outranks everything except real user interaction
            // (the overlays and volume strip above). Deliberately ahead of
            // "holding": at launch Windows fires a brightness broadcast and
            // the island would otherwise flash the brightness chip before the
            // hello - which was exactly the report this ordering fixes.
            if (ShowWelcome)
            {
                DrawWelcome(g);
                return;
            }

            if (holding)
            {
                if (transientKind == TransientKind.Volume)
                    DrawVolumePopup(g);
                else if (transientKind == TransientKind.Brightness)
                    DrawSliderHud(g, false);
                else if (transientKind == TransientKind.Notification)
                    DrawToastCard(g);
                else if (transientKind == TransientKind.Download)
                    DrawDownloadCard(g);
                else
                    DrawSystemActivityPopup(g);
                return;
            }

            float modeFade = GetModeSwitchProgress();
            if (modeFade < 1f)
                DrawFadedPop(g, modeFade, DrawActiveModeContent);
            else
                DrawActiveModeContent(g);

            // Painted after (and above) the pages — the indicator is fixed
            // furniture of the open card, no matter what animates under it.
            DrawPageIndicator(g);
        }

        // Everything after the pill background / dot / mute-popup that
        // depends on which "Live Activity" (Media or Battery) is currently
        // showing. Split out so it can be rendered either directly or into
        // an offscreen buffer for the fade-in transition below.
        private void DrawActiveModeContent(Graphics g)
        {
            // Hard boundary: nothing drawn for any mode/state may leave the
            // pill, however a marquee offset or mid-animation width computes
            // out. This is enforced here once instead of trusting every draw
            // method to clip itself correctly.
            var pillClip = new Region(new Rectangle(0, 0, PillWidth, Height));
            Region? previousClip = g.Clip;
            pillClip.Intersect(previousClip);
            g.Clip = pillClip;

            try
            {
                DrawActiveModeContentInner(g);
            }
            finally
            {
                g.Clip = previousClip;
                pillClip.Dispose();
            }
        }

        private void DrawActiveModeContentInner(Graphics g)
        {
            if (activeMode == IslandMode.Today)
            {
                // Expanded: weather + agenda. Collapsed: the clock and the
                // battery readout — the page's two faces at the two sizes.
                if (clicked) DrawTodayPanel(g);
                else DrawCollapsedToday(g);
                return;
            }

            // Search is a page of the card (the question card itself), and a
            // resting pill that says so: the lens.
            if (activeMode == IslandMode.Search)
            {
                if (clicked) DrawAskOverlay(g);
                else DrawSearchRest(g);
                return;
            }

            // Media and Queue are two heights of one card: un-expanded, both
            // show the track — the list only exists below the card.
            bool cardCollapsing = sizeAnimationRunning && !clicked &&
                sizeAnimationStart.Width >= fullSize.Width && targetSize.Width <= peekSize.Width;
            if (clicked || cardCollapsing)
            {
                DrawExpandedMedia(g);
                return;
            }

            bool hasTrackNow = !string.IsNullOrWhiteSpace(mediaText) &&
                !string.Equals(mediaText, "No media playing", StringComparison.OrdinalIgnoreCase);

            bool peekCollapsing = sizeAnimationRunning && targetSize == restingSize &&
                sizeAnimationStart.Width <= peekSize.Width &&
                sizeAnimationStart.Height <= peekSize.Height;
            if ((hovering || ShowAnnouncement || peekCollapsing) && hasTrackNow)
            {
                DrawPeekMedia(g);
                return;
            }

            DrawCollapsedMedia(g);
        }

        // How far into the post-tap transition we are, 0 (just switched) to
        // 1 (settled). Driving both the fade-in and a tiny "liquid" pop.
        private float GetModeSwitchProgress()
        {
            double elapsed = (DateTime.Now - modeSwitchAt).TotalMilliseconds;
            if (elapsed >= ModeSwitchMs) return 1f;
            return (float)Math.Clamp(elapsed / ModeSwitchMs, 0.0, 1.0);
        }

        // Renders `draw` into an offscreen buffer and composites it back with
        // both a fade-in and a subtle scale "pop" (starts very slightly
        // shrunk, eases up to full size) — a cheap stand-in for a real
        // liquid-glass morph, without needing a full blob/shape interpolation
        // engine in GDI+.
        private void DrawFadedPop(Graphics target, float progress, Action<Graphics> draw)
        {
            int w = Math.Max(1, Width);
            int h = Math.Max(1, Height);

            using var buffer = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var bg = Graphics.FromImage(buffer))
            {
                bg.SmoothingMode = SmoothingMode.AntiAlias;
                bg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                bg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                bg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                bg.CompositingQuality = CompositingQuality.HighQuality;

                // Ease-out pop: starts at 96% scale, settles to 100%.
                float eased = 1f - (float)Math.Pow(1f - progress, 3);
                float scale = 0.96f + 0.04f * eased;
                bg.TranslateTransform(w / 2f, h / 2f);
                bg.ScaleTransform(scale, scale);
                bg.TranslateTransform(-w / 2f, -h / 2f);

                draw(bg);
            }

            float alpha = 1f - (float)Math.Pow(1f - progress, 2); // ease-out fade
            var matrix = new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha };
            using var attr = new System.Drawing.Imaging.ImageAttributes();
            attr.SetColorMatrix(matrix, System.Drawing.Imaging.ColorMatrixFlag.Default,
                System.Drawing.Imaging.ColorAdjustType.Bitmap);
            target.DrawImage(buffer, new Rectangle(0, 0, w, h), 0, 0, w, h, GraphicsUnit.Pixel, attr);
        }

        private void DrawCenteredText(Graphics g, string value, float size, FontStyle style)
        {
            var font = CachedFont("Segoe UI", size, style);
            var rect = new Rectangle(0, 0, PillWidth, Height);
            TextRenderer.DrawText(g, value, font, rect, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private static Color ComputeAverageColor(Bitmap bmp)
        {
            long r = 0, g = 0, b = 0;
            int count = 0;
            int stepX = Math.Max(1, bmp.Width / 12);
            int stepY = Math.Max(1, bmp.Height / 12);

            for (int y = 0; y < bmp.Height; y += stepY)
            {
                for (int x = 0; x < bmp.Width; x += stepX)
                {
                    var px = bmp.GetPixel(x, y);
                    if (px.A < 32) continue; // skip transparent padding
                    r += px.R; g += px.G; b += px.B;
                    count++;
                }
            }

            if (count == 0) return Color.FromArgb(104, 182, 211);

            int ar = (int)(r / count), ag = (int)(g / count), ab = (int)(b / count);

            // Boost saturation/brightness a touch — raw average color tends
            // to read muddy/gray, while the reference ring reads vivid.
            float max = Math.Max(ar, Math.Max(ag, ab));
            if (max > 0 && max < 200)
            {
                float boost = 200f / max;
                ar = Math.Clamp((int)(ar * boost), 0, 255);
                ag = Math.Clamp((int)(ag * boost), 0, 255);
                ab = Math.Clamp((int)(ab * boost), 0, 255);
            }

            return Color.FromArgb(255, ar, ag, ab);
        }

        // Timestamp of the moment the collapsed pill (with its timeline)
        // last became visible after being hidden by Peek/Card. Drives the
        // "sweeps in from the start" reveal below.
        private DateTime timelineRevealAt = DateTime.MinValue;
        private const double TimelineRevealMs = 550;

        private void DrawProgressOutline(Graphics g, float progress, Color color)
        {
            progress = Math.Clamp(progress, 0f, 1f);
            double elapsed = (DateTime.Now - timelineRevealAt).TotalMilliseconds;
            float revealT = (float)Math.Clamp(elapsed / TimelineRevealMs, 0.0, 1.0);
            float eased = 1f - (float)Math.Pow(1f - revealT, 3);
            float shown = progress * eased;

            float inset = 1.4f;
            float bottom = Height - inset;
            float top = inset;
            float left = inset;
            float right = PillWidth - inset - 1f;
            float radius = Math.Min(12f, (Height - inset * 2f) / 2f);
            const int arcSamples = 16;
            var pts = new List<PointF>(arcSamples * 2 + 5)
            {
                new PointF(left, top),
                new PointF(left, bottom - radius)
            };

            for (int i = 0; i <= arcSamples; i++)
            {
                double angle = Math.PI - Math.PI / 2 * (i / (double)arcSamples);
                pts.Add(new PointF(
                    left + radius + (float)Math.Cos(angle) * radius,
                    bottom - radius + (float)Math.Sin(angle) * radius));
            }

            pts.Add(new PointF(right - radius, bottom));
            for (int i = 1; i <= arcSamples; i++)
            {
                double angle = Math.PI / 2 - Math.PI / 2 * (i / (double)arcSamples);
                pts.Add(new PointF(
                    right - radius + (float)Math.Cos(angle) * radius,
                    bottom - radius + (float)Math.Sin(angle) * radius));
            }
            pts.Add(new PointF(right, top));

            if (shown <= 0f) return;

            var lengths = new float[pts.Count];
            float total = 0f;
            for (int i = 1; i < pts.Count; i++)
            {
                total += Distance(pts[i - 1], pts[i]);
                lengths[i] = total;
            }

            float targetLen = total * shown;
            var strip = new List<PointF> { pts[0] };
            for (int i = 1; i < pts.Count; i++)
            {
                if (lengths[i] >= targetLen)
                {
                    float segStart = lengths[i - 1];
                    float segLen = Math.Max(0.0001f, lengths[i] - segStart);
                    float t = Math.Clamp((targetLen - segStart) / segLen, 0f, 1f);
                    strip.Add(Lerp(pts[i - 1], pts[i], t));
                    break;
                }
                strip.Add(pts[i]);
            }

            if (strip.Count < 2) return;

            var strip2 = strip.ToArray();

            using (var progressPen = new Pen(color, 2.4f)
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            })
                g.DrawLines(progressPen, strip2);

            static float Distance(PointF a, PointF b) => (float)Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
            static PointF Lerp(PointF a, PointF b, float t) => new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }

        private void DrawCollapsedMediaRow(Graphics g, int rowHeight)
        {
            const int iconSize = 28;
            const int left = 14;
            var iconRect = new Rectangle(left, (rowHeight - iconSize) / 2, iconSize, iconSize);

            // same signature glow, scaled to the thumbnail, resting/peek too

            Image? art = albumArtBox.Image ?? appIconBadge.Image;
            if (art != null)
            {
                // A small radius here (not iconSize, which clamps to a full
                // circle) — the reference shows a rounded square thumbnail.
                using var clip = RoundedRegion(iconRect, (int)(iconSize * 0.32));
                Region? previousClip = g.Clip;
                g.Clip = clip;
                g.DrawImage(art, iconRect);
                g.Clip = previousClip;
            }
            else
            {
                using var dotBrush = new SolidBrush(albumAccentColor);
                g.FillEllipse(dotBrush, left + iconSize / 2 - 4, rowHeight / 2 - 4, 8, 8);
            }

            // Fully collapsed means exactly two things: artwork on the left
            // and the live visualizer on the right.
            var waveRect = new Rectangle(PillWidth - 52, 0, 34, rowHeight);
            DrawMiniWaveform(g, waveRect, 7);
        }

        // Sneak Peek keeps the resting width and grows downward. Its media row
        // is pinned to the compact height and the remaining space becomes a
        // marquee title row. No progress outline here — that's collapsed-only.
        private void DrawPeekMedia(Graphics g)
        {
            DrawCollapsedMediaRow(g, PeekTopRowHeight);

            string song = string.IsNullOrWhiteSpace(mediaText) ? "No media playing" : mediaText;
            const int sidePadding = 16;
            var textRow = new Rectangle(sidePadding, PeekTopRowHeight,
                Math.Max(1, PillWidth - sidePadding * 2), Height - PeekTopRowHeight);

            // Hard stop pinned to PillWidth itself (not just the margin-
            // derived textRow width) — this is what stops the peek marquee
            // from visually running into the dot sitting just past the pill.
            int peekHardStop = PillWidth - sidePadding;

            DrawScrollingText(g, song, 9.8F, FontStyle.Bold,
                Color.White, textRow, ref peekMarqueeOffset, peekHardStop);
        }

        private void DrawCollapsedMedia(Graphics g)
        {
            bool hasTrack = !string.IsNullOrWhiteSpace(mediaText) &&
                !string.Equals(mediaText, "No media playing", StringComparison.OrdinalIgnoreCase);

            if (!hasTrack)
            {
                DrawCollapsedMediaRow(g, Height);
                DrawDownloadChip(g);
                return;
            }

            DrawCollapsedMediaRow(g, Height);

            // Progress starts at the upper-left, travels down and around the
            // lower edge, then finishes at the upper-right.
            if (trackDuration > TimeSpan.Zero)
            {
                float progress = (float)Math.Clamp(
                    GetCurrentPosition().TotalSeconds / trackDuration.TotalSeconds, 0d, 1d);
                DrawProgressOutline(g, progress, albumAccentColor);
            }
            DrawDownloadChip(g);
        }

        // --- Page indicator ---
        // One dot per page down the card's right edge, stretched into a tick
        // for the current page. The successor to the beside-notch bubble —
        // but furniture of the card, not a control welded to its side: flat
        // glass under it, fixed above every page, and its full-height strip
        // stays reserved (row rules, scroll bars, text columns and the
        // right-hand control all stop short of it).
        private const int IndicatorDotSize = 5;
        private const int IndicatorPitch = 14;
        private const int IndicatorZone = 18;

        // Full pitch for every card this island grows (media, ask, today,
        // extended queue); short windows compress the column to 10px slots,
        // and it withdraws entirely only when even that would not fit —
        // the wheel always flips pages regardless.
        private int IndicatorPitchNow()
        {
            int pages = Math.Max(2, PageCount());
            if (Height >= (pages - 1) * IndicatorPitch + IndicatorDotSize + 10)
                return IndicatorPitch;
            return Math.Max(10, (Height - IndicatorDotSize - 6) / (pages - 1));
        }

        private bool IndicatorVisible =>
            CardShowing && PageCount() > 1 &&
            Height >= (PageCount() - 1) * IndicatorPitchNow() + IndicatorDotSize + 8;

        private int IndicatorTopY()
        {
            int span = (PageCount() - 1) * IndicatorPitchNow() + IndicatorDotSize;
            return Height / 2 - span / 2;
        }

        private Rectangle GetIndicatorDotRect(int index)
        {
            int pitch = IndicatorPitchNow();
            int centerY = IndicatorTopY() + index * pitch + IndicatorDotSize / 2;
            bool active = index == pageIndex && activeMode == PageAt(index);
            int h = active ? pitch : IndicatorDotSize;
            return new Rectangle(PillWidth - 13, centerY - h / 2, IndicatorDotSize, h);
        }

        // Full-height slot per dot (pitch tall, from the window edge inward),
        // so hitting a dot never depends on which page happens to be under it.
        private Rectangle GetIndicatorHitRect(int index)
        {
            if (!IndicatorVisible) return Rectangle.Empty;
            int pitch = IndicatorPitchNow();
            int centerY = IndicatorTopY() + index * pitch + IndicatorDotSize / 2;
            return new Rectangle(PillWidth - IndicatorZone, centerY - pitch / 2,
                IndicatorZone, pitch);
        }

        private void DrawPageIndicator(Graphics g)
        {
            if (!IndicatorVisible) return;

            for (int i = 0; i < PageCount(); i++)
            {
                bool active = i == pageIndex && activeMode == PageAt(i);
                using var brush = new SolidBrush(active
                    ? Color.FromArgb(230, 255, 255, 255)
                    : Color.FromArgb(75, 255, 255, 255));
                FillRoundedRectangle(g, brush, GetIndicatorDotRect(i), 2);
            }
        }

        // Battery-mode icon: a clean horizontal capsule, thin outline, no
        // terminal nub, accent fill from the left. One glyph now that the
        // ring variant existed only for the deleted bubble.
        private void DrawBatteryCapsuleIcon(Graphics g, Rectangle area, Color accent)
        {
            int w = Math.Max(20, Math.Min(area.Width, 30));
            int h = Math.Max(10, area.Height / 2);
            var body = new Rectangle(area.X + (area.Width - w) / 2,
                area.Y + (area.Height - h) / 2, w, h);

            using (var outline = new Pen(Color.FromArgb(200, 235, 235, 235), 1.6f))
            using (var outlinePath = RoundedRectangle(body, h / 2))
                g.DrawPath(outline, outlinePath);

            const int inset = 3;
            int innerW = Math.Max(1, body.Width - inset * 2);
            int innerH = Math.Max(2, body.Height - inset * 2);
            int fillW = (int)(innerW * Math.Clamp(batteryPercent, 0f, 1f));
            if (fillW > 1)
            {
                using var fill = new SolidBrush(accent);
                FillRoundedRectangle(g, fill,
                    new Rectangle(body.X + inset, body.Y + inset, fillW, innerH),
                    Math.Max(1, innerH / 2));
            }
        }

        private Rectangle GetWeatherRefreshRect()
        {
            int w = Math.Max(PillWidth, 300);
            int h = Math.Max(Height, 130);
            string city = string.IsNullOrWhiteSpace(weatherCity) ? "Set location" : weatherCity;
            using var f = new Font("Segoe UI", 8.5F, FontStyle.Bold);
            int cw = TextRenderer.MeasureText(city, f, new Size(420, 20),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            // 14px, right beside the city line near the column's foot.
            return new Rectangle(12 + cw, h - 42, 14, 14);
        }

        // The sky block, painted offscreen at 80% strength and clipped to the
        // notch silhouette — vertical day/night gradient, low sun bloom by
        // day, twinkling star scatter by night, all dissolving rightward
        // into the card's black long before the clock column.
        private void DrawSkyWash(Graphics g, int w, int h, int colWidth)
        {
            int skyW = Math.Min(Math.Max(2, w), (int)(colWidth * 2.1f));
            int skyH = Math.Max(2, h);
            using var bmp = new Bitmap(skyW, skyH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var bg = Graphics.FromImage(bmp))
            {
                bg.SmoothingMode = SmoothingMode.AntiAlias;
                bg.CompositingQuality = CompositingQuality.HighQuality;
                bg.Clear(Color.Transparent);

                SkyTones(out Color top, out Color horizon);
                var vRect = new Rectangle(0, 0, skyW, skyH);
                using (var v = new LinearGradientBrush(vRect, top, horizon, 90f, true))
                {
                    var cb = new System.Drawing.Drawing2D.ColorBlend
                    {
                        Colors = new[] { top, SkyMix(top, horizon, .4f), SkyMix(top, horizon, .72f), horizon },
                        Positions = new[] { 0f, .38f, .7f, 1f },
                    };
                    v.InterpolationColors = cb;
                    bg.FillRectangle(v, vRect);
                }

                if (weatherIsDay)
                {
                    var sunRect = new Rectangle(-54, -40, 190, 150);
                    using var sp = new GraphicsPath();
                    sp.AddEllipse(sunRect);
                    using var sun = new PathGradientBrush(sp)
                    {
                        CenterPoint = new PointF(62f, 34f),
                        CenterColor = Color.FromArgb(150, 255, 241, 205),
                        SurroundColors = new[] { Color.FromArgb(0, 255, 241, 205) },
                    };
                    bg.FillEllipse(sun, sunRect);
                }
                else
                {
                    double t = DateTime.Now.TimeOfDay.TotalSeconds;
                    for (int i = 0; i < 26; i++)
                    {
                        float fx = (i * .7548776f) % 1f;
                        float fy = (i * .618034f) % 1f;
                        float px = 6 + fx * (skyW * .86f);
                        float py = 4 + fy * skyH * .55f;
                        float tw = .5f + .5f * (float)Math.Sin(t * 1.15 + i * 2.3);
                        int a = (int)(50 + 150 * tw);
                        float r = i % 5 == 0 ? 1.5f : 1f;
                        using var sb = new SolidBrush(Color.FromArgb(a, 242, 246, 255));
                        bg.FillEllipse(sb, px, py, r * 2, r * 2);
                    }
                }

                // breathe out rightward: black creeps in as x grows, so the
                // sky dissolves past the weather block instead of ending at it
                int fadeX = (int)(colWidth * .5f);
                int fadeW = Math.Max(2, skyW - fadeX);
                var hRect = new Rectangle(fadeX, 0, fadeW, skyH);
                using (var hf = new LinearGradientBrush(hRect,
                    Color.FromArgb(0, 0, 0, 0), Color.FromArgb(255, 0, 0, 0), 0f, true))
                {
                    var cb = new System.Drawing.Drawing2D.ColorBlend
                    {
                        Colors = new[]
                        {
                            Color.FromArgb(0, 0, 0, 0), Color.FromArgb(70, 0, 0, 0),
                            Color.FromArgb(190, 0, 0, 0), Color.FromArgb(255, 0, 0, 0),
                        },
                        Positions = new[] { 0f, .4f, .78f, 1f },
                    };
                    hf.InterpolationColors = cb;
                    bg.FillRectangle(hf, hRect);
                }
            }

            var state = g.Save();
            var bounds = new Rectangle(0, 0, Math.Max(1, PillWidth - 1), Math.Max(1, Height - 1));
            using (var np = AttachedNotchPath(bounds, Math.Min(28, Height / 2)))
                g.SetClip(np, CombineMode.Intersect);
            using (var attrs = new System.Drawing.Imaging.ImageAttributes())
            {
                var m = new System.Drawing.Imaging.ColorMatrix { Matrix33 = .8f };
                attrs.SetColorMatrix(m, System.Drawing.Imaging.ColorMatrixFlag.Default,
                    System.Drawing.Imaging.ColorAdjustType.Bitmap);
                g.DrawImage(bmp, new Rectangle(0, 0, skyW, skyH),
                    0, 0, skyW, skyH, GraphicsUnit.Pixel, attrs);
            }
            g.Restore(state);
        }

        private static Color SkyMix(Color a, Color b, float t) => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        private void SkyTones(out Color top, out Color horizon)
        {
            (Color, Color) d = weatherIconKind switch
            {
                "sun"    => (Color.FromArgb(84, 140, 208), Color.FromArgb(178, 216, 246)),
                "partly" => (Color.FromArgb(96, 130, 174), Color.FromArgb(172, 192, 214)),
                "cloud"  => (Color.FromArgb(96, 106, 124), Color.FromArgb(150, 158, 172)),
                "fog"    => (Color.FromArgb(112, 118, 128), Color.FromArgb(156, 160, 168)),
                "rain"   => (Color.FromArgb(52, 72, 102), Color.FromArgb(106, 130, 156)),
                "snow"   => (Color.FromArgb(98, 122, 154), Color.FromArgb(178, 202, 226)),
                "storm"  => (Color.FromArgb(44, 40, 72), Color.FromArgb(92, 84, 122)),
                _        => (Color.FromArgb(84, 140, 208), Color.FromArgb(178, 216, 246)),
            };
            if (weatherIsDay) { top = d.Item1; horizon = d.Item2; return; }
            top = SkyDim(d.Item1); horizon = SkyDim(d.Item2);
        }

        private static Color SkyDim(Color c) => Color.FromArgb(
            Math.Min(255, c.R * 10 / 255 + 9),
            Math.Min(255, c.G * 11 / 255 + 11),
            Math.Min(255, c.B * 17 / 255 + 28));

        // Chrome's reload: an open ring whose endpoint wears a proper arrow
        // head pointing along the clockwise tangent, gap centred on top.
        private static void DrawRefreshGlyph(Graphics g, Rectangle area, Color color)
        {
            var r = Rectangle.Inflate(area, 0, 0);
            using var pen = new Pen(color, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            const float start = 288, sweep = 324;           // GDI+ y-down/cw angles
            g.DrawArc(pen, r, start, sweep);

            double e = (start + sweep) * Math.PI / 180.0;   // 252° — near top, gap's left lip
            float rx = r.Width / 2f - 0.8f, ry = r.Height / 2f - 0.8f;
            float ex = r.X + r.Width / 2f + (float)Math.Cos(e) * rx;
            float ey = r.Y + r.Height / 2f + (float)Math.Sin(e) * ry;
            float dx = -(float)Math.Sin(e), dy = (float)Math.Cos(e);   // clockwise tangent
            using var head = new SolidBrush(color);
            g.FillPolygon(head, new[]
            {
                new PointF(ex + dx * 5.2f, ey + dy * 5.2f),
                new PointF(ex - dy * 3.1f + dx * 0.6f, ey + dx * 3.1f + dy * 0.6f),
                new PointF(ex + dy * 3.1f + dx * 0.6f, ey - dx * 3.1f + dy * 0.6f),
            });
        }

        // Collapsed face of the Today page: one vertically centred row —
        // time left, capsule right, percentage between them on hover. The
        // percentage is drawn with exactly the flags it was measured with
        // (no padding mismatch, no EndEllipsis to bite), and every string's
        // true width is reported back to GetTodayWideSize — so the pill
        // self-sizes until the whole readout fits.
        private void DrawCollapsedToday(Graphics g)
        {
            var capRect = new Rectangle(PillWidth - 14 - 26, (Height - 16) / 2, 26, 16);
            const TextFormatFlags mFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

            string timeText = DateTime.Now.ToString("h:mm tt");
            var timeFont = CachedFont("Segoe UI", 10F, FontStyle.Bold);
            int timeW = TextRenderer.MeasureText(g, timeText, timeFont,
                new Size(900, Height), mFlags).Width;

            string pct = "";
            var pctFont = CachedFont("Segoe UI", 11F, FontStyle.Bold);
            int pctW = 0;
            bool wide = PillWidth > restingSize.Width + 6;
            if (todayBattery && wide)
            {
                pct = $"{(int)Math.Round(Math.Clamp(batteryPercent, 0f, 1f) * 100)}%";
                pctW = TextRenderer.MeasureText(g, pct, pctFont,
                    new Size(900, Height), mFlags).Width;
            }
            int pctBoxW = pctW > 0 ? pctW + 8 : 0;

            // report the exact requirement to the sizer (feedback loop)
            todayNeededPx = 16 + timeW + 10 + (pctBoxW > 0 ? pctBoxW + 10 : 0) + 26 + 14;

            var pctRect = pctW == 0
                ? Rectangle.Empty
                : new Rectangle(capRect.X - 14 - pctBoxW, 0, pctBoxW, Height);
            int textRight = pctRect.IsEmpty ? capRect.X - 14 : pctRect.X - 10;
            var row = new Rectangle(16, 0, Math.Max(1, textRight - 16), Height);

            DrawTextAt(g, timeText, 10F, FontStyle.Bold, Color.White, row, false);

            if (todayBattery)
            {
                Color accent = batteryCharging ? Color.FromArgb(100, 220, 130)
                    : (batteryPercent <= 0.2f ? Color.FromArgb(230, 90, 90) : Color.White);
                if (!pctRect.IsEmpty)
                    TextRenderer.DrawText(g, pct, pctFont, pctRect, accent,
                        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
                DrawBatteryCapsuleIcon(g, capRect, accent);
            }
        }

        // Three-column "Today" card — weather / big clock+date / agenda —
        // matching the reference video's expanded notch widget, separated by
        // thin vertical hairlines the way the original does.
        private void DrawTodayPanel(Graphics g)
        {
            int w = Math.Max(PillWidth, 300);
            int h = Math.Max(Height, 130);

            int colWidth = w / 3;

            // A real sky, not a tint: a vertical gradient in the weather's
            // own tones, a low sun bloom by day or a scattered field of
            // slowly twinkling stars by night, then breathing out into the
            // card's black far before any hard edge. Composited offscreen at
            // soft strength and clipped to the notch silhouette — that clip
            // is what makes it curve around the bottom-left rounded corner
            // instead of squaring off over it, and compositing is what kills
            // the banding the direct gradient showed. No reading yet, no sky.
            if (weatherTemp.Length > 0)
                DrawSkyWash(g, w, h, colWidth);

            // The agenda column gives up the indicator strip on its right so
            // a long event name can never underline the page dots.
            int rightGuard = IndicatorVisible ? IndicatorZone : 0;
            var weatherCol = new Rectangle(0, 0, colWidth, h);
            var clockCol = new Rectangle(colWidth, 0, colWidth, h);
            var agendaCol = new Rectangle(colWidth * 2, 0,
                Math.Max(1, w - colWidth * 2 - rightGuard), h);

            // No column rules: the sky's own edge, the centred clock and the
            // agenda header separate the three areas well enough now — and
            // the whole panel reads as ONE surface. (The queue extension's
            // top line and the strip borders went for the same reason.)

            // --- Weather column ---
            // Icon and temperature sit side-by-side (not stacked) so the
            // icon has its own space and never gets covered by the temp text.
            var weatherIconRect = new Rectangle(weatherCol.X + 10, 13, 36, 36);
            DrawWeatherIcon(g, weatherIconRect, weatherIconKind, weatherIsDay);

            string temp = string.IsNullOrWhiteSpace(weatherTemp) ? "--°" : weatherTemp;
            var tempRect = new Rectangle(
                weatherIconRect.Right + 8, 16,
                Math.Max(1, weatherCol.Width - (weatherIconRect.Right + 8) - 6), 30);
            DrawTextAt(g, temp, 14F, FontStyle.Bold, Color.White, tempRect, false);

            var refresh = GetWeatherRefreshRect();
            string cityLine = string.IsNullOrWhiteSpace(weatherCity)
                ? "Unknown place — set a city in Settings" : weatherCity;
            int cityMax = Math.Max(30, refresh.X - weatherCol.X - 14);
            for (float fs = 8.5F; ; fs -= 0.4F)
            {
                var fnt = CachedFont("Segoe UI", fs, FontStyle.Bold);
                int cwpx = TextRenderer.MeasureText(g, cityLine, fnt,
                    new Size(int.MaxValue, 16),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
                bool lastTry = fs <= 6.9F;
                if (cwpx <= cityMax || lastTry)
                {
                    TextRenderer.DrawText(g, cityLine, fnt,
                        new Rectangle(weatherCol.X + 6, h - 40, cityMax, 16),
                        Color.FromArgb(205, 222, 226, 234),
                        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
                        | TextFormatFlags.VerticalCenter | (lastTry ? TextFormatFlags.EndEllipsis : (TextFormatFlags)0));
                    break;
                }
            }

            // Refresh: 14px circle-arrow beside the city name; flashes on press.
            float rp = GetPressFlash(5);
            if (rp > 0.01f)
            {
                using var hb = new SolidBrush(Color.FromArgb((int)(70 * rp), 255, 255, 255));
                g.FillEllipse(hb, refresh.X - 3, refresh.Y - 3, refresh.Width + 6, refresh.Height + 6);
            }
            DrawRefreshGlyph(g, refresh,
                rp > 0.01f ? Color.White : Color.FromArgb(170, 205, 210, 222));
            DrawTextAt(g, string.IsNullOrWhiteSpace(weatherCondition) ? "" : weatherCondition,
                8F, FontStyle.Regular, Color.FromArgb(150, 150, 158),
                new Rectangle(weatherCol.X + 6, h - 24, weatherCol.Width - 12, 16), false);

            // --- Clock column ---
            DrawTextAt(g, DateTime.Now.ToString("HH:mm"), 22F, FontStyle.Bold, Color.White,
                new Rectangle(clockCol.X, 30, clockCol.Width, 40), true);
            DrawTextAt(g, DateTime.Now.ToString("dddd, d MMM"), 9F, FontStyle.Regular,
                Color.FromArgb(190, 190, 198),
                new Rectangle(clockCol.X, 76, clockCol.Width, 18), true);

            // --- Agenda column ---
            var headerFont = CachedFont("Segoe UI", 11F, FontStyle.Bold);
            TextRenderer.DrawText(g, "Today", headerFont,
                new Rectangle(agendaCol.X + 4, 18, agendaCol.Width - 8, 22), Color.White,
                TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            if (agendaItems.Count == 0)
            {
                // "No events today" is the honest empty; but when the calendar
                // store itself said no, the panel says that instead rather
                // than silently pretending an empty day.
                DrawTextAt(g, agendaNote.Length > 0 ? agendaNote : "No events today",
                    9F, FontStyle.Regular, Color.FromArgb(140, 140, 148),
                    new Rectangle(agendaCol.X + 4, 46, agendaCol.Width - 8, 20), false);
                return;
            }

            int rowY = 46;
            int shown = Math.Min(agendaItems.Count, 3);
            for (int i = 0; i < shown; i++)
            {
                var item = agendaItems[i];
                using (var dotBrush = new SolidBrush(item.Dot))
                    g.FillEllipse(dotBrush, agendaCol.X + 4, rowY + 4, 7, 7);

                DrawTextAt(g, item.Title, 9.5F, FontStyle.Bold, Color.White,
                    new Rectangle(agendaCol.X + 16, rowY - 2, agendaCol.Width - 20, 16), false);
                DrawTextAt(g, item.TimeRange, 8F, FontStyle.Regular, Color.FromArgb(150, 150, 158),
                    new Rectangle(agendaCol.X + 16, rowY + 13, agendaCol.Width - 20, 14), false);

                rowY += 40;
            }
        }

        // Weather badges with actual weather colouring: gold sun with rays
        // (crescent moon at night), layered grey clouds, blue rain, white snow,
        // yellow lightning — all still pure vectors, but reading as little
        // badges rather than line icons.
        private static void DrawWeatherIcon(Graphics g, Rectangle area, string kind, bool day)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float d = Math.Min(area.Width, area.Height);
            float cx = area.X + d / 2f, cy = area.Y + d / 2f;

            void Sun(float scale, float ox, float oy)
            {
                float r = d * 0.19f * scale;
                var sc = new PointF(cx + ox, cy + oy);
                using (var rays = new Pen(Color.FromArgb(255, 236, 168, 92), Math.Max(1.6f, d * 0.055f))
                { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4;
                        g.DrawLine(rays, sc.X + (float)Math.Cos(a) * r * 1.55f, sc.Y + (float)Math.Sin(a) * r * 1.55f,
                            sc.X + (float)Math.Cos(a) * r * 2.15f, sc.Y + (float)Math.Sin(a) * r * 2.15f);
                    }
                using var core = new SolidBrush(Color.FromArgb(255, 246, 196, 84));
                g.FillEllipse(core, sc.X - r, sc.Y - r, r * 2, r * 2);
            }

            void Moon(float ox, float oy)
            {
                float r = d * 0.27f;
                var mc = new PointF(cx + ox, cy + oy);
                using (var moon = new SolidBrush(Color.FromArgb(232, 238, 248)))
                    g.FillEllipse(moon, mc.X - r, mc.Y - r, r * 2, r * 2);
                // crescent: a bite offset toward the light source
                using (var bite = new SolidBrush(Color.FromArgb(0, 0, 0)))
                    g.FillEllipse(bite, mc.X - r * 0.35f - r * 0.92f + r * 0.3f,
                        mc.Y - r * 1.02f, r * 1.7f, r * 1.7f);
                using (var star = new SolidBrush(Color.FromArgb(210, 255, 255, 255)))
                    g.FillEllipse(star, mc.X + r * 0.75f, mc.Y - r * 0.9f, 2.4f, 2.4f);
            }

            void Cloud(float ox, float oy, float scale, Color fill)
            {
                // Real cloud iconography: bumps whose bottoms ALIGN on one
                // flat base line, a taller crown in the middle, soft sides —
                // the old version floated bubbles of near-equal size, which
                // reads as a cluster, never as a cloud.
                float sc = d * 0.70f * scale;
                float x0 = cx + ox - sc / 2f;
                float baseY = cy + oy + sc * 0.20f;
                using var path = new GraphicsPath();
                path.AddEllipse(x0, baseY - sc * 0.40f, sc * 0.42f, sc * 0.42f);                 // left bump
                path.AddEllipse(x0 + sc * 0.21f, baseY - sc * 0.68f, sc * 0.60f, sc * 0.60f);    // crown
                path.AddEllipse(x0 + sc * 0.56f, baseY - sc * 0.38f, sc * 0.44f, sc * 0.44f);   // right bump
                path.AddRectangle(new RectangleF(x0 + sc * 0.04f, baseY - sc * 0.22f, sc * 0.92f, sc * 0.22f));
                using var brush = new SolidBrush(fill);
                g.FillPath(brush, path);
            }


            switch (kind)
            {
                case "sun":
                    if (day) Sun(1f, 0, 0); else Moon(0, 0);
                    break;
                case "partly":
                    if (day) Sun(0.72f, -d * 0.16f, -d * 0.16f); else Moon(0, -d * 0.14f);
                    Cloud(0, day ? d * 0.16f : d * 0.18f, 0.9f, Color.FromArgb(228, 232, 238));
                    break;
                case "cloud":
                    Cloud(0, -d * 0.06f, 0.72f, Color.FromArgb(150, 158, 172));
                    Cloud(0, d * 0.12f, 0.95f, Color.FromArgb(226, 230, 238));
                    break;
                case "fog":
                    Cloud(0, -d * 0.12f, 0.85f, Color.FromArgb(200, 206, 216));
                    using (var bars = new Pen(Color.FromArgb(200, 236, 240, 246), Math.Max(2f, d * 0.055f))
                    { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        for (int i = 0; i < 3; i++)
                            g.DrawLine(bars, cx - d * 0.34f, cy + d * 0.12f + i * d * 0.11f,
                                cx + d * 0.34f, cy + d * 0.12f + i * d * 0.11f);
                    break;
                case "rain":
                    Cloud(0, -d * 0.14f, 0.9f, Color.FromArgb(150, 160, 178));
                    using (var drops = new Pen(Color.FromArgb(96, 176, 240), Math.Max(2f, d * 0.06f))
                    { StartCap = LineCap.Round })
                        for (int i = -1; i <= 1; i++)
                            g.DrawLine(drops, cx + i * d * 0.22f, cy + d * 0.20f,
                                cx + i * d * 0.22f - d * 0.04f, cy + d * 0.38f);
                    break;
                case "snow":
                    Cloud(0, -d * 0.14f, 0.9f, Color.FromArgb(190, 204, 220));
                    using (var flakes = new Pen(Color.FromArgb(245, 250, 255), 1.6f)
                    { StartCap = LineCap.Round })
                        for (int i = -1; i <= 1; i++)
                        {
                            float fx = cx + i * d * 0.22f, fy = cy + d * 0.28f;
                            g.DrawLine(flakes, fx - 2.6f, fy, fx + 2.6f, fy);
                            g.DrawLine(flakes, fx, fy - 2.6f, fx, fy + 2.6f);
                        }
                    break;
                case "storm":
                    Cloud(0, -d * 0.14f, 0.9f, Color.FromArgb(120, 128, 146));
                    using (var boltB = new SolidBrush(Color.FromArgb(255, 214, 90)))
                        g.FillPolygon(boltB, new[]
                        {
                            new PointF(cx + d * 0.05f, cy + d * 0.12f),
                            new PointF(cx - d * 0.12f, cy + d * 0.34f),
                            new PointF(cx, cy + d * 0.30f),
                            new PointF(cx - d * 0.06f, cy + d * 0.50f),
                            new PointF(cx + d * 0.16f, cy + d * 0.22f),
                            new PointF(cx + d * 0.03f, cy + d * 0.26f),
                        });
                    break;
                default:
                    if (day) Sun(1f, 0, 0); else Moon(0, 0);
                    break;
            }
        }


        // --- Incoming call overlay (FaceTime-style) ---
        private void DrawCallOverlay(Graphics g)
        {
            int w = Math.Max(PillWidth, 200);
            int h = Math.Max(Height, callSize.Height);
            var (avatarRect, declineRect, acceptRect) = GetCallOverlayRects(w, h);

            const int artRadius = 23;
            if (callerAvatar != null)
            {
                using var clip = RoundedRegion(avatarRect, artRadius);
                Region? previousClip = g.Clip;
                g.Clip = clip;
                g.DrawImage(callerAvatar, avatarRect);
                g.Clip = previousClip;
            }
            else
            {
                using var artBrush = new SolidBrush(Color.FromArgb(48, 48, 54));
                g.FillEllipse(artBrush, avatarRect);
                DrawTextAt(g, "☎", 16F, FontStyle.Bold, Color.White, avatarRect, true);
            }

            var textRect = new Rectangle(avatarRect.Right + 14, 0, declineRect.X - avatarRect.Right - 22, h);
            DrawTextAt(g, "FaceTime", 9F, FontStyle.Regular, Color.FromArgb(170, 170, 178),
                new Rectangle(textRect.X, h / 2 - 18, textRect.Width, 16), false);
            DrawTextAt(g, string.IsNullOrWhiteSpace(callerName) ? "Unknown Caller" : callerName,
                12F, FontStyle.Bold, Color.White,
                new Rectangle(textRect.X, h / 2 - 2, textRect.Width, 20), false);

            using (var declineBrush = new SolidBrush(Color.FromArgb(240, 70, 70)))
                g.FillEllipse(declineBrush, declineRect);
            using (var acceptBrush = new SolidBrush(Color.FromArgb(70, 200, 90)))
                g.FillEllipse(acceptBrush, acceptRect);

            using (var iconPen = new Pen(Color.White, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                // Decline: phone-hangup glyph (simple diagonal receiver).
                var dc = new Point(declineRect.X + declineRect.Width / 2, declineRect.Y + declineRect.Height / 2);
                g.DrawLine(iconPen, dc.X - 8, dc.Y + 5, dc.X + 8, dc.Y - 5);
                g.DrawLine(iconPen, dc.X - 8, dc.Y - 5, dc.X - 3, dc.Y - 2);
                g.DrawLine(iconPen, dc.X + 8, dc.Y + 5, dc.X + 3, dc.Y + 2);

                // Accept: phone-answer glyph.
                var ac = new Point(acceptRect.X + acceptRect.Width / 2, acceptRect.Y + acceptRect.Height / 2);
                g.DrawLine(iconPen, ac.X - 8, ac.Y - 6, ac.X + 8, ac.Y + 6);
                g.DrawLine(iconPen, ac.X - 8, ac.Y - 6, ac.X - 3, ac.Y - 5);
                g.DrawLine(iconPen, ac.X - 8, ac.Y - 6, ac.X - 7, ac.Y - 1);
            }
        }

        // --- Message reply overlay ---
        private void DrawReplyOverlay(Graphics g)
        {
            int w = Math.Max(PillWidth, 200);
            int h = Math.Max(Height, replySize.Height);

            const int avatarSize = 34;
            var avatarRect = new Rectangle(16, 14, avatarSize, avatarSize);

            if (replyAvatar != null)
            {
                using var clip = RoundedRegion(avatarRect, avatarSize / 2);
                Region? previousClip = g.Clip;
                g.Clip = clip;
                g.DrawImage(replyAvatar, avatarRect);
                g.Clip = previousClip;
            }
            else
            {
                using var artBrush = new SolidBrush(Color.FromArgb(48, 48, 54));
                g.FillEllipse(artBrush, avatarRect);
            }

            var headerRect = new Rectangle(avatarRect.Right + 12, 12, w - avatarRect.Right - 28, 40);
            DrawTextAt(g, string.IsNullOrWhiteSpace(replySender) ? "Message" : replySender,
                9.5F, FontStyle.Bold, Color.White,
                new Rectangle(headerRect.X, headerRect.Y, headerRect.Width, 16), false);
            DrawTextAt(g, replyPreview, 8.5F, FontStyle.Regular, Color.FromArgb(190, 190, 198),
                new Rectangle(headerRect.X, headerRect.Y + 17, headerRect.Width, 32), false);

            // Text field track behind the real TextBox control.
            var sendRect = GetReplySendRect(w, h);
            var fieldRect = new Rectangle(16, h - 16 - 34, sendRect.X - 16 - 10 - 16, 34);
            using (var fieldBrush = new SolidBrush(Color.FromArgb(40, 40, 44)))
                FillRoundedRectangle(g, fieldBrush, fieldRect, 12);

            // Position the live TextBox to sit exactly inside that track.
            var screenField = new Rectangle(fieldRect.X + 10, fieldRect.Y + 6, fieldRect.Width - 20, fieldRect.Height - 12);
            if (replyBox.Bounds != screenField) replyBox.Bounds = screenField;

            using (var sendBrush = new SolidBrush(Color.FromArgb(104, 182, 211)))
                g.FillEllipse(sendBrush, sendRect);
            using (var sendPen = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                int cx = sendRect.X + sendRect.Width / 2, cy = sendRect.Y + sendRect.Height / 2;
                g.DrawLine(sendPen, cx - 5, cy + 4, cx, cy - 5);
                g.DrawLine(sendPen, cx, cy - 5, cx + 5, cy + 4);
                g.DrawLine(sendPen, cx, cy - 5, cx, cy + 5);
            }
        }

        private void DrawExpandedMedia(Graphics g)
        {
            // Previously this waited for Width/Height to land within 6px of the
            // exact fullSize target before drawing anything at all. If the size
            // animation got interrupted (e.g. hover state flickering during the
            // resize) it could sit just short of that threshold and never draw

            // the expanded content — which is why info wasn't fully appearing.
            // Instead, scale everything to whatever the current Width/Height
            // actually are, clamped to sane minimums, so content is always
            // visible and simply grows into place along with the pill.
            int w = Math.Max(PillWidth, 200);
            int h = Math.Max(Height, 110);

            const int artSize = 72;
            int left = 20;
            int top = 24;
            var artRect = new Rectangle(left, top, artSize, artSize);
            int artRadius = (int)(artSize * 0.28); // rounder than a plain square, iOS-tile feel

            // the aura sits behind everything — no text it can sit on top of

            if (albumArtBox.Image != null)
            {
                using var clip = RoundedRegion(artRect, artRadius);
                Region? previousClip = g.Clip;
                g.Clip = clip;
                g.DrawImage(albumArtBox.Image, artRect);
                g.Clip = previousClip;
            }
            else
            {
                using var artBrush = new SolidBrush(Color.FromArgb(48, 48, 54));
                using var artPath = RoundedRectangle(artRect, artRadius);
                g.FillPath(artBrush, artPath);
                DrawTextAt(g, "♪", 28F, FontStyle.Bold, Color.White, artRect, true);
            }

            // Reserve room on the right of the title row for the small waveform
            // glyph (matches the reference: it sits beside the title, top-right
            // corner, not floating in the middle of the card).
            const int titleLeftGap = 14;
            const int titleRightGap = 18;
            const int waveIconWidth = 32;

            int titleLeft = left + artSize + titleLeftGap;
            // Hard stop: title text may NEVER cross this X, regardless of
            // marquee offset or mid-animation width. Passed straight into
            // DrawScrollingText as an explicit clip edge instead of being
            // re-derived from margins inside that method.
            int titleHardStop = w - waveIconWidth - titleRightGap;
            int titleWidth = Math.Max(1, titleHardStop - titleLeft);

            Rectangle titleArea = new Rectangle(
                titleLeft,
                top,
                titleWidth,
                26
            );

            Rectangle artistArea = new Rectangle(
                titleLeft,
                top + 28,
                titleWidth,
                22
            );
            // Hard-exclude the art's bounds from the clip region before
            // drawing any of the info-column text. This guarantees the title
            // and artist can never paint over the artwork, independent of
            // whatever the computed text-area coordinates come out to.
            using (var textClip = new Region(new Rectangle(0, 0, w, h)))
            {
                using var artExclude = RoundedRegion(Rectangle.Inflate(artRect, 2, 2), artRadius + 2);
                textClip.Exclude(artExclude);
                Region? previousClip = g.Clip;
                textClip.Intersect(previousClip);
                g.Clip = textClip;

                DrawScrollingText(g,
                    string.IsNullOrWhiteSpace(mediaText) ? "No media playing" : mediaText,
                    13F, FontStyle.Bold, Color.White, titleArea, ref fullMarqueeOffset, titleHardStop);

                DrawTextAt(g, string.IsNullOrWhiteSpace(mediaArtist) ? "Windows media" : mediaArtist,
                    10F, FontStyle.Regular, Color.FromArgb(150, 150, 158), artistArea, false);

                // One line of synced lyrics, held against the real playback
                // position. Blank until the lookup answers, and blank permanently
                // for a track with no synced record — no placeholder text, since
                // "these lyrics don't exist" is not a problem to report.
                //
                // It sits inside the same clip that hard-excludes the artwork and
                // stops at the title's own hard stop, so a long line can cross
                // neither the cover nor the waveform glyph.
                g.Clip = previousClip;
            }

            var waveRect = new Rectangle(w - waveIconWidth - 16, top, waveIconWidth, 22);
            DrawMiniWaveform(g, waveRect, 4);

            Rectangle timelineTrack = GetProgressTrackRect();
            TimeSpan currentPosition = GetCurrentPosition();
            float progress = trackDuration > TimeSpan.Zero
                ? (float)Math.Clamp(currentPosition.TotalSeconds /
                    trackDuration.TotalSeconds, 0d, 1d)
                : 0f;

            using (var trackBrush = new SolidBrush(Color.FromArgb(70, 70, 76)))
                FillRoundedRectangle(g, trackBrush, timelineTrack, 2);

            if (progress > 0f)
            {
                using var progressBrush = new SolidBrush(Color.White);
                FillRoundedRectangle(g, progressBrush,
                    new Rectangle(timelineTrack.X, timelineTrack.Y,
                        Math.Max(3, (int)(timelineTrack.Width * progress)), timelineTrack.Height), 2);
            }

            TimeSpan remaining = trackDuration > currentPosition
                ? trackDuration - currentPosition
                : TimeSpan.Zero;
            var playedRect = new Rectangle(16, timelineTrack.Y - 8,
                Math.Max(1, timelineTrack.X - 24), 20);
            var remainingRect = new Rectangle(timelineTrack.Right + 8, timelineTrack.Y - 8,
                Math.Max(1, w - timelineTrack.Right - 24), 20);
            // list #2: the clock digits now wear the cover's own accent —
            // same family as the progress fill, no more dead-gray pair
            DrawTextAt(g, FormatTime(currentPosition), 7.5F, FontStyle.Bold,
                albumAccentColor, playedRect, false, true);
            DrawTextAt(g, trackDuration > TimeSpan.Zero ? $"-{FormatTime(remaining)}" : "--:--",
                7.5F, FontStyle.Bold, albumAccentColor, remainingRect, false);

            using var controlBrush = new SolidBrush(Color.White);
            using var mutedBrush = new SolidBrush(Color.FromArgb(125, 125, 132));

            // Controls row is anchored to the base card height (not the
            // possibly-taller total height while the volume strip is grown
            // in), so it animates into place during the initial expand and
            // then stays put while the strip appears beneath it.
            int controlsBaseH = Math.Min(h, fullSize.Height);
            int controlsY = controlsBaseH - 25;
            int center = w / 2;
            // Clamp how far prev/next sit from center so they can never collide
            // with the queue/mute icons (pinned near the edges) or each other,
            // even while w is still short of fullSize.Width mid-animation.
            int spread = Math.Clamp((w - 170) / 2, 26, 72);

            DrawControlFlash(g, 0, 42, controlsY);
            DrawQueueIcon(g, queueOpen ? controlBrush : mutedBrush, 42, controlsY);

            DrawControlFlash(g, 1, center - spread, controlsY);
            DrawPreviousIcon(g, controlBrush, center - spread, controlsY);

            int playFlashAlpha = Math.Min(255, 232 + (int)(GetPressFlash(2) * 23));
            using (var playBg = new SolidBrush(Color.FromArgb(playFlashAlpha, 255, 255, 255)))
                g.FillEllipse(playBg, new Rectangle(center - 18, controlsY - 18, 36, 36));

            using (var playGlyphBrush = new SolidBrush(Color.Black))
                DrawPlayPauseIcon(g, playGlyphBrush, center, controlsY, isPlaying);

            DrawControlFlash(g, 3, center + spread, controlsY);
            DrawNextIcon(g, controlBrush, center + spread, controlsY);

            DrawControlFlash(g, 4, w - 42, controlsY);
            if (mediaWatcher.ActiveSessionCount >= 2)
            {
                // "Open the playing app" - shown as that app's OWN icon when
                // Windows could resolve it, as the launch glyph otherwise.
                Image? playerIcon = appIconBadge.Image;
                if (playerIcon != null)
                {
                    var iconRectCtrl = new Rectangle(w - 42 - 12, controlsY - 12, 24, 24);
                    Region? oldClip = g.Clip;
                    using var clip = RoundedRegion(iconRectCtrl, 7);
                    var combined = (Region)clip.Clone();
                    combined.Intersect(oldClip);
                    g.Clip = combined;
                    g.DrawImage(playerIcon, iconRectCtrl);
                    combined.Dispose();
                    g.Clip = oldClip;
                }
                else
                {
                    DrawLaunchAppIcon(g, controlBrush, w - 42, controlsY);
                }
            }
            else
            {
                DrawShuffleIcon(g, shuffleOn ? controlBrush : mutedBrush, w - 42, controlsY);
            }

            bool volumeStripVisibleNow = ShowVolumeStrip ||
                (volumeStripClosing && sizeAnimationRunning);

            // "Playing Next" extends below the controls row rather than
            // replacing it, so neither ever hides the music info above — the
            // bottom extension it always had. It keeps painting through the
            // frames of its own collapse so the shrink looks like a retract.
            bool queuePanelVisible = queueOpen || (queuePanelClosing && sizeAnimationRunning);
            int panelTop = fullSize.Height;
            if (queuePanelVisible && h > panelTop + 4)
            {
                DrawQueuePanel(g, w, panelTop);
                panelTop = queueOpen
                    ? QueueCardHeight()
                    : Math.Max(panelTop, h - (volumeStripVisibleNow ? VolumeStripHeight : 0));
            }

            // The volume strip grows in below the foot of content: the base
            // card normally, the extended list when the queue is open.
            if (volumeStripVisibleNow && h > panelTop + 4)
                DrawVolumeStrip(g, w, panelTop);
        }

        // Volume feedback while the card is already expanded: a thin strip
        // that grows in below whatever page is showing, so the card's real
        // content never disappears — the page itself just gets a foot.
        private const int VolumeStripHeight = 40;

        private void DrawSystemActivityPopup(Graphics g)
        {
            if (transientKind == TransientKind.DeviceAudio)
            {
                DrawEarbudConnectCard(g);
                return;
            }

            if (!NotificationShowing)
            {
                const int iconSize = 30;
                var iconRect = new Rectangle(16, (Height - iconSize) / 2, iconSize, iconSize);
                DrawTransientIcon(g, iconRect, transientKind);

                int legacyLeft = iconRect.Right + 12;
                int legacyWidth = Math.Max(1, PillWidth - legacyLeft - 18);
                DrawTextAt(g, transientTitle, 9.6F, FontStyle.Bold, Color.White,
                    new Rectangle(legacyLeft, 11, legacyWidth, 21), false);
                DrawTextAt(g, transientDetail, 8.4F, FontStyle.Regular, Color.FromArgb(176, 176, 184),
                    new Rectangle(legacyLeft, 31, legacyWidth, 23), false);
                return;
            }

            // Header strip: identical in both tiers, so opening the banner looks
            // like the strip making room for the body rather than being replaced.
            DrawNotificationStrip(g, NotifCompactHeight);

            if (!notificationExpanded) return;

            int bodyTop = NotifCompactHeight;

            using (var divider = new Pen(Color.FromArgb(22, 255, 255, 255), 1f))
                g.DrawLine(divider, 16, bodyTop + 1, PillWidth - 16, bodyTop + 1);

            // App tile: the watcher hands over a display name and joined text,
            // never an icon, so the initial in a rounded tint is the honest
            // stand-in rather than a generic bell pretending to be branding.
            const int tileSize = 26;
            var tile = new Rectangle(16, bodyTop + 12, tileSize, tileSize);
            using (var tileBrush = new SolidBrush(TintForApp(transientTitle)))
                using (var tilePath = RoundedRectangle(tile, 7))
                    g.FillPath(tileBrush, tilePath);
            DrawTextAt(g, FirstLetter(transientTitle), 11.5F, FontStyle.Bold, Color.White, tile, true);

            int headLeft = tile.Right + 10;
            int headWidth = Math.Max(20, PillWidth - headLeft - 52);
            DrawTextAt(g, transientTitle, 9.4F, FontStyle.Bold, Color.White,
                new Rectangle(headLeft, bodyTop + 14, headWidth, 16), false);
            DrawTextAt(g, NotificationWhen, 8F, FontStyle.Regular, Color.FromArgb(150, 150, 158),
                new Rectangle(PillWidth - 46, bodyTop + 16, 30, 14), false, rightAlign: true);

            // The message at full width: three lines at this size, and where it
            // runs out the flags ellipsise on a whole word rather than drawing a
            // half line. Still the only place most of the text fits, which is the
            // whole reason the click exists: the strip shows a clipped beginning.
            var body = new Rectangle(16, bodyTop + 44,
                Math.Max(30, PillWidth - 32), NotifBannerBodyHeight - 52);
            var bodyFont = CachedFont("Segoe UI", 9.2F, FontStyle.Regular);
            TextRenderer.DrawText(g, transientDetail, bodyFont, body,
                Color.FromArgb(226, 226, 232),
                TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.WordBreak
                | TextFormatFlags.PathEllipsis | TextFormatFlags.PreserveGraphicsClipping);
        }

        // ═══ iOS-style toast ═══
        // ask-card sized, a real app icon as the opener, bold sender, wrapped
        // message, a hushed "now/2m" at the right — restrained glass, and
        // the icon falls back to a tinted letter tile when an app sends none
        private void DrawToastCard(Graphics g)
        {
            int w = PillWidth, h = Height;

            var iconBox = new Rectangle(14, (h - 44) / 2, 44, 44);
            if (toastAppIcon != null)
            {
                using (var clip = RoundedRegion(iconBox, 12))
                {
                    Region? prev = g.Clip;
                    g.Clip = clip;
                    g.DrawImage(toastAppIcon, iconBox);
                    g.Clip = prev;
                }
            }
            else
            {
                using (var tile = new SolidBrush(TintForApp(transientTitle)))
                using (var path = RoundedRectangle(iconBox, 12))
                    g.FillPath(tile, path);
                DrawTextAt(g, FirstLetter(transientTitle), 15F, FontStyle.Bold, Color.White, iconBox, true);
            }

            int textX = iconBox.Right + 12;
            int textW = Math.Max(20, w - textX - 58);
            DrawTextAt(g, transientTitle, 10.5F, FontStyle.Bold, Color.White,
                new Rectangle(textX, 16, textW, 18), false);
            DrawTextAt(g, NotificationWhen, 8F, FontStyle.Regular, Color.FromArgb(150, 150, 158),
                new Rectangle(w - 56, 18, 44, 14), false, rightAlign: true);

            var body = new Rectangle(textX, 36, textW, h - 46);
            var bodyFont = CachedFont("Segoe UI", 9.2F, FontStyle.Regular);
            TextRenderer.DrawText(g, transientDetail, bodyFont, body,
                Color.FromArgb(226, 226, 232),
                TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.WordBreak
                | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.EndEllipsis);
        }

        // The strip that closes each transient and reopens as the banner header.
        private void DrawNotificationStrip(Graphics g, int stripHeight)
        {
            const int glyphSize = 18;
            var glyphRect = new Rectangle(14, (stripHeight - glyphSize) / 2, glyphSize, glyphSize);
            DrawTransientIcon(g, glyphRect, transientKind);

            int whenWidth = 34;
            int textLeft = glyphRect.Right + 10;
            int textWidth = Math.Max(20, PillWidth - textLeft - whenWidth - 14);

            // The message, not the app name: on a phone the app icon identifies
            // the source, and the words are the part you actually came to read.
            string line = string.IsNullOrWhiteSpace(transientDetail) ? transientTitle : transientDetail;
            DrawTextAt(g, line, 9.2F, FontStyle.Regular, Color.White,
                new Rectangle(textLeft, (stripHeight - 18) / 2, textWidth, 18), false);

            DrawTextAt(g, NotificationWhen, 7.8F, FontStyle.Regular, Color.FromArgb(150, 150, 158),
                new Rectangle(PillWidth - whenWidth - 8, (stripHeight - 14) / 2, whenWidth, 14),
                false, rightAlign: true);
        }

        // Deterministic tint so the same app keeps the same colour between
        // popups, and nothing needs an icon that isn't available.
        private static Color TintForApp(string name)
        {
            if (string.IsNullOrEmpty(name)) return Color.FromArgb(90, 90, 98);
            int hash = 0;
            foreach (char c in name) hash = unchecked(hash * 31 + c);
            string[] palette =
            {
                "#2f7cf6", "#34c759", "#ff9f0a", "#ff375f",
                "#af52de", "#5ac8fa", "#ffd60a", "#64d2ff",
            };
            // Masked rather than Math.Abs: for int.MinValue Abs returns the
            // same negative value, which then indexes out of the palette.
            return ColorTranslator.FromHtml(palette[(hash & 0x7fffffff) % palette.Length]);
        }

        private static string FirstLetter(string value) =>
            string.IsNullOrWhiteSpace(value) ? "•"
            : char.ToUpperInvariant(value.TrimStart()[0]).ToString();

        // The earbud-connect moment, laid out like the reference: pair of
        // white buds standing at the left, one full 360° spin as they settle,
        // the device's own name large with a small grey kicker above it.
        private void DrawEarbudConnectCard(Graphics g)
        {
            var iconRect = new Rectangle(12, (Height - 36) / 2, 36, 36);
            float cx = iconRect.X + iconRect.Width / 2f;
            float cy = iconRect.Y + iconRect.Height / 2f;

            double age = (DateTime.Now - transientStartedAt).TotalMilliseconds;
            float t = Math.Clamp((float)(age / 750.0), 0f, 1f);
            float eased2 = 1f - (1f - t) * (1f - t) * (1f - t);   // ease-out cubic
            float angle = 360f * eased2;

            var state = g.Save();
            g.TranslateTransform(cx, cy);
            g.RotateTransform(angle);
            g.TranslateTransform(-cx, -cy);
            using (var brush = new SolidBrush(Color.White))
                DrawEarbudPairGlyph(g, brush, cx, cy, 15f);
            g.Restore(state);

            int textLeft = (int)cx + 28;
            int textWidth = Math.Max(30, PillWidth - textLeft - 18);
            DrawTextAt(g, transientDetail, 8.4F, FontStyle.Regular, Color.FromArgb(176, 176, 184),
                new Rectangle(textLeft, 12, textWidth, 16), false);
            DrawTextAt(g, transientTitle, 11.5F, FontStyle.Bold, Color.White,
                new Rectangle(textLeft, 27, textWidth, 24), false);
        }

        // Two earbuds, drawn as round head + short rounded stem, gently
        // tilted apart - reads as "the buds at attention" at 36px.
        private static void DrawEarbudPairGlyph(Graphics g, Brush brush, float cx, float cy, float s)
        {
            for (int side = -1; side <= 1; side += 2)
            {
                float headCx = cx + side * s * 0.36f;
                float headCy = cy - s * 0.34f;
                float headR = s * 0.46f;
                g.FillEllipse(brush, headCx - headR, headCy - headR, headR * 2, headR * 2);

                int stemW = Math.Max(3, (int)(s * 0.30f));
                var stem = new Rectangle(
                    (int)(headCx - stemW / 2f), (int)(headCy + headR * 0.55f),
                    stemW, (int)(s * 0.95f));
                FillRoundedRectangle(g, brush, stem, stemW / 2);
            }
        }

        private static void DrawTransientIcon(Graphics g, Rectangle area, TransientKind kind)
        {
            int cx = area.X + area.Width / 2;
            int cy = area.Y + area.Height / 2;
            using var pen = new Pen(Color.White, 2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            if (kind == TransientKind.Network)
            {
                g.DrawArc(pen, cx - 13, cy - 10, 26, 20, 205, 130);
                g.DrawArc(pen, cx - 8, cy - 5, 16, 12, 205, 130);
                g.FillEllipse(Brushes.White, cx - 2, cy + 6, 4, 4);
                return;
            }

            if (kind == TransientKind.Battery)
            {
                using var batteryPath = RoundedRectangle(new Rectangle(cx - 12, cy - 7, 22, 14), 3);
                g.DrawPath(pen, batteryPath);
                g.FillRectangle(Brushes.White, cx + 11, cy - 3, 3, 6);
                g.FillRectangle(Brushes.White, cx - 9, cy - 4, 8, 8);
                return;
            }

            if (kind == TransientKind.Brightness)
            {
                g.DrawEllipse(pen, cx - 5, cy - 5, 10, 10);
                for (int i = 0; i < 8; i++)
                {
                    double angle = i * Math.PI / 4d;
                    g.DrawLine(pen,
                        cx + (float)Math.Cos(angle) * 9f,
                        cy + (float)Math.Sin(angle) * 9f,
                        cx + (float)Math.Cos(angle) * 13f,
                        cy + (float)Math.Sin(angle) * 13f);
                }
                return;
            }

            if (kind == TransientKind.Device)
            {
                g.DrawArc(pen, cx - 11, cy - 11, 22, 22, 190, 160);
                g.DrawLine(pen, cx - 11, cy + 2, cx - 11, cy + 9);
                g.DrawLine(pen, cx + 11, cy + 2, cx + 11, cy + 9);
                g.DrawLine(pen, cx - 11, cy + 9, cx - 6, cy + 9);
                g.DrawLine(pen, cx + 11, cy + 9, cx + 6, cy + 9);
                return;
            }

            using var bell = new GraphicsPath();
            bell.AddBezier(cx - 9, cy + 5, cx - 8, cy - 8, cx - 5, cy - 10, cx, cy - 10);
            bell.AddBezier(cx, cy - 10, cx + 5, cy - 10, cx + 8, cy - 8, cx + 9, cy + 5);
            g.DrawPath(pen, bell);
            g.DrawLine(pen, cx - 11, cy + 5, cx + 11, cy + 5);
            g.FillEllipse(Brushes.White, cx - 2, cy + 8, 4, 4);
        }

        // The transient HUD, per the reference: NOTHING but the level-aware
        // icon and a slider. No percentage text, no label row - the fill and
        // the speaker's sound-waves are the readout themselves. One renderer
        // serves volume (speaker + waves) and brightness (sun), so the two
        // controls are visually identical.
        private void DrawVolumePopup(Graphics g) => DrawSliderHud(g, true);

        private void DrawSliderHud(Graphics g, bool isVolume)
        {
            int w = PillWidth;
            int h = Height;

            bool muted = false;
            if (isVolume)
            {
                try { muted = device?.AudioEndpointVolume.Mute == true; } catch { }
            }

            float shown = Math.Clamp(isVolume
                ? (muted ? 0f : volumeShownLevel)
                : Math.Max(0f, brightnessShownLevel), 0f, 1f);

            // Icon sits vertically centred at the left lip of the tile.
            const int iconSize = 24;
            var iconRect = new Rectangle(14, (h - iconSize) / 2, iconSize, iconSize);
            using var brush = new SolidBrush(Color.White);

            if (isVolume)
            {
                int waves = shown switch { >= 0.70f => 3, >= 0.34f => 2, > 0.02f => 1, _ => 0 };
                DrawSpeakerIcon(g, brush,
                    iconRect.X + iconRect.Width / 2, iconRect.Y + iconRect.Height / 2,
                    muted, 1.05f, waves);
            }
            else
            {
                DrawHudSunIcon(g, brush,
                    iconRect.X + iconRect.Width / 2, iconRect.Y + iconRect.Height / 2, 0.9f);
            }

            int trackX = iconRect.Right + 12;
            var trackRect = new Rectangle(trackX, (h - 7) / 2,
                Math.Max(30, w - trackX - 16), 7);

            using (var trackBrush = new SolidBrush(Color.FromArgb(72, 72, 78)))
                FillRoundedRectangle(g, trackBrush, trackRect, 4);

            int fillW = (int)(trackRect.Width * shown);
            if (fillW > 2)
            {
                using var fillBrush = new SolidBrush(Color.White);
                FillRoundedRectangle(g, fillBrush,
                    new Rectangle(trackRect.X, trackRect.Y, fillW, trackRect.Height), 4);
            }

            const int knob = 14;
            int knobX = Math.Max(trackRect.X, Math.Min(trackRect.X + fillW - knob / 2,
                trackRect.Right - knob));
            using (var knobBrush = new SolidBrush(Color.White))
                g.FillEllipse(knobBrush, knobX, trackRect.Y + (trackRect.Height - knob) / 2, knob, knob);
        }

        // Launch greeting, rebuilt from the reference video frame by frame:
        // card springs open, white ink is WRITTEN left-to-right with a soft
        // pen edge over ~1.4 s, sits for a second exactly as asked, then
        // fades and the spring collapses it. White only - the picture's
        // colours were just about the letter style; the motion came from
        // the video, so the animation is a pencil-drawing effect, not a
        // colourful one.
        private void DrawWelcome(Graphics g)
        {
            double elapsed = (DateTime.Now - welcomeStartAt).TotalMilliseconds;
            double remaining = (welcomeUntil - DateTime.Now).TotalMilliseconds;

            float alphaOut = Math.Clamp((float)(remaining / 500.0), 0f, 1f);
            if (alphaOut <= 0.01f) return;

            float pop = EaseOutBack(Math.Clamp((float)(elapsed / 650.0), 0f, 1f));
            float scale = 0.86f + 0.14f * pop;

            FontFamily? family = null;
            foreach (string name in new[] { "Ink Free", "Segoe Script", "Brush Script MT", "Comic Sans MS" })
            {
                try { family = new FontFamily(name); break; } catch { }
            }
            using (family)
            {
                Color ink = Color.FromArgb((int)(235 * alphaOut), 255, 255, 255);
                if (family == null)
                {
                    using var plain = new Font("Segoe UI", 40F * scale,
                        FontStyle.Bold | FontStyle.Italic);
                    TextRenderer.DrawText(g, "hello", plain,
                        new Rectangle(0, 0, PillWidth, Height), ink,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    return;
                }

                using var hello = new GraphicsPath();
                float em = Math.Max(54f, Height * 0.46f * scale);
                using var fmt = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                hello.AddString("hello", family, (int)FontStyle.Italic, em,
                    new PointF(PillWidth / 2f, Height / 2f + em * 0.06f), fmt);

                var b = hello.GetBounds();
                if (b.Width < 10f || b.Height < 10f) return;

                // Pen timing: starts just as the card settles, ease-in-out
                // over 1.38 s so the stroke accelerates and decelerates like
                // a real hand. Before it starts: blank card, as in the video.
                double t = Math.Clamp((elapsed - 320.0) / 1380.0, 0.0, 1.0);
                if (t <= 0.0) return;
                float eased = (float)(t * t * (3.0 - 2.0 * t));
                float head = b.Left - b.Width * 0.06f + eased * (b.Width * 1.12f);

                Region? previous = g.Clip;
                using var textClip = new Region(hello);
                textClip.Intersect(previous);
                Region before = g.Clip;
                g.Clip = textClip;

                // 1) dry ink already laid down behind the pen
                using (var solidInk = new SolidBrush(ink))
                    g.FillRectangle(solidInk, b.Left - 4, b.Top - 4,
                        Math.Max(0f, head - (b.Left - 4)), b.Height + 8);

                // 2) wet, fading edge at the pen tip - this is what sells
                //    "being written" instead of a marching wipe
                if (eased < 1.0)
                {
                    const float tail = 60f;
                    using var wet = new LinearGradientBrush(
                        new RectangleF(head, b.Top - 4, tail, b.Height + 8),
                        ink, Color.FromArgb(0, 255, 255, 255), 0f);
                    g.FillRectangle(wet, head, b.Top - 4, tail, b.Height + 8);
                }

                g.Clip = before;
            }
        }

        private void DrawVolumeStrip(Graphics g, int w, int panelTop)
        {
            // Same visual language as the standalone tile: icon, animated
            // track with a riding knob, live percentage - the strip version
            // of it lives in the card's bottom row. The geometry helpers are
            // shared with the click handlers, so mute/set land where drawn.
            bool muted = false;
            try { muted = device?.AudioEndpointVolume.Mute == true; } catch { }

            var iconRect = GetStripIconRect(panelTop);
            using (var speakerBrush = new SolidBrush(Color.White))
                DrawSpeakerIcon(g, speakerBrush,
                    iconRect.X + iconRect.Width / 2, iconRect.Y + iconRect.Height / 2,
                    muted, 0.9f);

            var trackRect = GetStripTrackRect(w, panelTop);
            using (var trackBrush = new SolidBrush(Color.FromArgb(70, 70, 76)))
                FillRoundedRectangle(g, trackBrush, trackRect, 4);

            float shown = muted ? 0f : Math.Clamp(volumeShownLevel, 0f, 1f);
            int fillW = (int)(trackRect.Width * shown);
            if (fillW > 2)
            {
                using var fillBrush = new SolidBrush(Color.White);
                FillRoundedRectangle(g, fillBrush,
                    new Rectangle(trackRect.X, trackRect.Y, fillW, trackRect.Height), 4);
            }

            const int knob = 14;
            int knobX = Math.Max(trackRect.X,
                Math.Min(trackRect.X + fillW - knob / 2, trackRect.Right - knob));
            using (var knobBrush = new SolidBrush(Color.White))
                g.FillEllipse(knobBrush, knobX, trackRect.Y + (trackRect.Height - knob) / 2,
                    knob, knob);
        }

        // How strong a control's instant click-flash should currently be
        // (1 = just pressed, fading to 0 over PressFlashMs).
        private float GetPressFlash(int controlIndex)
        {
            if (pressedControl != controlIndex) return 0f;
            double elapsed = (DateTime.Now - pressedAt).TotalMilliseconds;
            if (elapsed >= PressFlashMs) return 0f;
            return 1f - (float)(elapsed / PressFlashMs);
        }

        private void DrawControlFlash(Graphics g, int controlIndex, int centerX, int centerY)
        {
            float flash = GetPressFlash(controlIndex);
            if (flash <= 0f) return;
            using var flashBrush = new SolidBrush(Color.FromArgb((int)(90 * flash), 255, 255, 255));
            FillRoundedRectangle(g, flashBrush, new Rectangle(centerX - 17, centerY - 17, 34, 34), 11);
        }

        // Compact animated "now playing" waveform glyph — a handful of thin
        // bars driven by waveformLevels — used both in the collapsed pill and
        // as the small icon in the top-right of the expanded card.
        private void DrawMiniWaveform(Graphics g, Rectangle area, int barCount = 4)
        {
            using var brush = new SolidBrush(albumAccentColor);
            int barWidth = Math.Max(2, area.Width / (barCount * 2));
            int gap = barWidth;
            int totalWidth = barCount * barWidth + (barCount - 1) * gap;
            int startX = area.X + Math.Max(0, (area.Width - totalWidth) / 2);
            int centerY = area.Y + area.Height / 2;

            for (int i = 0; i < barCount; i++)
            {
                float level = waveformLevels[i % waveformLevels.Length];
                int barHeight = Math.Max(3, (int)(level * (area.Height - 4)));
                int x = startX + i * (barWidth + gap);
                FillRoundedRectangle(g, brush,
                    new Rectangle(x, centerY - barHeight / 2, barWidth, barHeight), 1);
            }
        }

        // Arabic and Hebrew blocks — used to switch TextRenderer into RTL
        // mode. Without this, GDI still lays the string out left-aligned in
        // LTR order, which for Arabic both mis-shapes joined letterforms and
        // pushes the visually-first characters outside the clipped area —
        // that's the "doesn't fully appear" cutoff.
        private static bool ContainsRtl(string value)
        {
            foreach (char c in value)
            {
                if ((c >= '\u0590' && c <= '\u08FF') || (c >= '\uFB1D' && c <= '\uFEFF'))
                    return true;
            }
            return false;
        }

        private void DrawScrollingText(
    Graphics g,
    string value,
    float size,
    FontStyle style,
    Color color,
    Rectangle area,
    ref float offset,
    int? hardStopX = null,
            string fontFamily = "Segoe UI")
        {
            var font = CachedFont(fontFamily, size, style);

            bool rtl = ContainsRtl(value);
            TextFormatFlags baseFlags =
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPrefix |
                TextFormatFlags.SingleLine |
                TextFormatFlags.PreserveGraphicsClipping;
            if (rtl) baseFlags |= TextFormatFlags.RightToLeft | TextFormatFlags.Right;

            const int leftBuffer = 6;
            const int rightBuffer = 6;
            const float gap = 28f;

            // hardStopX is an absolute X coordinate (in the same space as
            // `area`) that the visible text may never cross, on top of the
            // normal rightBuffer margin. This is what actually stops the
            // marquee from bleeding into the waveform glyph, album art, or
            // the dot beside the pill — margins alone only pad the intended
            // layout box, they don't clamp against a neighboring element
            // that this method has no knowledge of.
            int effectiveRight = area.X + area.Width - rightBuffer;
            if (hardStopX.HasValue)
                effectiveRight = Math.Min(effectiveRight, hardStopX.Value);

            Rectangle clippedArea = new Rectangle(
                area.X + leftBuffer,
                area.Y,
                Math.Max(1, effectiveRight - (area.X + leftBuffer)),
                area.Height);

            int textWidth = TextRenderer.MeasureText(
                g,
                value,
                font,
                new Size(int.MaxValue, area.Height),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
            ).Width;
            // Small safety margin: GDI's shaped-glyph measurement for joined
            // scripts (Arabic ligatures in particular) can run a couple of
            // pixels past MeasureText's estimate.
            textWidth += 4;

            if (textWidth <= clippedArea.Width)
            {
                TextRenderer.DrawText(
                    g,
                    value,
                    font,
                    clippedArea,
                    color,
                    baseFlags);

                return;
            }

            offset %= textWidth + gap;

            GraphicsState clipState = g.Save();
            g.SetClip(clippedArea, CombineMode.Intersect);

            int x = clippedArea.X - (int)offset;

            Rectangle rect1 = new Rectangle(
                x,
                area.Y,
                textWidth,
                area.Height);

            Rectangle rect2 = new Rectangle(
                x + textWidth + (int)gap,
                area.Y,
                textWidth,
                area.Height);

            TextRenderer.DrawText(
                g,
                value,
                font,
                rect1,
                color,
                baseFlags);

            TextRenderer.DrawText(
                g,
                value,
                font,
                rect2,
                color,
                baseFlags);

            // Fade the text out at both edges instead of hard-cutting it.
            // This overlays a gradient rectangle — opaque at the very edge,
            // fading to fully transparent moving inward — on top of the
            // text we just drew. Since the pill's background here is always
            // flat solid black (see FillLiquidGlass), painting an opaque-
            // black-to-transparent gradient on top reads visually identical
            // to a true per-pixel alpha fade on the text itself, without
            // the cost of a LockBits pass every frame. If the background
            // color behind this text is ever changed to something other
            // than flat black, FadeColor below needs to match it or the
            // fade will show as a mismatched-color band instead of a clean
            // fade-to-background.
            const int fadeWidth = 14; // <-- tune this for a longer/shorter fade
            Color fadeColor = Color.Black;

            // LinearGradientBrush throws ArgumentException on any rect with
            // a zero width or height. clippedArea.Height can briefly be 0
            // mid-animation (e.g. DrawPeekMedia's second row height is
            // Height - restingSize.Height, which is 0 for a frame or two
            // while the pill is still collapsing/expanding) — skip the
            // fade entirely for that one frame rather than crash.
            int actualFadeWidth = Math.Min(fadeWidth, clippedArea.Width / 2);
            if (actualFadeWidth > 0 && clippedArea.Height > 0)
            {
                using (var leftFade = new LinearGradientBrush(
                    new Rectangle(clippedArea.X, clippedArea.Y, actualFadeWidth, clippedArea.Height),
                    fadeColor, Color.FromArgb(0, fadeColor), 0f))
                {
                    g.FillRectangle(leftFade, clippedArea.X, clippedArea.Y, actualFadeWidth, clippedArea.Height);
                }

                using (var rightFade = new LinearGradientBrush(
                    new Rectangle(clippedArea.Right - actualFadeWidth, clippedArea.Y, actualFadeWidth, clippedArea.Height),
                    Color.FromArgb(0, fadeColor), fadeColor, 0f))
                {
                    g.FillRectangle(rightFade, clippedArea.Right - actualFadeWidth, clippedArea.Y, actualFadeWidth, clippedArea.Height);
                }
            }

            g.Restore(clipState);
        }

        // Spotify-style line handoff: the outgoing lyric lifts and softens as
        // it fades, while the incoming lyric rises from below into full focus.
        // Rendering into a small transparent layer lets the final alpha remain
        // smooth even though TextRenderer itself does not blend text opacity.
        // Per-word karaoke state for one lyric line: measured ONCE when the
        // line changes — widths, character ends, visual slots. Laid out
        // LEFT-TO-RIGHT even for Arabic: the words are placed right-to-left,
        // and each word is drawn with the RTL reading flag only when it
        // actually contains RTL characters, so Latin digits inside an Arabic
        // line stay readable. Joined scripts shape per word, and Arabic only
        // joins within a word, so drawing word-by-word is typographically
        // sound.
        private sealed class KaraokeRun
        {
            public string Text = "";
            public bool Rtl;
            public string[] Words = System.Array.Empty<string>();
            public int[] Widths = System.Array.Empty<int>();
            public int[] Left = System.Array.Empty<int>();
            public int[] WidthEnds = System.Array.Empty<int>();
            public int Total, SpaceW;
        }
        private KaraokeRun karaokeRun = new();
        private float karaokeScroll;

        private static readonly TextFormatFlags KaraokeBaseFlags =
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;

        // Word-level karaoke from line-level stamps — the Spotify trick.
        // The line's real duration is split across its words by character
        // count, squeezed into the first 92% of the window so the glow lands
        // a breath before the next stamp. When the line overflows the column
        // the row no longer free-runs: the scroll FOLLOWS the active word and
        // keeps it in view (that mismatch is what made it look like the glow
        // fell behind). And when the singing stops inside a line's window,
        // the last words fade away instead of hanging there through the
        // instrumental — blank until the next stamp, like a held breath.
        // Instrumental presence: a small beamed pair of notes, tinted toward
        // the album accent, that swells and sways with the SAME loopback
        // loudness driving the waveform bars — so a music-only stretch reads
        // as the song breathing, not as an empty row. `presence` fades it in
        // over the last stretch of the previous line's window.
        private void DrawInstrumentalNote(Graphics g, int x, int w, int y, int h, float presence)
        {
            float lvl = 0f;
            for (int i = 0; i < waveformLevels.Length; i++)
                lvl = Math.Max(lvl, waveformLevels[i]);
            float pop = 1f + 0.18f * Math.Clamp(lvl * 1.4f, 0f, 1f);
            double t = DateTime.Now.TimeOfDay.TotalSeconds;
            float sway = 0.05f * (float)Math.Sin(t * 1.6);

            int a = (int)(((190 + 50 * Math.Clamp(lvl, 0f, 1f)) * Math.Clamp(presence, 0f, 1f)));
            Color col = Color.FromArgb(Math.Min(240, a),
                (albumAccentColor.R + 255) / 2, (albumAccentColor.G + 255) / 2,
                (albumAccentColor.B + 255) / 2);

            float gh = 13f * pop, gw = 15.5f * pop;
            float gx = x + w / 2f - gw / 2f;
            float gy = y + h / 2f - gh / 2f + sway * 10f;

            using var brush = new SolidBrush(col);
            using var pen = new Pen(col, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.FillEllipse(brush, gx, gy + gh - 4.4f, 5.4f, 4.2f);
            g.FillEllipse(brush, gx + gw - 5.4f, gy + gh - 5.9f, 5.4f, 4.2f);
            g.DrawLine(pen, gx + 5.1f, gy + gh - 2.4f, gx + 5.1f, gy);
            g.DrawLine(pen, gx + gw - 0.2f, gy + gh - 3.9f, gx + gw - 0.2f, gy + 1.6f);
            g.DrawLine(pen, gx + 5.1f, gy + 0.7f, gx + gw - 0.2f, gy + 2.3f);
        }

        // One whole lyric line as a single shaped run, autoshrunk to fit
        // the column (9.8 → 8.2 pt), ellipsis only as a true last resort.
        // Arabic goes through GDI's own bidi with the reading flag set once
        // on the full sentence — which is exactly how it gets joined,
        // ordered and right-aligned correctly, where word-splitting could
        // never keep the promise.
        private void DrawFittedLyric(Graphics g, string text, Rectangle area, float alpha)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            int a = (int)(238 * Math.Clamp(alpha, 0f, 1f));
            if (a <= 8) return;
            var color = Color.FromArgb(a, 242, 242, 248);
            bool rtl = ContainsRtl(text);

            var baseFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
                | TextFormatFlags.VerticalCenter
                | (rtl ? TextFormatFlags.RightToLeft | TextFormatFlags.Right : (TextFormatFlags)0);

            for (float size = 9.8f; size >= 8.2f; size -= 0.4f)
            {
                var font = CachedFont("Segoe UI Semibold", size, FontStyle.Regular);
                Size m = TextRenderer.MeasureText(g, text, font,
                    new Size(area.Width + 40, area.Height), baseFlags);
                if (m.Width <= area.Width)
                {
                    TextRenderer.DrawText(g, text, font, area, color, baseFlags);
                    return;
                }
            }
            TextRenderer.DrawText(g, text,
                CachedFont("Segoe UI Semibold", 8.2f, FontStyle.Regular),
                area, color, baseFlags | TextFormatFlags.EndEllipsis);
        }

        private void DrawKaraokeLyric(
            Graphics g, string text, Rectangle area, int hardStop, float frac,
            double posMs, double lineStartMs, double lineEndMs)
        {
            var font = CachedFont("Segoe UI Semibold", 9.8F, FontStyle.Bold);

            if (karaokeRun.Text != text)
            {
                var run = new KaraokeRun
                {
                    Text = text,
                    Words = text.Split(' ', System.StringSplitOptions.RemoveEmptyEntries),
                };
                run.Rtl = ContainsRtl(text);
                run.Widths = new int[run.Words.Length];
                run.Left = new int[run.Words.Length];
                run.WidthEnds = new int[run.Words.Length];
                run.SpaceW = TextRenderer.MeasureText(g, " ", font,
                    new Size(int.MaxValue, area.Height), KaraokeBaseFlags).Width;
                int x = 0, c = 0;
                for (int i = 0; i < run.Words.Length; i++)
                {
                    var wf = KaraokeBaseFlags | (ContainsRtl(run.Words[i])
                        ? TextFormatFlags.RightToLeft | TextFormatFlags.Right : (TextFormatFlags)0);
                    run.Widths[i] = TextRenderer.MeasureText(g, run.Words[i], font,
                        new Size(int.MaxValue, area.Height), wf).Width;
                    x += run.Widths[i] + (i > 0 ? run.SpaceW : 0);
                    run.WidthEnds[i] = x;
                }
                run.Total = System.Math.Max(1, x);
                // visual slots: LTR order left-to-right; RTL order starts at
                // the right and walks left (word 0 rightmost)
                int acc = 0;
                for (int i = 0; i < run.Words.Length; i++)
                {
                    if (!run.Rtl) run.Left[i] = acc;
                    else run.Left[i] = run.Total - acc - run.Widths[i];
                    acc += run.Widths[i] + (i > 0 ? run.SpaceW : 0);
                }
                karaokeRun = run;
                karaokeScroll = 0f;

                // arm the acoustic tracker on the same window the glow uses
                voice.BeginLine(WordAligner.NowMs, posMs, lineStartMs, lineEndMs, run.Words.Length);
            }
            var r = karaokeRun;
            if (r.Words.Length == 0) return;

            int right = System.Math.Min(area.Right, hardStop);
            int usable = System.Math.Max(1, right - area.X);

            // Three rulers, best available wins:
            //   1. YouTube Music's actual per-word times (exact, or mapped
            //      proportionally when the two services tokenized the line
            //      differently);
            //   2. the acoustic layer — the width guess refined by where the
            //      VOICE actually hit, ±1 word max;
            //   3. the width ruler itself.
            // Scroll always follows whichever word finally won, so text and
            // glow can disagree by construction no longer.
            float target = System.Math.Clamp(frac / 0.92f, 0f, 0.9999f) * r.Total;
            int active = r.Words.Length - 1;
            for (int i = 0; i < r.Words.Length; i++)
                if (target < r.WidthEnds[i]) { active = i; break; }

            // One ruler, one refiner: the width walk plus the acoustic
            // layer's sung bursts. (The old third source — unofficial
            // YouTube word-times — was removed before publishing: a gray
            // API on the network list is not worth a marginally better
            // guess.)

            // ── the word-times ladder used WordLine; retired with it.
            active = voice.Refine(posMs, active);

            // instrumental hold inside the line's tail: fade words out, then
            // nothing until the next stamp
            float fade = 1f;
            if (frac > 0.82f)
            {
                float k = System.Math.Clamp((frac - 0.82f) / 0.18f, 0f, 1f);
                fade = 1f - k * k * (3f - 2f * k);
            }
            if (fade <= 0.03f) return;

            // scroll follows the active word instead of drifting free
            // Marquee follow, direction-aware. LTR pins the active word
            // ~42% in from the LEFT edge (content slides left); Arabic pins
            // it ~58% across from the left — i.e. ~42% from the RIGHT — so
            // content slides rightward, the way an Arabic reader's eye walks.
            if (r.Total > usable)
            {
                double activeEdge = r.Rtl ? r.Left[active] + r.Widths[active] : r.Left[active];
                double anchor = r.Rtl ? usable * .58 : usable * .42;
                float want = (float)System.Math.Clamp(activeEdge - anchor,
                    0, r.Total - usable);
                karaokeScroll += (want - karaokeScroll) * .12f;
                if (System.Math.Abs(want - karaokeScroll) < 0.5f) karaokeScroll = want;
            }
            else karaokeScroll = 0f;

            var state = g.Save();
            g.IntersectClip(new Rectangle(area.X, area.Y, usable, area.Height));

            // A short Arabic line right-aligns its block inside the column
            // (Arabic typographic default); LTR keeps the left edge.
            int baseX = area.X - (int)karaokeScroll +
                (r.Total <= usable && r.Rtl ? Math.Max(0, usable - r.Total) : 0);
            for (int i = 0; i < r.Words.Length; i++)
            {
                int x = baseX + r.Left[i];
                if (x + r.Widths[i] < area.X || x > right) continue;
                int a = (int)(255 * fade * (i == active ? 1f : i < active ? .78f : .46f));
                Color col = i == active
                    ? Color.FromArgb(Math.Min(255, (int)(255 * fade)), 255, 255)
                    : i < active ? Color.FromArgb(a, 200, 202, 212)
                                 : Color.FromArgb(a, 150, 152, 166);
                var wf = KaraokeBaseFlags | (ContainsRtl(r.Words[i])
                    ? TextFormatFlags.RightToLeft | TextFormatFlags.Right : (TextFormatFlags)0);
                TextRenderer.DrawText(g, r.Words[i], font,
                    new Rectangle(x, area.Y, r.Widths[i] + 10, area.Height), col, wf);
            }

            g.Restore(state);
        }

        private void DrawLyricTransitionLayer(
            Graphics target,
            string value,
            Rectangle area,
            float marqueeOffset,
            int hardStopX,
            float opacity,
            float verticalOffset,
            float softness)
        {
            if (opacity <= 0.001f || string.IsNullOrWhiteSpace(value)) return;

            int width = Math.Max(1, PillWidth);
            int height = Math.Max(1, Height);
            using var layer = new Bitmap(width, height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var lg = Graphics.FromImage(layer))
            {
                lg.Clear(Color.Transparent);
                lg.SmoothingMode = SmoothingMode.AntiAlias;
                lg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                lg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                lg.CompositingQuality = CompositingQuality.HighQuality;

                float localOffset = marqueeOffset;
                DrawScrollingText(lg, value,
                    9.8F, FontStyle.Regular, Color.FromArgb(238, 238, 244),
                    area, ref localOffset, hardStopX, "Segoe UI Semibold");
            }

            void Composite(float alpha, int y)
            {
                if (alpha <= 0.001f) return;
                var matrix = new System.Drawing.Imaging.ColorMatrix
                {
                    Matrix33 = Math.Clamp(alpha, 0f, 1f)
                };
                using var attributes = new System.Drawing.Imaging.ImageAttributes();
                attributes.SetColorMatrix(matrix,
                    System.Drawing.Imaging.ColorMatrixFlag.Default,
                    System.Drawing.Imaging.ColorAdjustType.Bitmap);
                target.DrawImage(layer,
                    new Rectangle(0, y, width, height),
                    0, 0, width, height,
                    GraphicsUnit.Pixel, attributes);
            }

            int yOffset = (int)Math.Round(verticalOffset);
            float ghostAlpha = opacity * softness * 0.12f;
            Composite(ghostAlpha, yOffset - 1);
            Composite(ghostAlpha, yOffset + 1);
            Composite(opacity, yOffset);
        }

        // Fonts sit on GDI handles behind the scenes. Allocating one (or
        // twelve) inside every 16 ms paint frame is exactly the handle + LOH
        // churn that reads as system-wide lag — so every text painter now
        // pulls from this cache. Keys are immutable tuples; fonts are shared,
        // never disposed (a handful live for the app's whole life).
        static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, float, FontStyle), Font>
            fontCache = new();

        static Font CachedFont(string family, float size, FontStyle style) =>
            fontCache.GetOrAdd((family, size, style), k => new Font(k.Item1, k.Item2, k.Item3));

        private void DrawTextAt(
            Graphics g,
            string value,
            float size,
            FontStyle style,
            Color color,
            Rectangle area,
            bool centered,
            bool rightAlign = false)
        {
            var font = CachedFont("Segoe UI", size, style);

            bool rtl = ContainsRtl(value);

            TextFormatFlags flags =
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.PreserveGraphicsClipping;

            if (rtl)
                flags |= TextFormatFlags.RightToLeft;

            if (centered)
                flags |= TextFormatFlags.HorizontalCenter;
            else if (rightAlign || rtl)
                flags |= TextFormatFlags.Right;

            TextRenderer.DrawText(
                g,
                value,
                font,
                area,
                color,
                flags);
        }
        private static void DrawQueueIcon(Graphics g, Brush brush, int centerX, int centerY)
        {
            using var pen = new Pen(brush, 1.9F)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            for (int i = -1; i <= 1; i++)
            {
                int y = centerY + i * 7;
                g.FillEllipse(brush, centerX - 10, y - 1.5f, 3, 3);
                g.DrawLine(pen, centerX - 4, y, centerX + 10, y);
            }
        }

        // Layout for one row of the queue panel — shared by DrawQueuePanel and
        // HandleQueueClick so drawing and hit-testing can never drift apart.
        private (Rectangle row, Rectangle art, Rectangle text, Rectangle right) GetQueueRowRects(int w, int rowTop)
        {
            const int leftPad = 20, rightPad = 20;
            var row = new Rectangle(leftPad, rowTop, Math.Max(1, w - leftPad - rightPad), QueueRowHeight - 6);
            var art = new Rectangle(row.X, row.Y + (row.Height - QueueArtSize) / 2, QueueArtSize, QueueArtSize);
            var right = new Rectangle(row.Right - QueueRightWidth, row.Y, QueueRightWidth, row.Height);
            var text = new Rectangle(art.Right + 12, row.Y, Math.Max(1, right.X - (art.Right + 12) - 10), row.Height);
            return (row, art, text, right);
        }

        private void DrawQueuePanel(Graphics g, int w, int panelTop)
        {
            const int leftPad = 20, rightPad = 20;

            // No rule across the top any more — where the list begins is
            // carried by the "Playing Next" header itself, so the media card
            // and the extension read as one surface.

            var headerFont = CachedFont("Segoe UI", 9.5F, FontStyle.Bold);
            var headerRect = new Rectangle(leftPad, panelTop + 8, w - leftPad - rightPad, 18);
            string header = "Playing Next";
            TextRenderer.DrawText(g, header, headerFont, headerRect,
                Color.FromArgb(190, 190, 198),
                TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            if (queueItems.Count > 0)
            {
                if (QueueNeedsAttention)
                {
                    // Rows are on screen, so the empty state's button cannot be
                    // reached — without this chip a rejected sign-in would be
                    // readable but not fixable from the card.
                    var chip = GetQueueChipRect(w, panelTop);
                    using (var fill = new SolidBrush(Color.FromArgb(58, 52, 40)))
                        using (var path = RoundedRectangle(chip, 8))
                            g.FillPath(fill, path);
                    using (var edge = new Pen(Color.FromArgb(232, 190, 96, 160), 1f))
                        using (var path = RoundedRectangle(chip, 8))
                            g.DrawPath(edge, path);
                    DrawTextAt(g, QueueConnectLabel, 8.2F, FontStyle.Bold,
                        Color.FromArgb(232, 190, 96), chip, true);
                }
                else
                {
                    // The count is the honest version of a scrollbar: it says the
                    // window is a window, without needing the whole list visible.
                    var countFont = CachedFont("Segoe UI", 8.5F, FontStyle.Regular);
                    string count = queueItems.Count + (queueItems.Count == 1 ? " track" : " tracks");
                    var countRect = new Rectangle(headerRect.Right - 130, panelTop + 9, 130, 16);
                    TextRenderer.DrawText(g, count, countFont, countRect,
                        Color.FromArgb(120, 190, 140),
                        TextFormatFlags.NoPadding | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }
            }

            if (queueItems.Count == 0)
            {
                DrawQueueEmptyState(g, w, panelTop, leftPad, rightPad);
                return;
            }

            // The list gets everything from the header block to the bottom
            // edge of the extended card (minus the volume strip when one is
            // stacked below it) — rows scroll through that window, three at
            // a time in a settled extension.
            int windowTop = panelTop + QueueHeaderHeight;
            int windowHeight = Math.Max(0, QueueListBottom() - windowTop);

            GraphicsState rowsClipped = g.Save();
            g.SetClip(new RectangleF(leftPad, windowTop,
                Math.Max(1, w - leftPad - rightPad), windowHeight));

            for (int index = 0; index < queueItems.Count; index++)
            {
                int rowTop = windowTop + (int)(index * QueueRowHeight - queueScrollPx);
                if (rowTop + QueueRowHeight <= windowTop) continue;          // scrolled above
                if (rowTop >= windowTop + windowHeight) break;               // below the window

                var item = queueItems[index];
                var (row, art, text, right) = GetQueueRowRects(w, rowTop);

                bool hovered = index == hoveredQueueRow;
                if (hovered)
                {
                    using var halo = new SolidBrush(Color.FromArgb(22, 255, 255, 255));
                    using var haloPath = RoundedRectangle(row, 10);
                    g.FillPath(halo, haloPath);
                }

                const int artRadius = 8;
                if (item.Art != null)
                {
                    using var clip = RoundedRegion(art, artRadius);
                    Region? previousClip = g.Clip;
                    g.Clip = clip;
                    g.DrawImage(item.Art, art);
                    g.Clip = previousClip;
                }
                else
                {
                    using var artBrush = new SolidBrush(Color.FromArgb(44, 44, 50));
                    using var artPath = RoundedRectangle(art, artRadius);
                    g.FillPath(artBrush, artPath);
                    DrawTextAt(g, "♪", 12F, FontStyle.Bold, Color.FromArgb(150, 150, 158), art, true);
                }

                bool starting = QueueStartPending && item.Uri == queueStartedUri;

                DrawTextAt(g, starting ? "Starting  " + item.Title : item.Title, 10F,
                    FontStyle.Bold, starting ? Color.FromArgb(30, 215, 90) : Color.White,
                    new Rectangle(text.X, text.Y, text.Width, text.Height / 2), false);
                var subLine = new Rectangle(text.X, text.Y + text.Height / 2, text.Width, text.Height / 2);
                if (QueueTapNoteOn(item.Uri))
                {
                    DrawTextAt(g, queueTapNoteText, 8.5F, FontStyle.Regular,
                        Color.FromArgb(232, 190, 96), subLine, false);
                }
                else
                {
                    DrawTextAt(g, item.Subtitle, 8.5F, FontStyle.Regular,
                        Color.FromArgb(150, 150, 158), subLine, false);
                }

                // Right cell: bare until hovered, when the play triangle appears.
                // The durations used to sit here and are gone: a column of numbers
                // competing with the titles for the same glance, and nothing in
                // the panel could act on them.
                if (hovered && !starting && item.Uri.Length > 0)
                {
                    using var glyph = new SolidBrush(Color.White);
                    int s2 = 13;
                    int cx = right.X + right.Width / 2 - 1;
                    int cy = row.Y + row.Height / 2;
                    g.FillPolygon(glyph, new[]
                    {
                        new PointF(cx - s2 / 2f + 1, cy - s2 / 2f),
                        new PointF(cx - s2 / 2f + 1, cy + s2 / 2f),
                        new PointF(cx + s2 / 2f + 1, cy),
                    });
                }
                else if (hovered)
                {
                    // A hovered row with nothing to start still answers the hover,
                    // so the highlight never reads as a dead control.
                    DrawTextAt(g, "·", 14F, FontStyle.Bold,
                        Color.FromArgb(120, 120, 128), right, true);
                }

                // Only between two fully-drawn rows — the window's own edge
                // (card padding or the strip below) is a rule of its own.
                if (index + 1 < queueItems.Count &&
                    row.Bottom + 1 < windowTop + windowHeight)
                    using (var rowSep = new Pen(Color.FromArgb(18, 255, 255, 255), 1f))
                        g.DrawLine(rowSep, text.X, row.Bottom, w - rightPad, row.Bottom);
            }

            g.Restore(rowsClipped);
            DrawQueueScrollHint(g, w, panelTop);
        }

        // Only appears when there is more list below the window. Deliberately a
        // 2px bar rather than a chrome scrollbar: the page is 430px wide and a
        // real thumb would eat the row text. It sits at the very edge — two
        // pixels clear of the page-indicator strip beside it.
        private void DrawQueueScrollHint(Graphics g, int w, int panelTop)
        {
            if (QueueScrollMaxPx <= 0f) return;

            int trackTop = panelTop + QueueHeaderHeight;
            int trackHeight = QueueListBottom() - trackTop - 6;
            if (trackHeight <= 12) return;

            float frac = queueScrollPx / QueueScrollMaxPx;
            int thumb = Math.Max(22, trackHeight * QueueWindowRows() / Math.Max(1, queueItems.Count));
            int y = trackTop + (int)((trackHeight - thumb) * frac);

            using var bg = new SolidBrush(Color.FromArgb(16, 255, 255, 255));
            using var bgPath = RoundedRectangle(new Rectangle(w - 10, trackTop, 3, trackHeight), 2);
            g.FillPath(bg, bgPath);

            using var fg = new SolidBrush(Color.FromArgb(90, 255, 255, 255));
            using var fgPath = RoundedRectangle(new Rectangle(w - 10, y, 3, thumb), 2);
            g.FillPath(fg, fgPath);
        }

        // No rows: the exact reason, in colour that matches severity, and the
        // one control that can actually fix it.
        private void DrawQueueEmptyState(Graphics g, int w, int panelTop, int leftPad, int rightPad)
        {
            int lineTop = panelTop + QueueHeaderHeight;

            DrawTextAt(g, QueueStatusLine(queueStatus), 9F, FontStyle.Regular,
                QueueStatusColor(queueStatus),
                new Rectangle(leftPad, lineTop, Math.Max(20, w - leftPad - rightPad), 20), false);

            if (!ShowQueueConnectButton) return;

            var rect = GetQueueCtaRect(w, lineTop + 26);
            using (var fill = new SolidBrush(Color.FromArgb(52, 52, 58)))
                using (var path = RoundedRectangle(rect, 9))
                    g.FillPath(fill, path);
            using (var edge = new Pen(Color.FromArgb(86, 86, 94), 1f))
            using (var path = RoundedRectangle(rect, 9))
                g.DrawPath(edge, path);

            DrawTextAt(g, QueueConnectLabel, 9F, FontStyle.Bold, Color.White, rect, true);
        }

        // Header chip, top-right of the panel: the row list's only control, and
        // deliberately in the header so it can never overlap a row's play target.
        private Rectangle GetQueueChipRect(int w, int panelTop)
        {
            const int width = 104, height = 22;
            return new Rectangle(w - 20 - width, panelTop + 6, width, height);
        }

        // Shared by the painter and the hit-test so the button can never drift
        // away from where clicks land.
        private Rectangle GetQueueCtaRect(int w, int top)
        {
            int width = Math.Min(168, Math.Max(120, w - 60));
            return new Rectangle((w - width) / 2, top, width, 30);
        }

        private static Color QueueStatusColor(SpotifyQueueStatus status) => status switch
        {
            // A clean empty queue is not a problem, so it must not read as one:
            // without this arm the default colour is the red reserved for
            // Spotify refusing something.
            SpotifyQueueStatus.Ok => Color.FromArgb(150, 150, 158),
            SpotifyQueueStatus.NotConnected => Color.FromArgb(170, 170, 178),
            SpotifyQueueStatus.Connecting => Color.FromArgb(120, 190, 140),
            SpotifyQueueStatus.NothingPlaying => Color.FromArgb(150, 150, 158),
            SpotifyQueueStatus.RateLimited => Color.FromArgb(150, 150, 158),
            SpotifyQueueStatus.PremiumRequired => Color.FromArgb(232, 190, 96),
            SpotifyQueueStatus.NoClientConfigured => Color.FromArgb(232, 190, 96),
            SpotifyQueueStatus.NetworkError => Color.FromArgb(232, 190, 96),
            SpotifyQueueStatus.ReapprovalNeeded => Color.FromArgb(232, 190, 96),
            _ => Color.FromArgb(236, 128, 122),
        };

        // What the empty panel says. Deliberately one sentence and actionable:
        // the previous single line blamed the media source for what was usually
        // a missing Client ID or an expired token.
        private string QueueStatusLine(SpotifyQueueStatus status) => status switch
        {
            // With rows on screen this line is never painted; it only shows when
            // Spotify answered cleanly and the list really is empty.
            SpotifyQueueStatus.Ok => "Nothing is queued up next yet.",
            SpotifyQueueStatus.Connecting =>
                string.IsNullOrWhiteSpace(queueStatusDetail) ? "Contacting Spotify..." : queueStatusDetail,
            SpotifyQueueStatus.NoClientConfigured =>
                "Add your Spotify app's Client ID in Settings.",
            SpotifyQueueStatus.NotConnected =>
                "Not signed in to Spotify yet.",
            SpotifyQueueStatus.ConnectionDeclined =>
                "Spotify sign-in was cancelled or timed out.",
            SpotifyQueueStatus.PremiumRequired =>
                string.IsNullOrWhiteSpace(queueStatusDetail)
                    ? "The up-next list needs Spotify Premium."
                    : queueStatusDetail,
            SpotifyQueueStatus.NothingPlaying =>
                "Nothing is playing in Spotify right now.",
            SpotifyQueueStatus.RateLimited =>
                "Spotify is rate-limiting; this retries by itself.",
            SpotifyQueueStatus.NetworkError =>
                "No route to Spotify — check your connection.",
            SpotifyQueueStatus.ReapprovalNeeded =>
                "This sign-in predates queue control — press Reconnect.",
            SpotifyQueueStatus.RejectedBySpotify =>
                string.IsNullOrWhiteSpace(queueStatusDetail)
                    ? "Spotify rejected the request."
                    : queueStatusDetail,
            _ => "No queue available for this source",
        };

        private bool HandleQueueClick(Point point)
        {
            int w = Math.Max(PillWidth, 200);
            int panelTop = QueuePanelTopY();

            if (queueItems.Count > 0)
            {
                // The chip lives above the rows, so it is asked first.
                if (QueueNeedsAttention &&
                    GetQueueChipRect(w, panelTop).Contains(point))
                {
                    _ = EnsureSpotifyLoginAndRefreshAsync();
                    return true;
                }

                int top = panelTop + QueueHeaderHeight;
                if (point.Y < top || point.Y >= QueueListBottom()) return false;

                int index = QueueRowIndexAt(point.Y, panelTop);
                if (index < 0 || index >= queueItems.Count) return false;

                PlayQueueRow(queueItems[index]);
                return true;
            }

            if (!ShowQueueConnectButton) return false;
            if (!GetQueueCtaRect(w, panelTop + QueueHeaderHeight + 26).Contains(point))
                return false;

            if (queueStatus == SpotifyQueueStatus.NoClientConfigured)
            {
                // The Client ID has to be typed somewhere the card cannot offer,
                // so this button opens the settings window instead of a handshake
                // that could never finish.
                SettingsRequested?.Invoke();
            }
            else
            {
                _ = EnsureSpotifyLoginAndRefreshAsync();
            }

            RefreshVisual();
            return true;
        }

        // One row, one action: start that track. The panel does not pretend to
        // know it worked — the tag holds for a few seconds and the next list
        // from Spotify is what actually confirms it, by no longer showing the
        // track in the queue.
        private void PlayQueueRow(QueueItem item)
        {
            // Only a row from the display-only SetQueue overload has no uri; a
            // Spotify row always does. Nothing to say in that case — the row has
            // already hidden its play triangle, which is the honest signal.
            if (string.IsNullOrWhiteSpace(item.Uri)) return;

            queueStartedUri = item.Uri;
            queueStartedAt = DateTime.Now;
            queueTapNoteUri = "";
            RefreshVisual();

            _ = StartSpotifyTrackAsync(item);
        }

        private async Task StartSpotifyTrackAsync(QueueItem item)
        {
            try
            {
                var outcome = await spotifyQueue.PlayTrackOutcomeAsync(item.Uri);

                if (outcome == SpotifyQueueService.PlayOutcome.StartedInQueue)
                {
                    // The queue kept its shape: this track starts and the list
                    // behind it stays. Nothing to say out loud.
                    _ = RefreshSpotifyQueueAsync();
                    return;
                }

                if (outcome == SpotifyQueueService.PlayOutcome.StartedOutsideQueue)
                {
                    // It plays, but Spotify is now following the track rather than
                    // the list, so the remaining rows are gone. That was the old
                    // behaviour by accident; saying it is the difference between a
                    // surprise and a bug.
                    queueStartedUri = "";
                    NoteQueueRow(item.Uri,
                        "Played — Spotify stopped following the list after this.");
                    _ = RefreshSpotifyQueueAsync();
                    RefreshVisual();
                    return;
                }

                // The tap did not take: drop the tag immediately rather than
                // letting it lie for the rest of the grace period, and put
                // Spotify's reason on the row it belongs to.
                queueStartedUri = "";
                NoteQueueRow(item.Uri, spotifyQueue.StatusDetail.Length > 0
                    ? spotifyQueue.StatusDetail
                    : "Spotify didn't start that track.");
                SetQueueStatus(spotifyQueue.Status, spotifyQueue.StatusDetail);
                RefreshVisual();
            }
            catch
            {
                queueStartedUri = "";
            }
        }
        // Layout shared between DrawCallOverlay and this hit-test so the
        // drawn buttons and the clickable areas can never drift apart.
        private (Rectangle avatar, Rectangle decline, Rectangle accept) GetCallOverlayRects(int w, int h)
        {
            const int avatarSize = 46, btnSize = 46;
            var avatar = new Rectangle(18, (h - avatarSize) / 2, avatarSize, avatarSize);
            var accept = new Rectangle(w - 18 - btnSize, (h - btnSize) / 2, btnSize, btnSize);
            var decline = new Rectangle(accept.X - 12 - btnSize, (h - btnSize) / 2, btnSize, btnSize);
            return (avatar, decline, accept);
        }

        private void HandleCallOverlayClick(Point point)
        {
            var (_, decline, accept) = GetCallOverlayRects(PillWidth, Height);
            if (accept.Contains(point))
            {
                var cb = onCallAccept;
                DismissOverlay();
                cb?.Invoke();
            }
            else if (decline.Contains(point))
            {
                var cb = onCallDecline;
                DismissOverlay();
                cb?.Invoke();
            }
        }

        private void HandleReplyOverlayClick(Point point)
        {
            var send = GetReplySendRect(PillWidth, Height);
            if (send.Contains(point))
                SendReply();
        }

        private Rectangle GetReplySendRect(int w, int h)
        {
            const int sendSize = 30;
            return new Rectangle(w - 16 - sendSize, h - 16 - sendSize, sendSize, sendSize);
        }

        // -1 = legacy "always show both waves"; 0..3 = level-driven wave count
        // (0 with muted handled by the slash path).
        private static void DrawSpeakerIcon(
            Graphics g, Brush brush, int centerX, int centerY, bool muted,
            float scale = 1f, int waveCount = -1)
        {
            using var pen = new Pen(brush, 1.8F * scale)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            PointF[] speaker =
            {
                new(centerX - 9 * scale, centerY - 4 * scale),
                new(centerX - 5 * scale, centerY - 4 * scale),
                new(centerX + 1 * scale, centerY - 9 * scale),
                new(centerX + 1 * scale, centerY + 9 * scale),
                new(centerX - 5 * scale, centerY + 4 * scale),
                new(centerX - 9 * scale, centerY + 4 * scale)
            };
            g.FillPolygon(brush, speaker);

            if (muted || waveCount == 0)
            {
                if (muted)
                {
                    using var slashPen = new Pen(brush, 2.2f * scale)
                    {
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round
                    };
                    g.DrawLine(slashPen, centerX + 5 * scale, centerY - 8 * scale,
                        centerX + 15 * scale, centerY + 8 * scale);
                }
                return;
            }

            int waves = waveCount < 0 ? 2 : Math.Min(waveCount, 3);
            g.DrawArc(pen, centerX - 2 * scale, centerY - 5.5f * scale, 9 * scale, 11 * scale, -58, 116);
            if (waves >= 2)
                g.DrawArc(pen, centerX - 2 * scale, centerY - 9 * scale, 15 * scale, 18 * scale, -56, 112);
            if (waves >= 3)
                g.DrawArc(pen, centerX - 2 * scale, centerY - 12.5f * scale, 21.5f * scale, 25 * scale, -54, 108);
        }

        // Sun glyph for the brightness HUD - same pen vocabulary as the tiny
        // transient icon, sized up to sit beside the slider.
        private static void DrawHudSunIcon(Graphics g, Brush brush, int centerX, int centerY, float scale)
        {
            using var pen = new Pen(brush, 1.9F * scale)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            float core = 5.5f * scale;
            g.FillEllipse(brush, centerX - core, centerY - core, core * 2, core * 2);
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                float r0 = 9f * scale, r1 = 13.5f * scale;
                g.DrawLine(pen,
                    centerX + (float)Math.Cos(a) * r0, centerY + (float)Math.Sin(a) * r0,
                    centerX + (float)Math.Cos(a) * r1, centerY + (float)Math.Sin(a) * r1);
            }
        }

        private static void DrawPreviousIcon(Graphics g, Brush brush, int centerX, int centerY)
        {
            g.FillPolygon(brush, new PointF[]
            {
                new(centerX, centerY - 8.5f),
                new(centerX - 9, centerY),
                new(centerX, centerY + 8.5f)
            });
            g.FillPolygon(brush, new PointF[]
            {
                new(centerX + 9, centerY - 8.5f),
                new(centerX, centerY),
                new(centerX + 9, centerY + 8.5f)
            });
        }

        private static void DrawNextIcon(Graphics g, Brush brush, int centerX, int centerY)
        {
            g.FillPolygon(brush, new PointF[]
            {
                new(centerX - 9, centerY - 8.5f),
                new(centerX, centerY),
                new(centerX - 9, centerY + 8.5f)
            });
            g.FillPolygon(brush, new PointF[]
            {
                new(centerX, centerY - 8.5f),
                new(centerX + 9, centerY),
                new(centerX, centerY + 8.5f)
            });
        }

        // Two crossing arrows glyph — standard shuffle iconography, replacing
        // the old mute/speaker button now that volume has its own strip.
        private static void DrawShuffleIcon(Graphics g, Brush brush, int centerX, int centerY)
        {
            using var pen = new Pen(brush, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

            int left = centerX - 9, right = centerX + 9, top = centerY - 6, bottom = centerY + 6;

            using (var p1 = new GraphicsPath())
            {
                p1.AddBezier(left, top, centerX - 2, top, centerX + 2, bottom, right, bottom);
                g.DrawPath(pen, p1);
            }
            using (var p2 = new GraphicsPath())
            {
                p2.AddBezier(left, bottom, centerX - 2, bottom, centerX + 2, top, right, top);
                g.DrawPath(pen, p2);
            }

            g.FillPolygon(brush, new[]
            {
                new Point(right - 5, top - 3), new Point(right + 2, top), new Point(right - 5, top + 3)
            });
            g.FillPolygon(brush, new[]
            {
                new Point(right - 5, bottom - 3), new Point(right + 2, bottom), new Point(right - 5, bottom + 3)
            });
        }

        private static void DrawPlayPauseIcon(
            Graphics g, Brush brush, int centerX, int centerY, bool playing)
        {
            if (!playing)
            {
                g.FillPolygon(brush, new[]
                {
                    new Point(centerX - 5, centerY - 8),
                    new Point(centerX + 7, centerY),
                    new Point(centerX - 5, centerY + 8)
                });
                return;
            }

            g.FillRectangle(brush, centerX - 6, centerY - 8, 4, 16);
            g.FillRectangle(brush, centerX + 3, centerY - 8, 4, 16);
        }

        private TimeSpan GetCurrentPosition()
        {
            if (!isPlaying)
                return basePosition;

            TimeSpan elapsed = DateTime.Now - basePositionStamp;
            TimeSpan position = basePosition + elapsed;
            return trackDuration > TimeSpan.Zero && position > trackDuration
                ? trackDuration
                : position;
        }

        private static string FormatTime(TimeSpan value)
        {
            if (value < TimeSpan.Zero) value = TimeSpan.Zero;
            return value.TotalHours >= 1
                ? value.ToString(@"h\:mm\:ss")
                : value.ToString(@"m\:ss");
        }

        private static void FillRoundedRectangle(Graphics g, Brush brush,
            Rectangle rectangle, int radius)
        {
            using var path = RoundedRectangle(rectangle, radius);
            g.FillPath(brush, path);
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            // Optional: Initialization code on load
        }
    }
}
