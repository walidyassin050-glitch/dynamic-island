using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MyDynamicIsland
{
    // Every reason the "Playing Next" list can be empty, named out loud. The
    // old stub's only failure mode was a blank panel with no explanation,
    // which is why this looks like overkill.
    internal enum SpotifyQueueStatus
    {
        Ok,
        NoClientConfigured,
        NotConnected,
        Connecting,
        ConnectionDeclined,
        PremiumRequired,
        NothingPlaying,
        RateLimited,
        NetworkError,
        RejectedBySpotify,

        // The stored consent predates the row-tap scope, so only a fresh
        // approval in the browser can cure it — RefreshAsync cannot add a scope
        // that was never granted, and pretending otherwise would loop Connect
        // forever without ever asking.
        ReapprovalNeeded,
    }

    // Real Spotify "Playing Next" data, straight from the official Web API.
    //
    // Sign-in is OAuth 2.0 authorization code + PKCE. A client secret cannot
    // ship inside a distributable exe, so the app's public Client ID plus a
    // loopback redirect do all the work: we open the browser, listen exactly
    // once on the redirect's port, take the ?code= out of the callback, swap
    // it for an access+refresh pair, and keep that pair in this exe's own
    // AppData folder, DPAPI-protected for the current Windows user.
    //
    // One-time setup (only you can do it — it is your Spotify application):
    //   developer.spotify.com/dashboard -> Create app
    //     Redirect URI : http://127.0.0.1:54123/callback   (match DefaultRedirect)
    //     Key          : paste the Client ID into Settings
    //
    // Spotify gates /me/player/queue behind Premium: every other call succeeds
    // on a free account and this one answers 403 NO_PREMIUM, so that case gets
    // its own status instead of masquerading as an empty queue.
    internal sealed class SpotifyQueueService : IDisposable
    {
        // The single place to change the redirect. It must be byte-identical to
        // the value registered in the Spotify dashboard — scheme, host, port and
        // path all included, no trailing slash differences.
        public const string DefaultRedirect = "http://127.0.0.1:54123/callback";

        private const string AuthorizeEndpoint = "https://accounts.spotify.com/authorize";
        private const string TokenEndpoint = "https://accounts.spotify.com/api/token";
        private const string QueueEndpoint = "https://api.spotify.com/v1/me/player/queue";
        // The write half: an empty POST-style body with a uri list starts that
        // track on the active device. Answered with 202 and no body.
        private const string PlayerPlayEndpoint = "https://api.spotify.com/v1/me/player/play";
        // Read first, then played against: this is where the *context* lives (the
        // playlist or album whose order the queue is following), which is what
        // lets a tap jump to a track without throwing the rest of the list away.
        private const string PlaybackStateEndpoint = "https://api.spotify.com/v1/me/player";
        // user-modify-playback-state is what lets a tapped row actually start:
        // it is a write scope, so a token minted before this line existed answers
        // 403 insufficient_scope, and the panel says to re-approve once.
        private const string Scopes =
            "user-read-playback-state user-read-currently-playing user-modify-playback-state";

        // Spotify's own queue is long and scrollable; the card paint window covers
        // a few rows, so the fetch asks for more than fits and the wheel steps
        // through the rest rather than truncating the list.
        private const int QueueFetchSize = 14;
        private const int BrowserGraceSeconds = 240;

        private static readonly HttpClient http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };

        private string clientId = "";
        private string redirectUri = DefaultRedirect;

        private readonly SemaphoreSlim gate = new(1, 1);

        private string? accessToken;
        private string? refreshToken;
        private DateTime accessExpiresUtc = DateTime.MinValue;

        public SpotifyQueueStatus Status { get; private set; } = SpotifyQueueStatus.NotConnected;
        public string StatusDetail { get; private set; } = "";

        // Raised whenever Status flips, so the island can repaint the queue
        // panel the instant a browser login lands instead of waiting up to 5
        // seconds for the next poll tick.
        public event Action? StatusChanged;

        // Power-user escape hatch, kept from the previous design: a raw bearer
        // token in this environment variable wins over everything and needs no
        // Client ID. It never renews, so when it lapses the status says so
        // rather than looping a browser you already authorised.
        private static string? EnvToken()
        {
            string? token = Environment.GetEnvironmentVariable("SPOTIFY_ACCESS_TOKEN");
            return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(clientId) || EnvToken() != null;

        // Feed in the real dashboard credentials and reload any token this
        // Client ID already obtained.
        public void Configure(AppSettings appSettings)
        {
            clientId = (appSettings.SpotifyClientId ?? "").Trim();
            redirectUri = string.IsNullOrWhiteSpace(appSettings.SpotifyRedirectUri)
                ? DefaultRedirect : appSettings.SpotifyRedirectUri.Trim();
            LoadTokens();
        }

        public SpotifyQueueService()
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MyDynamicIsland/1.0");
        }

        // ========================= public surface =========================

        // Silent path is used by the queue poll (see GetQueueAsync); the
        // interactive one is used by the Connect button and the tray entry.
        // Forces a fresh browser approval, ignoring a cached token. Needed the
        // moment Spotify's answer is "insufficient_scope": the refresh path can
        // only ever re-issue the scopes already granted, so Reconnect has to
        // skip the cache and re-consent on purpose.
        public async Task<bool> ReauthorizeAsync()
        {
            await gate.WaitAsync();
            try
            {
                accessToken = null;          // cache hit bypassed deliberately
                return await BrowserFlowAsync() != null;
            }
            finally { gate.Release(); }
        }

        // Interactive: runs the browser handshake when nothing cached works.
        public async Task<bool> ConnectAsync() => await AcquireAsync(interactive: true) != null;

        // null => no data at all (read Status/StatusDetail for the reason).
        // Non-null => a live answer, which may legitimately be empty because
        // Spotify has nothing queued.
        public async Task<List<SpotifyQueueTrack>?> GetQueueAsync()
        {
            string? token = await AcquireAsync(interactive: false);
            if (token == null) return null;

            var items = await ReadQueueAsync(token);

            // A token that died between two polls is the common case: renew it
            // once, quietly, and ask again before reporting anything.
            if (items == null && Status == SpotifyQueueStatus.RejectedBySpotify && refreshToken != null)
            {
                await gate.WaitAsync();
                try
                {
                    if (await RefreshAsync() && accessToken != null)
                        items = await ReadQueueAsync(accessToken);
                }
                finally { gate.Release(); }
            }

            return items;
        }

        // Drops the cached pair and the protected file, so the next Connect is a
        // genuinely fresh login — that is how you switch Spotify accounts
        // without hunting for the file by hand.
        public void Disconnect()
        {
            accessToken = null;
            refreshToken = null;
            accessExpiresUtc = DateTime.MinValue;
            try
            {
                if (File.Exists(StorePath)) File.Delete(StorePath);
            }
            catch
            {
                // A locked file is not worth taking the island down over.
            }
            SetStatus(SpotifyQueueStatus.NotConnected,
                "Signed out of Spotify on this device. Click Connect to sign into another account.");
        }

        private async Task<string?> AcquireAsync(bool interactive)
        {
            string? env = EnvToken();
            if (env != null)
            {
                SetStatus(SpotifyQueueStatus.Ok);
                return env;
            }

            await gate.WaitAsync();
            try
            {
                // Re-checked under the gate: a browser login that landed while
                // this call sat in the queue must win over a second handshake.
                if (accessToken != null && DateTime.UtcNow < accessExpiresUtc)
                {
                    SetStatus(SpotifyQueueStatus.Ok);
                    return accessToken;
                }

                if (refreshToken != null && await RefreshAsync())
                    return accessToken;

                return interactive ? await BrowserFlowAsync() : null;
            }
            finally { gate.Release(); }
        }

        private async Task<bool> RefreshAsync()
        {
            // Copied to locals so the null checks survive the awaits below
            // (field null-state is not tracked across an await).
            string? refresh = refreshToken;
            string id = clientId;

            if (string.IsNullOrWhiteSpace(id) || refresh == null)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    SetStatus(SpotifyQueueStatus.NoClientConfigured,
                        "No Client ID yet: developer.spotify.com/dashboard -> Create app, " +
                        "add redirect " + DefaultRedirect + ", paste the Key into Settings.");
                }
                return false;
            }

            return await PostTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = id,
            });
        }

        // Authorization-code + PKCE round trip through the user's default
        // browser. Returns the access token, or null after setting a status that
        // explains what stopped it.
        private async Task<string?> BrowserFlowAsync()
        {
            if (string.IsNullOrWhiteSpace(clientId))
            {
                SetStatus(SpotifyQueueStatus.NoClientConfigured,
                    "No Client ID yet: developer.spotify.com/dashboard -> Create app, " +
                    "add redirect " + DefaultRedirect + ", paste the Key into Settings.");
                return null;
            }

            if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out Uri? red) ||
                !string.Equals(red.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
                !(string.Equals(red.Host, "localhost", StringComparison.OrdinalIgnoreCase) || red.Host == "127.0.0.1"))
            {
                SetStatus(SpotifyQueueStatus.RejectedBySpotify,
                    "The redirect has to be a loopback http URL, e.g. " + DefaultRedirect);
                return null;
            }

            int port = red.IsDefaultPort ? 80 : red.Port;
            if (port == 80)
            {
                // HTTP.sys will not let a non-admin process bind port 80, so an
                // all-too-easy dashboard typo gets its own message.
                SetStatus(SpotifyQueueStatus.RejectedBySpotify,
                    "Port 80 needs admin rights to listen on. Register " + DefaultRedirect + " instead.");
                return null;
            }

            HttpListener? listener = null;
            try
            {
                string verifier = RandomToken(64);
                string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
                string state = RandomToken(24);

                listener = new HttpListener();
                // Bind the host exactly as the dashboard wrote it, so a redirect
                // registered with a hostname still lands on this listener.
                listener.Prefixes.Add($"http://{red.Host}:{port}/");
                listener.Start();

                string url =
                    $"{AuthorizeEndpoint}?client_id={Uri.EscapeDataString(clientId)}" +
                    "&response_type=code" +
                    $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                    $"&scope={Uri.EscapeDataString(Scopes)}" +
                    $"&state={state}" +
                    "&code_challenge_method=S256" +
                    $"&code_challenge={challenge}" +
                    "&show_dialog=true";

                SetStatus(SpotifyQueueStatus.Connecting, "Approve the request in the browser tab.");
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

                Task<HttpListenerContext> incoming = listener.GetContextAsync();
                Task done = await Task.WhenAny(incoming, Task.Delay(TimeSpan.FromSeconds(BrowserGraceSeconds)));

                if (done == incoming)
                {
                    HttpListenerContext ctx = await incoming;
                    string? code = ctx.Request.QueryString["code"];
                    string? echoedState = ctx.Request.QueryString["state"];
                    string? denial = ctx.Request.QueryString["error"];

                    await WriteAckAsync(ctx, code != null && echoedState == state
                        ? "<p style='font-family:Segoe UI'>Done — the island has your queue. " +
                          "This tab can be closed.</p>"
                        : "<p style='font-family:Segoe UI'>Spotify declined the request. " +
                          "Close this tab and press Connect again.</p>");

                    if (code == null || echoedState != state)
                    {
                        SetStatus(SpotifyQueueStatus.ConnectionDeclined,
                            string.IsNullOrWhiteSpace(denial)
                                ? "The sign-in didn't finish (state mismatch or no code)."
                                : "Spotify said: " + denial);
                        return null;
                    }

                    return await ExchangeCodeAsync(code, verifier);
                }

                // Nothing reached the listener. Either the tab was closed early or
                // Windows refused the loopback callback outright — a packaged
                // (MSIX) build can be, by the AppContainer rule, while the plain
                // exe never is. The address bar still holds a valid one-time code
                // in that case, so take it by hand rather than losing the sign-in.
                string? manual = ParseCodeFromUrl(PromptForCallbackUrl(), state);
                if (manual == null)
                {
                    SetStatus(SpotifyQueueStatus.ConnectionDeclined,
                        "The browser never reached the island. Press Connect again, and if you " +
                        "are running the packaged build, paste the address bar when asked.");
                    return null;
                }

                return await ExchangeCodeAsync(manual, verifier);
            }
            catch (HttpListenerException hx)
            {
                SetStatus(SpotifyQueueStatus.RejectedBySpotify,
                    "Couldn't listen on port " + port + " (" + hx.Message + "). Make sure the " +
                    "dashboard redirect uses this same port and that no other app owns it.");
                return null;
            }
            catch (Exception ex)
            {
                SetStatus(SpotifyQueueStatus.NetworkError, ex.Message);
                return null;
            }
            finally
            {
                try { listener?.Stop(); } catch { }
                listener?.Close();
            }
        }

        // The one half of the handshake that is not about the browser: swap a
        // fresh code plus its verifier for the token pair.
        private async Task<string?> ExchangeCodeAsync(string code, string verifier)
        {
            return await PostTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = clientId,
                ["code_verifier"] = verifier,
            }) ? accessToken : null;
        }

        // Pulls ?code= and ?state= out of whatever the user pasted. The state is
        // still checked, because it is the only thing tying that URL to the
        // verifier this handshake generated — a code from any other attempt (or
        // any other app) cannot be exchanged.
        private static string? ParseCodeFromUrl(string? pasted, string expectedState)
        {
            if (string.IsNullOrWhiteSpace(pasted)) return null;
            if (!Uri.TryCreate(pasted.Trim(), UriKind.Absolute, out Uri? u)) return null;

            string? code = null;
            string? state = null;
            foreach (string pair in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                string key = pair[..eq];
                string value = Uri.UnescapeDataString(pair[(eq + 1)..]);
                if (key == "code") code = value;
                else if (key == "state") state = value;
            }

            return code != null && state == expectedState ? code : null;
        }

        // Runs on the UI thread (a press started the handshake that got here),
        // so a modal dialog is safe; if that ever changes, this is the line to
        // marshal.
        private static string? PromptForCallbackUrl()
        {
            try
            {
                using var prompt = new ManualCodePrompt();
                return prompt.ShowDialog(Form.ActiveForm) == DialogResult.OK
                    ? prompt.Pasted
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static async Task WriteAckAsync(HttpListenerContext ctx, string bodyHtml)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(
                    "<html><head><title>Dynamic Island</title></head><body>" + bodyHtml + "</body></html>");
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            catch
            {
                // The tab is already closing; nothing to report.
            }
            finally
            {
                try { ctx.Response.Close(); } catch { }
            }
        }

        private async Task<bool> PostTokenAsync(Dictionary<string, string> fields)
        {
            try
            {
                using var form = new FormUrlEncodedContent(fields);
                using HttpResponseMessage resp = await http.PostAsync(TokenEndpoint, form);
                string body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    DescribeError(resp.StatusCode, body, tokenStage: true);
                    return false;
                }

                using var doc = JsonDocument.Parse(body);
                string? access = ReadString(doc.RootElement, "access_token");
                if (string.IsNullOrEmpty(access))
                {
                    SetStatus(SpotifyQueueStatus.RejectedBySpotify, "Spotify returned no access token.");
                    return false;
                }

                accessToken = access;
                // Refresh replies usually omit refresh_token — keep the one we hold.
                string? rotated = ReadString(doc.RootElement, "refresh_token");
                if (!string.IsNullOrEmpty(rotated)) refreshToken = rotated;

                int lifetime = ReadInt(doc.RootElement, "expires_in", 3600);
                // Refresh two minutes early: a token that expires mid-request
                // costs a failed call plus a retry every single poll.
                accessExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(120, lifetime - 120));

                SaveTokens();
                SetStatus(SpotifyQueueStatus.Ok);
                return true;
            }
            catch (Exception ex)
            {
                SetStatus(SpotifyQueueStatus.NetworkError, ex.Message);
                return false;
            }
        }

        // ========================= queue + parsing =========================
        // One row of the queue list, as drawn: what to play, what to label it
        // with, and how long it runs. Everything here comes from Spotify's own
        // JSON — nothing is invented locally.
        internal sealed class SpotifyQueueTrack
        {
            public string Id = "";
            public string Uri = "";
            public string Title = "";
            public string Artist = "";
            public string Album = "";
            public int DurationMs;
            public Bitmap? Art;

            // Smallest-first image offers, kept so the bitmap fetch can pick one
            // without re-reading the JSON.
            public readonly List<(string url, int width)> ArtOffers = new();
        }

        private async Task<List<SpotifyQueueTrack>?> ReadQueueAsync(string token)
        {
            string url = QueueEndpoint + "?limit=" + QueueFetchSize;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Accept.ParseAdd("application/json");

                using HttpResponseMessage resp = await http.SendAsync(req);

                // 204 is Spotify's way of saying "nothing is playing on any
                // device" — a normal answer, not a failure.
                if ((int)resp.StatusCode == 204)
                {
                    SetStatus(SpotifyQueueStatus.NothingPlaying,
                        "Nothing is playing in Spotify right now.");
                    return null;
                }

                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    DescribeError(resp.StatusCode, body, tokenStage: false);
                    return null;
                }

                var list = await ParseUpNextAsync(body);
                SetStatus(SpotifyQueueStatus.Ok);
                return list;
            }
            catch (Exception ex)
            {
                SetStatus(SpotifyQueueStatus.NetworkError, ex.Message);
                return null;
            }
        }

        // What happened when a row was tapped. The distinction between the two
        // successes matters more than the failures: one keeps your queue, the
        // other silently ends it, and the panel must never imply the second one
        // was the first.
        internal enum PlayOutcome
        {
            StartedInQueue,
            StartedOutsideQueue,
            Unauthorized,
            NoDevice,
            NotPremium,
            NeedsScope,
            NoRoleOnDevice,
            // The offset attempt answered 400: the track is not part of the
            // context that is playing, so the queue had already wandered off it.
            TrackNotInContext,
            Failed,
        }

        // Spotify exposes the queue in exactly three ways: read it, append to it,
        // and start a track. There is no way to remove or reorder an entry, and no
        // "jump to queue position N". The nearest thing to the client's own
        // "play this queued track" is to restart the *context* the queue came from
        // at that track — playlist or album order is preserved, so the tracks
        // after it stay lined up behind it, which is what the tap is for.
        //
        //   PUT /v1/me/player/play  {"context_uri": "spotify:playlist:...",
        //                            "offset": {"uri": "spotify:track:..."}}
        //
        // The bare {"uris": [track]} form is the fallback, and it is a repair, not
        // a success: that call makes the track the whole point of playback and
        // Spotify stops following the list. It is used only when there is no
        // offsettable context to work with (a DJ, radio or search mix) or when
        // Spotify says the track isn't part of the context at all.
        public async Task<PlayOutcome> PlayTrackOutcomeAsync(string trackUri)
        {
            if (string.IsNullOrWhiteSpace(trackUri))
            {
                SetStatus(SpotifyQueueStatus.RejectedBySpotify,
                    "Spotify didn't give this row a playable URI.");
                return PlayOutcome.Failed;
            }

            string? token = await AcquireAsync(interactive: false);
            if (token == null) return PlayOutcome.Failed;

            CurrentPlay? now = await ReadCurrentPlayAsync(token);

            // Playing against a device you aren't listening on would answer 204
            // and change nothing, so the active device id is passed through when
            // /me/player reports one.
            string uri = PlayerPlayEndpoint +
                (now?.DeviceId.Length > 0 ? "?device_id=" + Uri.EscapeDataString(now!.DeviceId!) : "");

            PlayOutcome outcome = PlayOutcome.Failed;

            // Playlists, albums and shows have a fixed order that can be started
            // at any one track, so they are worth the offset attempt. A DJ mix, a
            // radio seed or a search page has no such order, and asking Spotify to
            // jump inside one only produces a refusal.
            if (now != null && IsOffsettableContext(now.ContextUri))
            {
                outcome = await TryStartAsync(uri, token, new Dictionary<string, object>
                {
                    ["context_uri"] = now.ContextUri,
                    ["offset"] = new Dictionary<string, object> { ["uri"] = trackUri },
                    ["position_ms"] = 0,
                });

                if (outcome == PlayOutcome.StartedInQueue) return Succeeded(PlayOutcome.StartedInQueue);

                // 400 here means "that track isn't in this context" — the queue
                // had already wandered off the playlist, e.g. via an added song.
                // Everything else (Premium, scope, no device) is final: falling
                // back would trade a clear message for a wrong one.
                if (outcome != PlayOutcome.TrackNotInContext) return Failed(outcome);
            }

            outcome = await TryStartAsync(uri, token, new Dictionary<string, object>
            {
                ["uris"] = new[] { trackUri },
                ["position_ms"] = 0,
            });

            return outcome == PlayOutcome.StartedOutsideQueue
                ? Succeeded(PlayOutcome.StartedOutsideQueue)
                : Failed(outcome);
        }

        // 401 is the one answer worth retrying by ourselves: the access token
        // simply outlived its hour. Renew, repeat the *identical* tap once, and
        // never open a browser from here — that is the poll's job to avoid.
        private async Task<PlayOutcome> TryStartAsync(
            string url, string token, Dictionary<string, object> body)
        {
            PlayOutcome first = await SendToPlayerAsync(url, token, body);
            if (first != PlayOutcome.Unauthorized || refreshToken == null) return first;

            await gate.WaitAsync();
            try
            {
                return await RefreshAsync() && accessToken != null
                    ? await SendToPlayerAsync(url, accessToken, body)
                    : first;
            }
            finally { gate.Release(); }
        }

        private PlayOutcome Succeeded(PlayOutcome outcome)
        {
            SetStatus(SpotifyQueueStatus.Ok);
            return outcome;
        }

        private PlayOutcome Failed(PlayOutcome outcome)
        {
            switch (outcome)
            {
                case PlayOutcome.Unauthorized:
                    SetStatus(SpotifyQueueStatus.RejectedBySpotify,
                        "Spotify refused the playback token — press Reconnect.");
                    break;
                case PlayOutcome.NoDevice:
                    SetStatus(SpotifyQueueStatus.NothingPlaying,
                        "Spotify has no active device to play on.");
                    break;
                case PlayOutcome.NoRoleOnDevice:
                    SetStatus(SpotifyQueueStatus.RejectedBySpotify,
                        "This sign-in isn't allowed to control that Spotify device.");
                    break;
                case PlayOutcome.NotPremium:
                    SetStatus(SpotifyQueueStatus.PremiumRequired,
                        "Spotify only lets Premium accounts control playback from other apps.");
                    break;
                case PlayOutcome.NeedsScope:
                    SetStatus(SpotifyQueueStatus.ReapprovalNeeded,
                        "This sign-in predates queue control — reconnect to approve it.");
                    break;
                case PlayOutcome.TrackNotInContext:
                    SetStatus(SpotifyQueueStatus.RejectedBySpotify,
                        "Spotify no longer has that track inside the current list.");
                    break;
                default:
                    SetStatus(SpotifyQueueStatus.RejectedBySpotify,
                        "Spotify didn't start that track.");
                    break;
            }
            return outcome;
        }

        // Only a container whose order is fixed can be started at an arbitrary
        // track: playlists, albums, shows and episodes.
        private static bool IsOffsettableContext(string contextUri) =>
            contextUri.StartsWith("spotify:playlist:", StringComparison.OrdinalIgnoreCase) ||
            contextUri.StartsWith("spotify:album:", StringComparison.OrdinalIgnoreCase) ||
            contextUri.StartsWith("spotify:show:", StringComparison.OrdinalIgnoreCase) ||
            contextUri.StartsWith("spotify:episode:", StringComparison.OrdinalIgnoreCase);

        private sealed class CurrentPlay
        {
            public string DeviceId = "";
            public string ContextUri = "";
        }

        // GET /v1/me/player — the only place the active context and device id
        // live. 204 means nothing is playing anywhere, which is a legitimate
        // reason a tap can't do anything, so it is reported, not swallowed.
        private async Task<CurrentPlay?> ReadCurrentPlayAsync(string token)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, PlaybackStateEndpoint);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Accept.ParseAdd("application/json");

                using HttpResponseMessage resp = await http.SendAsync(req);
                if ((int)resp.StatusCode == 204 || !resp.IsSuccessStatusCode) return null;

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var state = new CurrentPlay
                {
                    DeviceId = ReadNested(doc.RootElement, "device", "id") ?? "",
                    ContextUri = ReadNested(doc.RootElement, "context", "uri") ?? "",
                };
                return state;
            }
            catch
            {
                // A failed read only costs us the offset path; the tap still
                // gets its fallback attempt.
                return null;
            }
        }

        private async Task<PlayOutcome> SendToPlayerAsync(
            string url, string token, Dictionary<string, object> body)
        {
            // A body carrying context_uri is the queue-preserving form, so a 202
            // from it means the list survives; a bare uris body means it doesn't.
            bool hasContext = body.ContainsKey("context_uri");

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Put, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                // Written out as text rather than through JsonContent.Create:
                // the shape of this body is fixed and tiny, and an explicit
                // string is one less thing to get wrong across framework versions.
                req.Content = new StringContent(
                    JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using HttpResponseMessage resp = await http.SendAsync(req);

                // Context+offset answers 202 and keeps the list; a bare uri answers 202
                // too, but from Spotify's side the queue is over afterwards.
                if (resp.IsSuccessStatusCode)
                {
                    return hasContext
                        ? PlayOutcome.StartedInQueue
                        : PlayOutcome.StartedOutsideQueue;
                }

                string text = await resp.Content.ReadAsStringAsync();
                int code = (int)resp.StatusCode;
                if (code == 401) return PlayOutcome.Unauthorized;
                if (code == 403 && text.Contains("insufficient_scope", StringComparison.OrdinalIgnoreCase))
                    return PlayOutcome.NeedsScope;
                if (code == 403) return PlayOutcome.NotPremium;
                if (code == 404) return PlayOutcome.NoDevice;
                if (code == 400 && text.Contains("offset", StringComparison.OrdinalIgnoreCase))
                    return PlayOutcome.TrackNotInContext;
                if (text.Contains("NO_ACTIVE_DEVICE", StringComparison.OrdinalIgnoreCase))
                    return PlayOutcome.NoDevice;
                if (code == 409 && text.Contains("NOT_ALLOWED", StringComparison.OrdinalIgnoreCase))
                    return PlayOutcome.NoRoleOnDevice;
                return PlayOutcome.Failed;
            }
            catch
            {
                return PlayOutcome.Failed;
            }
        }

        // The queue panel's data: /me/player/queue returns the up-next list plus
        // the track playing right now. The card already shows the current track,
        // so "currently_playing" is only the de-duplication key here.
        private static async Task<List<SpotifyQueueTrack>> ParseUpNextAsync(string body)
        {
            var parsed = new List<SpotifyQueueTrack>();
            string currentId = "";

            using var doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("currently_playing", out JsonElement now) &&
                now.ValueKind == JsonValueKind.Object)
            {
                currentId = ReadString(now, "id") ?? "";
            }

            // Spotify has served this list under both "items" and "queue" across
            // API revisions, so accept whichever is present.
            JsonElement entries = default;
            bool found = false;
            foreach (string key in new[] { "items", "queue" })
            {
                if (root.TryGetProperty(key, out JsonElement candidate) &&
                    candidate.ValueKind == JsonValueKind.Array)
                {
                    entries = candidate;
                    found = true;
                    break;
                }
            }

            if (!found) return parsed;

            foreach (JsonElement track in entries.EnumerateArray())
            {
                if (track.ValueKind != JsonValueKind.Object) continue;

                string? id = ReadString(track, "id");
                string title = ReadString(track, "name") ?? "";
                if (string.IsNullOrWhiteSpace(title) || (id != null && id == currentId)) continue;

                var row = new SpotifyQueueTrack
                {
                    Id = id ?? "",
                    Uri = ReadString(track, "uri") ?? "",
                    Title = title,
                    Artist = ArtistsOf(track),
                    Album = ReadNested(track, "album", "name") ?? "",
                    DurationMs = ReadInt(track, "duration_ms", 0),
                };

                if (track.TryGetProperty("album", out JsonElement album) &&
                    album.ValueKind == JsonValueKind.Object &&
                    album.TryGetProperty("images", out JsonElement images) &&
                    images.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement img in images.EnumerateArray())
                    {
                        string? u = ReadString(img, "url");
                        if (!string.IsNullOrWhiteSpace(u))
                            row.ArtOffers.Add((u, ReadInt(img, "width", int.MaxValue)));
                    }
                }

                parsed.Add(row);
            }

            // Art is one round trip per row; sequentially that is a dozen
            // requests deep before the panel can paint, so fetch them at once.
            var loads = new Task<Bitmap?>[parsed.Count];
            for (int i = 0; i < parsed.Count; i++) loads[i] = LoadArtAsync(parsed[i].ArtOffers);
            var loaded = await Task.WhenAll(loads);
            for (int i = 0; i < parsed.Count; i++) parsed[i].Art = loaded[i];

            return parsed;
        }

        private static string? ReadNested(JsonElement el, string parent, string property)
        {
            if (!el.TryGetProperty(parent, out JsonElement child) ||
                child.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            return ReadString(child, property);
        }

        private static string ArtistsOf(JsonElement track)
        {
            if (track.TryGetProperty("artists", out JsonElement artists) &&
                artists.ValueKind == JsonValueKind.Array)
            {
                var names = new List<string>();
                foreach (JsonElement a in artists.EnumerateArray())
                {
                    string? n = ReadString(a, "name");
                    if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
                }
                if (names.Count > 0) return string.Join(", ", names);
            }

            return track.TryGetProperty("album", out JsonElement album) &&
                   album.ValueKind == JsonValueKind.Object
                ? ReadString(album, "name") ?? ""
                : "";
        }

        // The bitmap returned here is owned by exactly one row: Form1's
        // SetQueue() disposes the art of the list it replaces, so nothing may be
        // shared between rows or cached across fetches.
        private static async Task<Bitmap?> LoadArtAsync(List<(string url, int width)> offers)
        {
            if (offers.Count == 0) return null;

            // Smallest offer wins: a row paints a 40px cover, so decoding a 640px
            // image buys nothing but memory and a slower first paint.
            string url = offers[0].url;
            int best = int.MaxValue;
            foreach (var offer in offers)
            {
                if (offer.width < best) { best = offer.width; url = offer.url; }
            }

            try
            {
                byte[] bytes = await http.GetByteArrayAsync(url);
                using var stream = new MemoryStream(bytes);
                using var loaded = new Bitmap(stream);
                // The copy detaches the bitmap from the stream, so the row keeps
                // drawing after this method's buffers are gone.
                return new Bitmap(loaded);
            }
            catch
            {
                // Missing art is normal offline; the row still lists the track.
                return null;
            }
        }

        private void SetStatus(SpotifyQueueStatus status, string detail = "")
        {
            bool changed = Status != status || StatusDetail != detail;
            Status = status;
            StatusDetail = detail;
            if (changed) StatusChanged?.Invoke();
        }

        // Turns Spotify's error envelope into a sentence that says what to do.
        // This whole service used to fail silently, which made every problem
        // look like "Spotify doesn't have a queue".
        private void DescribeError(HttpStatusCode code, string body, bool tokenStage)
        {
            string message = "";
            try
            {
                using var doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("error", out JsonElement err) && err.ValueKind == JsonValueKind.Object)
                    message = ReadString(err, "message") ?? ReadString(err, "reason") ?? "";
                else if (root.TryGetProperty("error_description", out JsonElement d))
                    message = ReadString(d) ?? "";
            }
            catch
            {
            }

            int n = (int)code;
            bool premiumNamed =
                message.Contains("Premium", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("NO_PREMIUM", StringComparison.OrdinalIgnoreCase);

            // A bare 403 means Premium only on the queue endpoint. The same code
            // from the token endpoint is Spotify refusing the Client ID /
            // redirect pair, and telling someone they need Premium for that is
            // how an afternoon disappears.
            if (premiumNamed || (n == 403 && message.Length == 0 && !tokenStage))
            {
                SetStatus(SpotifyQueueStatus.PremiumRequired,
                    "Spotify's up-next queue is a Premium feature." +
                    (message.Length > 0 ? " " + message : ""));
                return;
            }

            switch (n)
            {
                case 401:
                    // The caller retries once with a refreshed token before this
                    // reaches the panel, so the text points at reconnecting.
                    SetStatus(SpotifyQueueStatus.RejectedBySpotify,
                        tokenStage ? "Spotify refused the sign-in: " + message
                                   : "Spotify expired the session — press Connect again.");
                    return;
                case 403:
                    SetStatus(SpotifyQueueStatus.PremiumRequired,
                        "Spotify refused this account: " + message);
                    return;
                case 404:
                    SetStatus(SpotifyQueueStatus.NothingPlaying,
                        "No active Spotify playback device to read a queue from.");
                    return;
                case 429:
                    SetStatus(SpotifyQueueStatus.RateLimited,
                        "Spotify rate-limited the request; the panel retries by itself.");
                    return;
                default:
                    SetStatus(SpotifyQueueStatus.RejectedBySpotify,
                        "Spotify returned " + n + (message.Length > 0 ? ": " + message : ""));
                    return;
            }
        }

        private static string? ReadString(JsonElement el, string? property = null)
        {
            try
            {
                if (property != null)
                {
                    if (!el.TryGetProperty(property, out JsonElement child)) return null;
                    el = child;
                }
                return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
            }
            catch
            {
                return null;
            }
        }

        private static int ReadInt(JsonElement el, string property, int fallback)
        {
            try
            {
                return el.TryGetProperty(property, out JsonElement child) &&
                       child.ValueKind == JsonValueKind.Number && child.TryGetInt32(out int v)
                    ? v : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        // PKCE parameters are url-safe base64 without padding, drawn from the
        // RFC 7636 character set (A-z 0-9 - . _ ~).
        private static string RandomToken(int byteLength)
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(byteLength);
            return Base64Url(bytes);
        }

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // ---------- the token file ----------

        // Lives beside settings.json, but never as plain text: the refresh token
        // is a long-lived credential that buys anyone who reads it full access to
        // the account. DPAPI CurrentUser binds it to this Windows user, so the
        // file is inert if copied to another machine or opened from another
        // account. No third-party storage, no admin rights.
        private static string StorePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyDynamicIsland", "spotify-auth.bin");

        private sealed class SavedToken
        {
            public string Access { get; set; } = "";
            public string Refresh { get; set; } = "";
            public long ExpiresUnix { get; set; }
            public string ClientId { get; set; } = "";
        }

        private void SaveTokens()
        {
            try
            {
                var payload = new SavedToken
                {
                    Access = accessToken ?? "",
                    Refresh = refreshToken ?? "",
                    ExpiresUnix = new DateTimeOffset(accessExpiresUtc).ToUnixTimeSeconds(),
                    ClientId = clientId,
                };

                byte[] plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
                byte[] blob = Protect(plain, Entropy);

                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                File.WriteAllBytes(StorePath, blob);
            }
            catch
            {
                // Worst case the user re-authorises after a reboot; the island
                // must never crash over its own cache file.
            }
        }

        private void LoadTokens()
        {
            accessToken = null;
            refreshToken = null;
            accessExpiresUtc = DateTime.MinValue;

            try
            {
                if (!File.Exists(StorePath)) return;

                SavedToken? saved = JsonSerializer.Deserialize<SavedToken>(
                    Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(StorePath), Entropy)));

                if (saved == null || string.IsNullOrWhiteSpace(saved.Access)) return;

                // A file written under a different Client ID belongs to another
                // Spotify application; using it would just produce 401s.
                if (!string.IsNullOrWhiteSpace(clientId) &&
                    !string.Equals(saved.ClientId, clientId, StringComparison.Ordinal))
                {
                    return;
                }

                accessToken = saved.Access;
                refreshToken = string.IsNullOrWhiteSpace(saved.Refresh) ? null : saved.Refresh;
                accessExpiresUtc = DateTimeOffset.FromUnixTimeSeconds(saved.ExpiresUnix).UtcDateTime;
            }
            catch
            {
                // Corrupt or written by another user: drop it and treat the app
                // as signed out.
                accessToken = null;
                refreshToken = null;
                accessExpiresUtc = DateTime.MinValue;
            }
        }

        // ---------- DPAPI ----------
        // CryptProtectData / CryptUnprotectData straight from crypt32, so this
        // needs no extra NuGet package in the .csproj.

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CryptProtectData(
            ref DataBlob dataIn, string? description, ref DataBlob entropy,
            IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

        // The description is declared IntPtr rather than out string: crypt32
        // allocates it with LocalFree, while the default string marshaler frees
        // with CoTaskMemFree — a heap mismatch that only shows up as an
        // intermittent crash. It is never read, so it stays a raw pointer.
        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(
            ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
            IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr handle);

        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x01;

        // Application-specific additional entropy: without it any other app
        // running as the same user could unprotect the file. With it, only this
        // exe's own copy of the constant can.
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("MyDynamicIsland.SpotifyQueue.v1");

        private static byte[] Protect(byte[] plain, byte[] entropy)
        {
            IntPtr pData = Marshal.AllocHGlobal(plain.Length);
            IntPtr pEntropy = Marshal.AllocHGlobal(entropy.Length);
            try
            {
                Marshal.Copy(plain, 0, pData, plain.Length);
                Marshal.Copy(entropy, 0, pEntropy, entropy.Length);

                var inBlob = new DataBlob { cbData = plain.Length, pbData = pData };
                var entBlob = new DataBlob { cbData = entropy.Length, pbData = pEntropy };

                if (!CryptProtectData(ref inBlob, "MyDynamicIsland", ref entBlob, IntPtr.Zero,
                        IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out DataBlob outBlob))
                {
                    throw new ApplicationException("CryptProtectData failed");
                }

                try
                {
                    var result = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                    return result;
                }
                finally
                {
                    LocalFree(outBlob.pbData);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pData);
                Marshal.FreeHGlobal(pEntropy);
            }
        }

        private static byte[] Unprotect(byte[] cipher, byte[] entropy)
        {
            IntPtr pData = Marshal.AllocHGlobal(cipher.Length);
            IntPtr pEntropy = Marshal.AllocHGlobal(entropy.Length);
            try
            {
                Marshal.Copy(cipher, 0, pData, cipher.Length);
                Marshal.Copy(entropy, 0, pEntropy, entropy.Length);

                var inBlob = new DataBlob { cbData = cipher.Length, pbData = pData };
                var entBlob = new DataBlob { cbData = entropy.Length, pbData = pEntropy };

                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero,
                        IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out DataBlob outBlob))
                {
                    throw new ApplicationException("CryptUnprotectData failed");
                }

                try
                {
                    var result = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                    return result;
                }
                finally
                {
                    LocalFree(outBlob.pbData);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pData);
                Marshal.FreeHGlobal(pEntropy);
            }
        }

        public void Dispose()
        {
            // The queue rows themselves (and their bitmaps) belong to Form1,
            // which frees them through SetQueue()/FormClosing.
            gate.Dispose();
        }
    }
}
