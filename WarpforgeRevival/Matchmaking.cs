using System;
using System.Text.RegularExpressions;
using HarmonyLib;
using Il2Cpp;
using Il2CppEverguild.LiveOps;

namespace WarpforgeRevival
{
    /// <summary>
    /// The search for an opponent refuses to pair you with the player you faced last (and with
    /// members of your own alliance). That made sense with thousands of players; on a small
    /// community server it means two friends can never find each other twice. Drop those exclusions.
    ///
    /// Two more things keep players of one mode apart or mix players of different modes:
    ///  - rooms carry a "ranked" flag (C4) that the search also asks for - Classic has a ranked /
    ///    unranked switch. In Skirmish the two disagree: the room says 1, the search asks for 0, so
    ///    Skirmish players never meet. Skirmish rooms are opened with 0, as the search expects.
    ///    Since 0.15.27 everything is unranked: the server keeps no rating and no leaderboard, so the switch
    ///    only split the Classic queue into two pools (Nov 12-16: 611 ranked queue matches, 24 unranked, every
    ///    one of those against a bot). Every room is opened with 0, every search asks for 0, and the switch is
    ///    set to unranked and hidden (NoRankedSwitch).
    ///  - the waiting area ("lobby") is named after the kind of mode, and Classic and Long Game are
    ///    the same kind, so a Long Game search could land in a Classic room. Each mode gets a
    ///    waiting area of its own, named with the mode's id.
    /// </summary>
    internal static class Matchmaking
    {
        private static readonly Regex Exclusions = new Regex("\\s+AND\\s+C[23]\\s*!=\\s*\"[^\"]*\"", RegexOptions.Compiled);

        private static string modeId;          // the mode a room is being searched for / opened in, while that call runs
        private static string lobbyNoted;

        private static void Enter(IPlayEvent e)
        {
            modeId = null;
            try { var d = (object)e == null ? null : e.GetBaseData(); if ((object)d != null) modeId = d.eventId; }
            catch (Exception x) { RevivalMod.Log.Warning("[matchmaking] " + x.Message); }
        }

        /// <summary>The mode's own waiting area: the game's lobby name plus the mode's id.</summary>
        private static void OwnLobby(ref TypedLobby lobby)
        {
            if (string.IsNullOrEmpty(modeId) || (object)lobby == null) return;
            string name = lobby.Name ?? "";
            // ...and with the rules it is played under, so an older mod is never matched with a newer one
            string tag = "#" + modeId + "@" + RevivalMod.RulesTag;
            if (name.EndsWith(tag, StringComparison.Ordinal)) return;
            lobby = new TypedLobby(name + tag, lobby.Type);
            if (lobbyNoted != lobby.Name) { lobbyNoted = lobby.Name; RevivalMod.Log.Msg("[matchmaking] waiting area: " + lobby.Name); }
        }

        private static bool skirmishNoted;

        private static readonly Regex RankedAsked = new Regex("\\bC4\\s*==?\\s*1\\b", RegexOptions.Compiled);   // the game writes "C4 == 1"

        /// <summary>
        /// Every queue room is opened as unranked (C4 = 0): Skirmish always (its searches ask for 0, its rooms said 1),
        /// and since 0.15.27 Classic too, so there is one pool per mode. Rooms without the flag (friend challenges) are
        /// left alone.
        /// </summary>
        private static void UnrankedRoom(RoomOptions options, TypedLobby lobby)
        {
            if ((object)options == null || (object)lobby == null) return;
            var props = options.CustomRoomProperties;
            if ((object)props == null) return;
            var key = (Il2CppSystem.Object)(Il2CppSystem.String)"C4";
            if (!props.ContainsKey(key)) return;
            props[key] = default(Il2CppSystem.Int32).BoxIl2CppObject();   // a boxed 0
            if (!skirmishNoted) { skirmishNoted = true; RevivalMod.Log.Msg("[matchmaking] queue rooms are opened as unranked (C4 = 0): one pool per mode"); }
        }

        [HarmonyPatch(typeof(ChangeRankedModeToggle), nameof(ChangeRankedModeToggle.Initialize))]
        private static class NoRankedSwitch
        {
            private static bool noted;

            private static void Postfix(ChangeRankedModeToggle __instance, IRankedEvent rankedEvent)
            {
                try
                {
                    if ((object)rankedEvent != null && rankedEvent.IsRanked)
                    {
                        rankedEvent.ToggleRankedMode(false);
                        RevivalMod.Log.Msg("[matchmaking] ranked switch turned to unranked (the server keeps no rating)");
                    }
                    if ((object)__instance != null && __instance.gameObject.activeSelf) __instance.gameObject.SetActive(false);
                    if (!noted) { noted = true; RevivalMod.Log.Msg("[matchmaking] ranked / unranked switch hidden: every queue is unranked"); }
                }
                catch (Exception e) { RevivalMod.Log.Warning("[matchmaking] ranked switch: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(NetworkCustomManager), nameof(NetworkCustomManager.DoJoinRandomEventRoom))]
        private static class Searching
        {
            private static void Prefix(IPlayEvent matchEvent) => Enter(matchEvent);
            private static void Postfix() => modeId = null;
        }

        [HarmonyPatch(typeof(NetworkCustomManager), nameof(NetworkCustomManager.DoCreateEventRoom))]
        private static class Opening
        {
            private static void Prefix(IPlayEvent matchEvent) => Enter(matchEvent);
            private static void Postfix() => modeId = null;
        }

        [HarmonyPatch(typeof(PhotonNetwork), nameof(PhotonNetwork.CreateRoom),
            new[] { typeof(string), typeof(RoomOptions), typeof(TypedLobby), typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray) })]
        private static class OwnLobbyOnCreate
        {
            private static void Prefix(RoomOptions roomOptions, ref TypedLobby typedLobby)
            {
                try { UnrankedRoom(roomOptions, typedLobby); }
                catch (Exception e) { RevivalMod.Log.Warning("[matchmaking] " + e.Message); }
                try { OwnLobby(ref typedLobby); }
                catch (Exception e) { RevivalMod.Log.Warning("[matchmaking] " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(PhotonNetwork), nameof(PhotonNetwork.JoinRandomRoom),
            new[] { typeof(Il2CppExitGames.Client.Photon.Hashtable), typeof(byte), typeof(MatchmakingMode), typeof(TypedLobby), typeof(string), typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray) })]
        private static class AnyOpponent
        {
            private static void Prefix(ref string sqlLobbyFilter, ref TypedLobby typedLobby)
            {
                try { OwnLobby(ref typedLobby); }
                catch (Exception e) { RevivalMod.Log.Warning("[matchmaking] " + e.Message); }
                try
                {
                    if (string.IsNullOrEmpty(sqlLobbyFilter)) return;
                    string open = RankedAsked.Replace(Exclusions.Replace(sqlLobbyFilter, ""), "C4 == 0");   // unranked rooms only (0.15.27)
                    if (open == sqlLobbyFilter) return;
                    RevivalMod.Log.Msg($"[matchmaking] search filter widened: {open}");
                    sqlLobbyFilter = open;
                }
                catch (Exception e) { RevivalMod.Log.Warning("[matchmaking] " + e.Message); }
            }
        }
    }
}
