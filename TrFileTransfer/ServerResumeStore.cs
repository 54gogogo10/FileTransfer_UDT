using System;
using System.IO;
using System.Text;

namespace TrFileTransfer
{
    /// <summary>
    /// Persists server-side resume state to disk so a server restart does not
    /// lose the received offset of an in-progress resumable transfer.
    /// Format is the same key=value layout as the client ResumeState.
    /// </summary>
    public static class ServerResumeStore
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TrFileTransfer", "server-resume");

        public static void EnsureDir()
        {
            Directory.CreateDirectory(Dir);
        }

        public static string GetPath(Guid sessionId)
        {
            return Path.Combine(Dir, sessionId.ToString("N") + ".json");
        }

        /// <summary>Saves the server-side fields of a resume state (includes SavePath and ReceivedBytes).</summary>
        public static void Save(ResumeState state)
        {
            EnsureDir();
            var sb = new StringBuilder();
            sb.AppendLine("SessionId=" + state.SessionId.ToString("N"));
            sb.AppendLine("TotalSize=" + state.TotalSize);
            sb.AppendLine("ReceivedBytes=" + state.ReceivedBytes);
            sb.AppendLine("FileName=" + (state.FileName ?? "").Replace("\r", "").Replace("\n", ""));
            sb.AppendLine("SavePath=" + (state.SavePath ?? "").Replace("\r", "").Replace("\n", ""));
            sb.AppendLine("Peer=" + (state.Peer ?? "").Replace("\r", "").Replace("\n", ""));
            sb.AppendLine("Created=" + state.Created.ToString("o"));
            File.WriteAllText(GetPath(state.SessionId), sb.ToString(), Encoding.UTF8);
        }

        /// <summary>Loads a server resume state, or null if absent/corrupt.</summary>
        public static ResumeState Load(Guid sessionId)
        {
            string path = GetPath(sessionId);
            if (!File.Exists(path)) return null;
            var state = new ResumeState { SessionId = sessionId };
            try
            {
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int idx = line.IndexOf('=');
                    if (idx < 0) continue;
                    string key = line.Substring(0, idx);
                    string val = line.Substring(idx + 1);
                    switch (key)
                    {
                        case "TotalSize": { long v; if (long.TryParse(val, out v)) state.TotalSize = v; break; }
                        case "ReceivedBytes": { long v; if (long.TryParse(val, out v)) state.ReceivedBytes = v; break; }
                        case "FileName": state.FileName = val; break;
                        case "SavePath": state.SavePath = val; break;
                        case "Peer": state.Peer = val; break;
                        case "Created":
                            try
                            {
                                state.Created = DateTime.Parse(val,
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.RoundtripKind);
                            }
                            catch (FormatException) { }
                            break;
                    }
                }
                return state;
            }
            catch { return null; }
        }

        public static void Delete(Guid sessionId)
        {
            string path = GetPath(sessionId);
            try { File.Delete(path); } catch { }
        }

        /// <summary>Deletes orphaned session files older than the given age (clients that never came back).</summary>
        public static void CleanupStale(int days)
        {
            try
            {
                if (!Directory.Exists(Dir)) return;
                DateTime cutoff = DateTime.Now.AddDays(-days);
                string[] files = Directory.GetFiles(Dir, "*.json");
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        if (File.GetLastWriteTime(files[i]) < cutoff)
                            File.Delete(files[i]);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
