using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TrFileTransfer
{
    public class TextHistoryEntry
    {
        public DateTime When;
        public string Text;
    }

    /// <summary>
    /// Persistent log of received text messages. The receive dialog accumulates
    /// in memory only — a restart used to lose everything. One message per line:
    /// "yyyy-MM-dd HH:mm:ss|&lt;escaped text&gt;", where the escaping covers
    /// backslashes and CR/LF so multi-line messages stay one line. Bounded to the
    /// last MaxEntries messages (rewrite on overflow, StatsStore-style).
    /// Must stay C# 5: the test build compiles this file with the old csc.exe.
    /// </summary>
    public class TextHistoryStore
    {
        public const int MaxEntries = 200;

        private readonly string _path;
        private readonly object _lock = new object();

        public TextHistoryStore(string path)
        {
            _path = path;
        }

        public static string DefaultPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TrFileTransfer", "text-history.log");
            }
        }

        /// <summary>Escapes so one message occupies exactly one line.</summary>
        public static string Escape(string text)
        {
            return (text ?? "").Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        /// <summary>Inverse of Escape; stray escapes pass through untouched.</summary>
        public static string Unescape(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    char n = text[i + 1];
                    if (n == '\\') { sb.Append('\\'); i++; continue; }
                    if (n == 'r') { sb.Append('\r'); i++; continue; }
                    if (n == 'n') { sb.Append('\n'); i++; continue; }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Serialize(TextHistoryEntry e)
        {
            return e.When.ToString("yyyy-MM-dd HH:mm:ss") + "|" + Escape(e.Text);
        }

        private static TextHistoryEntry Parse(string line)
        {
            if (line == null) return null;
            int bar = line.IndexOf('|');
            if (bar <= 0) return null;
            DateTime when;
            if (!DateTime.TryParseExact(line.Substring(0, bar), "yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out when)) return null;
            return new TextHistoryEntry { When = when, Text = Unescape(line.Substring(bar + 1)) };
        }

        public void Add(string text)
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    File.AppendAllText(_path,
                        Serialize(new TextHistoryEntry { When = DateTime.Now, Text = text ?? "" }) + Environment.NewLine);
                    TryTrim();
                }
                catch { }
            }
        }

        private void TryTrim()
        {
            try
            {
                string[] lines = File.ReadAllLines(_path);
                if (lines.Length <= MaxEntries) return;
                var kept = new string[MaxEntries];
                Array.Copy(lines, lines.Length - MaxEntries, kept, 0, MaxEntries);
                File.WriteAllLines(_path, kept);
            }
            catch { }
        }

        /// <summary>All stored messages, oldest first; corrupt lines are skipped.</summary>
        public List<TextHistoryEntry> Load()
        {
            var result = new List<TextHistoryEntry>();
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_path)) return result;
                    foreach (string line in File.ReadAllLines(_path))
                    {
                        TextHistoryEntry e = Parse(line);
                        if (e != null) result.Add(e);
                    }
                }
                catch { }
            }
            return result;
        }

        /// <summary>Erases the history (kept as an empty file so appends never miss).</summary>
        public void Clear()
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    File.WriteAllText(_path, "");
                }
                catch { }
            }
        }
    }
}
