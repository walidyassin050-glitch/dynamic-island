using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel.Appointments;

namespace MyDynamicIsland
{
    // Live data for the Today page. SetWeather()/SetAgenda() existed as feed
    // methods on the form with nothing behind them — the panel drew its own
    // placeholders forever. This is the wire.
    //
    // Weather chains free, keyless providers over HTTPS only, best source
    // first: Windows' own location service, else BigDataCloud approximates
    // the public IP into lat/lon + a city name (Open-Meteo has coordinates
    // but no names to show), and Open-Meteo answers current conditions for
    // those coordinates. Both degrade silently: offline just keeps whatever
    // was last shown, or --° before the first answer ever lands.
    //
    // Agenda reads the Windows AppointmentStore (Outlook/Calendar accounts).
    // That API requires package identity, so it works in the MSIX install
    // (with the appointments capability in AppxManifest.xml) and throws
    // UnauthorizedAccessException in a loose exe — which the status enum
    // below turns into one honest line on the panel.

    internal sealed class WeatherInfo
    {
        public bool IsDay = true;
        public string City = "";
        public string Condition = "";
        public string Temp = "";
        public string IconKind = "sun";   // sun | partly | cloud | fog | rain | snow | storm
    }

    internal enum AgendaStatus { Ok, NoCalendars, NoAccess, Failed }

    internal sealed class AgendaEntry
    {
        public string Title = "";
        public string TimeRange = "";
    }

    internal static class WeatherFeed
    {
        // One process-wide client: pooled connections, a hard 12 s ceiling.
        private static readonly HttpClient http = new()
        {
            Timeout = TimeSpan.FromSeconds(12)
        };

        static WeatherFeed()
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MyDynamicIsland/1.0 (desktop dynamic island)");
        }

        private readonly record struct Geo(double Lat, double Lon, string City);

        // A city typed in Settings is resolved through Open-Meteo's free
        // geocoder and wins; blank (or a name it can't find) falls through to
        // IP detection. Exact beats approximate whenever the user chose.
        public static async Task<WeatherInfo?> LoadAsync(string? city, bool allowAuto)
        {
            Geo? loc = null;
            // priority chain: the typed city ALWAYS wins; otherwise Windows'
            // own location service (OS-consent-managed, no network beyond
            // the forecast itself); and only then the HTTPS IP approximation.
            if (!string.IsNullOrWhiteSpace(city)) loc = await ByCityAsync(city.Trim());
            if (loc == null && allowAuto) loc = await BySystemAsync();
            if (loc == null && allowAuto) loc = await ByIpAsync();
            if (loc == null) return null;

            try
            {
                string url = "https://api.open-meteo.com/v1/forecast" +
                    "?latitude=" + loc.Value.Lat.ToString("R", CultureInfo.InvariantCulture) +
                    "&longitude=" + loc.Value.Lon.ToString("R", CultureInfo.InvariantCulture) +
                    "&current=temperature_2m,weather_code,is_day" +
                    "&forecast_days=1&timezone=auto";

                using JsonDocument wx = JsonDocument.Parse(await http.GetStringAsync(url));
                JsonElement cur = wx.RootElement.GetProperty("current");
                double temp = cur.GetProperty("temperature_2m").GetDouble();
                int code = cur.GetProperty("weather_code").GetInt32();
                int isDay = cur.TryGetProperty("is_day", out JsonElement d) ? d.GetInt32() : 1;

                return new WeatherInfo
                {
                    IsDay = isDay != 0,
                    City = loc.Value.City,
                    Condition = Describe(code),
                    Temp = $"{Math.Round(temp):0}°",
                    IconKind = IconFor(code, isDay != 0),
                };
            }
            catch
            {
                return null;    // offline or endpoint hiccup: keep last known
            }
        }

        private static async Task<Geo?> ByCityAsync(string name)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(await http.GetStringAsync(
                    "https://geocoding-api.open-meteo.com/v1/search?name=" +
                    Uri.EscapeDataString(name) + "&count=1&language=en"));
                if (!doc.RootElement.TryGetProperty("results", out JsonElement res) ||
                    res.ValueKind != JsonValueKind.Array || res.GetArrayLength() == 0)
                    return null;

                JsonElement g = res[0];
                return new Geo(g.GetProperty("latitude").GetDouble(),
                    g.GetProperty("longitude").GetDouble(),
                    g.GetProperty("name").GetString() ?? name);
            }
            catch { return null; }
        }

        // One IP provider only, deliberately: ip-api was dropped because its
        // free tier is plain HTTP — a location lookup must never travel in
        // clear text. BigDataCloud is HTTPS with a no-key free client tier.
        private static async Task<Geo?> ByIpAsync()
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(await http.GetStringAsync(
                    "https://api.bigdatacloud.net/data/reverse-geocode-client?localityLanguage=en"));
                JsonElement r = doc.RootElement;
                if (r.TryGetProperty("latitude", out JsonElement la) &&
                    r.TryGetProperty("longitude", out JsonElement lo))
                {
                    string city = FirstNotEmpty(r, "city", "locality", "principalSubdivision", "countryName");
                    return new Geo(la.GetDouble(), lo.GetDouble(),
                        city.Length > 0 ? city : "Your area");
                }
            }
            catch { }
            return null;    // (no ip-api fallback: that one speaks HTTP — see comment above)
        }

        // Windows' location service: on a packaged build this raises the
        // standard one-time consent (AppxManifest needs <Capability
        // Name="userLocation"/>); on a loose exe it follows the machine's
        // Settings > Privacy > Location > "desktop apps" switch. Every
        // refusal or timeout simply returns null — the IP layer takes over
        // invisibly, and the worst case is a city typed by hand.
        private static async Task<Geo?> BySystemAsync()
        {
            try
            {
                var access = await Windows.Devices.Geolocation.Geolocator.RequestAccessAsync();
                if (access != Windows.Devices.Geolocation.GeolocationAccessStatus.Allowed)
                    return null;
                var gl = new Windows.Devices.Geolocation.Geolocator { DesiredAccuracyInMeters = 50000 };
                var task = gl.GetGeopositionAsync().AsTask();
                if (await Task.WhenAny(task, Task.Delay(5000)) != task) return null;
                var pos = (await task).Coordinate.Point.Position;
                return new Geo(pos.Latitude, pos.Longitude, "My area");
            }
            catch { return null; }
        }

        private static string FirstNotEmpty(JsonElement r, params string[] props)
        {
            foreach (string p in props)
                if (r.TryGetProperty(p, out JsonElement v) &&
                    v.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(v.GetString())) return v.GetString()!;
            return "";
        }

        // WMO weather codes as Open-Meteo reports them.
        private static string Describe(int code) => code switch
        {
            0 => "Clear",
            1 => "Mainly clear",
            2 => "Partly cloudy",
            3 => "Overcast",
            45 or 48 => "Fog",
            51 or 53 or 55 => "Drizzle",
            56 or 57 => "Freezing drizzle",
            61 => "Light rain",
            63 => "Rain",
            65 => "Heavy rain",
            66 or 67 => "Freezing rain",
            71 => "Light snow",
            73 => "Snow",
            75 => "Heavy snow",
            77 => "Snow grains",
            80 => "Light showers",
            81 or 82 => "Rain showers",
            85 or 86 => "Snow showers",
            95 => "Thunderstorm",
            96 or 99 => "Severe thunderstorm",
            _ => "",
        };

        private static string IconFor(int code, bool day) => code switch
        {
            0 or 1 => day ? "sun" : "cloud",
            2 => "partly",
            3 => "cloud",
            45 or 48 => "fog",
            >= 51 and <= 67 => "rain",
            >= 71 and <= 77 => "snow",
            80 or 81 or 82 => "rain",
            85 or 86 => "snow",
            >= 95 => "storm",
            _ => "cloud",
        };
    }

    internal static class AgendaFeed
    {
        public static async Task<(AgendaStatus status, List<AgendaEntry> items)> LoadTodayAsync()
        {
            var items = new List<AgendaEntry>();
            try
            {
                // For the MSIX build this is the USER's appointment store
                // (Outlook/Calendar accounts) and needs the "appointments"
                // capability in AppxManifest.xml. For a loose exe it throws
                // UnauthorizedAccessException — hence the NoAccess status,
                // which the panel states plainly instead of faking an empty
                // day. Windows raises a one-time consent prompt the first
                // time a packaged app reads it.
                AppointmentStore store = await AppointmentManager.RequestStoreAsync(
                    AppointmentStoreAccessType.AllCalendarsReadOnly);

                IReadOnlyList<AppointmentCalendar> calendars =
                    await store.FindAppointmentCalendarsAsync();
                if (calendars.Count == 0)
                    return (AgendaStatus.NoCalendars, items);

                DateTimeOffset from = new(DateTime.Today);
                IReadOnlyList<Appointment> found =
                    await store.FindAppointmentsAsync(from, TimeSpan.FromDays(1));

                foreach (Appointment ap in found
                             .Where(a => !string.IsNullOrWhiteSpace(a.Subject))
                             .OrderBy(a => a.StartTime)
                             .Take(4))
                {
                    DateTimeOffset end = ap.StartTime + ap.Duration;
                    string range = ap.AllDay
                        ? "All day"
                        : ap.StartTime.LocalDateTime.ToString("t") + " – " +
                          end.LocalDateTime.ToString("t");

                    items.Add(new AgendaEntry { Title = ap.Subject.Trim(), TimeRange = range });
                }

                return (AgendaStatus.Ok, items);
            }
            catch (UnauthorizedAccessException)
            {
                return (AgendaStatus.NoAccess, items);
            }
            catch
            {
                return (AgendaStatus.Failed, items);
            }
        }
    }

    // Throttling wrapper the form drives from a cheap timer: agenda wants the
    // freshest data (appointments move all day), weather is fine at 15
    // minutes, and a slow network must never stack two runs.
    internal sealed class TodayDataFeed
    {
        private readonly Func<(string city, bool allowAuto)> locationPrefs;
        private readonly Action<WeatherInfo> applyWeather;
        private readonly Action<AgendaStatus, List<AgendaEntry>> applyAgenda;

        private DateTime nextAgendaAt = DateTime.MinValue;
        private DateTime nextWeatherAt = DateTime.MinValue;
        private bool busy;

        public TodayDataFeed(Func<(string city, bool allowAuto)> locationPrefs, Action<WeatherInfo> applyWeather,
            Action<AgendaStatus, List<AgendaEntry>> applyAgenda)
        {
            this.locationPrefs = locationPrefs;
            this.applyWeather = applyWeather;
            this.applyAgenda = applyAgenda;
        }

        // Called from ApplyNewSettings the moment the typed city changes, so
        // the panel re-reads on the next knock (5 s) instead of waiting out
        // the quarter-hour cadence with the old town's temperature.
        public void InvalidateWeather() => nextWeatherAt = DateTime.MinValue;

        public void Tick()
        {
            if (busy) return;

            bool dueWeather = DateTime.Now >= nextWeatherAt;
            bool dueAgenda = DateTime.Now >= nextAgendaAt;
            if (!dueWeather && !dueAgenda) return;

            busy = true;
            _ = RunAsync(dueWeather, dueAgenda);
        }

        private async Task RunAsync(bool dueWeather, bool dueAgenda)
        {
            try
            {
                if (dueAgenda)
                {
                    nextAgendaAt = DateTime.Now.AddMinutes(5);
                    var (status, items) = await AgendaFeed.LoadTodayAsync();
                    applyAgenda(status, items);
                }

                if (dueWeather)
                {
                    nextWeatherAt = DateTime.Now.AddMinutes(15);
                    // Failure keeps the last known sky on screen: a widget
                    // briefly offline should not erase the weather — it shows
                    // the stale reading until a better one arrives.
                    WeatherInfo? wx = await WeatherFeed.LoadAsync(locationPrefs().city, locationPrefs().allowAuto);
                    if (wx != null) applyWeather(wx);
                }
            }
            finally
            {
                busy = false;
            }
        }
    }
}
