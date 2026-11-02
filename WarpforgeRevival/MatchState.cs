using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Il2Cpp;

namespace WarpforgeRevival
{
    /// <summary>
    /// Board fingerprints for finding desyncs (0.15.0, owner 2026-10-23). Each game works the match out by itself and
    /// only moves travel between them; when the two come out differently (a match on 2026-10-23: a dying Apothecary
    /// still targetable on one side, gone on the other) the game only notices when a move points at nothing, and the
    /// match ends with a "desync". So as each game resolves an end of turn (BattleManager.ResolveEndTurn, which both
    /// games run for every turn in the same order) it writes its view of the board as a short list - every card in the
    /// match by its in-match number: card id, where it is (deck, hand, play, dying, cemetery, removed) and, for cards in
    /// play, health, max health, attacks and armour - and sends it with its SHA-256 to the server
    /// (Revival/MatchState). The server keeps both players' lists next to the match record and logs the first turn
    /// whose fingerprints differ; comparing the two lists for that turn shows which card the games disagree on.
    /// Only online matches (a match room), never replays. Nothing hidden from a player is sent: both games already
    /// know every card's number and place (the deck order is not part of the list). Logged as [state].
    ///
    /// Server Deal matches only (0.15.14, server-deal referee): a step fingerprint after every action as well. When
    /// an action has resolved and nothing is left in the game's queues (BattleManager.FinishResolvingAction - the
    /// point where the player gets control back), the game notes a short fingerprint: the board's hash plus, for each
    /// side (named by its warlord's in-match number, the same on both games), Energy (total/unused), hand size and
    /// deck size, keyed by how many moves each side has made (0.15.15; the last fingerprint per key is kept). The steps
    /// of a turn travel with that turn's board in the same request (no extra requests); the server compares both
    /// games' steps key by key and names the first move after which they differ.
    /// </summary>
    internal static class MatchState
    {
        private static string room;
        private static int turn;
        private static int sentLogged;
        // the turn's steps, keyed by the moves made so far by each side ("<warlord no>:<moves>,<warlord no>:<moves>"):
        // the same moment on both games. A game can settle more than once after one move (its own animations,
        // draws, choices); the last fingerprint for the key is the one kept (0.15.14 numbered every settling,
        // and the two games settle a different number of times per turn, 2026-11-01).
        private static readonly List<(string k, string h, string c)> steps = new List<(string, string, string)>();
        private static int stepN;
        private static bool? dealMatch;            // decided once per match (ServerDeal.Active reads room properties)
        private static int stepFailLogged;
        private static BattleCardManager cards;    // kept for the match (searching the scene after every action costs)

        private static string Zone(CardStateOptions s)
        {
            switch (s)
            {
                case CardStateOptions.inDeck:
                case CardStateOptions.inDeckDrawing: return "D";
                case CardStateOptions.inPlay:
                case CardStateOptions.inPlayAttacking:
                case CardStateOptions.inPlayTransforming:
                case CardStateOptions.inPlayAminingAttack: return "P";
                case CardStateOptions.waitingToBePlayed: return "W";
                case CardStateOptions.waitingToDie: return "X";
                case CardStateOptions.inCemetery: return "C";
                case CardStateOptions.removedFromGame: return "R";
                default: return "H";            // in hand, drawing, showing, moving, being played, mulligan
            }
        }

        /// <summary>A card id in the characters the server takes (letters, digits, - _ .); anything else becomes _.</summary>
        private static string Clean(string id)
        {
            var sb = new StringBuilder(Math.Min(id.Length, 64));
            foreach (char ch in id)
            {
                if (sb.Length >= 64) break;
                sb.Append(((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')) || ch == '-' || ch == '_' || ch == '.' ? ch : '_');
            }
            return sb.ToString();
        }

        /// <summary>The board as one line of text, the same on both games when they agree.</summary>
        private static string Board(BattleCardManager manager)
        {
            var all = manager.GetAllCardsInGame();
            var rows = new SortedDictionary<int, string>();
            for (int i = 0; all != null && i < all.Count; i++)
            {
                var c = all[i];
                if ((object)c == null) continue;
                string id = Clean(c.rawCardData?.uniqueId ?? "?");
                string zone = Zone(c.cardState);
                string row = c.uniqueInGameID + ":" + id + ":" + zone;
                if (zone == "P" || zone == "X")
                {
                    int hp = c.currentHealth, max = c.currentMaxHealth, armour = c.currentArmourValue;
                    row += ":" + hp + "/" + max + ":" + c.CurrentMeleeAttack + "/" + c.CurrentRangeAttack + ":" + armour;
                }
                rows[c.uniqueInGameID] = row;
            }
            return string.Join(",", rows.Values);
        }

        // a new battle starts its count at 1, also when it is played in a room of the same name (a friend's Practice_<id>
        // room is reused for the next challenge, seen 2026-10-23: the second match was numbered from turn 11)
        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.StartBattleManager))]
        private static class NewBattle
        {
            private static void Prefix() { room = null; turn = 0; steps.Clear(); stepN = 0; dealMatch = null; cards = null; }
        }

        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.ResolveEndTurn))]
        private static class EndOfTurn
        {
            private static void Prefix(BattleManager __instance)
            {
                try
                {
                    if ((object)__instance == null || Replays.IsReplay(__instance) || !PhotonNetwork.inRoom) return;
                    var r = PhotonNetwork.room;
                    string name = (object)r == null ? null : r.Name;
                    if (string.IsNullOrEmpty(name)) return;
                    if (name != room) { room = name; turn = 0; }
                    turn++;
                    var manager = UnityEngine.Object.FindObjectOfType<BattleCardManager>();
                    if ((object)manager == null) return;
                    string board = Board(manager);
                    if (board.Length > 12000) board = board.Substring(0, 12000);
                    string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(board))).ToLowerInvariant().Substring(0, 16);
                    int t = turn;
                    var sent = steps.ToArray();
                    steps.Clear();
                    string json = Net.Json(w =>
                    {
                        w.WriteString("room", name);
                        w.WriteNumber("turn", t);
                        w.WriteString("hash", hash);
                        w.WriteString("board", board);
                        if (sent.Length > 0)
                        {
                            w.WriteStartArray("steps");
                            foreach (var st in sent)
                            {
                                w.WriteStartObject();
                                w.WriteString("k", st.k);
                                w.WriteString("h", st.h);
                                w.WriteString("c", st.c);
                                w.WriteEndObject();
                            }
                            w.WriteEndArray();
                        }
                    });
                    if (sentLogged < 3) { sentLogged++; RevivalMod.Log.Msg($"[state] end of turn {t}: board fingerprint {hash} ({board.Length} characters) sent to the server"); }
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        var reply = await Net.PostJson("/playfab/Revival/MatchState", json, TimeSpan.FromSeconds(15));
                        if (!reply.Ok && sentLogged < 6) { sentLogged++; RevivalMod.Log.Warning("[state] the server did not take the board fingerprint: " + reply.Error); }
                    });
                }
                catch (Exception e) { RevivalMod.Log.Warning("[state] " + e.GetType().Name + ": " + e.Message); }
            }
        }

        /// <summary>An action has finished resolving: in a Server Deal match with the queues empty, note a step.</summary>
        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.FinishResolvingAction))]
        private static class Step
        {
            private static void Postfix(BattleManager __instance)
            {
                try
                {
                    if ((object)__instance == null) return;
                    if (dealMatch == null)
                    {
                        if (Replays.IsReplay(__instance) || !PhotonNetwork.inRoom) return;
                        dealMatch = ServerDeal.Active;
                    }
                    if (dealMatch != true) return;
                    if (__instance.ActionsLeftInQueue() != 0 || __instance.AutoActionsLeftInQueue() != 0) return;
                    if (!Ui.Alive(cards)) cards = UnityEngine.Object.FindObjectOfType<BattleCardManager>();
                    var manager = cards;
                    if ((object)manager == null) return;
                    string board = Board(manager);
                    var sides = new SortedDictionary<int, string>();
                    var moved = new SortedDictionary<int, int>();
                    foreach (bool mine in new[] { true, false })
                    {
                        var hero = __instance.GetHero(mine);
                        if ((object)hero == null) continue;
                        var log = mine ? __instance.playerActionsLog : __instance.enemyActionsLog;
                        moved[hero.uniqueInGameID] = log == null ? 0 : log.Count;
                        int hand = 0, deck = 0;
                        var all = manager.GetAllCardsInGame();
                        for (int i = 0; all != null && i < all.Count; i++)
                        {
                            var c = all[i];
                            if ((object)c == null || c.isPlayer != mine) continue;
                            string z = Zone(c.cardState);
                            if (z == "H") hand++;
                            else if (z == "D") deck++;
                        }
                        sides[hero.uniqueInGameID] = hero.uniqueInGameID + ":" + __instance.GetTotalMana(mine) + "/" + __instance.GetUnusedMana(mine) + ":" + hand + ":" + deck;
                    }
                    string c2 = string.Join(",", sides.Values);
                    string h = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(board + "|" + c2))).ToLowerInvariant().Substring(0, 12);
                    var key = new StringBuilder();
                    foreach (var kv in moved) { if (key.Length > 0) key.Append(','); key.Append(kv.Key).Append(':').Append(kv.Value); }
                    string k = key.ToString();
                    var entry = (k, h, c2.Length > 120 ? c2.Substring(0, 120) : c2);
                    stepN++;
                    if (steps.Count > 0 && steps[steps.Count - 1].k == k) steps[steps.Count - 1] = entry;   // settled again after the same move
                    else if (steps.Count < 300) steps.Add(entry);
                }
                catch (Exception e)
                {
                    if (stepFailLogged++ < 3) RevivalMod.Log.Warning("[state] step fingerprint: " + e.GetType().Name + ": " + e.Message);
                }
            }
        }
    }
}
