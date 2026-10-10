# My Dynamic Island

A desktop-first Windows 10/11 WinForms island inspired by the macOS notch experience. The pure-black attached silhouette flows directly from the top screen edge and expands into focused cards — media, search, weather, and live activities for downloads and notifications.

## Interaction

- **Resting pill** — artwork + live visualizer. Hover it: the pill grows downward and the song name scrolls into the new band — nothing above it ever moves. Hover the visualizer itself and a liquid-glass **play/pause bead** appears; one tap drives the real media transport.
- **Cards & pages** — click the island to open the card. Three standing pages: Now Playing, Ask anything (Search), Today (weather + agenda). A **fourth, temporary page** joins the wheel and the dots whenever a download or upload is running, and dissolves the moment it finishes. Scroll the wheel (one notch, one page) or tap the page-indicator dots.
- **Live download activity** — the island tracks file transfers: its own (with working Pause/Resume/Cancel) **and your browser's** — Chrome, Edge, Firefox downloads are recognized by the markers their own folders carry, showing a real-time progress ring, speed, and time left, then a ✓ when the file lands. Tap anywhere on a browser-owned card to hide it (Hide, not Cancel — your download is not the island's to control).
- **Notification cards** — a Windows notification pops a quiet one-line banner at the island (round app identity when available); click before it fades and the *same card* expands to the full message. Tapping again dismisses.
- **Volume & brightness** — media keys no longer open anything: the level happens **to the pill itself** at its resting size — glyph left, hairline rail right, exactly while the keys keep coming. The rail is clickable to set level, the speaker mutes.
- **The Today page** — collapsed pill shows the time (12h everywhere); hover reveals battery percentage; expanded shows the weather under a sky tinted to the live conditions (day/night-aware text, refresh at the city name) and today's agenda.
- Auto-hide (default on): after idle seconds the notch slides past the screen edge leaving a 2px sliver — push the cursor up, or let any event wake it.
- Right-click the island for page, AI-ask, display, and exit controls; tray icon for settings. `Esc` collapses whatever is open.
- The battery readout appears only on machines with a battery (and can be switched off); low-battery and charging alerts keep their own popups.

## Download & run — no tooling required

Grab **`MyDynamicIsland.exe`** from this repository's [Releases](../../releases) page. One self-contained file: no install, no admin rights, no .NET download, no Visual Studio. Double-click and the island appears at the top edge.

First run of a fresh download: SmartScreen will likely say *"Windows protected your PC"* — it's an unsigned hobby build, not malware. **More info → Run anyway**.

**MSIX (recommended for full features):** the packaged build (see `msix/make-msix.ps1`) additionally enables Windows' notification listener and location APIs — the portable exe shows an honest "unavailable" note in Settings instead of pretending.

Optional Spotify queue: open **Playing Next** on the media page → **Connect Spotify** (OAuth PKCE, approval once, tokens encrypted to your Windows account). Premium is required by Spotify for the list itself.

Weather just works via Windows location or one HTTPS IP approximation — or pin a typed city in Settings.

Testing the look without waiting for events: Settings → **⊙ Simulate a notification** (toast flow) — no real notification needed.

## Security & Privacy

**Network footprint — every outbound call, and when it happens:**

| Endpoint | Why | Only when |
|---|---|---|
| `api.open-meteo.com` (HTTPS) | forecast for the Today page | weather fetch, city known |
| `geocoding-api.open-meteo.com` (HTTPS) | typed city → coordinates | you typed a city |
| `api.bigdatacloud.net` (HTTPS) | approximate coordinates from IP | auto-location on, no typed city, OS location denied |
| `lrclib.net` (HTTPS) | synced lyrics (engine idle in builds without the lyric row) | media track changes, media on |
| `accounts.spotify.com`, `api.spotify.com` (HTTPS) | OAuth PKCE + the Playing Next list | queue opened with a client id set |
| `https://127.0.0.1:54123/callback` | your browser's OAuth return hop, loopback only | during sign-in |
| `api.github.com`, `github.com` | update check + downloaded release | *you* press a download button |

No telemetry, no background update pings, no third-party server sees anything unless a feature above is in use. Location must never travel unencrypted — the old HTTP fallback is gone by design.

**What it reads locally:** the *names and sizes* of in-flight files in your Downloads folder (browser progress markers — never contents), the browser's own download bookkeeping for a *tracked transfer* (total size and final filename only — not history, not URLs), the Windows notification listener (toasts' visible text + registered app icons), system media sessions, default-audio loopback loudness (drives the visualizer, no audio stored), and the calendar (Today agenda, read-only). Nothing leaves the machine that isn't in the table above.

**Location policy:** typed city always wins; otherwise Windows' own location service (OS consent; packaged build has the capability); otherwise one HTTPS approximation. Automatic readings are never stored. Settings → *Automatic location* turns every automatic source off.

**Spotify:** a Client ID is a public identifier, not a secret (OAuth 2.0 PKCE, nothing embedded). Per-user tokens live in `%LocalAppData%\MyDynamicIsland\spotify-token.json`, encrypted with Windows DPAPI to your account — unreadable by other users or machines. Settings, token, and lyrics cache all live in `AppData`, never in the repo.

## Build from source

```
dotnet build -c Release        # needs the .NET 8 SDK, Windows 10/11 target
publish-app.bat                # self-contained single-file exe into Published\
msix\make-msix.ps1             # optional packaged build (notifications + location)
```

## License

MIT — see [LICENSE](LICENSE).
