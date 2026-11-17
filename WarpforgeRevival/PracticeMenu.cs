using System;
using HarmonyLib;
using Il2Cpp;

namespace WarpforgeRevival
{
    /// <summary>Fixes for the Play-page practice menu (pick a ready-made deck and fight the AI).</summary>
    internal static class PracticeMenu
    {
        // A player with no deck for the selected mode gets an empty placeholder deck (no warlord),
        // which the menu then dereferences. Treat that as "no own deck".
        [HarmonyPatch(typeof(PracticeModePopup), nameof(PracticeModePopup.SetArmyButtons))]
        private static class EmptyOwnDeck
        {
            private static void Prefix(PracticeModePopup __instance)
            {
                try
                {
                    var w = __instance.playerDeckInfoWrapper;
                    if ((object)w == null) return;
                    var d = w.cardDeck;
                    if ((object)d != null && (object)d.deckHero == null)
                    {
                        w.cardDeck = null;
                        RevivalMod.Log.Msg("[practice] no own deck for this mode; showing ready-made decks only");
                    }
                }
                catch (Exception e) { RevivalMod.Log.Warning("[practice] " + e.Message); }
            }
        }

        // Ready-made deck names are localization keys that the recovered text data does not contain.
        [HarmonyPatch(typeof(DemoDeckInfoSO), nameof(DemoDeckInfoSO.GetName))]
        private static class DeckName
        {
            private static void Postfix(DemoDeckInfoSO __instance, PlayModes gameMode, ref string __result)
            {
                try
                {
                    if (!string.IsNullOrEmpty(__result)) return;
                    var deck = __instance.GetDeck(gameMode);
                    if ((object)deck != null && !string.IsNullOrEmpty(deck.deckName)) __result = deck.deckName;
                }
                catch (Exception e) { RevivalMod.Log.Warning("[practice] " + e.Message); }
            }
        }

        // ---------------------------------------------------------------- Classic or Skirmish health
        // The final card data gives warlords their Skirmish health, and Classic adds to it through
        // the mode's rules on the server. Practice against the AI always runs under the one event
        // "PracticeEvent" whichever way its Classic/Skirmish switch is set, so the rules cannot differ
        // there; the mod adds the Classic amount itself when the switch was on Classic.
        private static bool practiceModeKnown;
        private static PlayModes practiceMode;

        [HarmonyPatch(typeof(PracticeModePopup), nameof(PracticeModePopup.BattleButtonOnClick))]
        private static class Started
        {
            private static void Prefix(PracticeModePopup __instance)
            {
                try
                {
                    practiceMode = __instance.currentPlayMode;
                    practiceModeKnown = true;
                    RevivalMod.Log.Msg($"[practice] starting a practice match on {practiceMode}");
                }
                catch (Exception e) { practiceModeKnown = false; RevivalMod.Log.Warning("[practice] " + e.Message); }
            }
        }

        // ---------------------------------------------------------------- the AI's deck follows the switch
        // The game picks the AI's practice deck without looking at the Classic/Skirmish switch, so a
        // Classic match could be played against a 12-card Skirmish deck (and the other way round).
        // When the deck it picked is for the other mode, a practice deck for the chosen mode is used.
        private static readonly System.Random Dice = new System.Random();

        /// <summary>Called from BotDecks' patch on MatchData.GetBotDeck (one place for "which deck the AI gets",
        /// code review M-26, 0.13.75).</summary>
        internal static class AiDeck
        {
            internal static void Postfix(MatchType matchType, ref PrebuiltDeck __result)
            {
                try
                {
                    if (!practiceModeKnown || matchType != MatchType.PracticeOffline) return;
                    if (practiceMode != PlayModes.Classic && practiceMode != PlayModes.Skirmish) return;
                    var picked = __result;
                    if ((object)picked != null && picked.gameMode == practiceMode) return;
                    var all = PlayerDataManager.singletonManager?.PrebuiltDeckCollection?.practiceDecks;
                    if (all == null) return;
                    var same = new System.Collections.Generic.List<PrebuiltDeck>();     // right mode and same difficulty
                    var any = new System.Collections.Generic.List<PrebuiltDeck>();      // right mode
                    for (int i = 0; i < all.Count; i++)
                    {
                        var d = all[i];
                        if ((object)d == null || d.m_CachedPtr == IntPtr.Zero || d.gameMode != practiceMode) continue;
                        var ids = d.cardLibraryIds;
                        if (ids == null || ids.Count == 0) continue;
                        any.Add(d);
                        if ((object)picked != null && d.difficulty == picked.difficulty) same.Add(d);
                    }
                    var pool = same.Count > 0 ? same : any;
                    if (pool.Count == 0)
                    {
                        RevivalMod.Log.Msg($"[practice] no {practiceMode} practice deck found for the AI; keeping '{((object)picked == null ? "none" : picked.deckName)}'");
                        return;
                    }
                    var chosen = pool[Dice.Next(pool.Count)];
                    RevivalMod.Log.Msg($"[practice] AI deck '{((object)picked == null ? "none" : picked.deckName)}' is not a {practiceMode} deck; using '{chosen.deckName}' ({chosen.cardLibraryIds.Count} cards)");
                    __result = chosen;
                }
                catch (Exception e) { RevivalMod.Log.Warning("[practice] AI deck: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.SetupFullHero))]
        private static class ClassicHealth
        {
            private static void Postfix(BattleManager __instance, bool isPlayer)
            {
                // What rules this match is really running under (written once per match, for both sides).
                try
                {
                    if (isPlayer)
                    {
                        var rules = __instance.matchData?.GameplayData;
                        RevivalMod.Log.Msg("[rules] match rules: " + ((object)rules == null ? "none found" :
                            $"energy at start {rules.startingMana} (going first) / {rules.startingManaSecond} (going second), +{rules.manaPerTurn} per turn, " +
                            $"second player bonus {rules.addedManaSecond}, carried over {rules.manaAccumulation}, deck {rules.deckSize}, hand limit {rules.handLimit}, " +
                            $"warlord health change {rules.warlordLifeChange}") +
                            $"; you go {(__instance.playerGoesFirst ? "first" : "second")}; starting energy you {__instance.GetStartingMana(true)}, opponent {__instance.GetStartingMana(false)}");
                    }
                }
                catch (Exception e) { RevivalMod.Log.Warning("[rules] " + e.Message); }
                try
                {
                    int bonus = ServerSettings.PracticeClassicLife;
                    if (bonus == 0 || !practiceModeKnown || practiceMode != PlayModes.Classic) return;
                    // Practice against the AI only (0.15.25). The practice-menu choice above is remembered for the rest
                    // of the session, so a friend challenge played afterwards also got the bonus - on the side of the
                    // player who had been in Practice only, which put the two games' warlord health out of step
                    // (desyncs late in challenges, 2026-11-16).
                    var md = __instance.matchData;
                    if ((object)md == null || md.matchType != MatchType.PracticeOffline) return;
                    var modes = LiveOpsManager.GetHandler<GameModes>();
                    var current = (object)modes == null ? null : modes.CurrentPlayingEvent;
                    var data = (object)current == null ? null : current.GetBaseData();
                    if ((object)data == null || data.eventId != "PracticeEvent") return;
                    // The game adds the mode's own health change itself; only step in when the match has none.
                    var own = __instance.matchData?.GameplayData;
                    if ((object)own != null && own.warlordLifeChange != 0) return;
                    var hero = __instance.GetHero(isPlayer);
                    if ((object)hero == null) return;
                    int before = hero.currentMaxHealth;
                    hero.currentMaxHealth = before + bonus;
                    hero.currentHealth = before + bonus;
                    try { hero.UpdateHealthText(false); } catch { }
                    RevivalMod.Log.Msg($"[practice] Classic practice: {(isPlayer ? "your" : "enemy")} warlord health {before} -> {before + bonus}");
                }
                catch (Exception e) { RevivalMod.Log.Warning("[practice] warlord health: " + e.Message); }
            }
        }

        // The first button is the player's own deck; its "Your deck" caption is a missing translation too.
        [HarmonyPatch(typeof(DeckSelectorMenuItemDemo), nameof(DeckSelectorMenuItemDemo.InitializeWithPlayerDeck))]
        private static class OwnDeckCaption
        {
            private static void Postfix(DeckSelectorMenuItemDemo __instance, CardDeck playerDeck)
            {
                try
                {
                    var label = __instance.deckName;
                    if ((object)label == null || !string.IsNullOrEmpty(label.text)) return;
                    string name = (object)playerDeck != null ? playerDeck.deckName : null;
                    label.text = string.IsNullOrEmpty(name) ? Loc.T("practice.yourDeck", "Your deck") : name;
                }
                catch (Exception e) { RevivalMod.Log.Warning("[practice] " + e.Message); }
            }
        }
    }
}
