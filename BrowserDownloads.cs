using System.IO;

namespace MyDynamicIsland
{
    // Lets the island see OTHER apps' downloads — Chrome, Edge, Firefox —
    // without any of them opting in. Every major browser writes an in-flight
    // download under its final name plus a marker extension (.crdownload /
    // .part), then renames it when done. Watching for those files in the
    // usual landing folders is enough: name, size, growth speed, completion.
    // Nothing is opened for reading (length comes from the directory entry),
    // no contents, no URLs, no network. It's the local-eyes rule again.
    internal static class BrowserDownloads
    {
        static FileSystemWatcher? watcher;
        static System.Threading.Timer? poll;
        static string dir = "", tempName = "";
        static long lastLen;
        static int stalls;
        static readonly string DownloadsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        static readonly string DesktopDir =
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        public static void Start()
        {
            if (watcher != null) return;
            try
            {
                Directory.CreateDirectory(DownloadsDir);
                watcher = new FileSystemWatcher(DownloadsDir)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size |
                                   NotifyFilters.LastWrite,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                };
                watcher.Created += (_, e) => OnEvent(e.Name);
                watcher.Changed += (_, e) => OnEvent(e.Name);
                watcher.Deleted += (_, e) => OnEvent(e.Name);
                watcher.Renamed += (_, e) => OnEvent(e.Name);
                poll = new System.Threading.Timer(Tick, null, 700, 700);
            }
            catch { watcher = null; }
        }

        static bool IsTempName(string? name) =>
            name != null && (name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase)
                           || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase));

        static void OnEvent(string? name)
        {
            if (!IsTempName(name)) return;
            string path = Path.Combine(DownloadsDir, name!);
            var cur = IslandDownloads.Current;
            if (cur is { Active: true } && cur.WatchPath == path) return; // already following
            Begin(path);
        }

        static void Begin(string path)
        {
            dir = DownloadsDir;
            tempName = Path.GetFileName(path);
            long len = 0;
            try { len = new FileInfo(path).Length; } catch { }
            var d = new IslandDownload
            {
                Title = Path.GetFileNameWithoutExtension(path),
                Total = -1,               // browsers don't advertise it in the temp file
                WatchPath = path,
            };
            d.Seed(len);
            lastLen = len; stalls = 0;
            IslandDownloads.Track(d);
        }

        static void Tick(object? _)
        {
            var d = IslandDownloads.Current;
            if (d == null || !d.Active || d.WatchPath == null) return;

            long len = -1;
            try { len = new FileInfo(d.WatchPath).Length; } catch { }

            if (len >= 0)
            {
                stalls = 0;
                if (len > d.Done) d.Add(len - d.Done);
                IslandDownloads.Raise();   // cheap: Form1 repaints, ring grows
                return;
            }

            // The temp file is gone — either renamed to the final file
            // (success) or deleted (cancelled). Either way the transfer end
            // is a fact of the filesystem; we just identify which one.
            string final = Path.ChangeExtension(d.WatchPath, null);
            bool landed = false;
            try { landed = File.Exists(final) || FindLanded(d.WatchPath) != null; } catch { }
            if (landed)
            {
                TitleTo(d, final);
                d.Seed(SizeOf(d, final));
                d.Finished = true;
            }
            else
            {
                d.Cancelled = true;
            }
            d.Active = false;
            IslandDownloads.Raise();
        }

        // Chrome names it back exactly; be generous and match case-insensitively
        static string? FindLanded(string tempPath)
        {
            try
            {
                string baseName = Path.GetFileNameWithoutExtension(tempPath);
                foreach (var f in Directory.GetFiles(Path.GetDirectoryName(tempPath)!))
                    if (string.Equals(Path.GetFileName(f), baseName,
                        StringComparison.OrdinalIgnoreCase) &&
                        !IsTempName(Path.GetFileName(f)))
                        return f;
            }
            catch { }
            return null;
        }

        static void TitleTo(IslandDownload d, string final)
        {
            var f = FindLanded(final);
            d.Title = Path.GetFileName(f ?? final);
        }

        static long SizeOf(IslandDownload d, string final)
        {
            try { return Math.Max(d.Done, new FileInfo(FindLanded(final)!).Length); }
            catch { return d.Done; }
        }
    }
}
