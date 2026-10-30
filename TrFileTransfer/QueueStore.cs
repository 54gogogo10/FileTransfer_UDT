using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace TrFileTransfer
{
    /// <summary>
    /// Persists the send queue so a queued batch survives a restart — dragging a
    /// dozen files into the queue and losing them to a reboot is exactly the
    /// overnight workflow this store protects. One line per task, '|'-separated:
    /// '|' is invalid in Windows file names, so a path can never break the format.
    /// Must stay C# 5: the test build compiles this file with the old csc.exe.
    /// </summary>
    public static class QueueStore
    {
        public const int MaxTasks = 500;

        public static string DefaultPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TrFileTransfer", "queue.txt");
            }
        }

        public static string Serialize(QueuedTask t)
        {
            return string.Join("|", new[]
            {
                t.FilePath ?? "",
                t.IsFolder ? "1" : "0",
                t.ServerIp ?? "",
                t.Port.ToString(CultureInfo.InvariantCulture),
                t.IsUdp ? "1" : "0",
                t.IsRawUdp ? "1" : "0",
                t.SrcPort.ToString(CultureInfo.InvariantCulture),
                t.Concurrency.ToString(CultureInfo.InvariantCulture),
                t.VerifyHash ? "1" : "0",
                t.SpeedLimit.ToString(CultureInfo.InvariantCulture)
            });
        }

        public static QueuedTask Parse(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            string[] f = line.Split('|');
            if (f.Length < 10) return null;
            int port, srcPort, concurrency, speed;
            if (!int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out port)) return null;
            if (!int.TryParse(f[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out srcPort)) return null;
            if (!int.TryParse(f[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out concurrency)) return null;
            if (!int.TryParse(f[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out speed)) return null;
            if (f[0].Length == 0 || port < 1 || port > 65535) return null;
            return new QueuedTask
            {
                FilePath = f[0],
                IsFolder = f[1] == "1",
                ServerIp = f[2],
                Port = port,
                IsUdp = f[4] == "1",
                IsRawUdp = f[5] == "1",
                SrcPort = srcPort,
                Concurrency = concurrency,
                VerifyHash = f[8] == "1",
                SpeedLimit = speed
            };
        }

        public static void Save(string path, IEnumerable<QueuedTask> tasks)
        {
            try
            {
                var lines = new List<string>();
                if (tasks != null)
                {
                    foreach (var t in tasks)
                    {
                        if (t == null) continue;
                        if (lines.Count >= MaxTasks) break;
                        lines.Add(Serialize(t));
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, lines.ToArray());
            }
            catch { }
        }

        /// <summary>All persisted tasks, bad lines skipped; empty when no file.</summary>
        public static List<QueuedTask> Load(string path)
        {
            var result = new List<QueuedTask>();
            try
            {
                if (!File.Exists(path)) return result;
                foreach (string line in File.ReadAllLines(path))
                {
                    QueuedTask t = Parse(line);
                    if (t != null) result.Add(t);
                    if (result.Count >= MaxTasks) break;
                }
            }
            catch { }
            return result;
        }
    }
}
