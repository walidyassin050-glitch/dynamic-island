using System.IO;
using Microsoft.Data.Sqlite;

namespace MyDynamicIsland
{
    // Lets the island see OTHER apps' downloads - Chrome, Edge, Firefox -
    // without any of them opting in. Every major browser writes an in-flight
    // download under its final name plus a marker extension (.crdownload /
    // .part), then renames it when done.
    //
    // Two subtleties the first cut missed:
    //  * Chrome often starts a download as "Unconfirmed xxx.crdownload" and
    //    RENAMES the temp file (not just the finish) once headers reveal the
    //    real name - so the watcher must follow the rename chain, otherwise
    //    a flawless download looks like a vanished temp file ("Cancelled").
    //  * The total size is not in the temp file, but Chromium browsers
    //    write it - plus the final target path and state - into their own
    //    History database while the download runs. A shared read of that file
    //    (SQLite, no exclusive lock held by the browser) turns the ring into
    //    real percent progress with the true final name. Incognito hides the
    //    row; then the card honestly degrades to "MB \u00b7 speed", never a fake
    //    spinner.
    //
    // Privacy, unchanged: names, sizes and completion facts from the user's
    // own Downloads folder and browser bookkeeping. No file contents, no
    // reading of browsing history beyond the downloads table of the active
    // transfer, no network.
    internal static class BrowserDownloads
    {
        static FileSystemWatcher? watcher;
        static System.Threading.Timer? poll;
        static string tempPath = "";
        static DateTime watchSince;
        static string lastLandedName = "";
        static DateTime lastLandedAt = DateTime.MinValue;
        static DateTime lastProbeAt = DateTime.MinValue;
        static long lastRaiseDone = -1;

        static readonly string DownloadsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

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
                watcher.Created += (_, e) => OnFileEvent(e.Name, e.FullPath);
                watcher.Changed += (_, e) => OnFileEvent(e.Name, e.FullPath);
                watcher.Deleted += (_, e) => OnDeleted(e.Name, e.FullPath);
                watcher.Renamed += (_, e) => OnRenamed(e.OldFullPath, e.Name, e.FullPath);
                poll = new System.Threading.Timer(Tick, null, 700, 700);
            }
            catch { watcher = null; }
        }

        static bool IsTempName(string? name) =>
            name != null && (name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase)
                           || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase));

        // ── event capture ─────────────────────────────────────────────────
        static void OnFileEvent(string? name, string fullPath)
        {
            if (name == null) return;
            if (IsTempName(name))
            {
                var cur = IslandDownloads.Current;
                if (cur is { Active: true } && string.Equals(cur.WatchPath, fullPath,
                        StringComparison.OrdinalIgnoreCase)) return;   // already following
                if (cur is { Active: true }) return;  // two files at once: keep the first
                Begin(fullPath);
                return;
            }
            // A non-temp file landing while we chase a temp one: Chrome's
            // final step without a Renamed event reaching us.
            var live = IslandDownloads.Current;
            if (live is { Active: true } && live.WatchPath.Length > 0)
            {
                lastLandedName = name;
                lastLandedAt = DateTime.Now;
            }
        }

        static void Begin(string path)
        {
            tempPath = path;
            watchSince = DateTime.Now;
            lastLandedName = ""; lastLandedAt = DateTime.MinValue;

            long len = 0;
            try { len = new FileInfo(path).Length; } catch { }
            var d = new IslandDownload { Title = NominalName(path), WatchPath = path, Total = -1 };
            d.Seed(len);
            IslandDownloads.Track(d);

            TryProbe(d);           // totals usually known within a heartbeat
            IslandDownloads.Raise();
        }

        // The honest display name: strip browser temp markers; "Unconfirmed …"
        // is never worth showing - the probe/chain will replace it.
        static string NominalName(string path)
        {
            string n = Path.GetFileName(path);
            if (n.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase))
                n = n[..^11];
            else if (n.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                n = n[..^5];
            if (n.StartsWith("Unconfirmed", StringComparison.OrdinalIgnoreCase)) return "download";
            return n.Length > 0 ? n : "download";
        }

        // ── rename chain ──────────────────────────────────────────────────
        static void OnRenamed(string oldFull, string? newName, string newFull)
        {
            var d = IslandDownloads.Current;
            if (d == null || !d.Active || d.WatchPath.Length == 0) return;
            if (!string.Equals(d.WatchPath, oldFull, StringComparison.OrdinalIgnoreCase)) return;

            if (IsTempName(newName))
            {
                // unconfirmed-123.crdownload -> realname.zip.crdownload
                d.WatchPath = newFull;
                tempPath = newFull;
                string shown = NominalName(newName!);
                if (shown != "download" || d.Title == "download") d.Title = shown;
                IslandDownloads.Raise();
                TryProbe(d);   // the row's current_path moved with the rename
                return;
            }

            // temp -> final: the download completing. This is the SUCCESS
            // case the first version mistook for a disappearance.
            d.Title = Path.GetFileName(newFull);
            long size = d.Done;
            try { size = Math.Max(size, new FileInfo(newFull).Length); } catch { }
            d.Seed(size);
            d.Total = size;              // it IS the whole thing now - 100% at rest
            d.Finished = true;
            d.Active = false;
            tempPath = "";
            IslandDownloads.Raise();
        }

        static void OnDeleted(string? name, string full)
        {
            var d = IslandDownloads.Current;
            if (d == null || !d.Active || d.WatchPath.Length == 0) return;
            if (!string.Equals(d.WatchPath, full, StringComparison.OrdinalIgnoreCase)) return;
            ResolveGone(d);
        }

        // ── polling: growth, totals, safety nets ──────────────────────────
        static void Tick(object? _)
        {
            var d = IslandDownloads.Current;
            if (d == null || !d.Active || d.WatchPath.Length == 0) return;

            long len;
            try { len = new FileInfo(d.WatchPath).Length; }
            catch { ResolveGone(d); return; }

            bool moved = len != d.Done;
            if (moved) d.Add(len - d.Done);

            // totals settle a beat after headers arrive - keep looking
            if (d.Total <= 0 && (DateTime.Now - lastProbeAt).TotalSeconds > 2.4)
                TryProbe(d);

            // repaint only when something actually changed - the whole
            // point of the lag complaint.
            if (moved || (d.Total > 0 && lastRaiseDone == -1))
            {
                lastRaiseDone = d.Done;
                IslandDownloads.Raise();
            }
        }

        // The temp file vanished without a rename we caught. Decide between
        // "it landed" and "it was cancelled" from whatever evidence exists.
        static void ResolveGone(IslandDownload d)
        {
            string guess = Path.ChangeExtension(d.WatchPath, null);

            if (File.Exists(guess)) { Land(d, guess); return; }

            if (lastLandedName.Length > 0 && lastLandedAt >= watchSince &&
                File.Exists(Path.Combine(DownloadsDir, lastLandedName)))
            { Land(d, Path.Combine(DownloadsDir, lastLandedName)); return; }

            var probed = ProbeFor(d.WatchPath);
            if (probed != null && probed.Value.state == 1 &&
                probed.Value.target.Length > 0 && File.Exists(probed.Value.target))
            { Land(d, probed.Value.target); return; }

            // anything new in the folder within the watch window and not a temp?
            try
            {
                foreach (var f in Directory.GetFiles(DownloadsDir))
                {
                    string n = Path.GetFileName(f);
                    if (IsTempName(n)) continue;
                    if (File.GetLastWriteTime(f) >= watchSince.AddSeconds(-2))
                    { Land(d, f); return; }
                }
            }
            catch { }

        d.Cancelled = true;
            d.Active = false;
            tempPath = "";
            IslandDownloads.Raise();
        }

        static void Land(IslandDownload d, string finalPath)
        {
            d.Title = Path.GetFileName(finalPath);
            long size = d.Done;
            try { size = Math.Max(size, new FileInfo(finalPath).Length); } catch { }
            d.Seed(size); d.Total = size;
            d.Finished = true; d.Active = false;
            tempPath = "";
            IslandDownloads.Raise();
        }

        // ── Chromium History probe: total_bytes + target for a path ──────
        record struct ProbeRow(long total, string target, int state);

        static void TryProbe(IslandDownload d)
        {
            lastProbeAt = DateTime.Now;
            var row = ProbeFor(d.WatchPath);
            if (row == null) return;
            if (row.Value.total > 0 && d.Total <= 0) d.Total = row.Value.total;
            if (row.Value.target.Length > 0)
            {
                string n = Path.GetFileName(row.Value.target);
                if (d.Title == "download" || d.Title.StartsWith("Unconfirmed",
                        StringComparison.OrdinalIgnoreCase)) d.Title = n;
            }
        }

        static ProbeRow? ProbeFor(string currentPath)
        {
            foreach (var hist in HistoryCandidates())
            {
                var hit = ProbeOne(hist, currentPath);
                if (hit != null) return hit;
            }
            return null;
        }

        static IEnumerable<string> HistoryCandidates()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var browser in new[] { @"Google\Chrome\User Data", @"Microsoft\Edge\User Data" })
            {
                string root = Path.Combine(local, browser);
                if (!Directory.Exists(root)) continue;
                foreach (var profile in new[] { "Default", "Profile 1", "Profile 2", "Profile 3" })
                {
                    string h = Path.Combine(root, profile, "History");
                    if (File.Exists(h)) yield return h;
                }
            }
        }

        static ProbeRow? ProbeOne(string historyPath, string currentPath)
        {
            // Copy first: the browser keeps History open (shared), rows settle
            // through a WAL - copying both makes a private, checkpointable
            // snapshot without ever touching the user's files.
            string tmp = Path.Combine(Path.GetTempPath(),
                $"mdis_histsnap_{Environment.ProcessId}_{Guid.NewGuid():N}.sqlite");
            try
            {
                CopyShared(historyPath, tmp);
                string wal = historyPath + "-wal";
                if (File.Exists(wal)) CopyShared(wal, tmp + "-wal");
                if (File.Exists(historyPath + "-shm"))
                { try { File.WriteAllBytes(tmp + "-shm", Array.Empty<byte>()); } catch { } }

                using var conn = new SqliteConnection($"Data Source={tmp}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT current_path, total_bytes, target_path, state FROM downloads " +
                    "ORDER BY start_time DESC LIMIT 64";
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    try
                    {
                        string current = rd.IsDBNull(0) ? "" : rd.GetString(0);
                        long total = rd.IsDBNull(1) ? 0 : rd.GetInt64(1);
                        string target = rd.IsDBNull(2) ? "" : rd.GetString(2);
                        int state = rd.IsDBNull(3) ? 0 : rd.GetInt32(3);
                        if (Matches(currentPath, current, target))
                            return new ProbeRow(total, target, state);
                    }
                    catch { /* schema drift on an old row: keep scanning */ }
                }
            }
            catch { }
            finally
            {
                TryDelete(tmp); TryDelete(tmp + "-wal"); TryDelete(tmp + "-shm");
            }
            return null;
        }

        // An exact current_path hit is the strong signal (the browser updates
        // that column when an "Unconfirmed" temp gets its real name). Fallback
        // while a name is still being revealed: the target file's name equals
        // the watched name once the temp suffix is stripped.
        static bool Matches(string watching, string currentPath, string target)
        {
            if (currentPath.Length > 0 && string.Equals(currentPath, watching,
                    StringComparison.OrdinalIgnoreCase)) return true;
            if (target.Length == 0) return false;
            string watchStem = Path.GetFileNameWithoutExtension(watching);  // "report.pdf"
            return watchStem.Length > 1 && string.Equals(watchStem,
                Path.GetFileName(target), StringComparison.OrdinalIgnoreCase);
        }

        static void CopyShared(string src, string dst)
        {
            using var s = new FileStream(src, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var d2 = new FileStream(dst, FileMode.Create);
            s.CopyTo(d2);
        }

        static void TryDelete(string p)
        { try { if (File.Exists(p)) File.Delete(p); } catch { } }
    }
}
