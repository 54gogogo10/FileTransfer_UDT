using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace TrFileTransfer
{
    /// <summary>Result of an integrity scan over a directory of received files.</summary>
    public sealed class VerifyReport
    {
        /// <summary>Re-hashed and identical to the remembered digest.</summary>
        public readonly List<string> Verified = new List<string>();
        /// <summary>Remembered digest no longer matches — the file was edited or corrupted
        /// after this app received it.</summary>
        public readonly List<string> Changed = new List<string>();
        /// <summary>No trustworthy digest recorded (never received by this app, or the entry
        /// aged out of the cache) — nothing to compare against.</summary>
        public readonly List<string> Unverified = new List<string>();
        /// <summary>Could not be read (locked, permissions) — not judged either way.</summary>
        public readonly List<string> Skipped = new List<string>();

        public int Total { get { return Verified.Count + Changed.Count + Unverified.Count + Skipped.Count; } }
    }

    /// <summary>
    /// Integrity self-check ("scrub") for a directory of received files. The digest cache
    /// remembers the SHA256 of every file this app received or verified, so a scan can tell
    /// "the file has changed since it landed" from "the file is exactly as received" without
    /// any help from the sender — the kind of drift (bit rot, accidental edits, sync
    /// accidents) that nothing else in a plain file copy detects.
    /// Classification per file: Verified / Changed / Unverified / Skipped — see VerifyReport.
    /// Entries are only trusted within the cache's normal rules (TTL, byte-for-byte mode
    /// disables answers entirely, making every file Unverified — run the scan with the cache
    /// trusted, i.e. Strict off).
    /// </summary>
    public static class LibraryVerifier
    {
        /// <summary>Scans rootDir recursively. cb (optional) receives throttled progress
        /// events with BytesTransferred/TotalBytes in bytes and the current file's name.
        /// Honours ct per file. Files whose re-hash fails to read are Skipped, not Changed —
        /// an unreadable file is not evidence of corruption.</summary>
        public static VerifyReport Verify(string rootDir, FileHashCache cache, WireCallbacks cb, CancellationToken ct)
        {
            var report = new VerifyReport();
            if (cache == null) throw new ArgumentNullException("cache");
            if (!Directory.Exists(rootDir)) return report;

            // Per-directory walk with per-directory error handling: AllDirectories
            // aborts the WHOLE scan on the first unreadable subdirectory (the UI then
            // shows "0 files" instead of "scan failed"), and it happily follows a
            // junction out of the tree. Reparse points are skipped and cycles are cut.
            string[] files = EnumerateFilesSafe(rootDir).ToArray();

            long totalBytes = 0;
            var sizes = new long[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                long sz, mt;
                FileHashCache.Stat(files[i], out sz, out mt);
                sizes[i] = sz;
                totalBytes += sz;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var progressTimer = System.Diagnostics.Stopwatch.StartNew();
            long doneBytes = 0;
            for (int i = 0; i < files.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                ClassifyOne(cache, files[i], ct, report);
                doneBytes += sizes[i];

                if (cb != null && progressTimer.ElapsedMilliseconds >= 100)
                {
                    progressTimer.Restart();
                    cb.RaiseProgress(new TransferProgress
                    {
                        BytesTransferred = doneBytes,
                        TotalBytes = totalBytes,
                        SpeedBytesPerSecond = doneBytes / Math.Max(sw.Elapsed.TotalSeconds, 0.001),
                        Elapsed = sw.Elapsed,
                        FileName = files[i]
                    });
                }
            }
            return report;
        }

        /// <summary>Recursive file walk that skips junctions/symlinks (a shared folder
        /// must not pull in unrelated trees) and tolerates unreadable subdirectories.</summary>
        private static List<string> EnumerateFilesSafe(string rootDir)
        {
            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();
            string rootFull;
            try { rootFull = Path.GetFullPath(rootDir); } catch { return files; }
            seen.Add(rootFull);
            stack.Push(rootFull);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                try
                {
                    string[] entries = Directory.GetFiles(dir);
                    for (int i = 0; i < entries.Length; i++) files.Add(entries[i]);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                string[] subDirs;
                try { subDirs = Directory.GetDirectories(dir); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                for (int i = 0; i < subDirs.Length; i++)
                {
                    string sub = subDirs[i];
                    try
                    {
                        if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                        if (!seen.Add(Path.GetFullPath(sub))) continue; // cycle guard
                    }
                    catch { continue; }
                    stack.Push(sub);
                }
            }
            return files;
        }

        private static void ClassifyOne(FileHashCache cache, string path, CancellationToken ct, VerifyReport report)
        {
            long size, mtime;
            if (!FileHashCache.Stat(path, out size, out mtime))
            {
                report.Skipped.Add(path);
                return;
            }

            byte[] remembered;
            if (!cache.TryGetDigest(path, size, mtime, out remembered))
            {
                report.Unverified.Add(path);
                return;
            }

            byte[] actual;
            try
            {
                actual = ClientWire.ComputeFileHash(path, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { report.Skipped.Add(path); return; }
            catch (UnauthorizedAccessException) { report.Skipped.Add(path); return; }

            if (Utils.ConstantTimeEquals(actual, remembered)) report.Verified.Add(path);
            else report.Changed.Add(path);
        }
    }
}
