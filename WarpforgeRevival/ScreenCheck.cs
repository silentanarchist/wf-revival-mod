using System;
using UnityEngine;

namespace WarpforgeRevival
{
    /// <summary>
    /// The start-up check that the loader managed to load the parts of .NET the mod needs (seen failing on a Pixel 6
    /// Pro), with a plain on-screen message when it did not.
    /// Until 0.15.22 this file also chased the phones' black menu: it read the picture back at set moments of every
    /// start-up and after each scene change, saved small copies (screens/screen-NN.bmp) and tried resizing the drawing
    /// area when it found black. The cause was found (the phone's hardware decoding of the loading video; fix:
    /// LoadingVideo = Still, which the Patcher app sets when a phone needs it), so the checks were removed (0.15.23).
    /// </summary>
    internal static class ScreenCheck
    {
        // ---------------------------------------------------------------- the loader's .NET

        /// <summary>True when the loader failed to load parts of .NET this start-up (the mod cannot work).</summary>
        internal static bool RuntimeBroken { get; private set; }
        private static string runtimeWhy;

        /// <summary>
        /// Once, before anything else: the parts of .NET the mod needs (encryption, the web client) can be
        /// loaded. On a Pixel 6 Pro one start-up in several failed to load them ("Method not found:
        /// SHA256.Create", "Failure has occurred while loading a type"); the mod then cannot reach the
        /// server and the game sat on a black loading screen. Now it says so on screen instead.
        /// </summary>
        internal static void CheckRuntime()
        {
            try { Probe(); }
            catch (Exception e)
            {
                RuntimeBroken = true;
                runtimeWhy = e.GetType().Name + ": " + e.Message;
                try { RevivalMod.Log.Error("[startup] the loader could not load parts of .NET this time (" + runtimeWhy + "); the mod cannot work. Close the game fully and start it again."); } catch { }
            }
        }

        // a method of its own: a missing part fails when this method is prepared, which the caller catches
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void Probe()
        {
            using (var sha = System.Security.Cryptography.SHA256.Create()) sha.ComputeHash(new byte[] { 1 });
            using (var h = new System.Net.Http.SocketsHttpHandler()) { }
            _ = System.Text.Json.JsonDocument.Parse("{}");
        }

        private static GUIStyle brokenStyle;

        private static void DrawBroken()
        {
            try { if (Event.current == null || Event.current.type != EventType.Repaint) return; } catch { return; }
            float w = Screen.width, h = Screen.height;
            brokenStyle ??= new GUIStyle(GUI.skin.label) { fontSize = Math.Max(24, (int)(h / 24)), wordWrap = true, alignment = TextAnchor.MiddleCenter };
            brokenStyle.normal.textColor = Color.white;
            GUI.Box(new Rect(w * 0.1f, h * 0.3f, w * 0.8f, h * 0.4f), "");
            // stays in English on purpose: the translations need the parts of .NET that failed to load here
            // (Loc starts an HttpClient and reads JSON), so Loc.T could fail just when this has to show
            GUI.Label(new Rect(w * 0.12f, h * 0.32f, w * 0.76f, h * 0.36f),
                "WF Revival could not start properly this time (the mod loader failed to load part of .NET).\n\n" +
                "Close the game fully (swipe it away) and open it again.", brokenStyle);
        }

        /// <summary>Called from the mod's OnGUI: the message when the mod could not start.</summary>
        internal static void OnGUI()
        {
            if (RuntimeBroken) { try { DrawBroken(); } catch { } }
        }
    }
}
