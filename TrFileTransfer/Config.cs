using System;
using System.Collections.Generic;
using System.IO;

namespace TrFileTransfer
{
    /// <summary>Simple key-value configuration persisted to %AppData%\TrFileTransfer\config.ini.</summary>
    #pragma warning disable 1591
    public static class Config
    {
        private static readonly string _dir;
        private static readonly string _path;
        private static readonly Dictionary<string, string> _values = new Dictionary<string, string>();
        // The UI thread writes here (control handlers) while server/client threads read
        // (encryption/compression options, IP filter, device naming). An unsynchronized
        // Dictionary can throw on a concurrent read+write — which used to surface as a
        // silently swallowed, fail-open receive-confirmation bypass.
        private static readonly object _lock = new object();

        static Config()
        {
            _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrFileTransfer");
            _path = Path.Combine(_dir, "config.ini");
        }

        public static void Load()
        {
            try
            {
                var loaded = new Dictionary<string, string>();
                if (File.Exists(_path))
                {
                    foreach (var line in File.ReadAllLines(_path))
                    {
                        int idx = line.IndexOf('=');
                        // Reject a missing '=' or an empty key, but keep empty values:
                        // "key=" must round-trip (it is how "cleared" is stored).
                        if (idx <= 0) continue;
                        loaded[line.Substring(0, idx)] = line.Substring(idx + 1);
                    }
                }
                // Swap under the lock so readers never observe a half-filled table
                lock (_lock)
                {
                    _values.Clear();
                    foreach (var kv in loaded) _values[kv.Key] = kv.Value;
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(_dir);
                string[] lines;
                lock (_lock)
                {
                    lines = new string[_values.Count];
                    int i = 0;
                    foreach (var kv in _values)
                        lines[i++] = kv.Key + "=" + kv.Value;
                }
                File.WriteAllLines(_path, lines);
            }
            catch { }
        }

        public static string Get(string key, string fallback)
        {
            lock (_lock)
            {
                string val;
                return _values.TryGetValue(key, out val) ? val : fallback;
            }
        }

        public static int GetInt(string key, int fallback)
        {
            string val = Get(key, null);
            if (val == null) return fallback;
            int result;
            return int.TryParse(val, out result) ? result : fallback;
        }

        public static bool GetBool(string key, bool fallback)
        {
            string val = Get(key, null);
            if (val == null) return fallback;
            return val == "true" || val == "1";
        }

        public static void Set(string key, string val) { lock (_lock) { _values[key] = val; } }
        public static void SetInt(string key, int val) { Set(key, val.ToString()); }
        public static void SetBool(string key, bool val) { Set(key, val ? "true" : "false"); }
    }
}
