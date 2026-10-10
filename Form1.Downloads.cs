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
    // one card anyway). Changed fires from worker threads - UI parts marshal.
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
        // (no network, no disk) - wired to a link in Settings.
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
        private readonly Size downloadSize = new(478, 172);   // E lives at the music card's size
        private Rectangle dlPauseRect, dlCancelRect;
        private bool dlExternal;   // watched, not owned - no Pause/Cancel claims
        private IslandDownload? dlAnnounced;   // popups fire once per phase,
        private string dlAnnouncedPhase = "";  // never a second time for the same one
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

            // One popup per phase, exactly like every other island activity:
            // it expands, shows the state, and collapses on its own after a
            // beat - running transfers stay reachable as the temporary 4th
            // page (wheel or dots) for as long as they live.
            bool unseen = !ReferenceEquals(dlAnnounced, d) || dlAnnouncedPhase != d.Phase;
            if (unseen)
            {
                dlAnnounced = d;
                dlAnnouncedPhase = d.Phase;
                transientKind = TransientKind.Download;
                transientTitle = d.Title;
                transientDetail = "";
                holding = true;
                holdUntil = DateTime.Now.AddMilliseconds(d.Active ? 3600 : 2800);
                collapseAt = null;
            }

            // The page evaporates with the transfer - if it was the page on
            // screen, land back on music.
            bool pageGone = !d.Active && pageIndex >= PageCount();
            if (pageGone)
            {
                pageIndex = 0;
                activeMode = PageAt(0);
                modeSwitchAt = DateTime.Now;
            }
            // Progress pings while no download UI is on screen must not drag
            // the whole island into extra repaints (“laggy” report): repaint
            // only when the popup is up or the progress page is what the user
            // is looking at (or a page just vanished with them on it).
            bool visible = unseen || pageGone || DownloadHudActive ||
                (CardShowing && activeMode == IslandMode.Download);
            if (visible) RefreshVisual();
        }

        // Style E - the iOS Dynamic Island download activity, at the music
        // card's exact footprint (478x172, 28px corners). Blue ring hugging a
        // squircle file tile, gray label rows, one Cancel pill + pause circle.
        private static readonly Color IosBlue = Color.FromArgb(10, 132, 255);
        private static readonly Color IosSurface = Color.FromArgb(44, 44, 46);
        private static readonly Color IosLabel = Color.FromArgb(142, 142, 147);
        private static readonly Color IosName = Color.FromArgb(242, 242, 247);

        private void GetDownloadButtonRects(int w, int h)
        {
            int y = h - 50;
            bool hideOnly = dlExternal || !(IslandDownloads.Current?.Active ?? false);
            if (hideOnly)
                dlCancelRect = new Rectangle(18, y, w - 36, 36);
            else
            {
                dlCancelRect = new Rectangle(18, y, w - 18 - 36 - 10 - 18, 36);
                dlPauseRect = new Rectangle(w - 18 - 36, y, 36, 36);
            }
        }

        private void DrawDownloadCard(Graphics g)
        {
            var d = IslandDownloads.Current;
            if (d == null) return;
            int w = PillWidth, h = Height;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // ring + tile cluster
            float cxr = 51.5f, cyr = h / 2f - 18f, ring = 27f;
            using (var track = new Pen(Color.FromArgb(58, 58, 60), 5f))
                g.DrawEllipse(track, cxr - ring, cyr - ring, ring * 2, ring * 2);

            float pct = d.Finished ? 1f : d.Pct;
            if (d.Finished || (d.Total > 0 && pct > 0.004f))
            {
                using var arcP = new Pen(IosBlue, 5f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round };
                if (pct >= 0.999f) g.DrawEllipse(arcP, cxr - ring, cyr - ring, ring * 2, ring * 2);
                else g.DrawArc(arcP, cxr - ring, cyr - ring, ring * 2, ring * 2, -90f, pct * 360f);
            }
            else if (d.Active)
            {
                // total not settled yet: calm full circle, never a spinner
                using var dim = new Pen(Color.FromArgb(90, 10, 132, 255), 5f);
                g.DrawEllipse(dim, cxr - ring, cyr - ring, ring * 2, ring * 2);
            }

            var tile = new Rectangle((int)(cxr - 20), (int)(cyr - 20), 40, 40);
            using (var tp = RoundedRectangle(tile, 11))
            using (var tb = new SolidBrush(Color.FromArgb(35, 35, 38)))
                g.FillPath(tb, tp);
            if (d.Finished)
                DrawTextAt(g, "\u2713", 13F, FontStyle.Bold, IosBlue, tile, true);
            else if (d.Cancelled)
                DrawTextAt(g, "\u2715", 12F, FontStyle.Regular, IosLabel, tile, true);
            else
                using (var pen = new Pen(IosName, 2.2f)
                    { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    float gx = tile.X + 20, gy = tile.Y + 20;
                    g.DrawLine(pen, gx, gy - 9, gx, gy + 2);
                    g.DrawLines(pen, new[]
                    {
                        new PointF(gx - 4.5f, gy - 2.5f), new PointF(gx, gy + 3f),
                        new PointF(gx + 4.5f, gy - 2.5f),
                    });
                    g.DrawLine(pen, gx - 8, gy + 8, gx + 8, gy + 8);
                }

            // text column
            int tx = 96, maxw = Math.Max(80, w - tx - 96);
            DrawTextAt(g, Fit(g, d.Title, 11F, FontStyle.Bold, maxw),
                11F, FontStyle.Bold, IosName,
                new Rectangle(tx, h / 2 - 55, maxw, 20), false);
            DrawTextAt(g, Fit(g, BuildMeta(d), 8.4F, FontStyle.Regular, maxw),
                8.4F, FontStyle.Regular, IosLabel,
                new Rectangle(tx, h / 2 - 33, maxw, 15), false);

            // right-side readout: one run of text, box pulled clear of the
            // corner radius and the dot band (the clipped '10%' report)
            var readout = new Rectangle(w - 178, h / 2 - 58, 152, 30);
            if (d.Finished)
                DrawTextAt(g, "Done", 15F, FontStyle.Bold, IosBlue, readout, false, true);
            else if (d.Cancelled)
                DrawTextAt(g, "\u2715", 15F, FontStyle.Regular, IosLabel, readout, false, true);
            else
                DrawTextAt(g, d.Total > 0 ? $"{(int)(pct * 100)}%" : d.Human(d.Done),
                    d.Total > 0 ? 16.5F : 13F, FontStyle.Bold, IosName, readout, false, true);

            // controls
            dlExternal = d.WatchPath.Length > 0;
            GetDownloadButtonRects(w, h);
            bool live = d.Active && !dlExternal;
            using (var pb = new SolidBrush(IosSurface))
                using (var p = RoundedRectangle(dlCancelRect, 18))
                    g.FillPath(pb, p);
            DrawTextAt(g, dlExternal ? "Hide"
                : d.Active ? "Cancel" : "Dismiss",
                10.5F, FontStyle.Regular, IosName, dlCancelRect, true);
            if (live || (dlPauseRect.Width == 36 && d.Active && dlExternal == false))
            {
                using var cb = new SolidBrush(IosSurface);
                g.FillEllipse(cb, dlPauseRect);
                using var bars = new SolidBrush(IosName);
                float bx = dlPauseRect.X + 18, by = dlPauseRect.Y + 18;
                if (d.Paused)
                    using (var tri = new SolidBrush(IosName))
                        g.FillPolygon(tri, new[]
                        {
                            new PointF(bx - 4, by - 6), new PointF(bx - 4, by + 6),
                            new PointF(bx + 6, by),
                        });
                else
                {
                    g.FillRectangle(bars, bx - 5.5f, by - 6, 3.4f, 12);
                    g.FillRectangle(bars, bx + 1.5f, by - 6, 3.4f, 12);
                }
            }
        }

        private static string BuildMeta(IslandDownload d)
        {
            if (d.Finished) return "Saved to your Downloads folder.";
            if (d.Cancelled) return "Cancelled.";
            var parts = new List<string>();
            if (d.Paused) parts.Add("Paused");
            double bps = d.BytesPerSec;
            if (bps > 1024) parts.Add($"{bps / 1024 / 1024:0.#} MB/s");
            else parts.Add($"{bps / 1024:0} KB/s");
            if (d.Total > 0 && !d.Paused)
            {
                double rem = (d.Total - d.Done) / Math.Max(1024, bps);
                parts.Add(rem < 90 ? $"{Math.Max(1, (int)rem)} sec left" : $"{rem / 60:0.#} min left");
            }
            parts.Add(d.Total > 0 ? d.Human(d.Total) : $"{d.Human(d.Done)} so far");
            return string.Join("  \u00b7  ", parts);
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

        private bool DownloadCardClick(Point p)
        {
            var d = IslandDownloads.Current;
            if (d == null) { holding = false; return true; }
            if (!dlExternal && d.Active && dlPauseRect.Contains(p))
            {
                d.Paused = !d.Paused;
                IslandDownloads.Raise();
                return true;
            }
            if (!dlExternal && d.Active && dlCancelRect.Contains(p))
            {
                d.Cancel();
                holding = false;
                return true;
            }
            holding = false;
            return true;
        }

        // The 4th page is open like any other page: taps do nothing except
        // the two controls (own transfers) - everything else belongs to the
        // normal card click path (page flips, collapse).
        private bool DownloadPageClick(Point p)
        {
            var d = IslandDownloads.Current;
            if (d == null) { SetPage(0); return true; }
            if (!d.Active) return false;
            DrawDownloadCardProbe(d);   // ensures rects/external flag are current
            if (dlExternal) return false;
            if (dlPauseRect.Contains(p))
            {
                d.Paused = !d.Paused;
                IslandDownloads.Raise();
                return true;
            }
            if (dlCancelRect.Contains(p))
            {
                d.Cancel();
                return true;
            }
            return false;
        }

        // Button rects come out of the last paint; refresh them for hit tests
        // that race the animation.
        private void DrawDownloadCardProbe(IslandDownload d)
        {
            dlExternal = d.WatchPath.Length > 0;
            GetDownloadButtonRects(PillWidth, Height);
        }
    }

}
