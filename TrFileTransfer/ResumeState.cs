using System;
using System.IO;
using System.Text;

namespace TrFileTransfer
{
    public class ResumeState
    {
        public Guid SessionId;
        public long TotalSize;
        public long SentBytes;
        public long SourceMTime;
        // Server-side only (not persisted)
        public byte[] ExpectedFullHash;
        public string FileName;
        public string FilePath;
        public string ServerIp;
        public int Port;
        public bool IsUdt;
        public DateTime Created;
        // Server-side only (not persisted)
        public string SavePath;
        public FileStream WriteStream;
        public long ReceivedBytes;

        private static readonly string ResumeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TrFileTransfer", "resume");

        public static void EnsureDir()
        {
            Directory.CreateDirectory(ResumeDir);
        }

        public static string GetPath(Guid sessionId)
        {
            return Path.Combine(ResumeDir, sessionId.ToString("N") + ".json");
        }

        public void Save()
        {
            EnsureDir();
            var sb = new StringBuilder();
            // SessionId stored for human readability only; not used on load
            sb.AppendLine("SessionId=" + SessionId.ToString("N"));
            sb.AppendLine("TotalSize=" + TotalSize);
            sb.AppendLine("SentBytes=" + SentBytes);
            sb.AppendLine("SourceMTime=" + SourceMTime);
            sb.AppendLine("FileName=" + (FileName ?? "").Replace("\r", "").Replace("\n", ""));
            sb.AppendLine("FilePath=" + (FilePath ?? "").Replace("\r", "").Replace("\n", ""));
            sb.AppendLine("ServerIp=" + (ServerIp ?? "").Replace("\r", "").Replace("\n", ""));
            sb.AppendLine("Port=" + Port);
            sb.AppendLine("IsUdt=" + (IsUdt ? "1" : "0"));
            sb.AppendLine("Created=" + Created.ToString("o"));
            File.WriteAllText(GetPath(SessionId), sb.ToString(), Encoding.UTF8);
        }

        public static ResumeState Load(Guid sessionId)
        {
            string path = GetPath(sessionId);
            if (!File.Exists(path)) return null;
            var state = new ResumeState { SessionId = sessionId };
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                int idx = line.IndexOf('=');
                if (idx < 0) continue;
                string key = line.Substring(0, idx);
                string val = line.Substring(idx + 1);
                switch (key)
                {
                    case "TotalSize": { long v; if (long.TryParse(val, out v)) state.TotalSize = v; break; }
                    case "SentBytes": { long v; if (long.TryParse(val, out v)) state.SentBytes = v; break; }
                    case "SourceMTime": { long v; if (long.TryParse(val, out v)) state.SourceMTime = v; break; }
                    case "FileName": state.FileName = val; break;
                    case "FilePath": state.FilePath = val; break;
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
                }
            }
            return state;
        }

        public static void Delete(Guid sessionId)
        {
            string path = GetPath(sessionId);
            try { File.Delete(path); } catch { }
        }

        public static ResumeState[] ListAll()
        {
            EnsureDir();
            var files = Directory.GetFiles(ResumeDir, "*.json");
            var result = new System.Collections.Generic.List<ResumeState>(files.Length);
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileNameWithoutExtension(files[i]);
                Guid sid;
                if (Guid.TryParseExact(name, "N", out sid))
                {
                    try
                    {
                        var state = Load(sid);
                        if (state != null) result.Add(state);
                    }
                    catch { }
                }
            }
            return result.ToArray();
        }
    }
}
