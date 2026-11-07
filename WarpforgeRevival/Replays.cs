using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HarmonyLib;
using Il2Cpp;
using Il2CppEverguild.Utils;
using Il2CppPlayFab.ClientModels;

namespace WarpforgeRevival
{
    /// <summary>
    /// Match replays.
    ///
    /// The game sends each match's recording to the server when the match ends, and asks for it
    /// back when Replay is pressed. The server answers {"recordingData": recording}, but the
    /// name the game itself looks the recording up under is not readable from its files, so the
    /// mod reads the server's answer here and hands the game the recording. When the answer has
    /// no recording the game's own handling runs (and shows "Error loading match replay").
    /// The replay's mulligan screen is moved on by itself (see Tick).
    ///
    /// Rules saved with each replay (0.12.43): as a match is dealt, the rules it is played under are
    /// noted (the mode, its gameplay values as set on this match, both starting hands, the warlord health
    /// multiplier, the turn timer) together with the mod's rules number and the card pack version, and
    /// sent with the recording ("revivalRules" next to the game's own fields). Watching it, the server
    /// hands them back with the recording and the replay is played under them (through ChallengeRules,
    /// which already sets one match's rules), so later changes to a mode do not break old replays,
    /// and custom-challenge matches play back as they were. A replay from another mod rules number or
    /// card pack is refused: the rules cannot cover what the cards themselves do.
    /// </summary>
    internal static class Replays
    {
        // A replay opens on the mulligan screen with the recorded swaps already marked and waits
        // for Done, as if the viewer were playing. Done is pressed for them after a short look.
        private const float MulliganShownSeconds = 2.5f;
        private static float doneAt;

        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.SetupReplayMulliganStart))]
        private static class MulliganShown
        {
            private static void Postfix() => doneAt = UnityEngine.Time.realtimeSinceStartup + MulliganShownSeconds;
        }

        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.ClickMulliganDone))]
        private static class MulliganDone
        {
            private static void Prefix() => doneAt = 0;          // pressed already (by the viewer or below)
        }

        /// <summary>Called every frame from the mod's update loop.</summary>
        internal static void Tick()
        {
            if (doneAt <= 0 || UnityEngine.Time.realtimeSinceStartup < doneAt) return;
            doneAt = 0;
            try
            {
                var mulligan = UnityEngine.Object.FindObjectOfType<MulliganManager>();
                if (mulligan != null) { mulligan.ClickMulliganDone(); RevivalMod.Log.Msg("[replay] mulligan shown, continuing"); }
            }
            catch (Exception e) { RevivalMod.Log.Warning("[replay] could not continue past the mulligan: " + e.Message); }
        }

        // ---------------------------------------------------------------- rules saved with each replay

        /// <summary>Accepted ranges of the saved rules (wide enough for every mode's own values; the server checks the same).</summary>
        private static readonly Dictionary<string, (int min, int max)> Ranges = new Dictionary<string, (int, int)>
        {
            ["warlordHealthMultiplier"] = (10, 30), ["warlordLifeChange"] = (-100, 100), ["startingHand"] = (0, 20),
            ["secondPlayerExtraCards"] = (0, 10), ["drawCardsPerTurn"] = (0, 10), ["handLimit"] = (1, 30),
            ["startingMana"] = (0, 20), ["startingManaSecond"] = (0, 20), ["manaPerTurn"] = (0, 10),
            ["overtimeTurn"] = (0, 200), ["turnSeconds"] = (0, 600),
        };
        /// <summary>
        /// Rules saved by newer mods (older replays lack them; any of them may be missing): from 0.13.24 the Offence
        /// card pick (0/1) and whether a challenge forced who went first (0/1); from 0.13.33 the unspent mana
        /// carried over to the next turn.
        /// </summary>
        private static readonly Dictionary<string, (int min, int max)> Optional = new Dictionary<string, (int, int)>
        {
            ["offenseCards"] = (0, 1), ["forcedFirst"] = (0, 1), ["manaAccumulation"] = (0, 10),
        };
        private static readonly Regex EventPattern = new Regex(@"^[A-Za-z0-9_.-]{1,64}$");
        private static readonly Regex CardsPattern = new Regex(@"^[A-Za-z0-9._-]{1,80}$");
        private static readonly Regex CardIdPattern = new Regex(@"^[A-Za-z0-9._-]{1,64}$");

        private static string noted;          // this match's rules, waiting for the recording to be sent
        /// <summary>The current (or last) match's rules as sent with its recording, or null (for bug reports).</summary>
        internal static string MatchRules => noted;
        private static float notedAt = -1f;

        /// <summary>As the hands are dealt (everything is set by then): the rules of this match, unless it is a replay.</summary>
        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.SetupMulliganPhase))]
        private static class Dealt
        {
            private static void Postfix(BattleManager __instance)
            {
                try
                {
                    if (IsReplay(__instance)) return;
                    noted = null;
                    var md = __instance.matchData;
                    var gv = (object)md == null ? null : md.GameplayData;
                    var sv = __instance.scenarioVariables;
                    if ((object)gv == null || (object)sv == null) { RevivalMod.Log.Msg("[replay] this match's rules could not be read; its replay will use the current rules"); return; }
                    double factor = ChallengeRules.HealthMultiplier is double m ? m : LongGame.InLongMatch() && ServerSettings.LongGameHealth > 0 ? ServerSettings.LongGameHealth : 1.0;
                    int tenths = (int)Math.Round(factor * 10);
                    var v = new Dictionary<string, int>
                    {
                        ["warlordHealthMultiplier"] = tenths, ["warlordLifeChange"] = gv.warlordLifeChange,
                        ["startingHand"] = sv.startingHand, ["secondPlayerExtraCards"] = sv.secondExtraCards,
                        ["drawCardsPerTurn"] = gv.drawCardsPerTurn, ["handLimit"] = gv.handLimit,
                        ["startingMana"] = gv.startingMana, ["startingManaSecond"] = gv.startingManaSecond, ["manaPerTurn"] = gv.manaPerTurn,
                        ["overtimeTurn"] = gv.overtimeTurn,
                        ["turnSeconds"] = ChallengeRules.TurnSeconds ?? ServerSettings.TurnSeconds ?? RevivalMod.Config.TurnSeconds,
                        ["offenseCards"] = BattlefieldCards.PickOn ? 1 : 0,
                        ["forcedFirst"] = MatchOptions.ForcedFirst ? 1 : 0,
                        ["manaAccumulation"] = gv.manaAccumulation,
                    };
                    string ev = ChallengeRules.ActiveEvent ?? EventOf(md) ?? LongGame.PlayingEventId() ?? "";
                    string cards = RevivalMod.Cards?.CardsVersion ?? RevivalMod.Cards?.PackVersion ?? "";
                    string bad = Math.Abs(factor * 10 - tenths) > 0.001 ? "warlordHealthMultiplier" : null;
                    foreach (var kv in v)
                    {
                        var range = Ranges.TryGetValue(kv.Key, out var rr) ? rr : Optional[kv.Key];
                        if (bad == null && (kv.Value < range.min || kv.Value > range.max)) bad = kv.Key;
                    }
                    if (bad == null && !EventPattern.IsMatch(ev)) bad = "mode";
                    if (bad == null && !CardsPattern.IsMatch(cards)) bad = "card pack";
                    if (bad != null) { RevivalMod.Log.Msg($"[replay] this match's rules are not saved with its replay ({bad} out of range)"); return; }
                    var rules = new JsonObject();
                    foreach (var kv in v) rules[kv.Key] = kv.Value;
                    noted = new JsonObject { ["v"] = 1, ["event"] = ev, ["mod"] = RevivalMod.RulesVersion, ["cards"] = cards, ["rules"] = rules }.ToJsonString();
                    notedAt = UnityEngine.Time.realtimeSinceStartup;
                    RevivalMod.Log.Msg("[replay] rules of this match noted for its replay: " + noted);
                }
                catch (Exception e) { RevivalMod.Log.Warning("[replay] noting this match's rules: " + e.Message); }
            }
        }

        internal static string EventOf(MatchData md)
        {
            try
            {
                var e = md.CurrentEvent;
                var data = (object)e == null ? null : e.GetBaseData();
                return (object)data == null || string.IsNullOrEmpty(data.eventId) ? null : data.eventId;
            }
            catch { return null; }
        }

        /// <summary>A replay's cards match this game's: the server's card bundle version, or (replays from
        /// before 0.13.20) the old card pack version still in the cached copy.</summary>
        private static bool SameCards(string recorded)
        {
            var c = RevivalMod.Cards;
            if ((object)c == null || string.IsNullOrEmpty(recorded)) return false;
            return recorded == c.CardsVersion || recorded == c.PackVersion;
        }

        // ---- in-match card numbers -> card ids, taken every few seconds while a match runs (0.13.39)
        private static readonly Dictionary<int, (string id, bool own)> seenCards = new Dictionary<int, (string, bool)>();
        private static float nextCardLook;
        private static bool cardsFromReplay;

        /// <summary>From the update loop: notes the match's cards (each card's in-match number, card id and side).</summary>
        private static BattleCardManager cardManager;
        private static bool Live(UnityEngine.Object o) => (object)o != null && o.Pointer != IntPtr.Zero && o.m_CachedPtr != IntPtr.Zero;

        internal static void NoteCards()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < nextCardLook) return;
            nextCardLook = now + 3f;
            try
            {
                var battle = BattleManager.Instance;
                if ((object)battle == null || battle.Pointer == IntPtr.Zero || battle.m_CachedPtr == IntPtr.Zero) return;
                if (IsReplay(battle)) { cardsFromReplay = true; return; }
                if (cardsFromReplay) { seenCards.Clear(); cardsFromReplay = false; }
                if (!Live(cardManager))
                {
                    cardManager = UnityEngine.Object.FindObjectOfType<BattleCardManager>();   // once a match (M-11)
                    // A new card manager is a new match. Card numbers start again from 0 each match, so the old
                    // test (is card 0 unseen?) never fired and a match whose recording was not saved left its
                    // table behind for the next one (0.15.20).
                    if (Live(cardManager)) seenCards.Clear();
                }
                var manager = cardManager;
                var all = (object)manager == null ? null : manager.GetAllCardsInGame();
                if (all == null) return;
                for (int i = 0; i < all.Count && seenCards.Count < 600; i++)
                {
                    var c = all[i];
                    if ((object)c == null) continue;
                    if (seenCards.ContainsKey(c.uniqueInGameID)) continue;       // M-11: each card read once a match
                    string id = c.rawCardData?.uniqueId;
                    if (string.IsNullOrEmpty(id) || id.Length > 64) continue;
                    seenCards[c.uniqueInGameID] = (id, c.isPlayer);
                }
            }
            catch { }
        }

        private static JsonObject CardList()
        {
            if (seenCards.Count == 0 || cardsFromReplay) return null;
            var list = new JsonArray();
            foreach (var kv in seenCards)
            {
                if (kv.Key < 0 || kv.Key > 100000 || !CardIdPattern.IsMatch(kv.Value.id)) continue;
                list.Add(new JsonArray(kv.Key, kv.Value.id, kv.Value.own ? 1 : 0));
            }
            seenCards.Clear();
            return new JsonObject { ["v"] = 1, ["cards"] = list };
        }

        internal static bool IsReplay(BattleManager battle)
        {
            try { return battle.IsReplayMatch(); } catch { return false; }
        }

        private static readonly byte[] SaveMark = System.Text.Encoding.UTF8.GetBytes("saveMatchRecording");

        /// <summary>
        /// Called with every cloud script call the game sends: the recording of the match just played
        /// gets the rules noted while it was dealt. Used once, and only within 3 hours of the deal.
        /// </summary>
        internal static byte[] WithRules(byte[] payload)
        {
            if (noted == null || payload.Length == 0 || payload.Length > 1 << 20) return payload;
            if (!PlayFabTransport.Has(payload, SaveMark)) return payload;      // M-10: not a recording, not parsed
            try
            {
                var root = JsonNode.Parse(payload) as JsonObject;
                if (root?["FunctionName"]?.GetValue<string>() != "saveMatchRecording") return payload;
                string rules = noted;
                bool fresh = UnityEngine.Time.realtimeSinceStartup - notedAt < 3 * 3600f;
                noted = null;
                var data = (root["FunctionParameter"] as JsonObject)?["Data"] as JsonObject;
                if (data == null || !fresh) { RevivalMod.Log.Msg("[replay] recording sent without its rules"); return payload; }
                data["revivalRules"] = JsonNode.Parse(rules);
                RevivalMod.Log.Msg("[replay] rules sent with the recording");
                // which card each in-match number was, so the admins' review of a reported match can name cards (0.13.39)
                var cards = CardList();
                if (cards != null) data["revivalCards"] = cards;
                return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
            }
            catch (Exception e) { RevivalMod.Log.Warning("[replay] adding the rules to the recording: " + e.Message); return payload; }
        }

        /// <summary>
        /// The rules sent back with a recording, in exactly the shape written above:
        /// {"v": 1, "event", "mod", "cards", "rules": {every rule, nothing else, whole numbers in range}}.
        /// Anything else is refused as a whole (null).
        /// </summary>
        private static (ChallengeRules.RuleSet rules, int mod, string cards)? ReadRules(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            var seen = new HashSet<string>();
            foreach (var p in e.EnumerateObject()) if (!seen.Add(p.Name)) return null;
            if (!seen.SetEquals(new[] { "v", "event", "mod", "cards", "rules" })) return null;
            bool Whole(JsonElement x, out int n) { n = 0; return x.ValueKind == JsonValueKind.Number && x.GetRawText().IndexOfAny(new[] { '.', 'e', 'E' }) < 0 && x.TryGetInt32(out n); }
            if (!Whole(e.GetProperty("v"), out int ver) || ver != 1) return null;
            var ev = e.GetProperty("event");
            if (ev.ValueKind != JsonValueKind.String || !EventPattern.IsMatch(ev.GetString() ?? "")) return null;
            if (!Whole(e.GetProperty("mod"), out int mod) || mod < 0 || mod > 10000) return null;
            var cards = e.GetProperty("cards");
            if (cards.ValueKind != JsonValueKind.String || !CardsPattern.IsMatch(cards.GetString() ?? "")) return null;
            var vals = e.GetProperty("rules");
            if (vals.ValueKind != JsonValueKind.Object) return null;
            var r = new ChallengeRules.RuleSet { EventId = ev.GetString() };
            foreach (var p in vals.EnumerateObject())
            {
                if ((!Ranges.TryGetValue(p.Name, out var range) && !Optional.TryGetValue(p.Name, out range)) || r.Values.ContainsKey(p.Name)) return null;
                if (!Whole(p.Value, out int n) || n < range.min || n > range.max) return null;
                r.Values[p.Name] = n;
            }
            // every rule of the first set; the newer ones may be missing (older replays)
            foreach (var k in Ranges.Keys) if (!r.Values.ContainsKey(k)) return null;
            // replays from before 0.13.33 were all played without unspent mana carried over (every mode had 0)
            if (!r.Values.ContainsKey("manaAccumulation")) r.Values["manaAccumulation"] = 0;
            return (r, mod, cards.GetString());
        }

        private static string refusal;
        private static float refusalAt = -100f;

        /// <summary>Why the last replay was refused, for the game's "Error loading match replay" popup (for 20 seconds).</summary>
        internal static string Refusal => UnityEngine.Time.realtimeSinceStartup - refusalAt < 20f ? refusal : null;

        [HarmonyPatch(typeof(PlayerDataManager), nameof(PlayerDataManager.ProcessMatchRecording))]
        private static class Load
        {
            private static bool Prefix(ExecuteCloudScriptResult result, ref BattleRecordData __result)
            {
                try
                {
                    if (result == null || result.FunctionResult == null) return true;
                    string json = JsonWrapper.SerializeObject(result.FunctionResult);
                    if (string.IsNullOrEmpty(json)) return true;
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                        !doc.RootElement.TryGetProperty("recordingData", out var rec) || rec.ValueKind != JsonValueKind.Object)
                        return true;
                    ChallengeRules.RuleSet rules = null;
                    if (doc.RootElement.TryGetProperty("revivalRules", out var rr))
                    {
                        var got = ReadRules(rr);
                        string why = got == null ? "its saved rules could not be read"
                                   : got.Value.mod != RevivalMod.RulesVersion ? "it was recorded with a different version of the mod's rules"
                                   : !SameCards(got.Value.cards) ? "it was recorded with a different set of cards"
                                   : null;
                        if (why != null)
                        {
                            // the player sees it on the game's popup, in their language (the log keeps the English reason)
                            refusal = got == null
                                ? Loc.T("replays.refusedUnreadable", "This replay cannot be played: its saved rules could not be read, so it would not play out as the real match did.")
                                : got.Value.mod != RevivalMod.RulesVersion
                                ? Loc.T("replays.refusedRules", "This replay cannot be played: it was recorded with a different version of the mod's rules, so it would not play out as the real match did.")
                                : Loc.T("replays.refusedCards", "This replay cannot be played: it was recorded with a different set of cards, so it would not play out as the real match did.");
                            refusalAt = UnityEngine.Time.realtimeSinceStartup;
                            RevivalMod.Log.Warning("[replay] replay refused: " + why);
                            return true;           // the game's own handling: "Error loading match replay" (with the reason, see PopupNotes)
                        }
                        rules = got.Value.rules;
                    }
                    var record = JsonWrapper.DeserializeObject<BattleRecordData>(rec.GetRawText());
                    if (record == null) return true;
                    if (rules != null)
                    {
                        ChallengeRules.ForReplay(rules);
                        RevivalMod.Log.Msg($"[replay] played under its saved rules ({rules.EventId}): {rules.ToJson()["v"]?.ToJsonString()}");
                    }
                    else
                    {
                        ChallengeRules.NoReplayRules();
                        RevivalMod.Log.Msg("[replay] no rules saved with it; played under the current rules");
                    }
                    __result = record;
                    RevivalMod.Log.Msg("[replay] recording loaded");
                    return false;
                }
                catch (Exception e)
                {
                    RevivalMod.Log.Warning("[replay] could not read the recording: " + e.Message);
                    return true;
                }
            }
        }
    }
}
