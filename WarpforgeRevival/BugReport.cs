using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace WarpforgeRevival
{
    /// <summary>
    /// Bug reports from testers (plan: claude/tester-bug-reports.md in the project docs).
    ///
    /// Manual: F8 on PC, Settings > Mod > Report a bug, or in a match the small "Report bug" tab on
    /// the left edge of the screen (testers only).
    /// The screen is captured first, then a short form asks for a title, type, how bad, and what
    /// happened; the mod adds the context (versions, device, scene, match rules), its last 400 log
    /// lines and the screenshot, and sends it to the server (POST /playfab/Revival/BugSubmit with the
    /// game session). The answer is a number (B-0042) and a link to the report on the website.
    ///
    /// Crashes and freezes: while the game runs, a small set of files in UserData/WarpforgeRevival/
    /// session/ (named by process id, so two games on one PC do not mix) keeps the context and the
    /// recent log on disk. A background watchdog notices when no frame has been drawn for 5 s and
    /// marks the run as frozen (on PC it also shows a Windows message after 30 s, once the main menu
    /// has been reached). A clean exit removes the files; if they are still there at the next start,
    /// the run ended unexpectedly, and testers are offered a report with what was saved.
    /// On Android, a run that ended while the game was in the background (swiped away) is not
    /// treated as a crash.
    /// Reports that cannot be sent are kept in bug-drafts/ and sent at the next start (at most 5).
    ///
    /// The watchdog never takes a lock the game's main thread could hold and never calls Unity:
    /// the ring of log lines is a plain array, the heartbeat a plain number, the context a string.
    /// </summary>
    internal static class BugReport
    {
        // ------------------------------------------------------------------ recent log (any thread)
        private const int RingSize = 400;
        private static readonly string[] ring = new string[RingSize];
        private static long written;

        internal static void Add(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            if (line.Length > 2000) line = line.Substring(0, 2000) + "...";
            long n = Interlocked.Increment(ref written);
            ring[(int)((n - 1) % RingSize)] = DateTime.Now.ToString("HH:mm:ss.fff") + " " + line;
        }

        internal static string RecentLog()
        {
            long n = Interlocked.Read(ref written);
            var sb = new StringBuilder();
            for (long i = Math.Max(0, n - RingSize); i < n; i++)
            {
                string s = ring[(int)(i % RingSize)];
                if (s != null) sb.Append(s).Append('\n');
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ set-up
        private static string dir, sessionDir, draftsDir;
        private static int pid;
        private static string runStarted;
        private static Thread watchdog;
        private static volatile bool ended;

        /// <summary>From OnInitializeMelon, after the config is loaded.</summary>
        internal static void Start()
        {
            try
            {
                dir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "WarpforgeRevival");
                sessionDir = Path.Combine(dir, "session");
                draftsDir = Path.Combine(dir, "bug-drafts");
                Directory.CreateDirectory(sessionDir);
                pid = Environment.ProcessId;
                runStarted = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                HookLogs();
                LookAtPreviousRuns();
                long procStart = 0;
                try { procStart = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks; } catch { }
                File.WriteAllText(Marker("running", ".txt"), pid + " " + procStart);
                beat = Stopwatch.GetTimestamp();
                // a normal end of the process counts as a clean exit even when the game's own quit
                // callback did not reach the mod (seen on PC: closing the window gave a crash report)
                try { AppDomain.CurrentDomain.ProcessExit += (_, __) => End(); } catch { }
                watchdog = new Thread(Watch) { IsBackground = true, Name = "WarpforgeRevival watchdog" };
                watchdog.Start();
            }
            catch (Exception e) { RevivalMod.Log.Warning("[bugs] could not start crash and freeze tracking: " + e.Message); }
        }

        private static string Marker(string what, string ext) => Path.Combine(sessionDir, $"{what}-{pid}{ext}");

        private static void HookLogs()
        {
            try
            {
                MelonLoader.MelonLogger.MsgDrawingCallbackHandler += (c1, c2, section, msg) => Add(msg);
                MelonLoader.MelonLogger.WarningCallbackHandler += (section, msg) => Add("[warning] " + msg);
                MelonLoader.MelonLogger.ErrorCallbackHandler += (section, msg) => Add("[error] " + msg);
            }
            catch (Exception e) { RevivalMod.Log.Warning("[bugs] could not follow the log: " + e.Message); }
        }

        /// <summary>Unity's own errors and exceptions (they reach the log only with DiagnosticLogging on). Main thread, once Unity is up.</summary>
        private static bool unityHooked;
        private static void HookUnity()
        {
            if (unityHooked) return;
            unityHooked = true;
            try
            {
                UnityEngine.Application.add_logMessageReceived(Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityEngine.Application.LogCallback>(
                    new Action<string, string, LogType>((text, stack, type) =>
                    {
                        if (type == LogType.Exception) Add("[unity exception] " + text + "\n" + stack);
                        else if (type == LogType.Error || type == LogType.Assert) Add("[unity error] " + text);
                    })));
            }
            catch (Exception e) { RevivalMod.Log.Warning("[bugs] could not follow Unity's errors: " + e.Message); }
            // The moment the game leaves the front (phone screen off, home button, another app, the
            // patcher; alt-tab on PC). Watching Application.isFocused every frame misses it on Android:
            // the game is paused at once and runs no further frame, so a run swiped away or ended by
            // Android afterwards looked like a crash. Unity's own focus event arrives before the pause.
            try
            {
                UnityEngine.Application.add_focusChanged(Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool>>(
                    new Action<bool>(focused => { if (focused == background) SetBackground(!focused); })));
            }
            catch (Exception e) { RevivalMod.Log.Warning("[bugs] could not follow the game's focus: " + e.Message); }
        }

        // ------------------------------------------------------------------ context (main thread)
        private static volatile string contextJson = "{}";
        private static string scene = "", lastScene = "";
        private static float nextContext;
        internal static volatile bool menuReached;

        private static Dictionary<string, string> Context()
        {
            var c = new Dictionary<string, string>();
            void Put(string k, Func<string> f, int max)
            {
                try { string v = f() ?? ""; if (v.Length > max) v = v.Substring(0, max); if (v.Length > 0) c[k] = v; } catch { }
            }
            Put("mod", () => RevivalMod.Version, 40);
#if ANDROID_PORT
            Put("platform", () => "Android", 20);
            Put("loadingVideo", () => RevivalMod.Config.LoadingVideo, 20);
#else
            Put("platform", () => "Windows", 20);
#endif
            Put("game", () => UnityEngine.Application.version, 40);
            Put("os", () => SystemInfo.operatingSystem, 120);
            Put("device", () => SystemInfo.deviceModel, 120);
            Put("gpu", () => SystemInfo.graphicsDeviceName, 120);
            Put("api", () => SystemInfo.graphicsDeviceType.ToString() + " " + SystemInfo.graphicsDeviceVersion, 60);
            Put("screen", () => Screen.width + "x" + Screen.height, 20);
            Put("scene", () => scene, 80);
            Put("rules", () => Replays.MatchRules, 2000);
            Put("runStarted", () => runStarted, 40);
            return c;
        }

        private static string ContextJson(Dictionary<string, string> c)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms)) { w.WriteStartObject(); foreach (var kv in c) w.WriteString(kv.Key, kv.Value); w.WriteEndObject(); }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        // ------------------------------------------------------------------ every frame (main thread)
        private static long beat;
        private static volatile bool background;

        internal static void Tick()
        {
            beat = Stopwatch.GetTimestamp();
            if (sessionDir == null) return;
            HookUnity();
            try
            {
                scene = GameState.Scene;
                if (!menuReached && scene.IndexOf("MainMenu", StringComparison.OrdinalIgnoreCase) >= 0) menuReached = true;
                float now = Time.realtimeSinceStartup;
                if (scene != lastScene || now >= nextContext)
                {
                    lastScene = scene;
                    nextContext = now + 5f;
                    contextJson = ContextJson(Context());
                }
                // both platforms: a run that ends while the game is not in front was closed by the
                // player (swiped away on a phone; closed from the taskbar, or by an install script
                // waiting for it, on PC), not a crash - the next start does not offer a report for it
                bool focused = UnityEngine.Application.isFocused;
                if (focused == background) SetBackground(!focused);
                if (menuReached && !string.IsNullOrEmpty(PlayFabTransport.SessionTicket)) AtMenu();
            }
            catch (Exception e) { if (!tickWarned) { tickWarned = true; RevivalMod.Log.Warning("[bugs] " + e.Message); } }
        }
        private static bool tickWarned;

        /// <summary>The game went to the background / lost focus (or came back). A run that ends then was closed by the player, not a crash.</summary>
        private static void SetBackground(bool value)
        {
            background = value;
            try
            {
                if (value) { File.WriteAllText(Marker("background", ".txt"), DateTime.UtcNow.ToString("o")); Flush(); }
                else File.Delete(Marker("background", ".txt"));
            }
            catch { }
        }

        /// <summary>A clean exit (and every way the mod closes the game itself): nothing to report next time.</summary>
        internal static void End()
        {
            if (ended || sessionDir == null) return;
            ended = true;
            foreach (var what in new[] { ("running", ".txt"), ("frozen", ".txt"), ("background", ".txt"), ("last-session", ".log"), ("context", ".json") })
                try { File.Delete(Marker(what.Item1, what.Item2)); } catch { }
        }

        // ------------------------------------------------------------------ watchdog (its own thread)
        private const double MarkAfter = 5, BoxAfter = 30;
        private static volatile bool boxOpen;

        private static void Watch()
        {
            double freq = Stopwatch.Frequency;
            int sinceFlush = 0;
            bool frozen = false, boxShown = false;
            double stuck = 0;
            while (!ended)
            {
                try
                {
                    Thread.Sleep(1000);
                    if (ended) return;
                    double age = (Stopwatch.GetTimestamp() - Interlocked.Read(ref beat)) / freq;
                    if (age >= MarkAfter && !background)
                    {
                        stuck = age;
                        if (!frozen) { frozen = true; Flush(); }
                        File.WriteAllText(Marker("frozen", ".txt"), ((int)age).ToString() + " " + lastScene);
#if !ANDROID_PORT
                        if (age >= BoxAfter && !boxShown && menuReached) { boxShown = true; ShowFrozenBox(); }
#endif
                    }
                    else if (frozen && age < 2)
                    {
                        frozen = false; boxShown = false;
                        try { File.Delete(Marker("frozen", ".txt")); } catch { }
                        RevivalMod.Log.Msg($"[bugs] the game was stuck for {stuck:0} s and recovered (scene {lastScene})");
                    }
                    if (++sinceFlush >= 10) { sinceFlush = 0; Flush(); }
                }
                catch { }
            }
        }

        private static void Flush()
        {
            try
            {
                File.WriteAllText(Marker("last-session", ".log"), RecentLog());
                File.WriteAllText(Marker("context", ".json"), contextJson);
            }
            catch { }
        }

#if !ANDROID_PORT
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

        private static void ShowFrozenBox()
        {
            if (boxOpen) return;
            boxOpen = true;
            var t = new Thread(() =>
            {
                try
                {
                    MessageBoxW(IntPtr.Zero,
                        Loc.T("bugReport.frozenBox", "Warpforge seems frozen.\n\nA bug report has been saved. It will be offered the next time you start the game (if the game recovers by itself, nothing is sent)."),
                        "WF Revival", 0x30 | 0x10000 | 0x40000);   // warning icon, set foreground, topmost
                }
                catch { }
                boxOpen = false;
            }) { IsBackground = true, Name = "WarpforgeRevival frozen message" };
            t.Start();
        }
#endif

        // ------------------------------------------------------------------ previous runs (at start)
        private const int KeepOld = 5;
        private static string pendingOld;          // folder of a crashed/frozen earlier run waiting to be offered

        private static void LookAtPreviousRuns()
        {
            string oldRoot = Path.Combine(sessionDir, "old");
            foreach (string running in Directory.GetFiles(sessionDir, "running-*.txt"))
            {
                try
                {
                    string[] parts = File.ReadAllText(running).Trim().Split(' ');
                    int other = int.Parse(parts[0]);
                    long start = parts.Length > 1 ? long.Parse(parts[1]) : 0;
                    if (other == pid || Alive(other, start)) continue;
                    string Of(string what, string ext) => Path.Combine(sessionDir, $"{what}-{other}{ext}");
                    string kind = File.Exists(Of("frozen", ".txt")) ? "freeze" : File.Exists(Of("background", ".txt")) ? "background" : "crash";
                    string frozenFor = "";
                    if (kind == "freeze") try { frozenFor = File.ReadAllText(Of("frozen", ".txt")).Split(' ')[0]; } catch { }
                    DateTime closedAt = File.Exists(Of("last-session", ".log")) ? File.GetLastWriteTimeUtc(Of("last-session", ".log")) : File.GetLastWriteTimeUtc(running);
                    if (kind == "freeze") closedAt = File.GetLastWriteTimeUtc(Of("frozen", ".txt"));
                    // the mod (or, on Android, the game itself) was replaced since then: that run was
                    // ended by installing the update, not by a crash
                    if (kind == "crash" && UpdatedSince(closedAt)) kind = "update";
                    string target = Path.Combine(oldRoot, closedAt.ToString("yyyyMMdd-HHmmss") + "-" + other);
                    Directory.CreateDirectory(target);
                    foreach (var (what, ext) in new[] { ("last-session", ".log"), ("context", ".json"), ("frozen", ".txt"), ("background", ".txt") })
                        if (File.Exists(Of(what, ext))) File.Move(Of(what, ext), Path.Combine(target, what + ext), true);
                    File.WriteAllText(Path.Combine(target, "meta.txt"), $"{kind}\n{frozenFor}\n{closedAt:yyyy-MM-ddTHH:mm:ssZ}\nnot offered\n");
                    File.Delete(running);
                    RevivalMod.Log.Msg($"[bugs] the run before (process {other}) ended unexpectedly: {(kind == "background" ? "closed while in the background (not a crash)" : kind == "update" ? "closed for an update (not a crash)" : kind)}" +
                                       (frozenFor.Length > 0 ? $" after being stuck for {frozenFor} s" : ""));
                }
                catch (Exception e) { RevivalMod.Log.Warning("[bugs] could not read an earlier run's files: " + e.Message); }
            }
            if (!Directory.Exists(oldRoot)) return;
            var olds = new List<string>(Directory.GetDirectories(oldRoot));
            olds.Sort(StringComparer.Ordinal);
            while (olds.Count > KeepOld) { try { Directory.Delete(olds[0], true); } catch { } olds.RemoveAt(0); }
            for (int i = olds.Count - 1; i >= 0; i--)
            {
                string[] meta = ReadMeta(olds[i]);
                if (meta != null && (meta[0] == "crash" || meta[0] == "freeze") && meta[3] == "not offered") { pendingOld = olds[i]; break; }
            }
        }

        /// <summary>True when the mod's file (or the installed game, on Android) is newer than the given moment.</summary>
        private static bool UpdatedSince(DateTime when)
        {
            var files = new List<string>();
            try { files.Add(typeof(BugReport).Assembly.Location); } catch { }
            try
            {
                string mods = MelonLoader.Utils.MelonEnvironment.ModsDirectory;
                files.AddRange(Migration.AllPaths());          // the new and old file names (the rename, 0.15.0)
            }
            catch { }
            try { files.Add(UnityEngine.Application.dataPath); } catch { }       // Android: the installed game's APK
            foreach (var f in files)
                try { if (!string.IsNullOrEmpty(f) && File.Exists(f) && File.GetLastWriteTimeUtc(f) > when) return true; } catch { }
            return false;
        }

        private static string[] ReadMeta(string folder)
        {
            try
            {
                var lines = File.ReadAllText(Path.Combine(folder, "meta.txt")).Replace("\r", "").Split('\n');
                return lines.Length >= 4 ? lines : null;
            }
            catch { return null; }
        }

        private static void MarkOffered(string folder)
        {
            var meta = ReadMeta(folder);
            if (meta == null) return;
            meta[3] = "offered";
            try { File.WriteAllText(Path.Combine(folder, "meta.txt"), string.Join("\n", meta)); } catch { }
        }

        private static bool Alive(int otherPid, long startTicks)
        {
            try
            {
                using var p = Process.GetProcessById(otherPid);
                if (p.HasExited) return false;
                if (startTicks == 0) return true;
                try { return p.StartTime.ToUniversalTime().Ticks == startTicks; } catch { return false; }
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ at the main menu (main thread)
        private static bool atMenuDone;

        private static void AtMenu()
        {
            if (atMenuDone || !AccountPage.StatusKnown) return;
            atMenuDone = true;
            SendDrafts();
            if (pendingOld != null && AccountPage.IsTester && !Form.Open)
            {
                string folder = pendingOld;
                pendingOld = null;
                MarkOffered(folder);
                OfferCrash(folder);
            }
        }

        // ------------------------------------------------------------------ the form
        internal static void OnGUI()
        {
            try
            {
                var e = Event.current;
                if (e == null) return;
                bool tester = AccountPage.IsTester && !string.IsNullOrEmpty(PlayFabTransport.SessionTicket);
#if !ANDROID_PORT
                if (tester && !Form.Open && e.type == EventType.KeyDown && e.keyCode == KeyCode.F8) { captureNext = true; e.Use(); }
#endif
                if (captureAt > 0f && Time.realtimeSinceStartup >= captureAt) { captureAt = 0f; captureNext = true; }
                if (captureNext && e.type == EventType.Repaint)
                {
                    captureNext = false;
                    byte[] shot = Screenshot();
                    Form.Show(new Draft { Kind = "manual", Screenshot = shot, Log = RecentLog(), ContextJson = ContextJson(Context()), Category = "other", Severity = "annoying" });
                }
                if (Form.Open) { Form.Draw(); return; }
                // no tab on the screen any more: in a match the gear menu has a Report bug button
                // (BattleMenuButton below), in the menus Settings > Mod > Report a bug, and F8 on PC
            }
            catch (Exception ex) { RevivalMod.Log.Warning("[bugs] form: " + ex.Message); Form.Hide(); }
        }
        private static bool captureNext;

        /// <summary>Starts a manual report (the screen is captured on the next frame). From the Mod page's button.</summary>
        internal static void Request()
        {
            if (AccountPage.IsTester && !Form.Open) captureNext = true;
        }

        /// <summary>Starts a manual report after a short wait (a window closing first, so it is not in the screenshot).</summary>
        internal static void RequestAfter(float seconds)
        {
            if (AccountPage.IsTester && !Form.Open) captureAt = Time.realtimeSinceStartup + seconds;
        }
        private static float captureAt;

        /// <summary>The picture on screen now, at most 1280 pixels wide, as a plain BMP (never Unity's PNG/JPG encoders: they crashed the loader's .NET on a Pixel 6 Pro).</summary>
        private static byte[] Screenshot()
        {
            Texture2D full = null;
            try
            {
                int w = Screen.width, h = Screen.height;
                if (w <= 0 || h <= 0) return null;
                full = new Texture2D(w, h, TextureFormat.RGB24, false);
                full.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                var px = full.GetPixels32();
                int ow = Math.Min(w, 1280), oh = (int)((long)h * ow / w);
                int rowBytes = (ow * 3 + 3) & ~3;
                var bmp = new byte[54 + rowBytes * oh];
                for (int y = 0; y < oh; y++)
                {
                    int sy = (int)((long)y * h / oh), row = 54 + y * rowBytes;
                    for (int x = 0; x < ow; x++)
                    {
                        var c = px[sy * w + (int)((long)x * w / ow)];
                        int o = row + x * 3;
                        bmp[o] = c.b; bmp[o + 1] = c.g; bmp[o + 2] = c.r;      // BMP rows run bottom-up, like the texture's
                    }
                }
                void Put(int at, int v) { bmp[at] = (byte)v; bmp[at + 1] = (byte)(v >> 8); bmp[at + 2] = (byte)(v >> 16); bmp[at + 3] = (byte)(v >> 24); }
                bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
                Put(2, bmp.Length); Put(10, 54); Put(14, 40); Put(18, ow); Put(22, oh);
                bmp[26] = 1; bmp[28] = 24; Put(34, rowBytes * oh);
                return bmp;
            }
            catch (Exception e) { RevivalMod.Log.Warning("[bugs] could not take the screenshot: " + e.Message); return null; }
            finally { if ((object)full != null) try { UnityEngine.Object.Destroy(full); } catch { } }
        }

        internal class Draft
        {
            public string Kind = "manual", Title = "", Category = "other", Severity = "annoying", Description = "";
            public string Log = "", ContextJson = "{}";
            public byte[] Screenshot, Dump;
            public bool SendShot = true, SendLog = true, SendDump = true, SendExtended;
            public byte[] Extended;                // built as the report is sent, when SendExtended is ticked
        }

        private static void OfferCrash(string folder)
        {
            var meta = ReadMeta(folder);
            string kind = meta[0], frozenFor = meta[1];
            string ctxJson = "{}", log = "", where = "";
            try { ctxJson = File.ReadAllText(Path.Combine(folder, "context.json")); } catch { }
            try { log = File.ReadAllText(Path.Combine(folder, "last-session.log")); } catch { }
            try
            {
                using var doc = JsonDocument.Parse(ctxJson);
                if (doc.RootElement.TryGetProperty("scene", out var s)) where = s.GetString();
            }
            catch { }
            // the context is that run's, plus how it ended
            var ctx = new Dictionary<string, string>();
            try { using var doc = JsonDocument.Parse(ctxJson); foreach (var p in doc.RootElement.EnumerateObject()) ctx[p.Name] = p.Value.GetString() ?? ""; } catch { }
            ctx["mod"] = ctx.TryGetValue("mod", out var m) ? m : RevivalMod.Version;
            if (frozenFor.Length > 0) ctx["frozenSeconds"] = frozenFor;
            ctx["closedAt"] = meta[2];
            byte[] dump = null;
#if !ANDROID_PORT
            try
            {
                string crashes = Path.Combine(Path.GetTempPath(), "Everguild", "Warpforge", "Crashes");
                DateTime when = DateTime.Parse(meta[2]).ToUniversalTime();
                if (Directory.Exists(crashes))
                    foreach (string d in Directory.GetDirectories(crashes))
                    {
                        if (Math.Abs((Directory.GetLastWriteTimeUtc(d) - when).TotalMinutes) > 3) continue;
                        string dmp = Path.Combine(d, "crash.dmp"), errLog = Path.Combine(d, "error.log");
                        if (File.Exists(errLog)) log += "\n---- Unity crash report (error.log) ----\n" + File.ReadAllText(errLog);
                        if (File.Exists(dmp) && new FileInfo(dmp).Length <= 4 * 1024 * 1024) dump = File.ReadAllBytes(dmp);
                        break;
                    }
            }
            catch (Exception e) { RevivalMod.Log.Warning("[bugs] looking for Unity's crash report: " + e.Message); }
#endif
            if (log.Length > 250 * 1024) log = log.Substring(log.Length - 250 * 1024);
            // the suggested title stays in English: it is what the admins read on the website
            string title = (kind == "freeze" ? "Freeze" : "Crash") + (where.Length > 0 ? ": " + where : "");
            Form.Show(new Draft
            {
                Kind = kind, Title = title, Category = "crash", Severity = "blocker", Log = log, Dump = dump,
                ContextJson = ContextJson(ctx)
            }, kind == "freeze"
                ? (frozenFor.Length > 0
                    ? Loc.T("bugReport.frozeStuck", "The game froze last time (stuck for {seconds} s) and was closed. Send a bug report?", ("seconds", frozenFor))
                    : Loc.T("bugReport.froze", "The game froze last time and was closed. Send a bug report?"))
                : Loc.T("bugReport.crashed", "The game closed unexpectedly last time. Send a bug report?"));
        }

        // ------------------------------------------------------------------ sending
        private static readonly TimeSpan BugTimeout = TimeSpan.FromSeconds(60);

        private static string Body(Draft d)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("title", d.Title.Trim());
                w.WriteString("category", d.Category);
                w.WriteString("severity", d.Severity);
                w.WriteString("kind", d.Kind);
                if (!string.IsNullOrWhiteSpace(d.Description)) w.WriteString("description", d.Description.Trim());
                w.WritePropertyName("context");
                using (var ctx = JsonDocument.Parse(string.IsNullOrEmpty(d.ContextJson) ? "{}" : d.ContextJson)) ctx.RootElement.WriteTo(w);
                if (d.SendLog && !string.IsNullOrEmpty(d.Log)) w.WriteString("log", Scrub(d.Log));
                // The server takes at most 8 MB per request and attachments grow by a third as base64: a
                // screenshot and a crash dump together could pass that (security review #25), so the
                // screenshot is left out when both would not fit; the crash dump says more. The extended logs
                // (0.15.3) already hold the newest crash dump, so with them the dump is not sent a second time,
                // and they come before the screenshot too.
                bool ext = d.SendExtended && d.Extended != null;
                bool dumpToo = d.SendDump && d.Dump != null && !ext;
                long others = (dumpToo ? d.Dump.Length : 0) + (ext ? d.Extended.Length : 0);
                bool shotFits = d.Screenshot != null && d.Screenshot.Length <= 3 * 1024 * 1024 && d.Screenshot.Length + others <= 5 * 1024 * 1024;
                if (d.SendShot && shotFits) w.WriteString("screenshot", Convert.ToBase64String(d.Screenshot));
                if (dumpToo) w.WriteString("dump", Convert.ToBase64String(d.Dump));
                if (ext) w.WriteString("extended", Convert.ToBase64String(d.Extended));
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        /// <summary>Lines that could hold a secret are dropped before anything leaves the device (the server scrubs again).</summary>
        private static string Scrub(string log)
        {
            string ticket = PlayFabTransport.SessionTicket ?? "", server = RevivalMod.Config.ServerUrl ?? "";
            var sb = new StringBuilder();
            foreach (string line in log.Split('\n'))
            {
                if (line.IndexOf("wfr-login-", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("X-Authorization", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("SessionTicket", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("recovery code", StringComparison.OrdinalIgnoreCase) >= 0
                    || (ticket.Length > 20 && line.Contains(ticket)))
                { sb.Append("<line removed>\n"); continue; }
                sb.Append(server.Length > 0 ? line.Replace(server, "<server>") : line).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>(id, url, null) on success; (null, null, message) on failure, with keep = true when it should be kept as a draft.</summary>
        private static async Task<(string id, string url, string error, bool keep)> Post(string body)
        {
            var reply = await Net.PostJson("/playfab/Revival/BugSubmit", body, BugTimeout,
                new[] { new KeyValuePair<string, string>("X-Revival-Mod", RevivalMod.Version) });
            if (reply.Ok && reply.Data.ValueKind == JsonValueKind.Object && reply.Data.TryGetProperty("id", out var id))
                return (id.GetString(), reply.Data.TryGetProperty("url", out var u) ? u.GetString() : "", null, false);
            if (reply.Unreachable) return (null, null, Loc.T("replay.unreachable", "could not reach the server"), true);     // shown in the window
            return (null, null, reply.Error, reply.Status >= 500 || reply.Status == 429);
        }

        private static void KeepDraft(string body)
        {
            try
            {
                Directory.CreateDirectory(draftsDir);
                File.WriteAllText(Path.Combine(draftsDir, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".json"), body);
                var files = new List<string>(Directory.GetFiles(draftsDir, "*.json"));
                files.Sort(StringComparer.Ordinal);
                while (files.Count > 5) { try { File.Delete(files[0]); } catch { } files.RemoveAt(0); }
            }
            catch (Exception e) { RevivalMod.Log.Warning("[bugs] could not keep the report to send later: " + e.Message); }
        }

        private static void SendDrafts()
        {
            if (draftsDir == null || !Directory.Exists(draftsDir)) return;
            var files = Directory.GetFiles(draftsDir, "*.json");
            if (files.Length == 0) return;
            Task.Run(async () =>
            {
                foreach (string f in files)
                {
                    string body;
                    try { body = File.ReadAllText(f); } catch { continue; }
                    var (id, _, error, keep) = await Post(body);
                    if (id != null) RevivalMod.Log.Msg($"[bugs] a saved report was sent as {id}");
                    else RevivalMod.Log.Warning($"[bugs] a saved report was not sent: {error}" + (keep ? " (kept for next time)" : " (dropped)"));
                    if (id != null || !keep) try { File.Delete(f); } catch { }
                    if (keep) break;
                }
            });
        }

        // ------------------------------------------------------------------ the window (IMGUI, like the sign-in window)
        internal static class Form
        {
            internal static bool Open;
            private static Draft d;
            private static string heading = "", status = "", sentUrl = "";
            private static bool busy, problem, sent;
            private static int openDrop;              // 0 none, 1 the Type list, 2 the severity list

            private static readonly string[] CatIds = { "crash", "picture", "card", "rules", "ui", "connection", "replay", "text", "other" };
            // the names are made each time they are drawn, so they follow the player's language (only the ids are sent)
            private static string[] CatNames() => new[]
            {
                Loc.T("bugReport.cat.crash", "Crash / freeze"), Loc.T("bugReport.cat.picture", "Black or wrong picture"),
                Loc.T("bugReport.cat.card", "Card does the wrong thing"), Loc.T("bugReport.cat.rules", "Rules / mode"),
                Loc.T("bugReport.cat.ui", "Menus & UI"), Loc.T("bugReport.cat.connection", "Connection / sign-in"),
                Loc.T("bugReport.cat.replay", "Replay"), Loc.T("bugReport.cat.text", "Text / translation"), Loc.T("bugReport.cat.other", "Other"),
            };
            private static readonly string[] SevIds = { "blocker", "match", "annoying", "cosmetic" };
            private static string[] SevNames() => new[]
            {
                Loc.T("bugReport.sev.blocker", "Can't play"), Loc.T("bugReport.sev.match", "Breaks a match"),
                Loc.T("bugReport.sev.annoying", "Annoying"), Loc.T("bugReport.sev.cosmetic", "Cosmetic"),
            };

            internal static void Show(Draft draft, string intro = null)
            {
                d = draft;
                heading = intro ?? Loc.T("modTab.reportBug", "Report a bug");
                status = ""; sentUrl = ""; busy = false; problem = false; sent = false; openDrop = 0;
                Open = true;
                BlockGame(true);
                RevivalMod.Log.Msg("[bugs] report window opened (" + draft.Kind + ")");
            }

            internal static void Hide() { Open = false; d = null; BlockGame(false); }

            // The game's own buttons sit under this window and would get the same clicks: its event
            // system is switched off while the window is open.
            private static UnityEngine.EventSystems.EventSystem blocked;
            private static void BlockGame(bool on)
            {
                try
                {
                    if (on)
                    {
                        var es = UnityEngine.EventSystems.EventSystem.current;
                        if ((object)es != null && es.enabled) { es.enabled = false; blocked = es; }
                    }
                    else if ((object)blocked != null)
                    {
                        if (blocked.Pointer != IntPtr.Zero && blocked.m_CachedPtr != IntPtr.Zero) blocked.enabled = true;
                        blocked = null;
                    }
                }
                catch (Exception e) { RevivalMod.Log.Warning("[bugs] " + e.Message); blocked = null; }
            }

            private static GUIStyle box, label, small, field, button;
            private static Texture2D shade, panel;
            private static float sc = 1f;
            private static Rect R(float x, float y, float w, float h) => new Rect(x * sc, y * sc, w * sc, h * sc);


            private static void Styles()
            {
                if (box == null)
                {
                    shade = Ui.Solid(new Color(0f, 0f, 0f, 0.6f));
                    panel = Ui.Solid(new Color(0.05f, 0.11f, 0.09f, 0.97f));
                    box = new GUIStyle(GUI.skin.box); box.normal.background = panel;
                    label = new GUIStyle(GUI.skin.label) { wordWrap = true }; label.normal.textColor = new Color(0.85f, 0.95f, 0.9f);
                    small = new GUIStyle(label); small.normal.textColor = new Color(0.6f, 0.75f, 0.7f);
                    field = new GUIStyle(GUI.skin.textField);
                    button = new GUIStyle(GUI.skin.button);
                }
                sc = Ui.GuiScale();
                label.fontSize = Mathf.RoundToInt(20 * sc); small.fontSize = Mathf.RoundToInt(15 * sc);
                field.fontSize = Mathf.RoundToInt(20 * sc); button.fontSize = Mathf.RoundToInt(18 * sc);
            }


            private static bool Tick(float x, float y, string text, bool on)
            {
                if (GUI.Button(R(x, y, 34, 34), on ? "X" : "", button)) on = !on;
                if (GUI.Button(R(x + 40, y, 300, 34), text, small)) on = !on;
                return on;
            }

            internal static void Draw()
            {
                if (d == null) { Open = false; return; }
                Styles();
                float w = Screen.width / sc, h = Screen.height / sc;
                GUI.DrawTexture(R(0, 0, w, h), shade);
                float pw = 760, ph = 560, x = (w - pw) / 2, y = Mathf.Max(6, (h - ph) / 2 - 40);
                GUI.Box(R(x, y, pw, ph), "", box);
                float cx = x + 30, cw = pw - 60, cy = y + 18;
                GUI.Label(R(cx, cy, cw, 56), heading, label); cy += 58;

                if (sent)
                {
                    GUI.Label(R(cx, cy, cw, 120), status, label); cy += 130;
                    if (!string.IsNullOrEmpty(sentUrl) && GUI.Button(R(cx, cy, 300, 46), Loc.T("bugReport.openWebsite", "Open on the website"), button))
                        try { Ui.OpenUrl(sentUrl); } catch (Exception e) { status += "\n" + sentUrl; RevivalMod.Log.Warning("[bugs] could not open the browser: " + e.Message); }
                    if (GUI.Button(R(cx + cw - 160, cy, 160, 46), Loc.T("common.close", "Close"), button)) Hide();
                    return;
                }

                // while a dropdown list is open, everything under it is switched off, so the list gets the click
                GUI.enabled = openDrop == 0;
                GUI.Label(R(cx, cy, 160, 34), Loc.T("bugReport.title", "Title"), label);
                d.Title = GUI.TextField(R(cx + 160, cy, cw - 160, 38), d.Title ?? "", 120, field); cy += 46;
                GUI.Label(R(cx, cy, 160, 34), Loc.T("bugReport.type", "Type"), label);
                string[] catNames = CatNames(), sevNames = SevNames();
                int ci = Math.Max(0, Array.IndexOf(CatIds, d.Category));
                int si = Math.Max(0, Array.IndexOf(SevIds, d.Severity));
                float dropY = cy + 40;
                Rect catBox = new Rect(cx + 160, cy, 300, 38), sevBox = new Rect(cx + 470, cy, cw - 470, 38);
                GUI.enabled = openDrop == 0 || openDrop == 1;
                if (GUI.Button(R(catBox.x, catBox.y, catBox.width, catBox.height), catNames[ci] + (openDrop == 1 ? "  ^" : "  v"), button)) openDrop = openDrop == 1 ? 0 : 1;
                GUI.enabled = openDrop == 0 || openDrop == 2;
                if (GUI.Button(R(sevBox.x, sevBox.y, sevBox.width, sevBox.height), sevNames[si] + (openDrop == 2 ? "  ^" : "  v"), button)) openDrop = openDrop == 2 ? 0 : 2;
                GUI.enabled = openDrop == 0;
                cy += 46;
                GUI.Label(R(cx, cy, 160, 34), Loc.T("bugReport.whatHappened", "What happened"), label);
                d.Description = GUI.TextField(R(cx + 160, cy, cw - 160, 38), d.Description ?? "", 1000, field); cy += 50;

                if (d.Screenshot != null) d.SendShot = Tick(cx, cy, Loc.T("bugReport.sendShot", "Send the screenshot"), d.SendShot);
                d.SendLog = Tick(cx + cw / 2, cy, Loc.T("bugReport.sendLog", "Send the recent log"), d.SendLog); cy += 42;
                if (d.Dump != null) d.SendDump = Tick(cx, cy, Loc.T("bugReport.sendDump", "Send Unity's crash dump (admins only)"), d.SendDump);
                d.SendExtended = Tick(cx + cw / 2, cy, Loc.T("bugReport.sendExtended", "Include extended logs (admins only)"), d.SendExtended); cy += 42;
                GUI.Label(R(cx, cy, cw, 60), Loc.T("bugReport.alsoSent", "Also sent: mod version, device, graphics, screen and what you were doing in the game (scene, match rules). Only you and the admins see the log and screenshot."), small);
                cy += 66;

                var keep = GUI.color;
                if (problem) GUI.color = new Color(1f, 0.55f, 0.5f);
                GUI.Label(R(cx, cy, cw, 48), busy ? Loc.T("common.sending", "Sending...") : status, small);
                GUI.color = keep;
                cy = y + ph - 66;
                GUI.enabled = !busy && openDrop == 0;
                if (GUI.Button(R(cx, cy, 200, 48), Loc.T("bugReport.send", "Send"), button)) Send();
                if (GUI.Button(R(cx + cw - 180, cy, 180, 48), d.Kind == "manual" ? Loc.T("common.cancel", "Cancel") : Loc.T("common.notNow", "Not now"), button)) Hide();
                GUI.enabled = true;

                // the open list, drawn last so it sits on top
                if (openDrop != 0)
                {
                    bool cat = openDrop == 1;
                    string[] names = cat ? catNames : sevNames, ids = cat ? CatIds : SevIds;
                    Rect head = cat ? catBox : sevBox;
                    float rowH = 36, listH = names.Length * rowH + 8;
                    GUI.Box(R(head.x, dropY, head.width, listH), "", box);
                    GUI.Box(R(head.x, dropY, head.width, listH), "", box);      // twice: the panel colour is slightly see-through
                    for (int i = 0; i < names.Length; i++)
                        if (GUI.Button(R(head.x + 4, dropY + 4 + i * rowH, head.width - 8, rowH - 2), names[i], button))
                        {
                            if (cat) d.Category = ids[i]; else d.Severity = ids[i];
                            openDrop = 0;
                        }
                    // a click anywhere else closes the list
                    var ev = Event.current;
                    if (openDrop != 0 && ev != null && ev.type == EventType.MouseDown) { openDrop = 0; ev.Use(); }
                }
            }

            private static void Send()
            {
                if ((d.Title ?? "").Trim().Length < 3) { status = Loc.T("bugReport.needTitle", "Give it a title (what went wrong, at least 3 letters)."); problem = true; return; }
                busy = true; problem = false;
                var draft = d;
                string unityLog = draft.SendExtended ? ExtendedLogs.UnityLogPath() : null;
                Task.Run(async () =>
                {
                    string body;
                    try
                    {
                        // every log and the recent crash files, zipped off the main thread (they can be a few MB)
                        if (draft.SendExtended && draft.Extended == null)
                        {
                            try { draft.Extended = ExtendedLogs.Build(unityLog, Scrub, out _, out _); }
                            catch (Exception e) { RevivalMod.Log.Warning("[bugs] extended logs left out: " + e.Message); }      // the report goes without them
                        }
                        body = Body(draft);
                    }
                    catch (Exception e)
                    {
                        PlayFabTransport.OnMainThread(() => { busy = false; status = Loc.T("bugReport.buildFailed", "Could not put the report together: {error}", ("error", e.Message)); problem = true; });
                        return;
                    }
                    var (id, url, error, keepIt) = await Post(body);
                    if (id == null && keepIt) KeepDraft(body);
                    PlayFabTransport.OnMainThread(() =>
                    {
                        busy = false;
                        if (id != null)
                        {
                            sent = true; sentUrl = url ?? "";
                            heading = Loc.T("bugReport.sentHeading", "Bug {id} sent", ("id", id));
                            status = sentUrl.Length > 0
                                ? Loc.T("bugReport.thanksLink", "Thank you. Add steps or more details on the website:\n{url}", ("url", sentUrl))
                                : Loc.T("bugReport.thanks", "Thank you. Add steps or more details on the website (Bugs page).");
                            RevivalMod.Log.Msg($"[bugs] report sent as {id}");
                        }
                        else if (keepIt)
                        {
                            sent = true; heading = Loc.T("bugReport.notSentYet", "Not sent yet");
                            status = Loc.T("bugReport.savedForLater", "Couldn't send it now ({error}). It is saved and will be sent the next time you start the game.", ("error", error));
                            RevivalMod.Log.Warning("[bugs] report not sent, kept for later: " + error);
                        }
                        else { status = Loc.T("bugReport.notSent", "Not sent: {error}", ("error", error)); problem = true; RevivalMod.Log.Warning("[bugs] report refused: " + error); }
                    });
                });
            }
        }
    }
}
