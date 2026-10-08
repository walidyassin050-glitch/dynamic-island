using System;
using System.IO;
using System.Text.Json;

namespace MyDynamicIsland
{
    public sealed class AppSettings
    {
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyDynamicIsland");
        private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

        public bool MediaActivity { get; set; } = true;
        public bool BatteryActivity { get; set; } = true;
        public bool LowBatteryAlerts { get; set; } = true;
        public bool WindowsNotifications { get; set; } = true;
        public bool NetworkChanges { get; set; } = true;
        public bool DeviceChanges { get; set; } = true;
        public bool BrightnessChanges { get; set; } = true;
        public bool WelcomeGreeting { get; set; } = true;

        // Auto-hide: after this many seconds of nobody touching/hovering the
        // island and nothing on screen (popups, announcements, open card),
        // the notch slides up past the screen edge until only a 2px sliver
        // remains. Reaching the top edge over its slot brings it back at
        // once, as does any event the island wants to show.
        public bool AutoHide { get; set; } = true;
        public int AutoHideSeconds { get; set; } = 5;

        // Location privacy. When on (default), weather may use Windows' own
        // location service first, and an HTTPS IP approximation as fallback.
        // When off, ONLY the manually typed city below is ever used — the
        // app sends no location anywhere. Nothing is ever stored from the
        // automatic sources: coordinates live in memory for one forecast
        // request and die with the process.
        public bool AllowAutoLocation { get; set; } = true;


        // "Ask anything" deep link: {query} is replaced by the typed words
        // percent-encoded. Default opens Google's AI Mode; swap to
        // https://chatgpt.com/?q={query} , https://www.copilot.microsoft.com/?q={query} ,
        // https://www.perplexity.ai/?q={query} ... - anything that takes a search URL.
        public string AiAskUrl { get; set; } =
            "https://www.google.com/search?q={query}&udm=50";

        // Today-page weather. Blank = approximate from the public IP; a city
        // name (e.g. "Algiers") is resolved exactly through Open-Meteo's
        // geocoder and wins over the IP — VPNs and distant ISP gateways make
        // IP guessing wrong, this setting makes it deliberate.
        public string WeatherCity { get; set; } = "";

        // --- Spotify "Playing Next" ---
        // A Client ID is not a secret: it only names YOUR application, and
        // because this app is distributed as a plain exe, the authorization
        // code exchange that keeps it usable is locked to one exact redirect
        // URI. That is why the two values below have to be a matched pair: the
        // URI you register in the dashboard and the one written here must be
        // byte-identical, or Spotify refuses the exchange and nothing loads.
        // For a public release: paste YOUR registered (public) Client ID
        // once here. A Client ID is an identifier, not a secret — the PKCE
        // exchange below is what keeps it safe — so committing it is the
        // design Spotify expects for desktop apps. Every user then connects
        // with one click on the queue; the field in Settings stays as the
        // escape hatch for anyone who registers their own app.
        public const string EmbeddedSpotifyClientId = "27b76f34eb4a4ed4ac3d40e88c08c65e";
        public string SpotifyClientId { get; set; } = EmbeddedSpotifyClientId;

        // Defaults to the canonical IPv4 loopback form. `localhost` also works
        // but resolves to ::1 first, which is a second thing that can go wrong,
        // so the default deliberately avoids relying on the hosts file.
        public string SpotifyRedirectUri { get; set; } = DefaultSpotifyRedirect;

        public const string DefaultSpotifyRedirect = "http://127.0.0.1:54123/callback";

        // True only when the saved values could actually complete a handshake:
        // a Client ID (not the placeholder text), a loopback redirect, and a
        // non-privileged port.
        public bool SpotifyCredentialsUsable()
        {
            string id = (SpotifyClientId ?? "").Trim();
            if (id.Length < 16 || id.Replace("-", "").Length != id.Length) return false;
            if (!Uri.TryCreate((SpotifyRedirectUri ?? "").Trim(), UriKind.Absolute, out Uri? u))
                return false;
            if (!string.Equals(u.Scheme, "http", StringComparison.OrdinalIgnoreCase)) return false;
            if (!(u.Host == "127.0.0.1" || string.Equals(u.Host, "localhost",
                    StringComparison.OrdinalIgnoreCase))) return false;
            return !u.IsDefaultPort && u.Port > 1024;
        }

        // The line the settings window shows when what is typed in cannot work.
        public string SpotifyCredentialProblem()
        {
            string id = (SpotifyClientId ?? "").Trim();
            if (id.Length == 0) return "No client ID yet.";
            if (id.Contains(" ", StringComparison.Ordinal))
                return "Remove the spaces — copy only the Key from the app page.";
            if (id.Length < 16) return "A Spotify Client ID is 32 characters.";

            string raw = (SpotifyRedirectUri ?? "").Trim();
            if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? u))
                return "The redirect isn't a URL: " + raw;
            if (!string.Equals(u.Scheme, "http", StringComparison.OrdinalIgnoreCase))
                return "The redirect must start with http://";
            if (!(u.Host == "127.0.0.1" || string.Equals(u.Host, "localhost",
                    StringComparison.OrdinalIgnoreCase)))
                return "The redirect host must be 127.0.0.1 or localhost";
            if (u.IsDefaultPort)
                return "The redirect needs an explicit port, e.g. " + DefaultSpotifyRedirect;
            if (u.Port <= 1024)
                return "Ports below 1025 need admin rights to listen on.";
            return "";
        }

        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new AppSettings();
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath))
                    ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
            }
        }
    }
}
