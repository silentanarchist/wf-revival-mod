using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace WarpforgeRevival
{
    /// <summary>
    /// Updates to official cards, played by testers in the custom-card modes first (owner, 2026-11-23;
    /// claude/official-card-updates.md). 0.15.32.
    ///
    /// An approved update in the card creator is the same card with new rules (cost, stats, abilities). The custom-card
    /// package carries each one as a card object of its own (build_custom.py: the official card with only what the update
    /// changed, same id, short id, picture and sounds), listed under "updates" in custom-tester.json. That object is NOT
    /// added to the game's card list: the collection, deck editors, deck codes and every other mode keep the official
    /// card. In a match of a mode the server lists in "customCardModes" (the testers' Custom Cards mode), every card the
    /// match makes goes through BattleCardManager.CreateCard - the deck, the opponent's deck, cards created or summoned
    /// by other cards - and there the official card is swapped for its update. The update's rules text is shown for the
    /// update's object only, so the official card's text never changes.
    /// Both players must have the same package: the match ticket carries its version (MatchTicket), and the match
    /// service only pairs testers with the same one.
    /// </summary>
    internal static class CardUpdates
    {
        /// <summary>Official card id -> its update (an object from the package's card bundle).</summary>
        private static readonly Dictionary<string, RawCardScript> updates = new Dictionary<string, RawCardScript>();
        /// <summary>The update object -> its rules text per language ("en" at least), for CardText.</summary>
        private static readonly Dictionary<IntPtr, Dictionary<string, string>> texts = new Dictionary<IntPtr, Dictionary<string, string>>();
        private static readonly HashSet<string> notedThisMatch = new HashSet<string>();
        private static string noted;

        internal static int Count => updates.Count;

        /// <summary>From CustomBundle, once the package's card bundle is open.</summary>
        internal static void Load(JsonElement root, AssetBundle cards)
        {
            updates.Clear(); texts.Clear();
            if ((object)cards == null || !root.TryGetProperty("updates", out var list) || list.ValueKind != JsonValueKind.Array) return;
            foreach (var u in list.EnumerateArray())
            {
                string id = u.TryGetProperty("id", out var i) ? i.GetString() : null;
                string guid = u.TryGetProperty("guid", out var g) ? g.GetString() : null;
                string design = u.TryGetProperty("design", out var d) ? d.GetString() : "?";
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(guid)) continue;
                RawCardScript card = null;
                try { card = cards.LoadAsset(guid, Il2CppType.Of<RawCardScript>())?.TryCast<RawCardScript>(); }
                catch (Exception e) { RevivalMod.Log.Warning($"[updates] {id}: {e.Message}"); }
                if ((object)card == null || card.uniqueId != id) { RevivalMod.Log.Warning($"[updates] the update to {id} ({design}) is not in the card bundle"); continue; }
                card.hideFlags = HideFlags.DontUnloadUnusedAsset;
                updates[id] = card;
                if (u.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.Object)
                {
                    var langs = new Dictionary<string, string>();
                    foreach (var lang in t.EnumerateObject())
                        if (lang.Value.TryGetProperty("text", out var body) && !string.IsNullOrEmpty(body.GetString())) langs[lang.Name] = body.GetString();
                    if (langs.Count > 0) texts[card.Pointer] = langs;
                }
                RevivalMod.Log.Msg($"[updates] update to {id} ({design}) ready: cost {card.manaCost}, {card.meleeAttack}/{card.rangeAttack}/{card.maxHealth}");
            }
        }

        /// <summary>The mode being played, when it is one where the updates are played (null otherwise). A replay plays in
        /// the mode it was recorded in (its saved rules).</summary>
        private static string ActiveMode(bool replay)
        {
            if (updates.Count == 0) return null;
            string mode = replay ? ChallengeRules.RulesEvent : ChallengeRules.ActiveEvent ?? LongGame.PlayingEventId();
            return ActiveIn(mode) ? mode : null;
        }

        /// <summary>Whether a match of this mode plays the updates (for the replay's card value).</summary>
        internal static bool ActiveIn(string mode) => mode != null && ServerSettings.CustomCardModes.Contains(mode);

        /// <summary>For CardText: an update's own rules text in the game's language (English otherwise).</summary>
        internal static bool TextOf(RawCardScript card, out string text)
        {
            text = null;
            if (texts.Count == 0 || (object)card == null) return false;
            if (!texts.TryGetValue(card.Pointer, out var langs)) return false;
            return langs.TryGetValue(Loc.Language() ?? "en", out text) || langs.TryGetValue("en", out text);
        }

        /// <summary>Every card a match makes: in a custom-card mode, an official card with an update becomes its update.</summary>
        [HarmonyPatch(typeof(BattleCardManager), nameof(BattleCardManager.CreateCard))]
        private static class Swap
        {
            private static void Prefix(BattleCardManager __instance, ref RawCardScript rawCardData)
            {
                if (updates.Count == 0) return;
                try
                {
                    if ((object)rawCardData == null) return;
                    string id = rawCardData.uniqueId;
                    if (id == null || !updates.TryGetValue(id, out var update) || !Ui.Alive(update)) return;
                    var battle = (object)__instance == null ? null : __instance.battleManager;
                    string mode = ActiveMode((object)battle != null && Replays.IsReplay(battle));
                    if (mode == null) return;
                    if (rawCardData.Pointer == update.Pointer) return;
                    rawCardData = update;
                    if (noted != mode) { noted = mode; notedThisMatch.Clear(); }
                    if (notedThisMatch.Add(id)) RevivalMod.Log.Msg($"[updates] {id} is played as its update in {mode}");
                }
                catch (Exception e) { RevivalMod.Log.Warning("[updates] " + e.Message); }
            }
        }

        /// <summary>A new match: the "played as its update" lines are written again.</summary>
        internal static void MatchStarted() { notedThisMatch.Clear(); noted = null; }
    }
}
