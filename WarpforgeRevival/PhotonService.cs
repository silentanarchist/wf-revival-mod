using System;
using HarmonyLib;
using Il2Cpp;

namespace WarpforgeRevival
{
    /// <summary>
    /// Matches run on the revival server's own match service - never on Photon's servers.
    ///
    /// The game was built on the Photon library and, left alone, connects to its publisher's Photon
    /// app. The revival server has a match service of its own that speaks the same language over
    /// TCP on the server's usual port. The game's match client is left as it is; only the address
    /// it connects to is changed - for every connection it makes, because it reconnects each time
    /// it enters or leaves a room.
    ///
    /// The port can be changed with "matchPort" in the server's revival-settings.json.
    /// </summary>
    internal static class PhotonService
    {
        private const string AppName = "WarpforgeRevival";
        private static string announced;

        /// <summary>host:port of the revival server's match service.</summary>
        private static bool Address(out string host, out int port)
        {
            host = null;
            port = ServerSettings.MatchPort > 0 ? ServerSettings.MatchPort : RevivalConfig.DefaultPort;
            try
            {
                var uri = new Uri(RevivalMod.Config.ServerUrl);
                host = uri.Host;
                // A written port is the game port, which also carries matches. An https address with
                // no port goes through a web proxy (443), which cannot carry them: the usual port then.
                if (ServerSettings.MatchPort <= 0 && uri.Port > 0 && (uri.Scheme == "http" || !uri.IsDefaultPort)) port = uri.Port;
            }
            catch (UriFormatException) { }
            return !string.IsNullOrEmpty(host);
        }

        /// <summary>Whatever address the match client was about to use, it goes to the revival server.</summary>
        [HarmonyPatch(typeof(NetworkingPeer), nameof(NetworkingPeer.Connect), new[] { typeof(string), typeof(ServerConnection) })]
        private static class Redirect
        {
            private static bool Prefix(ref string serverAddress, ref bool __result)
            {
                try
                {
                    if (Address(out string host, out int port))
                    {
                        MatchTunnel.Route(ref host, ref port, TimeSpan.Zero);
                        serverAddress = host + ":" + port;
                        return true;
                    }
                }
                catch (Exception e) { RevivalMod.Log.Warning("[match] " + e.Message); }
                // No usable server address: do not connect anywhere at all.
                RevivalMod.Log.Warning("[match] the server address is not usable, so the match service was not contacted");
                __result = false;
                return false;
            }
        }

        // The two ways the match client has of reaching Photon's own servers (to look up a region,
        // or to go to a region's servers). Nothing in the mod's path uses them; they stay shut in
        // case some corner of the game does.
        private static bool NoPhotonCloud(ref bool __result)
        {
            RevivalMod.Log.Warning("[match] the game tried to contact Photon's servers; refused");
            __result = false;
            return false;
        }

        [HarmonyPatch(typeof(NetworkingPeer), nameof(NetworkingPeer.ConnectToNameServer))]
        private static class NoNameServer { private static bool Prefix(ref bool __result) => NoPhotonCloud(ref __result); }

        [HarmonyPatch(typeof(NetworkingPeer), nameof(NetworkingPeer.ConnectToRegionMaster))]
        private static class NoRegionMaster { private static bool Prefix(ref bool __result) => NoPhotonCloud(ref __result); }

        [HarmonyPatch(typeof(NetworkCustomManager), nameof(NetworkCustomManager.ConnectToPhoton))]
        private static class Connect
        {
            private static bool Prefix(NetworkCustomManager __instance)
            {
                try
                {
                    // The port may come from the server's settings; give a slow answer a moment. The settings
                    // and the route below share one limit of 10 s (M-13: up to 3 + 10 s before), with at least 5 s for the
                    // route (an unanswered route means an unencrypted connection); both are normally known already,
                    // asked for at sign-in
                    var started = System.Diagnostics.Stopwatch.StartNew();
                    for (int i = 0; i < 30 && !ServerSettings.Loaded; i++) System.Threading.Thread.Sleep(100);
                    if (!Address(out string host, out int port))
                    {
                        RevivalMod.Log.Warning("[match] the server address is not usable, so matches are not available");
                        return false;
                    }
                    string server = host + ":" + port;
                    var left = TimeSpan.FromSeconds(10) - started.Elapsed;
                    MatchTunnel.Route(ref host, ref port, left > TimeSpan.FromSeconds(5) ? left : TimeSpan.FromSeconds(5));
                    var data = __instance.playerData;
                    string version = (object)data != null ? data.gameVersionWithEnviromentAndBundles : PhotonNetwork.gameVersion;
                    // Anything in the game that reconnects from its saved settings lands here too.
                    var s = PhotonNetwork.PhotonServerSettings;
                    if ((object)s != null)
                    {
                        s.HostType = Il2Cpp.ServerSettings.HostingOption.SelfHosted;
                        s.ServerAddress = host;
                        s.ServerPort = port;
                        s.Protocol = Il2CppExitGames.Client.Photon.ConnectionProtocol.Tcp;
                        s.AppID = AppName;
                        s.ChatAppID = "";
                    }
                    string where = server + (host == "127.0.0.1" ? " (encrypted)" : "");
                    if (announced != where) { announced = where; RevivalMod.Log.Msg("[match] matches use the revival server's match service at " + where); }
                    PhotonNetwork.SwitchToProtocol(Il2CppExitGames.Client.Photon.ConnectionProtocol.Tcp);
                    Reconnects.SetPeerTimeout();          // 0.15.20: ride out short network hiccups
                    PhotonNetwork.ConnectToMaster(host, port, AppName, version);
                }
                catch (Exception e) { RevivalMod.Log.Warning("[match] " + e.Message); }
                return false;       // the game's own connect (to its publisher's Photon app) never runs
            }
        }
    }
}
