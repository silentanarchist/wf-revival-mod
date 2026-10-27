using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace WarpforgeRevival
{
    /// <summary>
    /// "Include extended logs" in the bug report window (0.15.3, owner 2026-10-27): one zip with every log the loader
    /// keeps (MelonLoader\Latest.log and the older runs in MelonLoader\Logs), on PC also Unity's own Player.log and
    /// Player-prev.log and the recent crash folders Unity writes (error.log and crash.dmp), so a crash can be looked at
    /// without asking the tester to find the files. Off by default; admins only on the website. Text files go through the
    /// same scrub as the recent log before anything leaves the device (the server scrubs again and rebuilds the zip).
    /// The newest files come first and the zip stops at the size the server takes (5 MB), so older logs are the ones
    /// left out when there are many.
    /// </summary>
    internal static class ExtendedLogs
    {
        internal const int MaxZip = 5 * 1024 * 1024 - 64 * 1024;       // the server takes 5 MB; a little room for the zip's own headers
        private const int MaxFile = 24 * 1024 * 1024;                  // one file read at most (a log or a dump)
        private const int MaxDump = 4 * 1024 * 1024;
        private const int MaxFiles = 50;

        private struct Item { public string Path, Name; public bool Text; }

        private static void ReadShared(string path, int max, out byte[] raw)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long len = fs.Length;
            if (len > max) fs.Seek(len - max, SeekOrigin.Begin);         // a very long log: its end
            var buf = new byte[Math.Min(len, max)];
            int got = 0;
            while (got < buf.Length) { int n = fs.Read(buf, got, buf.Length - got); if (n <= 0) break; got += n; }
            if (got < buf.Length) Array.Resize(ref buf, got);
            raw = buf;
        }

        /// <summary>Files to include, the most useful first: the current log, Unity's log, the newest crash, then older runs.</summary>
        private static List<Item> Collect(string unityLog)
        {
            var list = new List<Item>();
            void Add(string path, string name, bool text)
            {
                try { if (!string.IsNullOrEmpty(path) && File.Exists(path) && !list.Exists(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) list.Add(new Item { Path = path, Name = name, Text = text }); }
                catch { }
            }
            string ml = null, mlLogs = null;
            try { ml = MelonLoader.Utils.MelonEnvironment.MelonLoaderDirectory; } catch { }
            try { mlLogs = MelonLoader.Utils.MelonEnvironment.MelonLoaderLogsDirectory; } catch { }
            if (!string.IsNullOrEmpty(ml)) Add(Path.Combine(ml, "Latest.log"), "MelonLoader/Latest.log", true);
#if !ANDROID_PORT
            if (!string.IsNullOrEmpty(unityLog))
            {
                Add(unityLog, "Unity/Player.log", true);
                Add(Path.Combine(Path.GetDirectoryName(unityLog) ?? "", "Player-prev.log"), "Unity/Player-prev.log", true);
            }
            var crashDirs = new List<DirectoryInfo>();
            try
            {
                var crashes = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "Everguild", "Warpforge", "Crashes"));
                if (crashes.Exists) crashDirs.AddRange(crashes.GetDirectories());
                crashDirs.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            }
            catch { }
            for (int i = 0; i < crashDirs.Count && i < 3; i++)
            {
                string folder = "Crashes/" + Safe(crashDirs[i].Name);
                Add(Path.Combine(crashDirs[i].FullName, "error.log"), folder + "/error.log", true);
                if (i == 0) Add(Path.Combine(crashDirs[i].FullName, "crash.dmp"), folder + "/crash.dmp", false);
            }
#endif
            if (!string.IsNullOrEmpty(mlLogs))
            {
                try
                {
                    var old = new List<FileInfo>(new DirectoryInfo(mlLogs).GetFiles("*.log"));
                    old.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                    foreach (var f in old) Add(f.FullName, "MelonLoader/Logs/" + Safe(f.Name), true);
                }
                catch { }
            }
#if !ANDROID_PORT
            for (int i = 1; i < crashDirs.Count && i < 3; i++)
                Add(Path.Combine(crashDirs[i].FullName, "crash.dmp"), "Crashes/" + Safe(crashDirs[i].Name) + "/crash.dmp", false);
#endif
            if (list.Count > MaxFiles) list.RemoveRange(MaxFiles, list.Count - MaxFiles);
            return list;
        }

        /// <summary>A name the server accepts: letters, digits, space and ._()- only.</summary>
        private static string Safe(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name)
                sb.Append((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || " ._()-".IndexOf(c) >= 0 ? c : '_');
            string s = sb.ToString();
            return s.Length > 80 ? s.Substring(s.Length - 80) : s;
        }

        private static byte[] Deflated(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, CompressionLevel.Optimal, true)) z.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        /// <summary>Unity's own log file (PC), read on the main thread before Build.</summary>
        internal static string UnityLogPath()
        {
            try { return UnityEngine.Application.consoleLogPath; } catch { return null; }
        }

        /// <summary>The zip (null when there is nothing to send), and how many files it holds / left out. Any thread.</summary>
        internal static byte[] Build(string unityLog, Func<string, string> scrub, out int included, out int left)
        {
            included = 0; left = 0;
            var chosen = new List<(string name, byte[] data)>();
            long budget = MaxZip;
            foreach (var item in Collect(unityLog))
            {
                try
                {
                    ReadShared(item.Path, item.Text ? MaxFile : MaxDump + 1, out var raw);
                    if (raw == null || raw.Length == 0) continue;
                    if (!item.Text)
                    {
                        if (raw.Length > MaxDump || raw.Length < 4 || raw[0] != (byte)'M' || raw[1] != (byte)'D' || raw[2] != (byte)'M' || raw[3] != (byte)'P') { left++; continue; }
                    }
                    else raw = Encoding.UTF8.GetBytes(scrub(Encoding.UTF8.GetString(raw)));
                    long cost = Deflated(raw).Length + 200 + item.Name.Length * 2;
                    if (cost > budget) { left++; continue; }
                    budget -= cost;
                    chosen.Add((item.Name, raw));
                }
                catch (Exception e) { left++; RevivalMod.Log.Warning("[bugs] extended logs: " + item.Name + ": " + e.Message); }
            }
            if (chosen.Count == 0) return null;
            using var outMs = new MemoryStream();
            using (var zip = new ZipArchive(outMs, ZipArchiveMode.Create, true))
                foreach (var (name, data) in chosen)
                {
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    using var s = entry.Open();
                    s.Write(data, 0, data.Length);
                }
            included = chosen.Count;
            var result = outMs.ToArray();
            RevivalMod.Log.Msg($"[bugs] extended logs: {included} file(s), {result.Length / 1024} KB" + (left > 0 ? $", {left} left out (too large or unreadable)" : ""));
            return result;
        }
    }
}
