using System;
using System.Collections.Generic;
using System.IO;

namespace WarpforgeRevival
{
    /// <summary>
    /// Memory figures in the log, to find out whether the game leaks memory over many matches (0.15.13, owner
    /// 2026-11-01: Android tends to crash outright where a PC recovers from a freeze; B-0009, a Galaxy S24, ended
    /// in its 8th match of a 2-hour run with no error logged). Logging only; nothing in the game changes.
    ///
    /// - At every scene change (so before and after each match) and every 5 minutes: the process's real memory
    ///   use (Android: VmRSS from /proc/self/status; PC: the working set), Unity's allocated and reserved memory,
    ///   the game's script heap, the mod's own .NET heap, texture memory, and on Android the device's free memory.
    ///   A leak shows as numbers that climb from one match to the next in the same run.
    /// - Unity's "memory is low" signal (Android passes it on before it starts closing apps), with the figures.
    /// - Android 11 and newer: how the app's last runs ended, as Android recorded it (killed for low memory, native
    ///   crash, not responding, closed by the user, ...), with its memory use at that moment. Logged at the start,
    ///   and the record matching a crashed run goes into that run's automatic crash report (BugReport.OfferCrash).
    /// Every reading is in its own try: a call this game's build does not carry is left out, once, with a note.
    /// </summary>
    internal static class MemoryWatch
    {
        private const float Every = 300f;            // seconds between readings while nothing else happens
        private static float next = 60f;
        private static int matches;
        private static bool lowHooked, lowNoted;
        private static readonly HashSet<string> missing = new HashSet<string>();

        /// <summary>Called every frame: a reading every 5 minutes.</summary>
        internal static void Tick()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < next) return;
            bool first = exits == null;
            next = now + Every;
            Log("every 5 minutes");
            if (first) RecentExits();              // logged once, a minute after the start
            if (!lowHooked) HookLowMemory();
        }

        /// <summary>A scene was loaded (MelonLoader's callback): a reading, counting the matches of this run.</summary>
        internal static void SceneReady(string scene)
        {
            try
            {
                if (!string.IsNullOrEmpty(scene) && scene.StartsWith("Battle", StringComparison.OrdinalIgnoreCase)) matches++;
                Log("scene '" + scene + "'");
                next = UnityEngine.Time.realtimeSinceStartup + Every;
                if (!lowHooked) HookLowMemory();
            }
            catch (Exception e) { RevivalMod.Log.Warning("[memory] " + e.Message); }
        }

        private static void Log(string when)
        {
            try { RevivalMod.Log.Msg($"[memory] {when} (match {matches} of this run, up {UnityEngine.Time.realtimeSinceStartup / 60f:0} min): {Figures()}"); }
            catch (Exception e) { RevivalMod.Log.Warning("[memory] " + e.Message); }
        }

        private static string MB(long bytes) => bytes < 0 ? "?" : (bytes / (1024.0 * 1024.0)).ToString("0") + " MB";

        private static long Try(string what, Func<long> read)
        {
            if (missing.Contains(what)) return -1;
            try { return read(); }
            catch (Exception e)
            {
                missing.Add(what);
                RevivalMod.Log.Msg($"[memory] {what} cannot be read in this game build ({e.GetType().Name}); left out");
                return -1;
            }
        }

        /// <summary>The figures as one line.</summary>
        internal static string Figures()
        {
            long process = Try("process memory", ProcessBytes);
            long allocated = Try("Unity allocated", () => UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong());
            long reserved = Try("Unity reserved", () => UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong());
            long heapUsed = Try("game heap used", () => UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong());
            long heap = Try("game heap", () => UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong());
            long textures = Try("texture memory", () => (long)UnityEngine.Texture.currentTextureMemory);
            long mod = GC.GetTotalMemory(false);
            string line = $"process {MB(process)}, Unity {MB(allocated)} allocated / {MB(reserved)} reserved, game heap {MB(heapUsed)} of {MB(heap)}, textures {MB(textures)}, mod {MB(mod)}";
#if ANDROID_PORT
            long free = Try("device free memory", DeviceAvailableBytes);
            line += $", device free {MB(free)}";
#endif
            return line;
        }

        private static long ProcessBytes()
        {
#if ANDROID_PORT
            foreach (string l in File.ReadLines("/proc/self/status"))
                if (l.StartsWith("VmRSS:", StringComparison.Ordinal))
                    return long.Parse(l.Substring(6).Trim().Split(' ')[0]) * 1024;
            return -1;
#else
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            return p.WorkingSet64;
#endif
        }

#if ANDROID_PORT
        private static long DeviceAvailableBytes()
        {
            foreach (string l in File.ReadLines("/proc/meminfo"))
                if (l.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    return long.Parse(l.Substring(13).Trim().Split(' ')[0]) * 1024;
            return -1;
        }
#endif

        private static void HookLowMemory()
        {
            lowHooked = true;
            try
            {
                UnityEngine.Application.add_lowMemory(Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityEngine.Application.LowMemoryCallback>(
                    new Action(() =>
                    {
                        try
                        {
                            if (!lowNoted) { lowNoted = true; RevivalMod.Log.Warning("[memory] the system says memory is low (first time this run): " + Figures()); }
                            else RevivalMod.Log.Warning("[memory] memory is low again: " + Figures());
                        }
                        catch { }
                    })));
            }
            catch (Exception e) { RevivalMod.Log.Msg("[memory] the low-memory signal cannot be followed in this game build (" + e.GetType().Name + ")"); }
        }

        // ------------------------------------------------------------------ Android: how the last runs ended

        internal sealed class Exit
        {
            public DateTime At;
            public int Reason, Status, Importance;
            public long PssKb, RssKb;
            public string Description;
            public override string ToString() =>
                $"{ReasonName(Reason)} ({Reason}) at {At:yyyy-MM-dd HH:mm:ss}Z, status {Status}, importance {Importance}, memory {PssKb / 1024} MB (pss) / {RssKb / 1024} MB (rss)" +
                (string.IsNullOrEmpty(Description) ? "" : ", \"" + Description + "\"");
        }

        private static List<Exit> exits;

        private static string ReasonName(int r)
        {
            switch (r)
            {
                case 1: return "exited by itself";
                case 2: return "killed by a signal";
                case 3: return "killed for low memory";
                case 4: return "crash (Java)";
                case 5: return "crash (native)";
                case 6: return "not responding (ANR)";
                case 7: return "failed to start";
                case 8: return "permission change";
                case 9: return "excessive resource use";
                case 10: return "closed by the user";
                case 11: return "stopped by the user";
                case 12: return "a dependency died";
                case 13: return "other";
                case 14: return "frozen by the system";
                case 15: return "package state change";
                case 16: return "package updated";
                default: return "unknown";
            }
        }

        /// <summary>How the app's recent runs ended, as Android recorded them (newest first); read once. Empty on PC
        /// and on Android before 11.</summary>
        internal static List<Exit> RecentExits()
        {
            if (exits != null) return exits;
            exits = new List<Exit>();
#if ANDROID_PORT
            try { ReadExits(exits); }
            catch (Exception e)
            {
                try { UnityEngine.AndroidJNI.ExceptionClear(); } catch { }
                RevivalMod.Log.Msg("[memory] Android's record of how the last runs ended could not be read: " + e.Message);
            }
            if (exits.Count > 0)
                RevivalMod.Log.Msg("[memory] how the last runs ended (Android): " + string.Join(" | ", exits));
#endif
            return exits;
        }

        /// <summary>Android's record of the run that ended around `closedAt` (within 3 minutes), or null.</summary>
        internal static Exit ExitNear(DateTime closedAtUtc)
        {
            Exit best = null;
            foreach (var e in RecentExits())
            {
                double d = Math.Abs((e.At - closedAtUtc).TotalMinutes);
                if (d <= 3 && (best == null || d < Math.Abs((best.At - closedAtUtc).TotalMinutes))) best = e;
            }
            return best;
        }

#if ANDROID_PORT
        private static void ReadExits(List<Exit> into)
        {
            IntPtr version = UnityEngine.AndroidJNI.FindClass("android/os/Build$VERSION");
            int sdk = UnityEngine.AndroidJNI.GetStaticIntField(version, UnityEngine.AndroidJNI.GetStaticFieldID(version, "SDK_INT", "I"));
            if (sdk < 30) { RevivalMod.Log.Msg($"[memory] Android {sdk} keeps no record of how runs ended (needs 11 / API 30)"); return; }

            var none = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<UnityEngine.jvalue>(0);
            IntPtr player = UnityEngine.AndroidJNI.FindClass("com/unity3d/player/UnityPlayer");
            IntPtr activity = UnityEngine.AndroidJNI.GetStaticObjectField(player, UnityEngine.AndroidJNI.GetStaticFieldID(player, "currentActivity", "Landroid/app/Activity;"));
            if (activity == IntPtr.Zero) throw new Exception("the app's screen was not found");

            IntPtr context = UnityEngine.AndroidJNI.FindClass("android/content/Context");
            IntPtr getService = UnityEngine.AndroidJNI.GetMethodID(context, "getSystemService", "(Ljava/lang/String;)Ljava/lang/Object;");
            var one = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<UnityEngine.jvalue>(1);
            IntPtr name = UnityEngine.AndroidJNI.NewStringUTF("activity");
            one[0] = new UnityEngine.jvalue { l = name };
            IntPtr am = UnityEngine.AndroidJNI.CallObjectMethod(activity, getService, one);
            UnityEngine.AndroidJNI.DeleteLocalRef(name);
            if (am == IntPtr.Zero) throw new Exception("no activity manager");

            IntPtr amClass = UnityEngine.AndroidJNI.FindClass("android/app/ActivityManager");
            IntPtr history = UnityEngine.AndroidJNI.GetMethodID(amClass, "getHistoricalProcessExitReasons", "(Ljava/lang/String;II)Ljava/util/List;");
            var three = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<UnityEngine.jvalue>(3);
            three[0] = new UnityEngine.jvalue { l = IntPtr.Zero };       // this app
            three[1] = new UnityEngine.jvalue { i = 0 };                 // any process of it
            three[2] = new UnityEngine.jvalue { i = 5 };                 // the newest five
            IntPtr list = UnityEngine.AndroidJNI.CallObjectMethod(am, history, three);
            if (list == IntPtr.Zero) return;

            IntPtr listClass = UnityEngine.AndroidJNI.FindClass("java/util/List");
            IntPtr size = UnityEngine.AndroidJNI.GetMethodID(listClass, "size", "()I");
            IntPtr get = UnityEngine.AndroidJNI.GetMethodID(listClass, "get", "(I)Ljava/lang/Object;");
            IntPtr info = UnityEngine.AndroidJNI.FindClass("android/app/ApplicationExitInfo");
            IntPtr reason = UnityEngine.AndroidJNI.GetMethodID(info, "getReason", "()I");
            IntPtr status = UnityEngine.AndroidJNI.GetMethodID(info, "getStatus", "()I");
            IntPtr importance = UnityEngine.AndroidJNI.GetMethodID(info, "getImportance", "()I");
            IntPtr timestamp = UnityEngine.AndroidJNI.GetMethodID(info, "getTimestamp", "()J");
            IntPtr pss = UnityEngine.AndroidJNI.GetMethodID(info, "getPss", "()J");
            IntPtr rss = UnityEngine.AndroidJNI.GetMethodID(info, "getRss", "()J");
            IntPtr description = UnityEngine.AndroidJNI.GetMethodID(info, "getDescription", "()Ljava/lang/String;");

            int n = UnityEngine.AndroidJNI.CallIntMethod(list, size, none);
            for (int k = 0; k < n && k < 5; k++)
            {
                var at = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<UnityEngine.jvalue>(1);
                at[0] = new UnityEngine.jvalue { i = k };
                IntPtr e = UnityEngine.AndroidJNI.CallObjectMethod(list, get, at);
                if (e == IntPtr.Zero) continue;
                var x = new Exit
                {
                    Reason = UnityEngine.AndroidJNI.CallIntMethod(e, reason, none),
                    Status = UnityEngine.AndroidJNI.CallIntMethod(e, status, none),
                    Importance = UnityEngine.AndroidJNI.CallIntMethod(e, importance, none),
                    At = DateTimeOffset.FromUnixTimeMilliseconds(UnityEngine.AndroidJNI.CallLongMethod(e, timestamp, none)).UtcDateTime,
                    PssKb = UnityEngine.AndroidJNI.CallLongMethod(e, pss, none),
                    RssKb = UnityEngine.AndroidJNI.CallLongMethod(e, rss, none),
                };
                IntPtr d = UnityEngine.AndroidJNI.CallObjectMethod(e, description, none);
                if (d != IntPtr.Zero)
                {
                    x.Description = UnityEngine.AndroidJNI.GetStringUTFChars(d);
                    if (x.Description != null && x.Description.Length > 200) x.Description = x.Description.Substring(0, 200);
                    UnityEngine.AndroidJNI.DeleteLocalRef(d);
                }
                UnityEngine.AndroidJNI.DeleteLocalRef(e);
                into.Add(x);
            }
            UnityEngine.AndroidJNI.DeleteLocalRef(list);
            UnityEngine.AndroidJNI.DeleteLocalRef(am);
            UnityEngine.AndroidJNI.DeleteLocalRef(activity);
        }
#endif
    }
}
