using System;
using System.Reflection;
using HarmonyLib;

namespace WarpforgeRevival
{
    /// <summary>
    /// Mirrors the game's own debug log (normally switched off in release builds) into the MelonLoader log as
    /// [game] lines, and on PC also Unity's Debug.Log/LogWarning as [unity] lines. (Until 0.15.10 it also hooked
    /// 42 login/loading methods to log [trace] lines; the [game] lines show the same steps, and on Android each
    /// hook added about 22 ms of black screen at start-up, so they were removed.) Patches are applied by name so
    /// a missing method only produces a warning.
    /// </summary>
    internal static class Diagnostics
    {
        public static void Install(HarmonyLib.Harmony harmony)
        {
            int ok = 0;

            // Forward the game's own debug output.
            var dbg = GameTypes.Find("CustomDebug");
            if (dbg != null)
            {
                foreach (var name in new[] { "Log", "LogWarning", "LogError" })
                {
                    var m = dbg.GetMethod(name, BindingFlags.Static | BindingFlags.Public);
                    var p = typeof(Diagnostics).GetMethod("Game" + name, BindingFlags.Static | BindingFlags.NonPublic);
                    if (m != null) { harmony.Patch(m, prefix: new HarmonyMethod(p)); ok++; }
                }
            }

            // Unity's own Debug.Log / LogWarning (Addressables and some game code log through these directly).
#if ANDROID_PORT
            // Left out on a phone for now: the game writes a line per inventory item through these.
            if (ok >= 0) { RevivalMod.Log.Msg($"[diag] forwarding the game's log ({ok} hooks)"); return; }
#endif
            try
            {
                harmony.Patch(AccessTools.Method(typeof(UnityEngine.Debug), nameof(UnityEngine.Debug.Log), new[] { typeof(Il2CppSystem.Object) }),
                    prefix: new HarmonyMethod(typeof(Diagnostics).GetMethod(nameof(UnityLog), BindingFlags.Static | BindingFlags.NonPublic)));
                harmony.Patch(AccessTools.Method(typeof(UnityEngine.Debug), nameof(UnityEngine.Debug.LogWarning), new[] { typeof(Il2CppSystem.Object) }),
                    prefix: new HarmonyMethod(typeof(Diagnostics).GetMethod(nameof(UnityLogWarning), BindingFlags.Static | BindingFlags.NonPublic)));
                ok += 2;
            }
            catch (Exception e) { RevivalMod.Log.Warning("[diag] could not hook Unity Debug.Log: " + e.Message); }

            RevivalMod.Log.Msg($"[diag] forwarding the game's log ({ok} hooks)");
        }

        private static void GameLog(string logString) => RevivalMod.Log.Msg("[game] " + logString);
        private static void GameLogWarning(string logString) => RevivalMod.Log.Warning("[game] " + logString);
        private static void GameLogError(string logString) => RevivalMod.Log.Error("[game] " + logString);

        private static void UnityLog(Il2CppSystem.Object message)
        {
            try
            {
                string text = message?.ToString();
                // Written once per inventory item and on many events (thousands per start, on PC and phones:
                // 3,343 of 4,886 lines in a PC log on 2026-10-16); it says nothing, so it is left out.
                if (text == "No listener registered to this event") return;
                RevivalMod.Log.Msg("[unity] " + text);
            }
            catch { }
        }

        // Unity repeats this one for every temporary picture it makes (the format falls back by itself and
        // nothing looks different), so after the first it is left out of the log.
        private static bool formatFallbackShown;

        private static void UnityLogWarning(Il2CppSystem.Object message)
        {
            try
            {
                string text = message?.ToString();
                if (text != null && text.Contains("RenderTexture::GetTemporary fallbacks to"))
                {
                    if (formatFallbackShown) return;
                    formatFallbackShown = true;
                    RevivalMod.Log.Warning("[unity] " + text + " (harmless; further warnings like this are left out of the log)");
                    return;
                }
                RevivalMod.Log.Warning("[unity] " + text);
            }
            catch { }
        }
    }

    /// <summary>Resolves game types regardless of the Il2Cpp namespace prefix MelonLoader uses.</summary>
    internal static class GameTypes
    {
        public static Type Find(string name)
        {
            // Look only in the game's assembly; scanning every assembly trips over Unity types
            // that can't be loaded through reflection (noisy warnings in the log).
            var asm = typeof(Il2Cpp.LoginManager).Assembly;
            return asm.GetType("Il2Cpp." + name) ?? asm.GetType("Il2Cpp" + name) ?? asm.GetType(name)
                ?? asm.GetType("Il2Cpp." + name.Replace('/', '+')) ?? asm.GetType(name.Replace('/', '+'));
        }
    }
}
