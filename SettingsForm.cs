using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MyDynamicIsland
{
    // Settings drawn in the island's own language: a borderless slab of the
    // same dark glass, rounded corners, a header you can drag (it carries a
    // live mini-notch), and options grouped in section cards — animated pill
    // switches instead of checkboxes, inset text wells instead of bare
    // boxes. Same constructor / Collect() contract as the old dialog, so
    // Program.cs and Form1 needed no changes.
    internal sealed class SettingsForm : Form
    {
        // ── island palette, tuned toward the reference's navy-glass depth ──
        static readonly Color GlassBack = Color.FromArgb(13, 14, 20);
        static readonly Color CardBack = Color.FromArgb(27, 28, 36);
        static readonly Color Well = Color.FromArgb(16, 17, 23);
        static readonly Color InkWhite = Color.FromArgb(240, 241, 248);
        static readonly Color InkGray = Color.FromArgb(152, 154, 168);
        static readonly Color InkFaint = Color.FromArgb(106, 108, 122);
        static readonly Color CardEdge = Color.FromArgb(30, 255, 255, 255);
        static readonly Color DividerLine = Color.FromArgb(47, 48, 55);
        static readonly Color Accent = Color.FromArgb(104, 182, 211);
        static readonly Color GoodGreen = Color.FromArgb(120, 190, 140);
        static readonly Color WarnAmber = Color.FromArgb(232, 190, 96);
        static readonly Color BadRed = Color.FromArgb(236, 128, 122);

        // icon-tile hues — the reference's iOS-settings colouring, kept in the
        // island's cooler end of the spectrum.
        static readonly Color TileTeal = Color.FromArgb(56, 128, 162);
        static readonly Color TileGreen = Color.FromArgb(58, 148, 92);
        static readonly Color TileRed = Color.FromArgb(176, 78, 72);
        static readonly Color TilePurple = Color.FromArgb(126, 100, 208);
        static readonly Color TileSlate = Color.FromArgb(96, 106, 128);
        static readonly Color TileBlue = Color.FromArgb(64, 112, 208);
        static readonly Color TileSky = Color.FromArgb(56, 140, 190);
        static readonly Color TileIndigo = Color.FromArgb(108, 98, 208);
        static readonly Color TileAmber = Color.FromArgb(190, 134, 54);
        static readonly Color TileViolet = Color.FromArgb(148, 84, 208);

        // header repainted its own Font objects on every WM_PAINT — cache them
        static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, float, FontStyle), Font>
            fonts = new();
        static Font CachedFont(string fam, float size, FontStyle style) =>
            fonts.GetOrAdd((fam, size, style), k => new Font(k.Item1, k.Item2, k.Item3));

        // ── values live in the switches / text wells ────────────────────
        ToggleSwitch mediaSw = new(), batterySw = new(), lowBatSw = new(),
            welcomeSw = new(), notifSw = new(), networkSw = new(), deviceSw = new(),
            brightnessSw = new(), autoHideSw = new(), allowLocSw = new();
        TextBox askUrlBox = new(), weatherCityBox = new(), autoHideSecsBox = new(),
            spotifyIdBox = new(), spotifyRedirectBox = new();
        Label spotifyStatusValue = null!;

        const int CardW = 436 - 40;   // form width minus 20px margins each side
        const int RowH = 46;          // a caption-only cell (reference: 44-50)
        const int RowH2 = 62;         // caption + hint line

        const int IconX = 14, IconW = 28, TextX = 14 + 28 + 12;  // tile column, then words

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        [DllImport("user32.dll")]
        static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool show);
        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        public SettingsForm(AppSettings current, string notificationStatus, string spotifyStatus = "")
        {
            DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            BackColor = GlassBack;
            ClientSize = new Size(436, 944);
            Font = new Font("Segoe UI", 9.5F);

            // Win11 can round a borderless window natively; the region clip
            // below is the Win10-safe fallback (and harmless on Win11).
            try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
            Region = new Region(RoundedPath(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), 18));

            BuildHeader();
            BuildFooter();
            BuildContent(current, notificationStatus, spotifyStatus);

            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };
        }

        // ── header: fused mini-notch, then the centred title stack from the
        //    reference (big bold app name, quiet grey kicker) ─────────────
        void BuildHeader()
        {
            var head = new Panel { Bounds = new Rectangle(0, 0, ClientSize.Width, 104) };
            head.Paint += HeaderPaint;
            head.MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    ReleaseCapture();
                    _ = SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, (IntPtr)2 /*HTCAPTION*/, IntPtr.Zero);
                }
            };
            Controls.Add(head);
        }

        void HeaderPaint(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(GlassBack);

            int pw = 132, px = (ClientSize.Width - pw) / 2;
            var pill = new Rectangle(px, 0, pw, 36);
            using (var black = new SolidBrush(Color.FromArgb(3, 3, 4)))
            {
                // fused to the edge: the rounding is bottom-only, so a
                // squared strip closes the top corners of the path.
                using (var p = RoundedPath(new Rectangle(px, 0, pw, 36), 12))
                    g.FillPath(black, p);
                g.FillRectangle(black, px, 0, pw, 14);
            }

            var art = new Rectangle(pill.X + 12, 8, 16, 16);
            using (var ab = new SolidBrush(Accent))
            using (var ap = RoundedPath(art, 4)) g.FillPath(ab, ap);
            using (var ink = new SolidBrush(Color.FromArgb(215, 255, 255, 255)))
                g.FillRectangle(ink, art.Right + 8, 12, 46, 3);
            using (var dim = new SolidBrush(Color.FromArgb(110, 255, 255, 255)))
                g.FillRectangle(dim, art.Right + 8, 17, 26, 3);
            int bx = pill.Right - 24;
            int[] hh = { 6, 12, 4, 9 };
            for (int i = 0; i < hh.Length; i++)
            {
                using var bar = new SolidBrush(Color.FromArgb(150, 255, 255, 255));
                g.FillRectangle(bar, bx + i * 4, 19 - hh[i], 2, hh[i]);
            }

            // centred title stack — the reference's opening, island-scale
            var t = CachedFont("Segoe UI", 15F, FontStyle.Bold);
            TextRenderer.DrawText(g, "Dynamic Island", t,
                new Rectangle(0, 44, ClientSize.Width, 28), InkWhite,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            var sub = CachedFont("Segoe UI", 9F, FontStyle.Regular);
            TextRenderer.DrawText(g, "App settings — everything applies the moment you save", sub,
                new Rectangle(0, 76, ClientSize.Width, 18), InkGray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }

        static void Draw(Graphics g, string text, float size, FontStyle style, Color color, Rectangle rect) =>
            TextRenderer.DrawText(g, text, CachedFont("Segoe UI", size, style), rect, color,
                TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        // ── body ────────────────────────────────────────────────────────
        void BuildContent(AppSettings c, string notifStatus, string spotifyStatus)
        {
            var body = new SectionPanel
            {
                Bounds = new Rectangle(0, 104, ClientSize.Width, ClientSize.Height - 104 - 66),
            };
            Controls.Add(body);

            int y = 6;

            // the reference's opening promo card, voiced by the island
            var promo = AddCard(body, y);
            promo.AddPromo();
            y = promo.Bottom + 20;

            // ═══ ISLAND ═══
            y = SectionTitle(body, "Island", "what the notch itself does", y);
            var island = AddCard(body, y);
            mediaSw = island.AddRow("music", TileTeal, "Media — now playing, controls, queue",
                "GMTC sessions, album art, the draggable timeline.", c.MediaActivity);
            batterySw = island.AddRow("battery", TileGreen, "Battery charge on the Today pill",
                "Percentage + capsule collapsed, power popups on plug changes.", c.BatteryActivity);
            lowBatSw = island.AddRow("alert", TileRed, "Low-battery alerts", "", c.LowBatteryAlerts);
            welcomeSw = island.AddRow("sparkle", TilePurple, "Say hello when the island starts",
                "The iPhone-boot greeting at launch.", c.WelcomeGreeting);
            autoHideSw = island.AddRow("eyewash", TileSlate, "Auto-hide when idle",
                "Glides up to the screen edge; wake it with the top edge or any popup.",
                c.AutoHide);
            autoHideSecsBox = island.AddInline("Hide after", Math.Clamp(c.AutoHideSeconds, 1, 600).ToString(), 42, "seconds (1-600)");
            y = island.Bottom + 20;

            // ═══ SYSTEM EVENTS ═══
            y = SectionTitle(body, "System events", "popups that borrow the notch", y);
            var events = AddCard(body, y);
            bool notifBad = notifStatus.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                            notifStatus.Contains("failed", StringComparison.OrdinalIgnoreCase);
            notifSw = events.AddRow("bell", TileBlue, "Windows notifications → island", notifStatus,
                c.WindowsNotifications, notifBad ? WarnAmber : GoodGreen);
            networkSw = events.AddRow("wifi", TileSky, "Network connect / disconnect", "", c.NetworkChanges);
            deviceSw = events.AddRow("device", TileIndigo, "Device connect popups",
                "Buds, pads, displays — whatever plug event.", c.DeviceChanges);
            brightnessSw = events.AddRow("sun", TileAmber, "Brightness change popups", "", c.BrightnessChanges);
            var privacy = Link("Open Windows notification privacy settings");
            privacy.Location = new Point(body.Left + 24, events.Bottom + 6);
            privacy.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "explorer.exe", "ms-settings:privacy-notifications") { UseShellExecute = true });
                }
                catch { }
            };
            body.Controls.Add(privacy);
            y = privacy.Bottom + 16;

            // ═══ INTELLIGENCE ═══
            y = SectionTitle(body, "Intelligence", "the search page's two wires", y);
            var clever = AddCard(body, y);
            askUrlBox = clever.AddTextField("lens", TileViolet, "Ask-anything link — {query} becomes your words",
                string.IsNullOrWhiteSpace(c.AiAskUrl) ? new AppSettings().AiAskUrl : c.AiAskUrl, 8.5F, true);
            allowLocSw = clever.AddRow("pin", TileSlate, "Automatic location for weather",
                "Windows location first, then an HTTPS IP check. Off: only the typed city is ever used — and nothing automatic is stored.",
                c.AllowAutoLocation);
            weatherCityBox = clever.AddTextField("cloud", TileSky, "Typed city always wins (blank = automatic)",
                c.WeatherCity, 9.5F, false);
            y = clever.Bottom + 20;

            // ═══ SPOTIFY ═══
            y = SectionTitle(body, "Spotify — Playing Next", "the queue extension's account", y);
            var spotify = AddCard(body, y);
            spotify.AddNote("developer.spotify.com/dashboard → create app → register this exact " +
                            "redirect → paste the Key. Up-next is Premium-gated by Spotify.");
            spotifyIdBox = spotify.AddTextField("key", TileGreen, "Client ID", c.SpotifyClientId, 8.5F, true);
            spotifyRedirectBox = spotify.AddTextField("redirect", TileGreen, "Redirect URI",
                string.IsNullOrWhiteSpace(c.SpotifyRedirectUri) ? AppSettings.DefaultSpotifyRedirect : c.SpotifyRedirectUri,
                8F, true);
            spotifyStatusValue = spotify.AddStatusLine(
                string.IsNullOrWhiteSpace(spotifyStatus) ? c.SpotifyCredentialProblem() : spotifyStatus);
            var recheck = Link("Re-check credentials");
            recheck.Location = new Point(24, spotify.Bottom + 6);
            recheck.Click += (_, _) => Recheck();
            body.Controls.Add(recheck);
            body.ContentHeight = recheck.Bottom + 14;
            body.Invalidate();
        }

        int SectionTitle(SectionPanel body, string title, string sub, int y)
        {
            body.Controls.Add(new Label
            {
                Text = title, AutoSize = false, BackColor = GlassBack, ForeColor = InkWhite,
                Font = new Font("Segoe UI", 11.5F, FontStyle.Bold),
                Bounds = new Rectangle(24, y, CardW, 20),
            });
            body.Controls.Add(new Label
            {
                Text = sub, AutoSize = false, BackColor = GlassBack, ForeColor = InkFaint,
                Font = new Font("Segoe UI", 8.5F),
                Bounds = new Rectangle(24, y + 21, CardW, 14),
            });
            return y + 42;
        }

        Card AddCard(SectionPanel body, int y)
        {
            var card = new Card { Location = new Point(20, y), Width = CardW };
            body.Controls.Add(card);
            return card;
        }

        Button Link(string text)
        {
            var b = new Button
            {
                Text = text, AutoSize = false, Size = new Size(CardW - 8, 26),
                FlatStyle = FlatStyle.Flat, BackColor = Color.Transparent,
                ForeColor = Accent, Font = new Font("Segoe UI", 8.5F),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseDownBackColor = Color.Transparent;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(20, 104, 182, 211);
            return b;
        }

        void Recheck()
        {
            var draft = Collect();
            string why = draft.SpotifyClientId.Length == 0 ? "No client ID yet — or none needed."
                : draft.SpotifyCredentialProblem();
            if (why.Length == 0 && !draft.SpotifyCredentialsUsable()) why = "Client ID or redirect is not usable.";
            SetSpotifyStatus(why.Length != 0 ? why : "Usable. Open the island's Playing Next and press Connect.",
                why.Length != 0 ? WarnAmber : GoodGreen);
        }

        void SetSpotifyStatus(string text, Color color)
        {
            spotifyStatusValue.Text = text;
            spotifyStatusValue.ForeColor = color;
            spotifyStatusValue.Invalidate();
        }

        // ── footer ──────────────────────────────────────────────────────
        void BuildFooter()
        {
            var bar = new Panel
            {
                Bounds = new Rectangle(0, ClientSize.Height - 66, ClientSize.Width, 66),
                BackColor = GlassBack,
            };
            bar.Paint += (_, e) =>
            {
                using var pen = new Pen(Color.FromArgb(20, 255, 255, 255), 1f);
                e.Graphics.DrawLine(pen, 20, 0, ClientSize.Width - 20, 0);
            };

            var save = new Button
            {
                Text = "Save", Size = new Size(96, 34), DialogResult = DialogResult.OK,
                Location = new Point(ClientSize.Width - 20 - 96 - 92 - 10, 17),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(36, 36, 40), ForeColor = InkWhite,
            };
            save.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 88);
            save.Click += (_, _) =>
            {
                // Same gate as before, restyled: a half-typed Spotify pair
                // gets answered in the card above, not with a silent close.
                var draft = Collect();
                string why = draft.SpotifyClientId.Length > 0 ? draft.SpotifyCredentialProblem() : "";
                if (why.Length == 0 && draft.SpotifyClientId.Length > 0 && !draft.SpotifyCredentialsUsable())
                    why = "Client ID or redirect is not usable.";
                if (why.Length != 0)
                {
                    SetSpotifyStatus(why, BadRed);
                    DialogResult = DialogResult.None;
                    return;
                }
                SetSpotifyStatus("Saved.", GoodGreen);
            };

            var close = new Button
            {
                Text = "Close", Size = new Size(84, 34),
                Location = new Point(ClientSize.Width - 20 - 84, 17),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(22, 22, 26), ForeColor = InkGray,
            };
            close.FlatAppearance.BorderColor = Color.FromArgb(56, 56, 62);

            bar.Controls.Add(save);
            bar.Controls.Add(close);
            Controls.Add(bar);
            AcceptButton = save;
            CancelButton = close;
        }

        public AppSettings Collect() => new AppSettings
        {
            MediaActivity = mediaSw.Value,
            BatteryActivity = batterySw.Value,
            LowBatteryAlerts = lowBatSw.Value,
            WelcomeGreeting = welcomeSw.Value,
            WindowsNotifications = notifSw.Value,
            NetworkChanges = networkSw.Value,
            DeviceChanges = deviceSw.Value,
            BrightnessChanges = brightnessSw.Value,
            AutoHide = autoHideSw.Value,
            AutoHideSeconds = int.TryParse(autoHideSecsBox.Text.Trim(), out int secs)
                ? Math.Clamp(secs, 1, 600) : 5,
            AiAskUrl = string.IsNullOrWhiteSpace(askUrlBox.Text)
                ? new AppSettings().AiAskUrl : askUrlBox.Text.Trim(),
            WeatherCity = weatherCityBox.Text.Trim(),
            AllowAutoLocation = allowLocSw.Value,
            SpotifyClientId = spotifyIdBox.Text.Trim(),
            SpotifyRedirectUri = string.IsNullOrWhiteSpace(spotifyRedirectBox.Text)
                ? AppSettings.DefaultSpotifyRedirect : spotifyRedirectBox.Text.Trim(),
        };

        static GraphicsPath RoundedPath(Rectangle r, int rad)
        {
            int d = Math.Max(2, rad * 2);
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ── container that scrolls but shows no chrome of its own ───────
        sealed class SectionPanel : Panel
        {
            int contentHeight;
            public int ContentHeight
            {
                get => contentHeight;
                set { contentHeight = value; AutoScrollMinSize = new Size(0, Math.Max(value, Height)); Invalidate(); }
            }
            public SectionPanel()
            {
                DoubleBuffered = true;
                BackColor = GlassBack;
                AutoScroll = true;
            }
            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                HideBars();
            }
            protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); HideBars(); }
            void HideBars()
            {
                // The wheel still scrolls it; the classic bars would be the
                // one light-mode thing in a dark-glass window.
                ShowScrollBar(Handle, 0 /*SB_HORZ*/, false);
                ShowScrollBar(Handle, 1 /*SB_VERT*/, false);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (contentHeight <= Height) return;
                int track = Height - 8;
                int thumb = Math.Max(28, track * Height / contentHeight);
                float frac = -AutoScrollPosition.Y / Math.Max(1, contentHeight - Height);
                int y = 4 + (int)((track - thumb) * frac);
                using var b = new SolidBrush(Color.FromArgb(60, 255, 255, 255));
                using var p = RoundedPath(new Rectangle(Width - 8, y, 3, thumb), 2);
                e.Graphics.FillPath(b, p);
            }
        }

        // ── one group card; cells stack themselves downward ─────────────
        // Reference geometry: rounded 16px card, 28px tinted icon tile inset
        // 14, caption column starting after the tile, control flush right,
        // and a hairline divider inset to the text column.
        sealed class Card : Panel
        {
            int cursor = 12;
            int rows;

            public Card()
            {
                DoubleBuffered = true;
                // Solid parent colour: every pixel the rounded card body does
                // not cover is exactly the glass behind it, and nothing in
                // this window has to walk the parent chain to paint.
                BackColor = GlassBack;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var back = new SolidBrush(CardBack);
                using var path = RoundedPath(new Rectangle(0, 0, Width, Math.Max(Height, cursor + 8)), 16);
                g.FillPath(back, path);
                using var edge = new Pen(CardEdge, 1f);
                g.DrawPath(edge, path);
            }

            void Divider()
            {
                if (rows == 0) return;   // no rule above the first cell
                Controls.Add(new Panel
                {
                    BackColor = DividerLine,   // pre-blended 8% white on CardBack
                    Bounds = new Rectangle(TextX, cursor, Width - TextX - IconX, 1),
                });
            }

            void Finish(int advance)
            {
                cursor += advance;
                Height = cursor + 12;
                Invalidate();
            }

            public ToggleSwitch AddRow(string glyph, Color tile, string caption, string hint,
                bool initial, Color? hintColor = null)
            {
                bool two = hint.Length > 0;
                int h = two ? RowH2 : RowH;
                Divider();

                AddTile(glyph, tile, cursor, h);
                Controls.Add(new Label
                {
                    Text = caption, AutoSize = false, BackColor = CardBack, ForeColor = InkWhite,
                    Font = new Font("Segoe UI", 10F),
                    Bounds = new Rectangle(TextX, cursor + (two ? 10 : (h - 20) / 2), Width - TextX - 72, 20),
                });
                if (two)
                    Controls.Add(new Label
                    {
                        Text = hint, AutoSize = false, BackColor = CardBack,
                        ForeColor = hintColor ?? InkFaint, Font = new Font("Segoe UI", 8.5F),
                        Bounds = new Rectangle(TextX, cursor + 32, Width - TextX - 72, 24),
                    });
                var sw = new ToggleSwitch { Location = new Point(Width - 66, cursor + (h - 28) / 2), Value = initial };
                Controls.Add(sw);
                rows++;
                Finish(h);
                return sw;
            }

            void AddTile(string glyph, Color tile, int y, int rowH)
            {
                Controls.Add(new TilePanel(glyph, tile)
                {
                    Bounds = new Rectangle(IconX, y + (rowH - IconW) / 2, IconW, IconW),
                });
            }

            // the reference's opening banner, speaking for the island
            public void AddPromo()
            {
                var tile = new TilePanel("island", TileIndigo)
                {
                    Bounds = new Rectangle(IconX, 12, IconW, IconW),
                };
                Controls.Add(tile);
                Controls.Add(new Label
                {
                    Text = "Make the notch yours", AutoSize = false, BackColor = CardBack,
                    ForeColor = InkWhite, Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
                    Bounds = new Rectangle(TextX, 11, Width - TextX - 40, 20),
                });
                Controls.Add(new Label
                {
                    Text = "Every switch below applies the moment you save.", AutoSize = false,
                    BackColor = CardBack, ForeColor = InkGray, Font = new Font("Segoe UI", 8.5F),
                    Bounds = new Rectangle(TextX, 33, Width - TextX - 40, 16),
                });
                rows = 1;
                Finish(64 - 12);
            }

            // A slim inline field (the auto-hide seconds) hugging the right edge.
            public TextBox AddInline(string label, string value, int boxWidth, string after = "")
            {
                Divider();
                int y = cursor;
                Controls.Add(new Label
                {
                    Text = label, AutoSize = false, BackColor = CardBack, ForeColor = InkGray,
                    Font = new Font("Segoe UI", 8.5F),
                    Bounds = new Rectangle(TextX, y + 9, 150, 16),
                });
                int x = Width - 32 - boxWidth - (after.Length > 0 ? 96 : 0);
                var host = new Panel { BackColor = Well, Bounds = new Rectangle(x, y + 5, boxWidth, 24) };
                var box = new TextBox
                {
                    BorderStyle = BorderStyle.None, BackColor = Well, ForeColor = InkWhite,
                    Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), TextAlign = HorizontalAlignment.Center,
                    Dock = DockStyle.Fill, Margin = new Padding(4, 5, 4, 5), Text = value,
                };
                host.Controls.Add(box);
                Controls.Add(host);
                if (after.Length > 0)
                    Controls.Add(new Label
                    {
                        Text = after, AutoSize = false, BackColor = CardBack, ForeColor = InkFaint,
                        Font = new Font("Segoe UI", 8F),
                        Bounds = new Rectangle(host.Right + 8, y + 10, 92, 14),
                    });
                rows++;
                Finish(36);
                return box;
            }

            public void AddNote(string note)
            {
                int y = cursor;
                Controls.Add(new Label
                {
                    Text = note, AutoSize = false, BackColor = CardBack, ForeColor = InkFaint,
                    Font = new Font("Segoe UI", 8F), Bounds = new Rectangle(TextX, y, Width - TextX - IconX, 30),
                });
                Finish(38);
            }

            public TextBox AddTextField(string glyph, Color tile, string caption, string value,
                float font, bool mono)
            {
                Divider();
                int y = cursor;
                AddTile(glyph, tile, y, 44);
                Controls.Add(new Label
                {
                    Text = caption, AutoSize = false, BackColor = CardBack, ForeColor = InkGray,
                    Font = new Font("Segoe UI", 8.5F), Bounds = new Rectangle(TextX, y + 2, Width - TextX - IconX, 14),
                });
                var host = new Panel { BackColor = Well, Bounds = new Rectangle(TextX, y + 19, Width - TextX - IconX, 26) };
                var box = new TextBox
                {
                    BorderStyle = BorderStyle.None, BackColor = Well, ForeColor = InkWhite,
                    Font = new Font(mono ? "Consolas" : "Segoe UI", font),
                    Dock = DockStyle.Fill, Margin = new Padding(8, 5, 8, 5), Text = value,
                };
                host.Controls.Add(box);
                Controls.Add(host);
                rows++;
                Finish(54);
                return box;
            }

            public Label AddStatusLine(string text)
            {
                var l = new Label
                {
                    Text = text, AutoSize = false, BackColor = CardBack, ForeColor = InkGray,
                    Font = new Font("Segoe UI", 8.5F), Bounds = new Rectangle(TextX, cursor, Width - TextX - IconX, 30),
                };
                Controls.Add(l);
                Finish(36);
                return l;
            }
        }

        // ── a rounded icon tile with a hand-drawn white glyph ───────────
        sealed class TilePanel : Control
        {
            readonly string kind;
            readonly Color tint;
            public TilePanel(string kind, Color tint)
            {
                this.kind = kind; this.tint = tint;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer, true);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var bg = new SolidBrush(tint);
                using var path = RoundedPath(new Rectangle(0, 0, Width, Height), 8);
                g.FillPath(bg, path);
                Glyphs.Draw(g, kind, new Rectangle(5, 5, Width - 10, Height - 10));
            }
        }

        // monochrome 18px vocabulary — same line-drawing dialect as the island
        static class Glyphs
        {
            static readonly Color Paper = Color.FromArgb(27, 28, 36);

            public static void Draw(Graphics g, string kind, Rectangle a)
            {
                using var white = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
                using var pen = new Pen(Color.FromArgb(235, 255, 255, 255), 1.6f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                float cx = a.X + a.Width / 2f, cy = a.Y + a.Height / 2f;

                switch (kind)
                {
                    case "music":
                        g.FillEllipse(white, a.X + 2, a.Bottom - 6, 5, 4);
                        g.FillRectangle(white, a.X + 5.6f, a.Y + 2, 1.6f, a.Height - 7);
                        g.FillEllipse(white, a.Right - 7, a.Bottom - 5, 5, 4);
                        g.FillRectangle(white, a.Right - 1.6f, a.Y + 2, 1.6f, a.Height - 6);
                        g.FillRectangle(white, a.X + 5.6f, a.Y + 2, a.Width - 7, 2.4f);
                        break;
                    case "battery":
                        using (var body = RoundedPathF(new RectangleF(a.X + 1, cy - 4, a.Width - 5, 8), 2.5f))
                            g.DrawPath(pen, body);
                        g.FillRectangle(white, a.Right - 2.4f, cy - 1.6f, 2, 3.2f);
                        g.FillRectangle(white, a.X + 3, cy - 1.8f, (a.Width - 8) * 0.6f, 3.6f);
                        break;
                    case "alert":
                        g.FillPolygon(white, new[]
                        {
                            new PointF(cx, a.Y + 1), new PointF(a.Right - 1, a.Bottom - 2),
                            new PointF(a.X + 1, a.Bottom - 2),
                        });
                        using (var punch = new SolidBrush(Paper))
                        {
                            g.FillRectangle(punch, cx - .9f, a.Y + 6, 1.9f, 4.6f);
                            g.FillRectangle(punch, cx - .9f, a.Bottom - 4.4f, 1.9f, 1.9f);
                        }
                        break;
                    case "sparkle":
                        Star(g, white, cx, cy, 8, 2.6f);
                        Star(g, white, a.X + 1.5f, a.Bottom - 3, 3.4f, 1.2f);
                        break;
                    case "eyewash":
                        g.DrawArc(pen, a.X + 1, cy - 6, a.Width - 2, 12, 35, 110);
                        g.DrawArc(pen, a.X + 1, cy - 6, a.Width - 2, 12, 205, 110);
                        g.FillEllipse(white, cx - 2, cy - 2, 4, 4);
                        g.DrawLine(pen, a.X + .5f, a.Bottom - .5f, a.Right - .5f, a.Y + .5f);
                        break;
                    case "bell":
                        using (var dome = new GraphicsPath())
                        {
                            dome.AddBezier(cx - 5.5f, cy + 3, cx - 5, cy - 5, cx - 3, cy - 6.5f, cx, cy - 6.5f);
                            dome.AddBezier(cx, cy - 6.5f, cx + 3, cy - 6.5f, cx + 5, cy - 5, cx + 5.5f, cy + 3);
                            g.DrawPath(pen, dome);
                        }
                        g.DrawLine(pen, a.X + 1.5f, cy + 3, a.Right - 1.5f, cy + 3);
                        g.FillEllipse(white, cx - 1.3f, cy + 5, 2.6f, 2.6f);
                        break;
                    case "wifi":
                        g.DrawArc(pen, cx - 8, cy - 6, 16, 13, 215, 130);
                        g.DrawArc(pen, cx - 4.5f, cy - 2, 9, 8, 225, 110);
                        g.FillEllipse(white, cx - 1.4f, cy + 5, 2.8f, 2.8f);
                        break;
                    case "device":
                        using (var lp = RoundedPathF(new RectangleF(a.X + 2, cy - 5.5f, 5, 11), 2.2f))
                            g.DrawPath(pen, lp);
                        using (var rp = RoundedPathF(new RectangleF(a.Right - 7, cy - 5.5f, 5, 11), 2.2f))
                            g.DrawPath(pen, rp);
                        g.DrawLine(pen, a.X + 4.5f, cy - 5.5f, a.X + 4.5f, a.Y + 1);
                        g.DrawLine(pen, a.Right - 4.5f, cy - 5.5f, a.Right - 4.5f, a.Y + 1);
                        break;
                    case "sun":
                        g.FillEllipse(white, cx - 3, cy - 3, 6, 6);
                        for (int i = 0; i < 8; i++)
                        {
                            double q = i * Math.PI / 4;
                            g.DrawLine(pen, cx + (float)Math.Cos(q) * 5.4f, cy + (float)Math.Sin(q) * 5.4f,
                                cx + (float)Math.Cos(q) * 8.2f, cy + (float)Math.Sin(q) * 8.2f);
                        }
                        break;
                    case "lens":
                        g.DrawEllipse(pen, a.X + 1, a.Y + 1, a.Width - 7, a.Height - 7);
                        g.DrawLine(pen, cx + 1.6f, cy + 1.6f, a.Right - .5f, a.Bottom - .5f);
                        break;
                    case "cloud":
                        using (var c = new GraphicsPath())
                        {
                            c.AddEllipse(a.X, cy - 1, 8, 8);
                            c.AddEllipse(a.X + 4, cy - 5, 9, 9);
                            c.AddEllipse(a.X + 8, cy - 1, 8, 8);
                            c.AddRectangle(new RectangleF(a.X + 3, cy + 1, a.Width - 8, 4));
                            g.FillPath(white, c);
                        }
                        break;
                    case "key":
                        g.DrawEllipse(pen, a.X + .5f, cy - 4, 8, 8);
                        g.DrawLine(pen, a.X + 8, cy, a.Right - .5f, cy);
                        g.DrawLine(pen, a.Right - 4, cy, a.Right - 4, cy + 4);
                        g.DrawLine(pen, a.Right - .5f, cy, a.Right - .5f, cy + 3);
                        break;
                    case "redirect":
                        g.DrawLine(pen, a.X + 1, a.Y + 1, a.X + 1, cy + 3);
                        g.DrawLine(pen, a.X + 1, cy + 3, a.Right - 5, cy + 3);
                        g.DrawLine(pen, a.Right - 7, cy - 1, a.Right - 1, cy + 3);
                        g.DrawLine(pen, a.Right - 7, cy + 7, a.Right - 1, cy + 3);
                        break;
                    case "island":
                        using (var ip = RoundedPathF(new RectangleF(a.X, a.Y, a.Width, a.Height), 5))
                        using (var b = new SolidBrush(Color.FromArgb(4, 4, 6)))
                            g.FillPath(b, ip);
                        break;
                }
            }

            static void Star(Graphics g, Brush br, float cx, float cy, float r, float waist)
            {
                g.FillPolygon(br, new[]
                {
                    new PointF(cx, cy - r), new PointF(cx + waist, cy - waist),
                    new PointF(cx + r, cy), new PointF(cx + waist, cy + waist),
                    new PointF(cx, cy + r), new PointF(cx - waist, cy + waist),
                    new PointF(cx - r, cy), new PointF(cx - waist, cy - waist),
                });
            }

            static GraphicsPath RoundedPathF(RectangleF r, float rad)
            {
                float d = Math.Max(2, rad * 2);
                var p = new GraphicsPath();
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                return p;
            }
        }

        // ── the pill ────────────────────────────────────────────────────
        sealed class ToggleSwitch : Control
        {
            readonly System.Windows.Forms.Timer tick = new() { Interval = 16 };
            bool value;
            float anim;

            public bool Value
            {
                get => value;
                set
                {
                    if (this.value == value) return;
                    this.value = value;
                    tick.Start();   // animates, then stops itself in OnPaint's
                    Invalidate();   // settle check — an idle switch is free.
                }
            }

            public ToggleSwitch()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
                Cursor = Cursors.Hand;
                TabStop = false;
                Size = new Size(50, 28);
                tick.Tick += (_, _) =>
                {
                    float target = Value ? 1f : 0f;
                    anim += (target - anim) * 0.35f;
                    if (Math.Abs(target - anim) < 0.01f)
                    {
                        anim = target;
                        tick.Stop();      // idle switch = no timer running
                    }
                    Invalidate();
                };
            }

            protected override bool ShowFocusCues => false;

            protected override void OnMouseDown(MouseEventArgs e)
            {
                Value = !Value;
                Focus();
            }

            static Color Mix(Color a, Color b, float t) => Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(CardBack);

                using (var track = new SolidBrush(Mix(Color.FromArgb(58, 58, 64), Accent, anim)))
                using (var p = RoundedPath(new Rectangle(0, 0, Width, Height), Height / 2))
                    g.FillPath(track, p);

                int d = Height - 6;
                int x = 3 + (int)Math.Round(anim * (Width - d - 6));
                using var knob = new SolidBrush(Color.FromArgb(230 + (int)(25 * anim), 255, 255, 255));
                g.FillEllipse(knob, x, 3, d, d);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) tick.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
