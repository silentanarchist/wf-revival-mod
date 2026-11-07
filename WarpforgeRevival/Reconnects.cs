using System;
using HarmonyLib;
using Il2Cpp;

namespace WarpforgeRevival
{
    /// <summary>
    /// Longer, steadier reconnect window in matches (0.15.20).
    /// The game gives a dropped player globalVars.maxSecToReconnect (30 s) minus 3 s for every earlier drop of theirs
    /// this match (BattleManager.AttemptReconnect: floor 10 s, and no attempt at all after the 6th drop); the opponent
    /// waits the same 30 s minus 3 s per drop before winning by disconnect (ForfeitDisconnectedEnemy, floor 15 s).
    /// Both countdowns are started from ShowDisconnectionPopup, so before each start this sets the window from the
    /// server's "reconnectSeconds" (default 30) and clears the game's own drop counter, so the window no longer
    /// shrinks. The drop limit is kept, but counted here: after "reconnectDrops" (default 9) drops of our own in one
    /// match the game's counter is set to its own limit and the next drop is not retried. The opponent's side has no
    /// limit in the game; their own client stops retrying at the same limit.
    /// </summary>
    internal static class Reconnects
    {
        private const int DefaultSeconds = 30, DefaultDrops = 9;
        private const int GameDropLimit = 6;         // AttemptReconnect runs only while playerDisconnects < 6
        private static IntPtr match;                 // the BattleManager the counts below belong to
        private static int ownDrops;

        [HarmonyPatch(typeof(BattleManager), "ShowDisconnectionPopup")]
        private static class SteadyWindow
        {
            private static void Prefix(BattleManager __instance, bool __0)
            {
                try
                {
                    if ((object)__instance == null || __instance.Pointer == IntPtr.Zero) return;
                    if (__instance.Pointer != match) { match = __instance.Pointer; ownDrops = 0; }
                    var vars = __instance.globalVars;
                    int seconds = ServerSettings.ReconnectSeconds > 0 ? ServerSettings.ReconnectSeconds : DefaultSeconds;
                    int limit = ServerSettings.ReconnectDrops > 0 ? ServerSettings.ReconnectDrops : DefaultDrops;
                    bool isPlayer = __0;       // true: our own connection dropped
                    if (isPlayer)
                    {
                        ownDrops++;
                        // AttemptReconnect takes 3 s off the window even on the first drop; the opponent waits 5 s
                        // longer than we try, so a player who cannot get back gives up on their own side first.
                        if ((object)vars != null) vars.maxSecToReconnect = seconds + 3;
                        __instance.playerDisconnects = ownDrops <= limit ? 0 : GameDropLimit;
                        RevivalMod.Log.Msg(ownDrops <= limit
                            ? $"[reconnect] connection lost (drop {ownDrops} of {limit} this match): trying for {seconds} s"
                            : $"[reconnect] connection lost a {ownDrops}th time this match (limit {limit}): not retrying");
                    }
                    else
                    {
                        if ((object)vars != null) vars.maxSecToReconnect = seconds + 5;
                        __instance.enemyDisconnects = 0;
                        RevivalMod.Log.Msg($"[reconnect] the opponent lost their connection: waiting {seconds + 5} s");
                    }
                }
                catch (Exception e) { RevivalMod.Log.Warning("[reconnect] " + e.Message); }
            }
        }

        /// <summary>
        /// How long the match connection may go without an answer before the game counts it as lost: PUN's
        /// DisconnectTimeout, 10 s by default. A short mobile-network hiccup often lasts longer than that, and every
        /// such drop costs a full reconnect; 25 s rides most of them out (the server's own timeout is 30 s).
        /// </summary>
        internal static void SetPeerTimeout()
        {
            try
            {
                var peer = PhotonNetwork.networkingPeer;
                if ((object)peer == null) return;
                int ms = (ServerSettings.HiccupSeconds > 0 ? ServerSettings.HiccupSeconds : 25) * 1000;
                if (peer.DisconnectTimeout != ms)
                {
                    peer.DisconnectTimeout = ms;
                    RevivalMod.Log.Msg($"[reconnect] match connection timeout set to {ms / 1000} s");
                }
            }
            catch (Exception e) { RevivalMod.Log.Warning("[reconnect] timeout: " + e.Message); }
        }
    }
}
