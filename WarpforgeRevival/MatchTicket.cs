using System;
using System.Threading.Tasks;
using HarmonyLib;
using Il2Cpp;

namespace WarpforgeRevival
{
    /// <summary>
    /// Proves to the match service who is connecting.
    ///
    /// The game's match client signs in to the match service on its own, encrypted, with nothing the
    /// revival server can check. So the mod asks the game server (over https, with its session) for a
    /// match ticket, and sends it to the match service as one extra message just before the match
    /// client signs in. A server that requires accounts refuses match connections without one, and
    /// gives a seat in a room back only to the same signed-in player.
    /// The ticket is good for half an hour and for every connection in that time (the match client
    /// reconnects each time it enters or leaves a room); a fresh one is fetched before it runs out.
    /// </summary>
    internal static class MatchTicket
    {
        private const byte OpTicket = 160;                 // the same number the server listens for
        private static volatile string ticket;
        private static DateTime validUntil = DateTime.MinValue;
        private static Task fetching;
        private static readonly object Gate = new object();
        private static bool reported;

        /// <summary>Starts fetching a ticket if the one held is missing or running out (any thread).</summary>
        internal static Task Refresh()
        {
            lock (Gate)
            {
                if (ticket != null && DateTime.UtcNow < validUntil.AddMinutes(-10)) return Task.CompletedTask;
                if (fetching != null && !fetching.IsCompleted) return fetching;
                string session = PlayFabTransport.SessionTicket;
                if (string.IsNullOrEmpty(session)) return Task.CompletedTask;
                fetching = Task.Run(() => Fetch(session));
                return fetching;
            }
        }

        /// <summary>Asks for a new ticket now: something it carries changed (the custom-card package was loaded).</summary>
        internal static void Renew()
        {
            lock (Gate) { validUntil = DateTime.UtcNow; }      // counts as running out: Refresh fetches a new one, the old stays usable meanwhile
            try { Refresh(); } catch (Exception e) { RevivalMod.Log.Warning("[match] ticket: " + e.Message); }
        }

        private static async Task Fetch(string session)
        {
            try
            {
                // the version goes with it: from 0.14.17 the match service picks who goes first when both players' mods follow it
                // and the card data this run plays with (0.15.30): the match service only pairs players with the same cards
                var headers = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>
                    { new System.Collections.Generic.KeyValuePair<string, string>("X-Revival-Mod", RevivalMod.Version) };
                string cards = CardsCheck.InUse;
                if (cards != null) headers.Add(new System.Collections.Generic.KeyValuePair<string, string>("X-Revival-Cards", cards));
                // and the testers' custom-card package (0.15.32: it carries updates to official cards), so testers are only
                // paired with testers who have the same one
                int custom = CustomBundle.Version;
                if (custom > 0) headers.Add(new System.Collections.Generic.KeyValuePair<string, string>("X-Revival-Custom", custom.ToString()));
                var reply = await Net.PostJson("/playfab/Revival/MatchTicket", "{}", TimeSpan.FromSeconds(10), headers.ToArray(), retry: true);
                var data = reply.Data;
                if (reply.Ok && data.ValueKind == System.Text.Json.JsonValueKind.Object && data.TryGetProperty("ticket", out var t))
                {
                    int seconds = data.TryGetProperty("seconds", out var s) && s.TryGetInt32(out int n) ? n : 1800;
                    ticket = t.GetString();
                    validUntil = DateTime.UtcNow.AddSeconds(seconds);
                    if (!reported) { reported = true; RevivalMod.Log.Msg("[match] match ticket received"); }
                }
                else RevivalMod.Log.Warning("[match] the server gave no match ticket (" + (reply.Unreachable ? reply.Error : reply.Status.ToString()) + ")");
            }
            catch (Exception e) { RevivalMod.Log.Warning("[match] could not get a match ticket: " + e.Message); }
        }

        // Fetch one as soon as the game starts connecting to the match service...
        [HarmonyPatch(typeof(NetworkCustomManager), nameof(NetworkCustomManager.StartConnection))]
        private static class Early
        {
            private static void Prefix() { try { Refresh(); MatchTunnel.Probe(); } catch { } }
        }

        /// <summary>
        /// The match service refused the ticket (0.13.62). It keeps tickets only while it runs, so after a server restart
        /// a game that was already open kept sending its old ticket and was refused every 2 seconds for up to 20
        /// minutes. Now the ticket is dropped and a new one fetched at once; the game's next reconnect sends it.
        /// </summary>
        internal static void Rejected()
        {
            lock (Gate)
            {
                if (ticket == null) return;
                ticket = null;
                validUntil = DateTime.MinValue;
            }
            RevivalMod.Log.Msg("[match] the match service refused the match ticket (server restarted?): getting a new one");
            Refresh();
        }

        // The match service's answer to signing in: 32767 ("invalid authentication") is how it refuses a ticket.
        [HarmonyPatch(typeof(NetworkingPeer), nameof(NetworkingPeer.OnOperationResponse))]
        private static class Answer
        {
            private static void Prefix(Il2CppExitGames.Client.Photon.OperationResponse operationResponse)
            {
                try
                {
                    if ((object)operationResponse == null || operationResponse.ReturnCode != 32767) return;
                    byte code = operationResponse.OperationCode;
                    if (code == 230 || code == 231) Rejected();
                }
                catch { }
            }
        }

        // ...and hand it over just before the match client signs in (on every connection).
        [HarmonyPatch(typeof(NetworkingPeer), "CallAuthenticate")]
        private static class Hand
        {
            private static void Prefix(NetworkingPeer __instance)
            {
                try
                {
                    var pending = Refresh();
                    if (ticket == null && !pending.IsCompleted) pending.Wait(TimeSpan.FromSeconds(5));
                    string t = ticket;
                    if (string.IsNullOrEmpty(t)) { RevivalMod.Log.Warning("[match] no match ticket to send"); return; }
                    var parameters = new Il2CppSystem.Collections.Generic.Dictionary<byte, Il2CppSystem.Object>();
                    parameters.Add(1, new Il2CppSystem.Object(Il2CppInterop.Runtime.IL2CPP.ManagedStringToIl2Cpp(t)));
                    __instance.SendOperation(OpTicket, parameters, Il2CppExitGames.Client.Photon.SendOptions.SendReliable);
                }
                catch (Exception e) { RevivalMod.Log.Warning("[match] could not send the match ticket: " + e.Message); }
            }
        }
    }
}
