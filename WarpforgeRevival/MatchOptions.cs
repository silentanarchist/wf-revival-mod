using System;
using HarmonyLib;
using Il2Cpp;

namespace WarpforgeRevival
{
    /// <summary>
    /// Per-match options (0.13.24).
    ///
    /// Offence cards: each game mode has its own switch on the server ("offenseCards" in its
    /// gameplayVariables, sent to the mod as "offenseCardModes"). As a battle starts the pick step
    /// (BattlefieldCards) is switched on or off for it: a friend challenge with custom rules uses its
    /// rules' choice, a replay the choice saved with it (older replays: their mode's current switch),
    /// anything else its game mode's switch (a mode the server does not list has none).
    ///
    /// Who goes first: a friend challenge's rules may say the challenger or the friend challenged goes
    /// first ("firstPlayer" 1 or 2; 0 leaves it to the game: the warlord with the higher initiative,
    /// then a coin toss). Both games work it out the same way - the challenger's game from its side,
    /// the friend's from theirs - after the game's own decision
    /// (SupportMethods.GetPlayerGoesFirstWithInitiative), and the forced choice is also written on the
    /// match (forceFirstPlayer), which the game saves with the recording, so the replay starts the same.
    ///
    /// The server's coin (0.14.17): every playable warlord has the same initiative, so online the game
    /// decides who goes first by a coin, and a friend challenge with custom rules showed the two games can
    /// toss it differently and both start first (the match breaks on the first move). So when the second
    /// player joins the match room, the server tosses the coin and writes the room property "RevivalFirst"
    /// (the id of the player going first), which both games read here. The server writes it only when
    /// both players run a mod that follows it, so a pair with an older mod plays as before. A challenge's
    /// own "Goes first" rule still wins.
    /// </summary>
    internal static class MatchOptions
    {
        private static bool forcedFirst;
        /// <summary>The match being played had who goes first set by a challenge's rules (saved with its replay).</summary>
        internal static bool ForcedFirst => forcedFirst;

        internal static void BattleStarting(BattleManager battle)
        {
            forcedFirst = false;
            try
            {
                bool replay = Replays.IsReplay(battle);
                var md = battle.matchData;
                bool on;
                string why;
                int? rule = ChallengeRules.Offence;
                string ev = ChallengeRules.RulesEvent ?? ((object)md == null ? null : Replays.EventOf(md)) ?? LongGame.PlayingEventId();
                // An ordinary friend challenge (no custom rules): both games follow one mode (0.15.25). Before, each game
                // took the mode its own player had selected, so friends in different modes disagreed on whether there
                // is an Offence pick, and the match broke on its first move (2026-11-16).
                bool ordinaryChallenge = rule == null && !replay && InFriendChallenge(md);
                if (ordinaryChallenge)
                {
                    string own = ev;
                    ev = ServerSettings.ChallengeMode;
                    if (own != ev) RevivalMod.Log.Msg($"[match] friend challenge without custom rules: following {ev} (your selected mode is {own ?? "unknown"})");
                }
                if (rule != null)
                {
                    on = rule == 1;
                    why = ChallengeRules.ActiveIsReplay ? "saved with the replay" : "the challenge's rules";
                }
                else
                {
                    on = ServerSettings.OffenceFor(ev);
                    why = "game mode " + (ev ?? "unknown") + (replay ? " (replay without a saved choice)" : ordinaryChallenge ? " (friend challenge)" : "");
                }
                BattlefieldCards.Set(on);
                // the Defence card: from the deck, or picked after the mulligan when the mode has no slot in the editor
                BattlefieldCards.SetDefenceSource(ev);
                RevivalMod.Log.Msg($"[match] Offence cards {(on ? "on" : "off")} for this {(replay ? "replay" : "match")} ({why})" +
                                   (BattlefieldCards.PickOn != on ? " - could not be switched on this device" : ""));
            }
            catch (Exception e) { RevivalMod.Log.Warning("[match] Offence cards for this match: " + e.Message); }
        }

        /// <summary>A friend challenge: its match type, or (they may not be set yet as the battle starts) its room,
        /// which the game names Practice_&lt;challenger&gt; (BattleNetworkManager.GetPracticeRoomName).</summary>
        private static bool InFriendChallenge(MatchData md)
        {
            try
            {
                if ((object)md != null && (md.playMode == PlayModes.Duel || md.matchType == MatchType.Duel)) return true;
                if (PhotonNetwork.offlineMode || !PhotonNetwork.inRoom) return false;
                var room = PhotonNetwork.room;
                string name = (object)room == null ? null : room.Name;
                return name != null && name.StartsWith("Practice_", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        [HarmonyPatch(typeof(SupportMethods), nameof(SupportMethods.GetPlayerGoesFirstWithInitiative))]
        private static class WhoGoesFirst
        {
            private static void Postfix(MatchData matchData, ref bool __result)
            {
                try
                {
                    if ((object)matchData == null) return;
                    if (!ChallengeRules.Active) { FollowServer(matchData, ref __result); return; }
                    if (ChallengeRules.ActiveIsReplay)
                    {
                        // the recording carries the match's forceFirstPlayer (1 this player, 2 the opponent)
                        int saved = matchData.forceFirstPlayer;
                        if (!ChallengeRules.ReplayForcedFirst || (saved != 1 && saved != 2)) return;
                        bool was = saved == 1;
                        if (__result != was) RevivalMod.Log.Msg($"[match] replay: {(was ? "you" : "the opponent")} went first in the recorded match (challenge rules)");
                        __result = was;
                        return;
                    }
                    int fp = ChallengeRules.FirstPlayer;
                    if ((fp != 1 && fp != 2) || matchData.playMode != PlayModes.Duel) { FollowServer(matchData, ref __result); return; }
                    bool me = ChallengeRules.ActiveIsChallenger ? fp == 1 : fp == 2;
                    matchData.forceFirstPlayer = me ? 1 : 2;
                    forcedFirst = true;
                    RevivalMod.Log.Msg($"[match] challenge rules: {(fp == 1 ? "the challenger" : "the friend challenged")} goes first - " +
                                       $"{(me ? "you" : "your opponent")} (the game had picked {(__result ? "you" : "your opponent")})");
                    __result = me;
                }
                catch (Exception e) { RevivalMod.Log.Warning("[match] who goes first: " + e.Message); }
            }

            /// <summary>No rule of a challenge says who goes first: the server's coin, when it tossed one.</summary>
            private static void FollowServer(MatchData matchData, ref bool result)
            {
                bool? pick = ServerPick(matchData);
                if (pick == null) return;
                bool me = pick.Value;
                matchData.forceFirstPlayer = me ? 1 : 2;          // the game's own tie-break then agrees, on both sides
                if (result != me) RevivalMod.Log.Msg($"[match] server's coin: {(me ? "you" : "your opponent")} go{(me ? "" : "es")} first (the game had picked {(result ? "you" : "your opponent")})");
                else RevivalMod.Log.Msg($"[match] server's coin: {(me ? "you" : "your opponent")} go{(me ? "" : "es")} first");
                result = me;
            }

            /// <summary>The server's coin for this match, when it tossed one: true = this player goes first.</summary>
            private static bool? ServerPick(MatchData matchData)
            {
                try
                {
                    if ((object)matchData == null || !PhotonNetwork.inRoom) return null;
                    var mode = matchData.playMode;
                    if (mode == PlayModes.Replay || mode == PlayModes.TutorialReplay || mode == PlayModes.OfflinePractice ||
                        mode == PlayModes.Tutorial || mode == PlayModes.Campaign || mode == PlayModes.OwnDeckTraining) return null;
                    var room = PhotonNetwork.room;
                    var props = (object)room == null ? null : room.CustomProperties;
                    if ((object)props == null) return null;
                    var key = (Il2CppSystem.Object)(Il2CppSystem.String)"RevivalFirst";
                    if (!props.ContainsKey(key)) return null;
                    string first = props[key]?.ToString();
                    string mine = PlayerDataManager.singletonManager?.playFabId;
                    if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(mine)) return null;
                    bool me = string.Equals(first, mine, StringComparison.OrdinalIgnoreCase);
                    if (!me && !OpponentIs(first)) { RevivalMod.Log.Warning("[match] the server's coin names a player not in this match; the game decides"); return null; }
                    return me;
                }
                catch (Exception e) { RevivalMod.Log.Warning("[match] server's coin: " + e.Message); return null; }
            }

            private static bool OpponentIs(string id)
            {
                try
                {
                    var others = PhotonNetwork.otherPlayers;     // the game names each player by their id
                    if (others == null || others.Length == 0) return true;        // nobody to compare with yet: trust the server
                    foreach (var p in others)
                        if ((object)p != null && string.Equals(p.NickName, id, StringComparison.OrdinalIgnoreCase)) return true;
                    return false;
                }
                catch { return true; }
            }
        }

        /// <summary>
        /// Skulls in friend matches (0.14.31). The game hands out its three skulls (the enemy warlord at
        /// 20, 10 and 0 life) only in some play modes - MatchData.GetMilestones returns no thresholds at
        /// all for Duel (every friend challenge), so those matches showed no skulls, reported none at the
        /// end, and the cards that trigger on earning a skull (EarnedSkull, 17 of them) never did: the
        /// trigger fires from the same threshold count. That makes this a change both games must make
        /// together, so it is on only when the server says the opponent's mod makes it too: with the coin
        /// the server writes the room property "RevivalMod" = the lower of the two players' mod versions,
        /// and the skulls are on when that is 0.14.31 or newer. A pair with an older mod plays as before.
        /// </summary>
        [HarmonyPatch(typeof(MatchData), nameof(MatchData.GetMilestones))]
        private static class SkullsInDuels
        {
            private static readonly Version Needs = new Version(0, 14, 31);

            private static void Postfix(MatchData __instance, ref Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<HealthThresholdData> __result)
            {
                try
                {
                    if ((object)__instance == null || __instance.playMode != PlayModes.Duel) return;
                    if (__result != null && __result.Length > 0) return;
                    var pair = PairMod();
                    if (pair == null || pair < Needs)
                    {
                        RevivalMod.Log.Msg("[match] skulls stay off for this friend match (" +
                                           (pair == null ? "the server did not say the opponent's mod version" : "the opponent's mod is " + pair) + ")");
                        return;
                    }
                    __result = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<HealthThresholdData>(3);
                    __result[0] = new HealthThresholdData(20);
                    __result[1] = new HealthThresholdData(10);
                    __result[2] = new HealthThresholdData(0);
                    RevivalMod.Log.Msg("[match] skulls on for this friend match (both mods " + pair + " or newer)");
                }
                catch (Exception e) { RevivalMod.Log.Warning("[match] skulls for this match: " + e.Message); }
            }
        }

        /// <summary>The lower of the two players' mod versions in this match room, as the server wrote it ("RevivalMod"), or null.</summary>
        internal static Version PairMod()
        {
            try
            {
                if (!PhotonNetwork.inRoom) return null;
                var room = PhotonNetwork.room;
                var props = (object)room == null ? null : room.CustomProperties;
                if ((object)props == null) return null;
                var key = (Il2CppSystem.Object)(Il2CppSystem.String)"RevivalMod";
                if (!props.ContainsKey(key)) return null;
                string text = props[key]?.ToString();
                return Version.TryParse(text, out var v) ? v : null;
            }
            catch { return null; }
        }
    }
}
