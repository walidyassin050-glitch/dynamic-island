using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MyDynamicIsland
{
    // One line of time-stamped lyrics.
    internal readonly record struct LyricLine(TimeSpan At, string Text);

    // Time-synced lyric lines for the media card, from LRCLIB (lrclib.net).
    //
    // Why not Spotify: the Web API has no lyrics endpoint at all. The lyrics
    // panel in the Spotify app is Musixmatch content served over a private
    // client API using the desktop app's own session, which no OAuth scope
    // exposes — reaching it from here would be a ToS violation that stops
    // working the day Spotify touches it. LRCLIB is a free, no-key database of
    // the same LRC-format synced lyrics, matched on artist + title (duration and
    // album improve the hit rate), so a line can be highlighted against the
    // real playback position instead of guessed at.
    //
    // One request per track, on the UI thread's dispatcher but never blocking
    // it: a track flip that is still in flight when the next flip arrives is
    // simply dropped by the key check in the callback.
    internal sealed class LyricService : IDisposable
    {
        private const string BaseUrl = "https://lrclib.net/api";
        private const int MaxLines = 800;

        // LRCLIB asks that API users identify their application.
        private static readonly HttpClient http = new()
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        static LyricService()
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MyDynamicIsland/1.0 (desktop dynamic island)");
        }

        private readonly object gate = new();
        private string key = "";

        private List<LyricLine> lines = new();
        private bool requestInFlight;

        // How many lines are loaded for the current track, and the current
        // track's lines. Cheap: called every repaint frame.
        public bool HasLyrics
        {
            get { lock (gate) return lines.Count > 0; }
        }

        // The line that should be on screen at a playback position, i.e. the
        // last one whose stamp is at or before it (Spotify behaves the same way,
        // holding the previous line through an instrumental break). -1 when
        // nothing has started yet, so the card can stay blank for the intro.
        public int LineAt(TimeSpan position)
        {
            lock (gate)
            {
                if (lines.Count == 0) return -1;
                int found = -1;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].At <= position) found = i;
                    else break;
                }
                return found;
            }
        }

        public string TextAt(int index)
        {
            lock (gate)
                return index >= 0 && index < lines.Count ? lines[index].Text : "";
        }

        public int LineCount
        {
            get { lock (gate) return lines.Count; }
        }

        // When the given line begins. The karaoke renderer divides the span
        // between one line's stamp and the next across its words, which is
        // how Spotify fakes word timing from line timing — LRCLIB (like every
        // free lyrics source) only stamps whole lines.
        public TimeSpan LineStartAt(int index)
        {
            lock (gate)
                return index >= 0 && index < lines.Count ? lines[index].At : TimeSpan.Zero;
        }

        // Clears immediately (so the previous song's words cannot flash under
        // the new one) then fetches in the background. Callable from any thread;
        // onReady runs wherever the response resumes (the UI thread when called
        // from it, since nothing here opts out of the context), so the caller
        // still marshals and disposes-checks before touching the form.
        public void LoadFor(string artist, string title, string album, int durationSeconds,
            Action<List<LyricLine>> onReady)
        {
            string trackKey = BuildKey(artist, title);

            lock (gate)
            {
                bool same = trackKey == key;
                if (same && (requestInFlight || lines.Count > 0)) return;

                if (!same)
                {
                    // A different track: drop the old words immediately so they
                    // cannot flash under the new one. A request still in flight
                    // for the previous track is allowed to finish; its answer is
                    // discarded below by the key check.
                    key = trackKey;
                    lines = new List<LyricLine>();
                }
            }

            _ = FetchAsync(artist, title, album, durationSeconds, trackKey, onReady);

        }

        public void Clear()
        {
            lock (gate)
            {
                key = "";
                lines = new List<LyricLine>();
                requestInFlight = false;
            }
        }

        private static string BuildKey(string artist, string title) =>
            (artist ?? "").Trim().ToLowerInvariant() + "|" + (title ?? "").Trim().ToLowerInvariant();

        private async Task FetchAsync(string artist, string title, string album,
            int durationSeconds, string trackKey, Action<List<LyricLine>> onReady)
        {
            lock (gate)
            {
                requestInFlight = true;
            }

            List<LyricLine>? parsed = null;
            try
            {
                string? lrc = await RequestSyncedAsync(BuildQuery(artist, title, album, durationSeconds));

                // The exact-match route needs artist and title to agree
                // character-for-character with the database, which punctuation
                // like "(Feat. X)" routinely defeats. Search is the same lookup
                // with the fuzziness the caller already knows about.
                if (lrc == null)
                    lrc = await SearchSyncedAsync(artist, title);

                parsed = ParseLrc(lrc);
            }
            catch
            {
                // No lyrics is a normal outcome, not an error to surface: the
                // card simply has no line to show.
                parsed = null;
            }

            // Deliver only if the user is still on the same track; a late answer
            // for a song they skipped past twice is dropped rather than painted.
            bool stillCurrent;
            lock (gate)
            {
                stillCurrent = key == trackKey;
                if (stillCurrent) requestInFlight = false;
                if (stillCurrent && parsed != null) lines = parsed;
            }

            if (stillCurrent && parsed != null)
            {
                try
                {
                    onReady(parsed);
                }
                catch
                {
                }
            }
        }

        private static string BuildQuery(string artist, string title, string album, int durationSeconds)
        {
            var sb = new StringBuilder("?track_name=").Append(Uri.EscapeDataString(title))
                .Append("&artist_name=").Append(Uri.EscapeDataString(artist));
            if (!string.IsNullOrWhiteSpace(album))
                sb.Append("&album_name=").Append(Uri.EscapeDataString(album));
            if (durationSeconds > 0)
                sb.Append("&duration=").Append(durationSeconds.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static async Task<string?> RequestSyncedAsync(string query)
        {
            try
            {
                using HttpResponseMessage resp = await http.GetAsync(BaseUrl + "/get" + query);
                if (!resp.IsSuccessStatusCode) return null;    // 404 = no record

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                return ReadSyncedField(doc.RootElement);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string?> SearchSyncedAsync(string artist, string title)
        {
            try
            {
                string url = BaseUrl + "/search?track_name=" + Uri.EscapeDataString(title) +
                    "&artist_name=" + Uri.EscapeDataString(artist);
                using HttpResponseMessage resp = await http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return null;

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

                // First candidate that actually carries synced text. Plain
                // lyrics are skipped deliberately: without stamps there is
                // nothing to line up with playback position, and a wall of
                // undated words in a one-line slot reads as a bug.
                foreach (JsonElement candidate in doc.RootElement.EnumerateArray())
                {
                    string? lrc = ReadSyncedField(candidate);
                    if (!string.IsNullOrWhiteSpace(lrc)) return lrc;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static string? ReadSyncedField(JsonElement record)
        {
            return record.TryGetProperty("syncedLyrics", out JsonElement synced) &&
                   synced.ValueKind == JsonValueKind.String
                ? synced.GetString()
                : null;
        }

        // LRC: "[00:12.43]line of text", one per line, with optional metadata
        // tags ([ar], [al], [ti], [by], [length]) and an [offset:] in
        // milliseconds that shifts every stamp from the line it appears on. Repeated stamps on one line
        // ("[00:01.00][02:15.00]chorus") are emitted once per stamp, which is
        // how the format encodes a reprise.
        internal static List<LyricLine> ParseLrc(string? lrc)
        {
            var result = new List<LyricLine>();
            if (string.IsNullOrWhiteSpace(lrc)) return result;

            TimeSpan offset = TimeSpan.Zero;
            foreach (string raw in lrc.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] != '[') continue;

                var stamps = new List<TimeSpan>();
                int cursor = 0;

                while (cursor < line.Length && line[cursor] == '[')
                {
                    int close = line.IndexOf(']', cursor);
                    if (close < 0) break;
                    string tag = line[(cursor + 1)..close];
                    cursor = close + 1;

                    if (TryReadStamp(tag, out TimeSpan stamp))
                    {
                        stamps.Add(stamp);
                        continue;
                    }

                    // A named tag. Only [offset:] affects the display, and it is
                    // read the moment it appears: a file can carry any number of
                    // [ar]/[al]/[ti]/[by] tags after it, so a single "last tag
                    // wins" string would have the offset overwritten before
                    // anything ever looked at it.
                    int sep = tag.IndexOf(':');
                    if (sep <= 0) continue;
                    if (!tag[..sep].Equals("offset", StringComparison.OrdinalIgnoreCase)) continue;
                    if (double.TryParse(tag[(sep + 1)..], NumberStyles.Float,
                            CultureInfo.InvariantCulture, out double offsetMs))
                    {
                        // Milliseconds; negative means the stamps run late and
                        // every line from here on is pulled earlier to match.
                        offset = TimeSpan.FromMilliseconds(offsetMs);
                    }
                }

                if (stamps.Count == 0) continue;

                string text = line[cursor..].Trim();
                if (text.Length == 0) continue;      // instrumental gap: hold the previous line

                foreach (TimeSpan stamp in stamps)
                {
                    TimeSpan at = stamp + offset;
                    if (at < TimeSpan.Zero) at = TimeSpan.Zero;
                    result.Add(new LyricLine(at, text));
                    if (result.Count >= MaxLines) return SortUnique(result);
                }
            }

            return SortUnique(result);
        }

        private static List<LyricLine> SortUnique(List<LyricLine> parsed)
        {
            parsed.Sort((a, b) => a.At.CompareTo(b.At));
            var clean = new List<LyricLine>(parsed.Count);
            foreach (LyricLine l in parsed)
            {
                if (clean.Count > 0 && clean[^1].At == l.At && clean[^1].Text == l.Text) continue;
                clean.Add(l);
            }
            return clean;
        }

        // "[mm:ss.xx]", "[mm:ss]", "[mm.xxx]"
        private static bool TryReadStamp(string tag, out TimeSpan at)
        {
            at = TimeSpan.Zero;
            if (tag.Length < 5) return false;

            int colon = tag.IndexOf(':');
            if (colon <= 0) return false;

            if (!int.TryParse(tag[..colon], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes))
                return false;

            string rest = tag[(colon + 1)..];
            int dot = rest.IndexOfAny(new[] { '.', ':', ',' });
            if (!int.TryParse(dot < 0 ? rest : rest[..dot], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int seconds))
            {
                return false;
            }

            int hundredths = 0;
            if (dot >= 0 && dot + 1 < rest.Length)
            {
                // One digit is tenths of a second, two is hundredths; anything
                // longer (some encoders write millis) is truncated, not rounded,
                // so a stamp never lands late on a beat by construction.
                string frac = rest[(dot + 1)..];
                if (frac.Length > 2) frac = frac[..2];
                if (int.TryParse(frac, NumberStyles.Integer, CultureInfo.InvariantCulture, out int f))
                    hundredths = frac.Length == 1 ? f * 10 : f;
            }

            // A bare fractional form ("[00:12.43]" seconds part may be >59) is
            // still valid, so normalise rather than clamp.
            at = new TimeSpan(0, 0, minutes, seconds, hundredths * 10);
            return true;
        }

        // Clears the cache rather than tearing anything down. The HttpClient is
        // static and shared on purpose — one pooled connection for the island's
        // whole life — so disposing a service instance must never dispose it.
        // The only caller is a form closing, where killing the client would
        // break any other holder of the service in the same process.
        public void Dispose() => Clear();
    }
}
