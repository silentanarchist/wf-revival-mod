using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppEverguild.LiveOps;

namespace WarpforgeRevival
{
    /// <summary>
    /// One hook per game method that several features of the mod need (0.15.12). Until then each feature hooked the
    /// method itself, so the same game method carried two or three hooks; every hook costs start-up time (about 20 ms
    /// each on a phone, where the loader rewrites the game's code for it) and Harmony rebuilds the method for each one.
    /// The features' own code is unchanged and stays in their files; each is called here, in a fixed order, each
    /// inside its own try so one failing does not stop the others.
    /// </summary>
    internal static class SharedHooks
    {
        private static void Run(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { RevivalMod.Log.Warning($"[hooks] {what}: {e.Message}"); }
        }

        // ---------------------------------------------------------------- the menu bar

        /// <summary>The menu bar is built (a new one after every match).</summary>
        [HarmonyPatch(typeof(NavigationPanelController), nameof(NavigationPanelController.Awake))]
        private static class NavAwake
        {
            private static void Postfix(NavigationPanelController __instance)
            {
                Run("menu bar built: menu cleanup", () => MenuCleanup.OnAwake.Postfix(__instance));
                Run("menu bar built: Testers button", () => TestersTab.BarBuilt.Postfix(__instance));
                Run("menu bar built: menu events", () => MenuEvents.NavBuilt(__instance));
            }
        }

        /// <summary>The menu bar is set up (the game's start-up is complete).</summary>
        [HarmonyPatch(typeof(NavigationPanelController), nameof(NavigationPanelController.OnInitializationComplete))]
        private static class NavReady
        {
            private static void Postfix(NavigationPanelController __instance)
            {
                Run("menu bar ready: menu cleanup", () => MenuCleanup.OnReady.Postfix(__instance));
                Run("menu bar ready: Testers button", () => TestersTab.BarReady.Postfix(__instance));
            }
        }

        // ---------------------------------------------------------------- windows with tabs

        /// <summary>A window with tabs wires up its ribbon (once per window, as it first opens).</summary>
        [HarmonyPatch(typeof(GameWindowWithTabs), nameof(GameWindowWithTabs.SetupTabs))]
        private static class SetupTabs
        {
            /// <summary>Before the game wires up the ribbon: tabs the mod adds to the Social window.</summary>
            private static void Prefix(GameWindowWithTabs __instance)
            {
                Run("tabs: Social tabs", () => SocialTabs.AddTabs.Prefix(__instance));
            }

            /// <summary>After: the ribbon's layout, then the Testers window's Play tab.</summary>
            private static void Postfix(GameWindowWithTabs __instance)
            {
                Run("tabs: menu layout", () => MenuLayout.Built.Postfix(__instance));
                Run("tabs: Testers Play tab", () => TestersCollection.PlayTab.Postfix(__instance));
            }
        }

        /// <summary>The tab a window opens on.</summary>
        [HarmonyPatch(typeof(GameWindowWithTabs), nameof(GameWindowWithTabs.GetStartingTab))]
        private static class StartingTab
        {
            private static void Postfix(GameWindowWithTabs __instance, ref WindowTabBase __result)
            {
                var result = __result;
                Run("starting tab: Social", () => SocialPage.StartOnFriends.Postfix(__instance, ref result));
                Run("starting tab: Testers", () => TestersCollection.StartingTab.Postfix(__instance, ref result));
                __result = result;
            }
        }

        // ---------------------------------------------------------------- the challenge popup

        /// <summary>The game's challenge popup opens (a profile's or an alliance member's Challenge button).</summary>
        [HarmonyPatch(typeof(DuelPopupWindow), nameof(DuelPopupWindow.Open))]
        private static class DuelOpen
        {
            private static void Postfix(DuelPopupWindow __instance)
            {
                Run("challenge popup: labels", () => DuelWindow.Opened.Postfix(__instance));
                Run("challenge popup: Challenge page", () => ChallengeMenu.DuelOpened.Postfix(__instance));
            }
        }

        // ---------------------------------------------------------------- chat

        /// <summary>A chat panel starts.</summary>
        [HarmonyPatch(typeof(ChatPanel), nameof(ChatPanel.Start))]
        private static class ChatStart
        {
            private static void Postfix(ChatPanel __instance)
            {
                Run("chat panel: message length", () => GlobalChat.LongerBox.Postfix(__instance));
#if ANDROID_PORT
                Run("chat panel: keyboard sends", () => GlobalChat.KeyboardSends.Postfix(__instance));
#endif
            }
        }

        // ---------------------------------------------------------------- decks

        /// <summary>The deck editor's list of cards to offer.</summary>
        [HarmonyPatch(typeof(DeckEditingWindow), nameof(DeckEditingWindow.GetCardCollection))]
        private static class EditorCards
        {
            private static void Postfix(DeckEditingWindow __instance, ref Il2CppSystem.Collections.Generic.List<RawCardScript> __result)
            {
                var result = __result;
                Run("deck editor cards: custom cards", () => CustomCards.EditorList.Postfix(__instance, ref result));
                Run("deck editor cards: Offence cards", () => OffenceInCollection.Editor.Postfix(result));
                __result = result;
            }
        }

        /// <summary>The deck list switches to a mode's decks.</summary>
        [HarmonyPatch(typeof(SelectDecksTab), nameof(SelectDecksTab.ToggleSelectDeck))]
        private static class DeckList
        {
            private static void Prefix(IOwnDeckPlayEvent deckPlayEvent)
            {
                Run("deck list: mode", () => LongGame.DeckListMode.Prefix(deckPlayEvent));
            }

            private static void Postfix(SelectDecksTab __instance, IOwnDeckPlayEvent deckPlayEvent)
            {
                Run("deck list: Testers switch", () => TestersCollection.ListShown.Postfix(__instance, deckPlayEvent));
            }
        }
    }
}
