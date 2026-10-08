# My Dynamic Island

A desktop-first Windows 10/11 WinForms island inspired by the macOS notch experience. The pure-black attached silhouette flows directly from the top screen edge and expands into a focused media card.

## Interaction

- Hover the island for a compact now-playing preview.
- Click the island to open or close the card.
- The card has three pages — Now Playing, Search, and Today. Turn them with the mouse wheel while the card is open (one notch turns one page, with a short settle so a hard flick can't blow through them), or tap a dot in the fixed page indicator on the card's right edge.
- The queue button on the media page expands the "Playing Next" list below the card, exactly as before — it belongs to the media page, not to the page cycle. Over the list itself the wheel scrolls the tracks.
- The Search page is the typed question card: a collapsed pill shows a lens, clicking it opens the card, Enter asks (through the configurable AI URL, with the words also placed on the clipboard), Esc leaves. The tray's "Ask anything (AI)" still works from anywhere.
- The Today page is one page with two faces: the collapsed pill shows the time and battery icon — hover it and the pill widens to reveal the full percentage; expanded it shows weather (with the sky tinted to the current conditions) and the agenda/calendar, and a small refresh beside the city name re-pulls the forecast on demand.
- Right-click the island for page, AI agent, display, and exit controls.
- Double-click the tray icon to open the settings.
- Press `Esc` to collapse the active card (and retract the queue extension with it).
- Auto-hide (on by default): after a few idle seconds the notch slides up past the screen edge, leaving a 2px sliver. Push the cursor to the top edge over its slot — or any event (new track, volume, notification) — and it glides back. Toggle it and the delay in Settings.

The battery readout in the collapsed Today pill appears only on machines with a battery (and can be switched off in Settings); low-battery and charging alerts keep their own popups regardless.

## Download & run — no tooling required

Grab **`MyDynamicIsland.exe`** from this repository's [Releases](../../releases) page. It is one self-contained file: nothing to install, no admin rights, no .NET runtime to download, no Visual Studio. Double-click and the island appears at the top edge of the screen; the tray icon holds the controls.

First run on a fresh download: Windows SmartScreen will likely say *"Windows protected your PC"* — that's because it's an unsigned hobby build, not malware. Click **More info → Run anyway**. (Media info/controls rely on Windows' system media sessions: whatever you play — Spotify, browsers, players — must report to Windows.)

Optional Spotify queue: with the app shipped, open the island's **Playing Next** page and press **Connect Spotify** — a browser tab asks for approval once, tokens are then kept encrypted on your machine (see Security below). No dashboard, no setup. A Premium account is required by Spotify for the up-next list; everything else works without Spotify at all.

Weather just works: it uses Windows' location (or one HTTPS IP approximation) unless your internet or privacy settings block it — or type a city in Settings to pin it.

## Security & Privacy

**Network footprint — every outbound call, and when it happens:**

| Endpoint | Why | Only when |
|---|---|---|
| `api.open-meteo.com` (HTTPS) | forecast for the Today page | weather fetch, city known |
| `geocoding-api.open-meteo.com` (HTTPS) | typed city → coordinates | you typed a city |
| `api.bigdatacloud.net` (HTTPS) | approximate coordinates from IP | auto-location on, no typed city, OS location denied |
| `lrclib.net` (HTTPS) | synced lyrics line | media track changes, media on |
| `accounts.spotify.com`, `api.spotify.com` (HTTPS) | OAuth PKCE + the Playing Next list | queue opened with a client id set |
| `https://127.0.0.1:54123/callback` | your browser's OAuth return hop, loopback only | during sign-in |

No telemetry, no update pings, no third-party server ever sees anything unless a feature above is in use. The former HTTP (plain-text) IP fallback is gone by design: location must never travel unencrypted.

**Location policy:** typed city always wins; otherwise Windows' own location service (OS consent, works in the packaged build with the `userLocation` capability); otherwise one HTTPS IP approximation. Automatic readings are never stored — coordinates live in memory for a single forecast request. Settings → *Automatic location for weather* turns every automatic source off; with it off only a typed city is ever used.

**Spotify:** a Client ID is a public identifier, not a secret — this is OAuth 2.0 PKCE, so there is no embedded app secret. Per-user tokens are held in `%LocalAppData%\MyDynamicIsland\spotify-token.json`, encrypted with Windows DPAPI to your Windows account (unreadable by other users or machines). Everything personal — settings, token, lyrics cache — lives in `AppData`, never in the project, never in the repo.

**Contributors / self-builders:** sign with your own certificate; the install script expects a locally generated package. No signing key belongs in git.

Calendar events require the MSIX-packaged build with the `appointments` capability and a Windows-connected calendar account, and degrade silently when offline.

## Build from source (only if you want to tinker)

```powershell
dotnet publish MyDynamicIsland.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o Published
```

Needs the free .NET 8 SDK once; the `Published\MyDynamicIsland.exe` output is the same self-contained file the Releases page carries. Visual Studio is not required either way — `dotnet build -c Release` works from a plain terminal.
