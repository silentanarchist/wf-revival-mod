using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarmonyLib;
using Il2Cpp;
using Il2CppEverguild.LiveOps;
using UnityEngine;

namespace WarpforgeRevival
{
    /// <summary>
    /// Rules a friend challenge is played with (Social > Challenge). The challenger picks a game mode
    /// as the preset (its deck rules, and the starting point for the match rules) and may change the
    /// match rules. They travel with the game's own challenge message ("revivalRules" added to its
    /// JSON) and, once the match starts, both games play by them:
    ///  - the mode's gameplay variables (warlord health, mana, cards drawn, hand limit, overtime) are
    ///    set on the mode for the length of the match and put back afterwards;
    ///  - the starting hand, the second player's extra cards and the turn timer are read from here
    ///    by LongGame (starting hand) and TurnClock;
    ///  - the Offence card pick and who goes first (0.13.24) are applied by MatchOptions.
    /// Since 0.15.26 every challenge carries them: one sent from the game's own buttons gets its mode's preset
    /// (EveryChallengeHasRules). A challenge without "revivalRules" (another mode) is an ordinary one.
    /// </summary>
    internal static class ChallengeRules
    {
        internal sealed class Field
        {
            public string Key;
            public int Min, Max, Step;
            private readonly Func<string> label;
            public Field(string key, Func<string> label, int min, int max, int step = 1) { Key = key; this.label = label; Min = min; Max = max; Step = step; }
            /// <summary>The rule's name in the player's language.</summary>
            public string Text() => label();
        }

        /// <summary>The match rules the menu offers, in its order.</summary>
        internal static readonly Field[] Fields =
        {
            // in tenths: 10 = x1.0 .. 30 = x3.0, in steps of 0.2 (every warlord's health is a multiple of
            // 5, so the result is always a whole number)
            new Field("warlordHealthMultiplier", () => Loc.T("challenge.rule.warlordHealthMultiplier", "Warlord health multiplier"), 10, 30, 2),
            new Field("warlordLifeChange", () => Loc.T("challenge.rule.warlordLifeChange", "Warlord health change (added after the multiplier)"), -20, 40),
            new Field("startingHand", () => Loc.T("challenge.rule.startingHand", "Starting hand (at most the hand limit)"), 1, 10),
            new Field("secondPlayerExtraCards", () => Loc.T("challenge.rule.secondPlayerExtraCards", "Extra cards for the second player"), 0, 3),
            new Field("drawCardsPerTurn", () => Loc.T("challenge.rule.drawCardsPerTurn", "Cards drawn per turn"), 0, 5),
            new Field("handLimit", () => Loc.T("challenge.rule.handLimit", "Hand limit"), 3, 20),
            new Field("startingMana", () => Loc.T("challenge.rule.startingMana", "Starting Energy (first player)"), 0, 10),
            new Field("startingManaSecond", () => Loc.T("challenge.rule.startingManaSecond", "Starting Energy (second player)"), 0, 10),
            new Field("manaPerTurn", () => Loc.T("challenge.rule.manaPerTurn", "Energy gained per turn"), 0, 5),
            // 0.13.33: unspent mana carried over to the next turn, at most this much (Skirmish: 1)
            new Field("manaAccumulation", () => Loc.T("challenge.rule.manaAccumulation", "Unspent Energy carried over (at most)"), 0, 5),
            new Field("overtimeTurn", () => Loc.T("challenge.rule.overtimeTurn", "Overtime starts on turn"), 5, 60),
            new Field("turnSeconds", () => Loc.T("challenge.rule.turnSeconds", "Turn timer (seconds)"), 30, 300, 5),
            new Field("offenseCards", () => Loc.T("challenge.rule.offenseCards", "Offence cards"), 0, 1),
            // 0 the game decides (warlord initiative, then a coin toss), 1 the challenger, 2 the friend challenged
            new Field("firstPlayer", () => Loc.T("challenge.rule.firstPlayer", "Goes first"), 0, 2),
            // 0.14.34: the server-deal test (ServerDeal.cs). Shown to testers only; the server accepts 1 from testers only.
            new Field("serverDeal", () => Loc.T("challenge.rule.serverDeal", "Server deal (test)"), 0, 1),
        };

        /// <summary>Rules only testers see on the Challenge page (every rule set still carries them, at their lowest value).</summary>
        internal static bool TestersOnly(string key) => key == "serverDeal";

        /// <summary>A rule's value as the menu shows it ("x1.4", "+5", "4").</summary>
        internal static string Show(string key, int v) =>
            key == "warlordHealthMultiplier" ? "x" + (v / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            : key == "warlordLifeChange" && v > 0 ? "+" + v
            : key == "offenseCards" || key == "serverDeal" ? (v == 1 ? Loc.Game("MainMenu/Settings/ButtonLabel/Music_On", "On") : Loc.Game("MainMenu/Settings/ButtonLabel/Music_Off", "Off"))
            : key == "firstPlayer" ? (v == 1 ? Loc.T("challenge.firstYou", "You") : v == 2 ? Loc.T("challenge.firstOpponent", "Opponent") : Loc.Game("MenuDeck/Button/Random", "Random"))      // as the challenger sees it
            : v.ToString();

        /// <summary>
        /// Keeps the starting hand within the hand limit: the starting hand at most the limit, and the
        /// second player's starting hand (starting hand + extra cards) too.
        /// </summary>
        internal static void CapHand(Dictionary<string, int> v)
        {
            if (!v.TryGetValue("handLimit", out int limit)) return;
            if (v.TryGetValue("startingHand", out int hand) && hand > limit) v["startingHand"] = hand = Math.Max(1, limit);
            if (v.TryGetValue("secondPlayerExtraCards", out int extra) && v.TryGetValue("startingHand", out hand) && hand + extra > limit)
                v["secondPlayerExtraCards"] = Math.Max(0, limit - hand);
        }

        internal static bool HandFits(Dictionary<string, int> v) =>
            v["startingHand"] <= v["handLimit"] && v["startingHand"] + v["secondPlayerExtraCards"] <= v["handLimit"];

        internal sealed class RuleSet
        {
            public string EventId = "";
            public Dictionary<string, int> Values = new Dictionary<string, int>();

            public int? Get(string key) => Values.TryGetValue(key, out int v) ? v : (int?)null;

            public JsonObject ToJson()
            {
                var v = new JsonObject();
                foreach (var kv in Values) v[kv.Key] = kv.Value;
                return new JsonObject { ["event"] = EventId, ["v"] = v };
            }

            /// <summary>
            /// Read from a challenge message. Only the exact shape the menu sends is accepted:
            /// {"event": one of the offered modes, "v": {every rule of Fields, nothing else, each a whole
            /// number within its range and on its step}}. Anything else is refused as a whole (null),
            /// never repaired, so a hand-made message cannot slip other values into a match.
            /// </summary>
            public static RuleSet From(JsonElement e)
            {
                if (e.ValueKind != JsonValueKind.Object) return null;
                int props = 0;
                foreach (var _ in e.EnumerateObject()) props++;
                if (props != 2 || !e.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.String) return null;
                if (!e.TryGetProperty("v", out var vals) || vals.ValueKind != JsonValueKind.Object) return null;
                var r = new RuleSet { EventId = ev.GetString() ?? "" };
                if (!AllowedModes().Contains(r.EventId)) return null;
                foreach (var p in vals.EnumerateObject())
                {
                    var f = Array.Find(Fields, x => x.Key == p.Name);
                    if (f == null || r.Values.ContainsKey(f.Key)) return null;                 // unknown or repeated rule
                    if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out int n)) return null;
                    if (p.Value.GetRawText().IndexOfAny(new[] { '.', 'e', 'E' }) >= 0) return null; // whole numbers only
                    if (n < f.Min || n > f.Max || (n - f.Min) % f.Step != 0) return null;
                    r.Values[f.Key] = n;
                }
                if (r.Values.Count != Fields.Length) return null;                              // every rule present
                return HandFits(r.Values) ? r : null;                                          // starting hands within the hand limit
            }

            /// <summary>The rules in a few short lines, for the challenge popup.</summary>
            public string Describe(string modeName)
            {
                int G(string k) => Get(k) ?? 0;
                int change = G("warlordLifeChange");
                return Loc.T("challenge.sum.mode", "Mode: {mode}", ("mode", modeName)) + "\n" +
                       Loc.T("challenge.sum.health", "Warlord health: {multiplier}, then {change}",
                             ("multiplier", Show("warlordHealthMultiplier", G("warlordHealthMultiplier"))), ("change", (change >= 0 ? "+" : "") + change)) + "\n" +
                       Loc.T("challenge.sum.hand", "Starting hand: {hand} (+{extra} for the second player) · hand limit {limit}",
                             ("hand", G("startingHand")), ("extra", G("secondPlayerExtraCards")), ("limit", G("handLimit"))) + "\n" +
                       Loc.T("challenge.sum.energy", "Energy: starts {first} / {second} (first / second player), +{perTurn} per turn, up to {carried} unspent carried over",
                             ("first", G("startingMana")), ("second", G("startingManaSecond")), ("perTurn", G("manaPerTurn")), ("carried", G("manaAccumulation"))) + "\n" +
                       Loc.T("challenge.sum.turns", "Cards drawn per turn: {draw} · overtime from turn {overtime} · turn timer {seconds} s",
                             ("draw", G("drawCardsPerTurn")), ("overtime", G("overtimeTurn")), ("seconds", G("turnSeconds"))) + "\n" +
                       Loc.T("challenge.sum.offence", "Offence cards: {offence}", ("offence", Show("offenseCards", G("offenseCards")))) + " · " +
                       (G("firstPlayer") == 1 ? Loc.T("challenge.sum.firstChallenger", "goes first: your challenger")
                        : G("firstPlayer") == 2 ? Loc.T("challenge.sum.firstYou", "goes first: you")
                        : Loc.T("challenge.sum.firstRandom", "goes first: random")) +
                       (G("serverDeal") == 1 ? "\n" + Loc.T("challenge.sum.serverDeal", "Server deal (test): the server shuffles and deals") : "");
            }
        }

        // ---------------------------------------------------------------- the mode's own values

        /// <summary>The preset values of a mode: its gameplay variables, its starting hand and the turn timer.</summary>
        internal static RuleSet Preset(IPlayEvent ev, string eventId)
        {
            var r = new RuleSet { EventId = eventId };
            var gv = Variables(ev);
            if ((object)gv != null)
            {
                r.Values["warlordLifeChange"] = gv.warlordLifeChange;
                r.Values["startingMana"] = gv.startingMana;
                r.Values["startingManaSecond"] = gv.startingManaSecond;
                r.Values["manaPerTurn"] = gv.manaPerTurn;
                r.Values["drawCardsPerTurn"] = gv.drawCardsPerTurn;
                r.Values["manaAccumulation"] = gv.manaAccumulation;
                r.Values["handLimit"] = gv.handLimit;
                r.Values["overtimeTurn"] = gv.overtimeTurn;
            }
            // a mode that does not set its own starting hand (Classic) uses the game's: 3 cards, +1 for the
            // second player (read from the game once a match has been played)
            var (hand, extra) = ServerSettings.ModeHand(eventId);
            var (gameHand, gameExtra) = LongGame.GameDefaultHand;
            r.Values["startingHand"] = hand > 0 ? hand : gameHand > 0 ? gameHand : 3;
            r.Values["secondPlayerExtraCards"] = extra >= 0 ? extra : gameExtra >= 0 ? gameExtra : 1;
            double factor = eventId == ServerSettings.LongGameEvent && ServerSettings.LongGameHealth > 0 ? ServerSettings.LongGameHealth : 1.0;
            r.Values["warlordHealthMultiplier"] = (int)Math.Round(factor * 10);
            int secs = ServerSettings.TurnSeconds ?? RevivalMod.Config.TurnSeconds;
            r.Values["turnSeconds"] = secs >= 30 && secs <= 300 ? secs : 60;
            r.Values["offenseCards"] = ServerSettings.OffenceFor(eventId) ? 1 : 0;
            r.Values["firstPlayer"] = 0;
            r.Values["serverDeal"] = 0;
            foreach (var f in Fields)
            {
                int v = r.Values.TryGetValue(f.Key, out int got) ? got : f.Min;
                v = Math.Clamp(v, f.Min, f.Max);
                v = f.Min + (int)Math.Round((v - f.Min) / (double)f.Step) * f.Step;   // on the field's step, as the friend's game checks
                r.Values[f.Key] = Math.Min(v, f.Max);
            }
            CapHand(r.Values);
            return r;
        }

        internal static GameplayVariablesData Variables(IPlayEvent ev)
        {
            try
            {
                var data = (object)ev == null ? null : ev.GetBaseData();
                var play = (object)data == null ? null : data.TryCast<PlayEventData>();
                return (object)play == null ? null : play.gameplayVariables;
            }
            catch { return null; }
        }

        /// <summary>Deck rules of a mode, every rarity spelled out ("60 cards · 4 copies of a Common, 3 of a Rare, ...").</summary>
        internal static string DeckRules(IPlayEvent ev, string eventId)
        {
            var gv = Variables(ev);
            if ((object)gv == null) return "";
            bool isLong = eventId == ServerSettings.LongGameEvent;
            // the rarity names are the game's own; the sentence around them is ours (one text, so translators keep its order)
            int Copies(int rarity) => isLong && ServerSettings.LongGameCopies(rarity) > 0 ? ServerSettings.LongGameCopies(rarity)
                                      : rarity == 4 ? gv.numberOfCopiesLegendary : gv.numberOfCopiesOtherRarities;
            return Loc.T("challenge.deckRules", "Decks: {size} cards · at most {common} of a {commonName}, {rare} of a {rareName}, {epic} of an {epicName}, {legendary} of a {legendaryName}.",
                         ("size", gv.deckSize),
                         ("common", Copies(1)), ("commonName", Loc.Game("Card_Rarity/Common", "Common")),
                         ("rare", Copies(2)), ("rareName", Loc.Game("Card_Rarity/Rare", "Rare")),
                         ("epic", Copies(3)), ("epicName", Loc.Game("Card_Rarity/Epic", "Epic")),
                         ("legendary", Copies(4)), ("legendaryName", Loc.Game("Card_Rarity/Legendary", "Legendary")));
        }

        /// <summary>The modes a challenge may use (the menu's presets): Classic, Skirmish, and the long-game mode.</summary>
        internal static HashSet<string> AllowedModes()
        {
            var ids = new HashSet<string> { "RevivalClassic", "RevivalSkirmish" };
            if (!string.IsNullOrEmpty(ServerSettings.LongGameEvent)) ids.Add(ServerSettings.LongGameEvent);
            return ids;
        }

        internal static IPlayEvent FindEvent(string id)
        {
            try
            {
                var found = LiveOpsManager.GetEvent(id);
                return (object)found == null ? null : found.TryCast<IPlayEvent>();
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- sending and receiving

        private static RuleSet outgoing;
        private static string outgoingTo;
        private static float outgoingAt;
        private static readonly Dictionary<string, (RuleSet rules, float at)> incoming = new Dictionary<string, (RuleSet, float)>();

        /// <summary>The menu's challenge: remembered so it goes with the game's challenge message to this friend.</summary>
        internal static void Outgoing(string to, RuleSet rules)
        {
            outgoing = rules;
            outgoingTo = to;
            outgoingAt = Time.realtimeSinceStartup;
            Apply(rules, true);     // the challenger's own match uses them too
        }

        /// <summary>
        /// Every challenge carries a rule set (0.15.26). A challenge sent from the game's own buttons (the friend list's
        /// Challenge and its mode popup) used to carry none, and each game then filled the gaps from its own state - its
        /// player's selected mode, the practice menu's last choice - so the two games could play by different rules
        /// (Offence pick on one side only, different warlord health) and the match desynced. Such a challenge now sends
        /// the chosen mode's preset, exactly as the Challenge page does when nothing is changed. The page's own
        /// challenge (rules just noted for this friend) is left alone.
        /// </summary>
        [HarmonyPatch(typeof(ChallengeManager), nameof(ChallengeManager.StartChallengeWithUser))]
        private static class EveryChallengeHasRules
        {
            private static void Prefix(string __1, IPlayEvent __3)
            {
                try
                {
                    string friend = __1;
                    if (string.IsNullOrEmpty(friend)) return;
                    if (outgoing != null && string.Equals(friend, outgoingTo, StringComparison.OrdinalIgnoreCase) &&
                        Time.realtimeSinceStartup - outgoingAt <= 15f) return;          // the Challenge page's own
                    var data = (object)__3 == null ? null : __3.GetBaseData();
                    string mode = (object)data == null ? null : data.eventId;
                    if (string.IsNullOrEmpty(mode) || !AllowedModes().Contains(mode))
                    {
                        RevivalMod.Log.Msg($"[challenge] challenge to {friend} in '{mode ?? "unknown"}': not a challenge mode, sent without rules");
                        return;
                    }
                    var rules = Preset(__3, mode);
                    RevivalMod.Log.Msg($"[challenge] challenge to {friend} from the game's own menu: sending {mode}'s rules");
                    Outgoing(friend, rules);
                }
                catch (Exception e) { RevivalMod.Log.Warning("[challenge] rules for a challenge: " + e.Message); }
            }
        }

        /// <summary>
        /// Called for every private message the game sends: the challenge message ("Practice") to the
        /// friend the menu challenged gets the rules added. Others are passed on unchanged.
        /// </summary>
        internal static string AddTo(string target, string json, string kind)
        {
            try
            {
                if (kind == "RemovePractice") incoming.Remove(target);    // this player turned that friend's challenge down
                if (kind == "RemovePractice" && outgoing != null && string.Equals(target, outgoingTo, StringComparison.OrdinalIgnoreCase))
                {
                    outgoing = null;                                   // the challenger called it off
                    if (!battleSeen) Restore();
                    RevivalMod.Log.Msg($"[challenge] challenge to {target} called off");
                    return json;
                }
                if (kind != "Practice" || outgoing == null) return json;
                // Only the challenge the menu just sent carries the rules. Any other challenge (another friend,
                // or the same friend later from the friend list) is an ordinary one: the noted rules are dropped,
                // so this game never plays custom rules its opponent was not sent.
                if (!string.Equals(target, outgoingTo, StringComparison.OrdinalIgnoreCase) || Time.realtimeSinceStartup - outgoingAt > 15f)
                {
                    outgoing = null;
                    if (!battleSeen) Restore();
                    RevivalMod.Log.Msg($"[challenge] ordinary challenge to {target}; custom rules dropped");
                    return json;
                }
                var root = JsonNode.Parse(json) as JsonObject;
                if (root == null) return json;
                var rulesJson = outgoing.ToJson();
                RevivalMod.Log.Msg($"[challenge] custom rules sent to {target}: {rulesJson.ToJsonString()}");
                root["revivalRules"] = rulesJson;
                return root.ToJsonString();
            }
            catch (Exception e) { RevivalMod.Log.Warning("[challenge] " + e.Message); return json; }
        }

        /// <summary>Called for every private message received: a challenge with rules is remembered and shown.</summary>
        internal static void Read(string from, string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("messageType", out var t)) return;
                int type = t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out int n) ? n
                         : t.ValueKind == JsonValueKind.String ? (t.GetString() == "Practice" ? 1 : t.GetString() == "RemovePractice" ? 2 : -1) : -1;
                if (type == 2) { incoming.Remove(from); return; }       // the challenger called it off
                if (type != 1) return;
                incoming.Clear();      // only the latest challenge counts: an earlier one's rules never reach another friend's match
                refused.Remove(from);
                if (!root.TryGetProperty("revivalRules", out var rr)) { incoming.Remove(from); return; }
                if (json.Length > 4096) { Refuse(from, "too large"); return; }
                var rules = RuleSet.From(rr);
                if (rules == null) { Refuse(from, "not in the expected form"); return; }
                incoming[from] = (rules, Time.realtimeSinceStartup);
                string who = from;
                try { if (root.TryGetProperty("parameter1", out var p1) && p1.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(p1.GetString())) who = p1.GetString(); } catch { }
                var ev = FindEvent(rules.EventId);
                string mode = (object)ev == null ? rules.EventId : TestersPage.Label(ev.TryCast<LiveOpsEvent>(), EventLabelReferenceType.Title, rules.EventId);
                RevivalMod.Log.Msg($"[challenge] {from} challenges with custom rules: {rr.GetRawText()}");
                // shown inside the game's own "... has challenged you" popup, which comes right after (see Decorate);
                // a popup of our own here would take that one's place and its Accept button with it
                lastRules = rules;
                lastRulesMode = mode;
                lastRulesAt = Time.realtimeSinceStartup;
            }
            catch (Exception e) { RevivalMod.Log.Warning("[challenge] " + e.Message); }
        }

        // A challenge whose rules were not in the exact expected form: accepting it is blocked, so the two
        // games can never play by different rules.
        private static readonly HashSet<string> refused = new HashSet<string>();

        private static void Refuse(string from, string why)
        {
            incoming.Remove(from);
            refused.Add(from);
            RevivalMod.Log.Warning($"[challenge] challenge from {from} refused: its rules are {why}");
            try
            {
                WindowsManager.Instance?.ShowPopUp(Loc.T("challenge.unreadable", "A challenge arrived with rules this game cannot read, so it cannot be accepted. Both players should update the mod and try again."),
                                                   false, true, Loc.Game("MainMenu/General/OK", "OK"), (Il2CppSystem.Action)null);
            }
            catch { }
        }

        private static float biggerPopup = -1f;
        private static RuleSet lastRules;
        private static string lastRulesMode;
        private static float lastRulesAt = -100f;
        private static string rulesHeading = "Custom rules:";     // the heading Decorate put on the popup, in the player's language

        /// <summary>
        /// Called for every popup the game is about to show: its "... has challenged you in Classic mode!
        /// Play friendly match?" popup gets the rules of the challenge that just arrived, between the two
        /// lines, so the player sees them on the popup they accept with.
        /// </summary>
        internal static string Decorate(string text)
        {
            try
            {
                if (lastRules == null || string.IsNullOrEmpty(text) || Time.realtimeSinceStartup - lastRulesAt > 20f) return text;
                if (text.IndexOf("challenged you", StringComparison.OrdinalIgnoreCase) < 0) return text;
                string head = text, tail = "";
                int q = text.LastIndexOf('\n');
                if (q > 0) { head = text.Substring(0, q).TrimEnd(); tail = text.Substring(q + 1).Trim(); }
                string rules = string.Join("\n", lastRules.Describe(lastRulesMode).Split('\n').Skip(1));    // the mode is already in the game's line
                lastRules = null;
                biggerPopup = Time.realtimeSinceStartup;
                RevivalMod.Log.Msg("[challenge] rules added to the game's challenge popup");
                rulesHeading = Loc.T("challenge.customRules", "Custom rules:");      // EnlargePopup finds the popup's text by it
                return head + "\n" + rulesHeading + "\n" + rules + (tail.Length > 0 ? "\n\n" + tail : "");
            }
            catch (Exception e) { RevivalMod.Log.Warning("[challenge] popup: " + e.Message); return text; }
        }

        /// <summary>
        /// The game's popup sizes its text to fit, so the rules came out small: once it is on screen, its
        /// text is allowed to be larger (and the box taller where the popup lets it grow).
        /// </summary>
        private static float nextPopupLook;

        private static void EnlargePopup()
        {
            if (biggerPopup < 0) return;
            if (Time.realtimeSinceStartup - biggerPopup > 3f) { biggerPopup = -1f; return; }
            if (Time.realtimeSinceStartup < nextPopupLook) return;             // M-11: 10 times a second, not every frame
            nextPopupLook = Time.realtimeSinceStartup + 0.1f;
            try
            {
                var popup = WindowsManager.Instance?.PopupWindow;
                if ((object)popup == null || !popup.isActiveAndEnabled) return;
                foreach (var t in popup.GetComponentsInChildren<Il2CppTMPro.TMP_Text>(true))
                {
                    if (t.text == null || t.text.IndexOf(rulesHeading, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    t.enableAutoSizing = true;
                    t.fontSizeMax = Math.Max(t.fontSizeMax, 38f);
                    t.fontSizeMin = Math.Max(t.fontSizeMin, 22f);
                    t.fontSize = 34f;
                    biggerPopup = -1f;
                    RevivalMod.Log.Msg("[challenge] rules popup text enlarged");
                }
            }
            catch (Exception e) { biggerPopup = -1f; RevivalMod.Log.Warning("[challenge] popup: " + e.Message); }
        }

        // ---------------------------------------------------------------- the match

        private static RuleSet active;
        private static GameplayVariablesData activeVars;
        private static readonly Dictionary<string, int> saved = new Dictionary<string, int>();
        private static float activeAt;
        private static bool battleSeen;
        private static float nextCheck;

        internal static double? HealthMultiplier => active?.Get("warlordHealthMultiplier") is int m ? m / 10.0 : (double?)null;
        internal static int? HealthChange => active?.Get("warlordLifeChange");
        internal static int? StartingHand => active?.Get("startingHand");
        internal static int? SecondExtra => active?.Get("secondPlayerExtraCards");
        internal static int? TurnSeconds => active?.Get("turnSeconds");
        /// <summary>The chosen mode of a custom-rules challenge being played (not set for a replay).</summary>
        internal static string ActiveEvent => active != null && !activeIsReplay && battleSeen ? active.EventId : null;

        // whether this game sent the challenge whose rules are active (false for the friend who accepted, and for replays)
        private static bool activeIsChallenger;

        /// <summary>The match about to start (or being played) is under these rules: a challenge's or a replay's saved ones.</summary>
        internal static bool Active => active != null;
        internal static bool ActiveIsReplay => active != null && activeIsReplay;
        internal static bool ActiveIsChallenger => active != null && activeIsChallenger;
        internal static string RulesEvent => active?.EventId;
        /// <summary>The Offence card pick under the active rules (1 on, 0 off), or null when they do not say (older replays).</summary>
        internal static int? Offence => active?.Get("offenseCards");
        /// <summary>Who goes first under a challenge's rules: 0 the game decides, 1 the challenger, 2 the friend challenged.</summary>
        internal static int FirstPlayer => active?.Get("firstPlayer") ?? 0;
        /// <summary>The match being played is a server-deal test (0.14.34; ServerDeal.cs checks the room too).</summary>
        internal static bool ServerDealRule => active != null && !ActiveIsReplay && active.Get("serverDeal") == 1;
        /// <summary>A replay's saved rules say a challenge forced who went first in it.</summary>
        internal static bool ReplayForcedFirst => active != null && activeIsReplay && active.Get("forcedFirst") == 1;

        // a replay's saved rules (Replays): the next battle, if it is that replay, is played under them
        private static bool activeIsReplay;

        internal static void ForReplay(RuleSet rules)
        {
            Apply(rules, false);
            activeIsReplay = true;
        }

        /// <summary>A replay without saved rules: nothing noted for a challenge may reach it.</summary>
        internal static void NoReplayRules()
        {
            if (active != null && activeIsReplay) Restore();
        }

        /// <summary>The replay's Restart button (0.14.56): the game reloads the battle scene, and for a moment there is no battle.
        /// Tick would take that for the replay's end and drop its saved rules, so the restarted replay played under the
        /// mode's plain rules (and without its "who went first") and went wrong. The rules are kept as for a replay that
        /// has not started yet.</summary>
        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.ClickRestartReplay))]
        private static class ReplayRestart
        {
            private static void Prefix()
            {
                try
                {
                    if (active == null || !activeIsReplay) return;
                    battleSeen = false;
                    activeAt = Time.realtimeSinceStartup;
                    RevivalMod.Log.Msg("[replay] restarting: the replay's saved rules are kept for the restart");
                }
                catch (Exception e) { RevivalMod.Log.Warning("[replay] restart: " + e.Message); }
            }
        }

        // Both sides start a challenge match through here: the challenger once the friend accepts, the
        // friend when accepting. With custom rules for this opponent, the match is played in the rules'
        // mode and by its values.
        [HarmonyPatch(typeof(ChallengeManager), nameof(ChallengeManager.TryChallengeMatchStart))]
        private static class MatchStart
        {
            // The game's own challenge is left exactly as it is (0.12.35-0.12.37 switched the challenge to the
            // chosen mode here, which stopped the friend's accept from starting the match). Only the rules
            // are noted for the coming match; they go onto the match itself when the battle starts.
            private static bool Prefix(ChallengeManager __instance, IPlayEvent playEvent, ref bool __result)
            {
                try
                {
                    string opponent = __instance.currentOpponentPlayfabId ?? "";
                    if (!ChallengeDecks.DeckOk(playEvent, "challenging"))
                    {
                        __result = false;
                        return false;
                    }
                    if (refused.Contains(opponent))
                    {
                        RevivalMod.Log.Warning($"[challenge] not starting the match with {opponent}: its rules were refused");
                        __result = false;
                        return false;
                    }
                    // the challenger's game calls this as it sends, before it knows the opponent's id
                    RuleSet rules = null;
                    if (outgoing != null && (opponent.Length == 0 || string.Equals(opponent, outgoingTo, StringComparison.OrdinalIgnoreCase))) rules = outgoing;
                    else if (incoming.TryGetValue(opponent, out var got)) rules = got.rules;
                    // No guess from a single waiting challenge here (mod code review M-18, 2026-10-16): with the
                    // opponent not known yet this is the challenger's own send, and a friend's custom-rules challenge
                    // still waiting would put its rules on an ordinary challenge to someone else, on this side only.
                    // The accepting side's guess is FromIncoming, at the battle start.
                    RevivalMod.Log.Msg($"[challenge] challenge match starting with '{opponent}': {(rules == null ? "ordinary rules" : "custom rules")}");
                    // nothing is cleared here for an ordinary one: rules already noted stay until the battle
                    // (which only uses them in a friend match), a call-off, or 5 minutes
                    if (rules != null && !ReferenceEquals(rules, active)) Apply(rules, ReferenceEquals(rules, outgoing));
                }
                catch (Exception e) { RevivalMod.Log.Warning("[challenge] match start: " + e.Message); }
                return true;
            }
        }

        private static void Apply(RuleSet rules, bool challenger)
        {
            Restore();
            active = rules;
            activeIsChallenger = challenger;
            activeAt = Time.realtimeSinceStartup;
            battleSeen = false;
            RevivalMod.Log.Msg($"[challenge] next match with custom rules in {rules.EventId}: {rules.ToJson()["v"]?.ToJsonString()}");
        }

        // The match's own copy of the rules (MatchData.GameplayData: a friend match runs under the
        // practice mode's rules, not the chosen mode's) gets the challenge's values as the battle starts,
        // before the warlords and hands are set up.
        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.StartBattleManager))]
        private static class BattleStart
        {
            private static void Prefix(BattleManager __instance)
            {
                Rules(__instance);
                MatchOptions.BattleStarting(__instance);     // the Offence card pick for this match, once its rules are known
            }

            private static void Rules(BattleManager __instance)
            {
                bool replay = Replays.IsReplay(__instance);
                if (active != null && activeIsReplay != replay)
                {
                    // a replay's rules only for that replay; a challenge's rules never for a replay
                    RevivalMod.Log.Msg(replay ? "[challenge] a replay is starting; the challenge's rules are not used for it"
                                              : "[replay] this match is not the replay; its saved rules are not used");
                    Restore();
                }
                try { if (active == null && !replay) FromIncoming(__instance); } catch (Exception e) { RevivalMod.Log.Warning("[challenge] " + e.Message); }
                if (active == null) return;
                try
                {
                    var md = __instance.matchData;
                    if (replay && (object)md != null) { SetAll(md.GameplayData, "[replay] saved rules applied to this replay"); return; }
                    if ((object)md == null || md.playMode != PlayModes.Duel)
                    {
                        RevivalMod.Log.Msg($"[challenge] this match is not a friend match ({((object)md == null ? "no match data" : md.playMode.ToString())}); custom rules not used");
                        Restore();
                        return;
                    }
                    if (!SetAll(md.GameplayData, "[challenge] custom rules applied to this match")) return;
                    outgoing = null;
                    incoming.Clear();
                }
                catch (Exception e) { RevivalMod.Log.Warning("[challenge] applying rules: " + e.Message); }
            }
        }

        /// <summary>The active rules onto the match's own rules object (the mode's values are noted to put back).</summary>
        private static bool SetAll(GameplayVariablesData gv, string done)
        {
            if ((object)gv == null) { RevivalMod.Log.Warning("[challenge] the match has no rules object; the rules were not applied"); return false; }
            if ((object)activeVars != null) foreach (var kv in saved) Write(activeVars, kv.Key, kv.Value);
            activeVars = gv;
            saved.Clear();
            Set(gv, "warlordLifeChange", active); Set(gv, "startingMana", active); Set(gv, "startingManaSecond", active);
            Set(gv, "manaPerTurn", active); Set(gv, "drawCardsPerTurn", active); Set(gv, "handLimit", active); Set(gv, "overtimeTurn", active);
            Set(gv, "manaAccumulation", active);
            battleSeen = true;
            RevivalMod.Log.Msg($"{done}: mana {gv.startingMana}/{gv.startingManaSecond} +{gv.manaPerTurn}, " +
                               $"draw {gv.drawCardsPerTurn}, hand limit {gv.handLimit}, overtime {gv.overtimeTurn}, health change {gv.warlordLifeChange}, mana carried over {gv.manaAccumulation}");
            return true;
        }

        /// <summary>
        /// The friend's side: accepting goes straight to the match without the challenge-start call the
        /// challenger's game makes, so the rules of the accepted challenge are looked up as the battle starts:
        /// a friend match against the player whose challenge with rules arrived in the last 5 minutes.
        /// </summary>
        private static void FromIncoming(BattleManager battle)
        {
            if (incoming.Count == 0) return;
            var md = battle.matchData;
            if ((object)md == null || md.playMode != PlayModes.Duel) return;
            string opponent = "";
            try { opponent = UnityEngine.Object.FindObjectOfType<ChallengeManager>()?.currentOpponentPlayfabId ?? ""; } catch { }
            float now = Time.realtimeSinceStartup;
            (RuleSet rules, float at) got = default;
            bool found = opponent.Length > 0 && incoming.TryGetValue(opponent, out got);
            if (!found && opponent.Length == 0 && incoming.Count == 1) { got = incoming.Values.First(); found = true; opponent = incoming.Keys.First(); }
            if (!found || now - got.at > 300f) { RevivalMod.Log.Msg($"[challenge] friend match: no challenge rules for '{opponent}'"); return; }
            RevivalMod.Log.Msg($"[challenge] friend match with {opponent}: the accepted challenge's rules");
            Apply(got.rules, false);
        }

        private static void Set(GameplayVariablesData gv, string key, RuleSet rules)
        {
            int? want = rules.Get(key);
            if (want == null) return;
            int now = Read(gv, key);
            if (!saved.ContainsKey(key)) saved[key] = now;
            Write(gv, key, want.Value);
        }

        private static int Read(GameplayVariablesData gv, string key) => key switch
        {
            "warlordLifeChange" => gv.warlordLifeChange,
            "startingMana" => gv.startingMana,
            "startingManaSecond" => gv.startingManaSecond,
            "manaPerTurn" => gv.manaPerTurn,
            "drawCardsPerTurn" => gv.drawCardsPerTurn,
            "manaAccumulation" => gv.manaAccumulation,
            "handLimit" => gv.handLimit,
            "overtimeTurn" => gv.overtimeTurn,
            _ => 0,
        };

        private static void Write(GameplayVariablesData gv, string key, int v)
        {
            switch (key)
            {
                case "warlordLifeChange": gv.warlordLifeChange = v; break;
                case "startingMana": gv.startingMana = v; break;
                case "startingManaSecond": gv.startingManaSecond = v; break;
                case "manaPerTurn": gv.manaPerTurn = v; break;
                case "drawCardsPerTurn": gv.drawCardsPerTurn = v; break;
                case "manaAccumulation": gv.manaAccumulation = v; break;
                case "handLimit": gv.handLimit = v; break;
                case "overtimeTurn": gv.overtimeTurn = v; break;
            }
        }

        /// <summary>The mode's own values back, once the custom match is over (or never started).</summary>
        internal static void Restore()
        {
            try
            {
                if ((object)activeVars != null && activeVars.Pointer != IntPtr.Zero)
                    foreach (var kv in saved) Write(activeVars, kv.Key, kv.Value);
                if (active != null) RevivalMod.Log.Msg("[challenge] mode rules put back");
            }
            catch (Exception e) { RevivalMod.Log.Warning("[challenge] restore: " + e.Message); }
            active = null;
            activeIsReplay = false;
            activeIsChallenger = false;
            activeVars = null;
            saved.Clear();
        }

        /// <summary>Called every frame: puts the mode's rules back when the custom match has ended.</summary>
        internal static void Tick()
        {
            EnlargePopup();
            if (active == null) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextCheck) return;
            nextCheck = now + 1f;
            try
            {
                bool inBattle = GameState.InBattle;
                if (inBattle) battleSeen = true;
                else if (battleSeen || now - activeAt > 300f) Restore();
            }
            catch { }
        }
    }
}
