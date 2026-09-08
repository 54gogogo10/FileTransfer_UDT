using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TrFileTransfer
{
    /// <summary>
    /// Transfer statistics: an append-only local log plus aggregation helpers.
    /// One line per completed transfer: "yyyy-MM-dd HH:mm:ss|S|R|peer|bytes|files|seconds".
    /// Must stay C# 5: the test build compiles this file with the old csc.exe.
    /// </summary>
    #pragma warning disable 1591

    public class StatsEntry
    {
        public DateTime When;
        public char Direction;   // 'S' = sent by this machine, 'R' = received
        public string Peer;      // IP of the other side
        public long Bytes;
        public int Files;
        public double Seconds;

        public string Serialize()
        {
            return When.ToString("yyyy-MM-dd HH:mm:ss") + "|" + Direction + "|" + (Peer ?? "") + "|" +
                Bytes.ToString(CultureInfo.InvariantCulture) + "|" +
                Files.ToString(CultureInfo.InvariantCulture) + "|" +
                Seconds.ToString("F1", CultureInfo.InvariantCulture);
        }

        public static StatsEntry Parse(string line)
        {
            string[] f = line.Split('|');
            if (f.Length != 6) return null;
            DateTime when;
            if (!DateTime.TryParseExact(f[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out when)) return null;
            if (f[1] != "S" && f[1] != "R") return null;
            long bytes;
            int files;
            double secs;
            if (!long.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes)) return null;
            if (!int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out files)) return null;
            if (!double.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out secs)) return null;
            if (bytes < 0 || files < 0 || secs < 0) return null;
            return new StatsEntry
            {
                When = when,
                Direction = f[1][0],
                Peer = f[2],
                Bytes = bytes,
                Files = files,
                Seconds = secs
            };
        }
    }

    /// <summary>Aggregated totals for the stats dialog.</summary>
    public class StatsSummary
    {
        public long TodayBytes;
        public int TodayCount;
        public long WeekBytes;
        public int WeekCount;
        public long AllBytes;
        public int AllCount;

        /// <summary>Per-peer volume, merged over both directions, sorted by total desc.</summary>
        public List<PeerStats> Peers = new List<PeerStats>();
    }

    public class PeerStats
    {
        public string Peer;
        public long Sent;
        public long Received;
        public int Count;
        public long Total { get { return Sent + Received; } }
    }

    public class StatsStore
    {
        private readonly string _path;
        private readonly object _lock = new object();

        public StatsStore(string path)
        {
            _path = path;
        }

        public static string DefaultPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TrFileTransfer", "stats.log");
            }
        }

        public void Append(StatsEntry entry)
        {
            if (entry == null) return;
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    File.AppendAllText(_path, entry.Serialize() + Environment.NewLine);
                    TryCompact();
                }
                catch { }
            }
        }

        /// <summary>Keeps the append-only file bounded: rewrites to the last 5000 lines
        /// once it grows past ~2 MB.</summary>
        private void TryCompact()
        {
            try
            {
                var fi = new FileInfo(_path);
                if (!fi.Exists || fi.Length < 2 * 1024 * 1024) return;
                string[] lines = File.ReadAllLines(_path);
                int keep = Math.Min(lines.Length, 5000);
                var kept = new string[keep];
                Array.Copy(lines, lines.Length - keep, kept, 0, keep);
                File.WriteAllLines(_path, kept);
            }
            catch { }
        }

        public List<StatsEntry> LoadAll()
        {
            var result = new List<StatsEntry>();
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_path)) return result;
                    foreach (var line in File.ReadAllLines(_path))
                    {
                        var entry = StatsEntry.Parse(line);
                        if (entry != null) result.Add(entry);
                    }
                }
                catch { }
            }
            return result;
        }

        /// <summary>Aggregates raw entries into today / last-7-days / all-time totals and
        /// a per-peer breakdown. "Today" is the local calendar day of 'now'.</summary>
        public static StatsSummary Aggregate(IEnumerable<StatsEntry> entries, DateTime now)
        {
            var summary = new StatsSummary();
            DateTime startOfToday = now.Date;
            DateTime weekStart = startOfToday.AddDays(-6);
            var peers = new Dictionary<string, PeerStats>();

            foreach (var e in entries)
            {
                summary.AllBytes += e.Bytes;
                summary.AllCount++;

                if (e.When >= weekStart)
                {
                    summary.WeekBytes += e.Bytes;
                    summary.WeekCount++;
                }
                if (e.When >= startOfToday)
                {
                    summary.TodayBytes += e.Bytes;
                    summary.TodayCount++;
                }

                string peer = string.IsNullOrEmpty(e.Peer) ? "?" : e.Peer;
                PeerStats ps;
                if (!peers.TryGetValue(peer, out ps))
                {
                    ps = new PeerStats { Peer = peer };
                    peers[peer] = ps;
                }
                if (e.Direction == 'S') ps.Sent += e.Bytes;
                else ps.Received += e.Bytes;
                ps.Count++;
            }

            summary.Peers = new List<PeerStats>(peers.Values);
            summary.Peers.Sort(delegate(PeerStats a, PeerStats b) { return b.Total.CompareTo(a.Total); });
            return summary;
        }
    }
}
