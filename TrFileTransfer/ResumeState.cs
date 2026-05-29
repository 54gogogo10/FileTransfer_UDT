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
            sb.AppendLine("SessionId=" + SessionId.ToString("N"));
            sb.AppendLine("TotalSize=" + TotalSize);
            sb.AppendLine("SentBytes=" + SentBytes);
            sb.AppendLine("FileName=" + FileName);
            sb.AppendLine("FilePath=" + FilePath);
            sb.AppendLine("ServerIp=" + ServerIp);
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
                    case "TotalSize": state.TotalSize = long.Parse(val); break;
                    case "SentBytes": state.SentBytes = long.Parse(val); break;
                    case "FileName": state.FileName = val; break;
                    case "FilePath": state.FilePath = val; break;
                    case "ServerIp": state.ServerIp = val; break;
                    case "Port": state.Port = int.Parse(val); break;
                    case "IsUdt": state.IsUdt = val == "1"; break;
                    case "Created": state.Created = DateTime.Parse(val); break;
                }
            }
            return state;
        }

        public static void Delete(Guid sessionId)
        {
            string path = GetPath(sessionId);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public static ResumeState[] ListAll()
        {
            EnsureDir();
            var files = Directory.GetFiles(ResumeDir, "*.json");
            var list = new ResumeState[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileNameWithoutExtension(files[i]);
                Guid sid;
                if (Guid.TryParseExact(name, "N", out sid))
                    list[i] = Load(sid);
            }
            return list;
        }
    }
}
