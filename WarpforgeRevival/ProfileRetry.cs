using System;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace WarpforgeRevival
{
    /// <summary>
    /// A player's profile that needs a second click (an original-game bug, reported 2026-10-27: in a match the first
    /// click on the opponent only makes the click sound; the second opens the profile, and it then works for the rest of
    /// the match). PlayerProfileMenu.Open shows a profile at once when it already has that player's data, and otherwise
    /// asks for it and shows it when the answer comes (OnComplete), or an error popup (OnError). On the first click of a
    /// match the profile stays hidden although neither shows anything - the request is passed with a 1000 limit in the
    /// game's code, so most likely the answer comes too late for it and is dropped; the second click then finds the data.
    /// So: when an Open leaves the profile hidden, the mod asks again (the game's own Open, as the second click does)
    /// after 1.5 s and once more after 3 s if it is still hidden, and stops as soon as the profile shows or the window is
    /// closed. Logged as [profile], with how long the answer took, so the cause can be confirmed from a log.
    /// </summary>
    internal static class ProfileRetry
    {
        private static PlayerProfileMenu menu;
        private static float openedAt, askedAt;
        private static int retries;
        private static bool reopening;

        private static bool Shown(PlayerProfileMenu m)
        {
            try { var c = m.tabContent; return (object)c != null && c.activeSelf; } catch { return true; }
        }

        [HarmonyPatch(typeof(PlayerProfileMenu), nameof(PlayerProfileMenu.Open))]
        private static class Opened
        {
            private static void Postfix(PlayerProfileMenu __instance)
            {
                try
                {
                    if (reopening || !Ui.Alive(__instance)) return;
                    bool mine = false;
                    try { mine = __instance.IsCurrentPlayer; } catch { }
                    if (mine || Shown(__instance)) return;                  // own profile, or the data was already there
                    menu = __instance;
                    openedAt = askedAt = Time.realtimeSinceStartup;
                    retries = 0;
                }
                catch (Exception e) { RevivalMod.Log.Warning("[profile] " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(PlayerProfileMenu), nameof(PlayerProfileMenu.OnComplete))]
        private static class Answered
        {
            private static void Prefix(PlayerProfileMenu __instance)
            {
                if ((object)menu != null && Ui.Alive(__instance) && __instance.Pointer == menu.Pointer)
                    RevivalMod.Log.Msg($"[profile] player data arrived after {Time.realtimeSinceStartup - openedAt:0.0} s");
            }
        }

        [HarmonyPatch(typeof(PlayerProfileMenu), nameof(PlayerProfileMenu.OnError))]
        private static class Failed
        {
            private static void Prefix(string obj) => RevivalMod.Log.Warning("[profile] the player's data could not be fetched: " + obj);
        }

        /// <summary>Every frame (cheap when no profile is waiting).</summary>
        internal static void Tick()
        {
            if ((object)menu == null) return;
            try
            {
                if (!Ui.Alive(menu) || !menu.IsOpen()) { menu = null; return; }
                float now = Time.realtimeSinceStartup;
                if (Shown(menu))
                {
                    if (retries > 0) RevivalMod.Log.Msg($"[profile] shown after asking again {retries} time(s) ({now - openedAt:0.0} s)");
                    menu = null;
                    return;
                }
                if (now - askedAt < 1.5f) return;
                if (retries >= 2) { RevivalMod.Log.Warning($"[profile] still not shown after {now - openedAt:0.0} s; left to the player"); menu = null; return; }
                retries++;
                askedAt = now;
                RevivalMod.Log.Msg($"[profile] the profile did not show after {now - openedAt:0.0} s; asking again ({retries})");
                reopening = true;
                try { menu.Open(); } finally { reopening = false; }
            }
            catch (Exception e) { menu = null; RevivalMod.Log.Warning("[profile] asking again: " + e.Message); }
        }
    }
}
