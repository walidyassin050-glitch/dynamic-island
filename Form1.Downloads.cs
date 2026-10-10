using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace MyDynamicIsland
{
    // One running (or freshly finished) file transfer.
    internal sealed class IslandDownload
    {
        public string Title = "download";
        public string DestPath = "";
        public long Total;                       // -1 when the server hides it
        private long _done;
        public long Done => Interlocked.Read(ref _done);
        public void Add(long n) => Interlocked.Add(ref _done, n);
        public void Seed(long v) => Interlocked.Exchange(ref _done, v);

        // Identity of a *phase*: dismiss one and the island stays out of the
        // way until something genuinely new happens (pause flips, finishes…)
        public string Phase =>
            Title + "|" + (Finished ? "f" : Cancelled ? "c"
                : Active ? (Paused ? "p" : "a") : "d");

        public volatile bool Paused;
        public volatile bool Active = true;      // transferring (or waiting, paused)
        public bool Finished, Cancelled;
        public string WatchPath = "";   // set for browser-watched transfers
        public readonly Stopwatch Sw = Stopwatch.StartNew();
        public readonly CancellationTokenSource Cts = new();

        public double Seconds => Sw.Elapsed.TotalSeconds;
        public float Pct => Total > 0 ? Math.Clamp((float)(Done / (double)Total), 0f, 1f) : 0f;
        public double BytesPerSec => Seconds > 0.4 ? Done / Seconds : 0;

        public string Human(long bytes)
        {
            double b = bytes; string[] u = { "B", "KB", "MB", "GB" };
            int i = 0;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            return $"{b:0.#} {u[i]}";
        }

        public string StatusLine
        {
            get
            {
                if (Cancelled) return "Cancelled.";
                if (Finished) return "Done — saved to your Downloads folder.";
                if (Paused) return $"{Human(Done)} of {Human(Math.Max(Done, Total))} · paused";
                string speed = BytesPerSec > 1024 ? $"{BytesPerSec / 1024 / 1024:0.0} MB/s"
                                                  : $"{BytesPerSec / 1024:0} KB/s";
                string left = "";
                if (Total > 0)
                {
                    double rem = (Total - Done) / Math.Max(1024, BytesPerSec);
                    left = rem < 90 ? $" · {Math.Max(1, (int)rem)}s left"
                                    : $" · {rem / 60:0.#} min left";
                }
                return Total > 0
                    ? $"{Human(Done)} of {Human(Total)} · {speed}{left}"
                    : $"{Human(Done)} · {speed}";
            }
        }

        public void Cancel()
        {
            if (!Active) return;
            Cancelled = true; Active = false;
            try { Cts.Cancel(); } catch { }
        }
    }

    // Tiny global transfer registry: exactly one at a time (the island shows
    // one card anyway). Changed fires from worker threads — UI parts marshal.
    internal static class IslandDownloads
    {
        public static IslandDownload? Current { get; private set; }
        public static event Action? Changed;
        public static IslandDownload? Running => Current is { Active: true } c ? c : null;

        public static void Raise() => Changed?.Invoke();

        private static readonly HttpClient Http = new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
        }) { Timeout = TimeSpan.FromMinutes(10) };

        // Adoption entry point for the browser watcher: an already-running
        // transfer appears on the island without us owning its bytes.
        public static void Track(IslandDownload d)
        {
            if (Current != null && ReferenceEquals(Current, d)) return;
            Current?.Cancel();
            Current = d;
            Raise();
        }

        static System.Threading.Timer? demoTimer;

        // Self-explanatory demo of the page with a simulated 96 MB transfer
        // (no network, no disk) — wired to a link in Settings.
        public static void StartDemo()
        {
            StopDemo();
            var d = new IslandDownload { Title = "demo-installer.exe", Total = 96L * 1024 * 1024 };
            Current = d;
            Raise();
            var rnd = new Random();
            demoTimer = new System.Threading.Timer(_ =>
            {
                if (!d.Active || d.Cancelled) { StopDemo(); return; }
                if (!d.Paused)
                {
                    long add = (long)(6.5 * 1024 * 1024 * 0.2 * (0.6 + rnd.NextDouble() * 0.8));
                    if (d.Done + add >= d.Total)
                    {
                        d.Seed(d.Total); d.Finished = true; d.Active = false;
                        Raise(); StopDemo(); return;
                    }
                    d.Add(add);
                }
                Raise();
            }, null, 220, 220);
        }

        public static void StopDemo()
        {
            demoTimer?.Dispose();
            demoTimer = null;
        }

        public static void Start(string url, string destPath, string title)
        {
            Current?.Cancel();
            var d = new IslandDownload { DestPath = destPath, Title = title, Total = -1 };
            Current = d;
            Raise();
            Task.Run(async () =>
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.UserAgent.ParseAdd("MyDynamicIsland");
                    using var res = await Http.SendAsync(req,
                        HttpCompletionOption.ResponseHeadersRead, d.Cts.Token);
                    res.EnsureSuccessStatusCode();
                    d.Total = res.Content.Headers.ContentLength ?? -1;

                    using var src = await res.Content.ReadAsStreamAsync(d.Cts.Token);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    using var fs = new FileStream(destPath, FileMode.Create,
                        FileAccess.Write, FileShare.Read, 81920);
                    var buf = new byte[81920];
                    long lastRaise = 0;
                    while (true)
                    {
                        while (d.Paused && !d.Cancelled)
                            await Task.Delay(150, d.Cts.Token);
                        int n = await src.ReadAsync(buf, d.Cts.Token);
                        if (n <= 0) break;
                        await fs.WriteAsync(buf.AsMemory(0, n), d.Cts.Token);
                        d.Add(n);
                        if (d.Sw.ElapsedMilliseconds - lastRaise > 450)
                        { lastRaise = d.Sw.ElapsedMilliseconds; Raise(); }
                    }
                    d.Finished = true;
                }
                catch (OperationCanceledException) { d.Cancelled = true; }
                catch { d.Cancelled = true; }
                finally { d.Active = false; Raise(); }
            });
        }

        // Returns a release object when GitHub carries something newer than
        // the running assembly, null when up to date / unreachable.
        public static async Task<(string url, string file, string tag)?> CheckGitHubUpdateAsync(
            string repo)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.github.com/repos/{repo}/releases/latest");
                req.Headers.UserAgent.ParseAdd("MyDynamicIsland");
                using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if (!res.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                var current = typeof(Form1).Assembly.GetName().Version ?? new Version(1, 0);
                var wanted = Version.TryParse(tag.TrimStart('v', 'V'), out var v) ? v : new Version(0, 0);
                if (wanted <= current) return null;
                if (root.TryGetProperty("assets", out var assets))
                    foreach (var a in assets.EnumerateArray())
                    {
                        string name = a.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                        string url = a.TryGetProperty("browser_download_url", out var u)
                            ? u.GetString() ?? "" : "";
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && url.Length > 0)
                            return (url, name, tag);
                    }
                return null;
            }
            catch { return null; }
        }
    }

    public partial class Form1
    {
        private readonly Size downloadSize = new(418, 151);
        private Rectangle dlPauseRect, dlCancelRect;
        private bool dlExternal;   // watched, not owned — no Pause/Cancel claims
        private string downloadDismissedPhase = "";
        private bool downloadWired;

        private bool DownloadHudActive =>
            holding && transientKind == TransientKind.Download;

        private void WireDownloads()
        {
            if (downloadWired) return;
            downloadWired = true;
            IslandDownloads.Changed += () =>
            {
                try { BeginInvoke(new Action(OnDownloadsChanged)); } catch { }
            };
        }

        private void OnDownloadsChanged()
        {
            var d = IslandDownloads.Current;
            if (d == null) return;

            // The card keeps itself up while the transfer lives; a manual
            // dismissal is respected FOR THAT PHASE — progress pings of the
            // same running transfer never re-pop it, but pausing, resuming,
            // finishing or a brand-new download each show themselves once.
            bool dismissed = downloadDismissedPhase == d.Phase;
            if (d.Active && !dismissed)
            {
                transientKind = TransientKind.Download;
                transientTitle = d.Title;
                transientDetail = "";
                holding = true;
                holdUntil = DateTime.Now.AddMinutes(30);
                collapseAt = null;
            }
            else if (!d.Active && !dismissed)
            {
                // finishing beat: show the result briefly, then it's gone
                transientKind = TransientKind.Download;
                transientTitle = d.Title;
                holding = true;
                holdUntil = DateTime.Now.AddSeconds(3.4);
            }
            else if (!d.Active && dismissed && DownloadHudActive) { holding = false; }
            RefreshVisual();
        }

        private void GetDownloadButtonRects(int w, int h)
        {
            dlPauseRect = new Rectangle(w - 14 - 78 - 10 - 84, h - 44, 84, 30);
            dlCancelRect = new Rectangle(w - 14 - 78, h - 44, 78, 30);
        }

        private void DrawDownloadCard(Graphics g)
        {
            var d = IslandDownloads.Current;
            if (d == null) return;
            int w = PillWidth, h = Height;

            // ring geometry: 64 diameter, 7 stroke, 15pt percent — the lab values
            const float ring = 32f, stroke = 7f;
            float ccx = 16 + ring + 8, ccy = h / 2f - 12;

            using (var track = new Pen(Color.FromArgb(40, 255, 255, 255), stroke))
            {
                track.StartCap = LineCap.Round; track.EndCap = LineCap.Round;
                g.DrawEllipse(track, ccx - ring, ccy - ring, ring * 2, ring * 2);
            }

            bool timed = d.Total > 0;   // browsers hide the total — then the ring just spins
            float pct = d.Finished ? 1f : (timed ? d.Pct : 0f);
            var boxR = new Rectangle((int)(ccx - ring), (int)(ccy - ring), (int)(ring * 2), (int)(ring * 2));
            if (!d.Cancelled)
            {
                using var grad = new LinearGradientBrush(boxR,
                    Color.FromArgb(127, 212, 255), Color.FromArgb(180, 140, 255), 45f);
                using var arc = new Pen(grad, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                if (d.Finished || (timed && pct >= 0.999f))
                    g.DrawEllipse(arc, boxR.X, boxR.Y, boxR.Width, boxR.Height);
                else if (timed && pct > 0)
                    g.DrawArc(arc, boxR.X, boxR.Y, boxR.Width, boxR.Height, -90f, pct * 360f);
                else if (!timed && d.Active)
                    g.DrawArc(arc, boxR.X, boxR.Y, boxR.Width, boxR.Height,
                        (float)(Environment.TickCount64 % 1400) / 1400f * 360f, 130f);
            }

            string bigText = d.Finished ? "✓" : d.Cancelled ? "✕"
                : timed ? $"{(int)(pct * 100)}%" : d.Human(d.Done);
            DrawTextAt(g, bigText, (timed || d.Finished || d.Cancelled) ? 15F : 8.2F,
                FontStyle.Bold, Color.White,
                new Rectangle((int)(ccx - ring), (int)(ccy - 11), (int)(ring * 2), 22), true);

            // text block right of the ring. Fit() guarantees one clean line:
            // an old-school name like "Unconfirmed 4a7.crdownload" just ends
            // in a real ellipsis, it can never overflow or hard-clip.
            int tx = (int)(ccx + ring) + 26;
            int tw = Math.Max(80, w - tx - 18);
            string dlTitle = Fit(g, d.Title, 12.5F, FontStyle.Bold, tw);
            string dlStatus = Fit(g, d.StatusLine, 10F, FontStyle.Regular, tw);
            DrawTextAt(g, dlTitle, 12.5F, FontStyle.Bold, Color.FromArgb(238, 240, 246),
                new Rectangle(tx, h / 2 - 44, tw, 20), false);
            DrawTextAt(g, dlStatus, 10F, FontStyle.Regular, Color.FromArgb(154, 160, 176),
                new Rectangle(tx, h / 2 - 22, tw, 16), false);

            // belt-and-braces linear bar under the detail row (browser style)
            int barW = Math.Min(tw, 300);
            using (var bt = new SolidBrush(Color.FromArgb(28, 255, 255, 255)))
                g.FillRectangle(bt, tx, h / 2f + 2, barW, 5);
            using (var bf = new SolidBrush(Color.FromArgb(127, 210, 255)))
                g.FillRectangle(bf, tx, h / 2f + 2, barW * pct, 5);

            // A browser's own download is only observed here — the card
            // says Hide, never Pause/Cancel, so it can't lie about control.
            dlExternal = d.WatchPath.Length > 0;
            GetDownloadButtonRects(w, h);
            if (dlExternal)
            {
                DrawDownloadButton(g, dlCancelRect, "Hide", true);
                return;
            }

            // Pause / Cancel — Pause dims out once the transfer is over
            bool live = d.Active;
            DrawDownloadButton(g, dlPauseRect, live
                ? (d.Paused ? "Resume" : "Pause") : "Close", live);
            DrawDownloadButton(g, dlCancelRect, live ? "Cancel" : "Hide", live);
        }

        // Measure-and-trim: the card text always fits the column it owns.
        private static string Fit(Graphics g, string text, float size, FontStyle style, int maxPx)
        {
            var fnt = CachedFont("Segoe UI", size, style);
            if (TextRenderer.MeasureText(g, text, fnt, new Size(int.MaxValue, 20),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width <= maxPx)
                return text;
            string cut = text;
            while (cut.Length > 1 && TextRenderer.MeasureText(g, cut + "…", fnt,
                    new Size(int.MaxValue, 20), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width > maxPx)
                cut = cut[..^1];
            return cut.Length > 0 && cut != text ? cut.TrimEnd() + "…" : cut;
        }

        private void DrawDownloadButton(Graphics g, Rectangle r, string text, bool enabled)
        {
            using var bg = new SolidBrush(enabled
                ? Color.FromArgb(35, 39, 51) : Color.FromArgb(26, 29, 38));
            using var path = RoundedRectangle(r, 9);
            g.FillPath(bg, path);
            DrawTextAt(g, text, 10.5F, FontStyle.Regular,
                enabled ? Color.FromArgb(223, 227, 238) : Color.FromArgb(110, 116, 130),
                r, true);
        }

        // Resting-pill chip: a mini progress ring just left of the waveform,
        // visible without expanding anything while a transfer is live.
        private void DrawDownloadChip(Graphics g)
        {
            var d = IslandDownloads.Running;
            if (d == null || clicked || holding) return;
            float r = 8.5f;
            float x = PillWidth - 86, y = Height / 2f;
            using (var t = new Pen(Color.FromArgb(45, 255, 255, 255), 2.4f))
                g.DrawEllipse(t, x - r, y - r, r * 2, r * 2);
            using (var p = new Pen(Color.FromArgb(127, 212, 255), 2.4f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (d.Total > 0)
                    g.DrawArc(p, x - r, y - r, r * 2, r * 2, -90f, d.Pct * 360f);
                else
                    g.DrawArc(p, x - r, y - r, r * 2, r * 2,
                        (float)(DateTime.Now.Second % 2 * 180), 140);
            }
        }

        private bool DownloadCardClick(Point p)
        {
            var d = IslandDownloads.Current;
            if (d == null) { holding = false; return true; }
            if (dlExternal)
            {
                downloadDismissedPhase = d.Phase;   // watcher keeps this phase while
                holding = false;                    // it runs — match means stay hidden
                return true;
            }
            if (d.Active && dlPauseRect.Contains(p))
            {
                d.Paused = !d.Paused;
                IslandDownloads.Raise();
                return true;
            }
            if (d.Active && dlCancelRect.Contains(p))
            {
                d.Cancel();
                holding = false;
                return true;
            }
            // anywhere else: dismiss for now (transfer keeps running)
            downloadDismissedPhase = d.Phase;
            holding = false;
            return true;
        }
    }

}
