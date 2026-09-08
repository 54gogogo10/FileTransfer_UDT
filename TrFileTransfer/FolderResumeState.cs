using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TrFileTransfer
{
    /// <summary>
    /// Client-side state for an interrupted folder resume session (0x04). Persisted to
    /// %AppData%\TrFileTransfer\folder-resume so the resume dialog can offer the session
    /// again. Server-side progress is derived from the files on disk; this record only
    /// remembers the session identity, the source folder, and the last known progress.
    /// </summary>
    #pragma warning disable 1591
    public class FolderResumeState
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TrFileTransfer", "folder-resume");

        public Guid SessionId;
        public string FolderPath;
        public string FolderName;
        public string ServerIp;
        public int Port;
        public bool IsUdt;
        public DateTime Created;
        public int FileCount;
        public long TotalBytes;
        public long SentBytes;
        /// <summary>True for sync-mode sessions — they are managed by 同步模式 (kept after
        /// completion so repeated syncs send only differences) and hidden from the
        /// resume dialog.</summary>
        public bool IsSync;

        /// <summary>
        /// Stable session ID for a sync pair (source folder + target + protocol). The
        /// same source folder synced to the same target always maps to the same session,
        /// so the server-side 0x04 scan skips unchanged files and only differences travel.
        /// </summary>
        public static Guid DeriveSyncSession(string folderPath, string serverIp, int port, bool isUdt)
        {
            string key = "sync|" + (folderPath ?? "").TrimEnd('\\', '/').ToLowerInvariant()
                + "|" + (serverIp ?? "")
                + "|" + port.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "|" + (isUdt ? "udt" : "tcp");
            using (var sha1 = System.Security.Cryptography.SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(key));
                var bytes = new byte[16];
                Buffer.BlockCopy(hash, 0, bytes, 0, 16);
                return new Guid(bytes);
            }
        }

        private static string GetPath(Guid sessionId)
        {
            return Path.Combine(Dir, sessionId.ToString("N") + ".json");
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            var sb = new StringBuilder();
            sb.AppendLine("SessionId=" + SessionId.ToString("N"));
            sb.AppendLine("FolderPath=" + (FolderPath ?? "").Replace("\r", "").Replace("\n", ""));
            sb.AppendLine("FolderName=" + (FolderName ?? "").Replace("\r", "").Replace("\n", ""));
            sb.AppendLine("ServerIp=" + (ServerIp ?? "").Replace("\r", "").Replace("\n", ""));
            sb.AppendLine("Port=" + Port);
            sb.AppendLine("IsUdt=" + (IsUdt ? "1" : "0"));
            sb.AppendLine("Created=" + Created.ToString("o"));
            sb.AppendLine("FileCount=" + FileCount);
            sb.AppendLine("TotalBytes=" + TotalBytes);
            sb.AppendLine("SentBytes=" + SentBytes);
            sb.AppendLine("IsSync=" + (IsSync ? "1" : "0"));
            File.WriteAllText(GetPath(SessionId), sb.ToString(), Encoding.UTF8);
        }

        public static FolderResumeState Load(Guid sessionId)
        {
            string path = GetPath(sessionId);
            if (!File.Exists(path)) return null;
            var state = new FolderResumeState { SessionId = sessionId };
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
                        case "FolderPath": state.FolderPath = val; break;
                        case "FolderName": state.FolderName = val; break;
                        case "ServerIp": state.ServerIp = val; break;
                        case "Port": { int v; if (int.TryParse(val, out v)) state.Port = v; break; }
                        case "IsUdt": state.IsUdt = val == "1"; break;
                        case "Created":
                            try
                            {
                                state.Created = DateTime.Parse(val,
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.RoundtripKind);
                            }
                            catch (FormatException) { }
                            break;
                        case "FileCount": { int v; if (int.TryParse(val, out v)) state.FileCount = v; break; }
                        case "TotalBytes": { long v; if (long.TryParse(val, out v)) state.TotalBytes = v; break; }
                        case "SentBytes": { long v; if (long.TryParse(val, out v)) state.SentBytes = v; break; }
                        case "IsSync": state.IsSync = val == "1"; break;
                    }
                }
                return state;
            }
            catch { return null; }
        }

        public static List<FolderResumeState> ListAll()
        {
            var result = new List<FolderResumeState>();
            try
            {
                if (!Directory.Exists(Dir)) return result;
                foreach (var file in Directory.GetFiles(Dir, "*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    Guid sid;
                    if (!Guid.TryParse(name, out sid)) continue;
                    var state = Load(sid);
                    if (state != null && !state.IsSync) result.Add(state);
                }
            }
            catch { }
            return result;
        }

        public static void Delete(Guid sessionId)
        {
            try { File.Delete(GetPath(sessionId)); } catch { }
        }
    }
}
