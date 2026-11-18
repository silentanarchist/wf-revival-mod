using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using Il2CppPlayFab.Internal;

namespace WarpforgeRevival
{
    /// <summary>
    /// Sends every PlayFab API call to the revival server instead of PlayFab.
    ///
    /// The SDK's UnityWebRequest transport would refuse plain http:// (Unity's
    /// "insecure connection" rule), so we replace it: the request is sent with .NET's
    /// HttpClient on a worker thread, and the JSON reply is handed back to the SDK's own
    /// OnResponse on the main thread - from there the game parses it exactly as if it came
    /// from PlayFab.
    /// </summary>
    internal static class PlayFabTransport
    {
        private static readonly HttpClient Http = Net.Client(TimeSpan.FromSeconds(30));
        private static readonly ConcurrentQueue<Action> MainThread = new ConcurrentQueue<Action>();
        // Keep in-flight containers referenced from managed code while the request runs.
        private static readonly HashSet<CallRequestContainer> InFlight = new HashSet<CallRequestContainer>();

        public static string RewriteUrl(string fullUrl)
        {
            // https://<title>.playfabapi.com/Client/LoginWithSteam?sdk=...  ->  <server>/playfab/Client/LoginWithSteam?sdk=...
            var uri = new Uri(fullUrl);
            return RevivalMod.Config.ServerUrl + "/playfab" + uri.PathAndQuery;
        }

        /// <summary>Called from OnUpdate: delivers finished requests on Unity's main thread.</summary>
        /// <summary>The signed-in player's session ticket, as the game last sent it (null before sign-in).</summary>
        internal static volatile string SessionTicket;

        /// <summary>Runs an action on the game's main thread on the next frame.</summary>
        internal static void OnMainThread(Action a) => MainThread.Enqueue(a);

        public static void Pump()
        {
            int n = 0;
            while (n++ < 128 && MainThread.TryDequeue(out var a))
            {
                try { a(); }
                catch (Exception e) { RevivalMod.Log.Error("[playfab] callback failed: " + e); }
            }
        }

        [HarmonyPatch(typeof(PlayFabUnityHttp), nameof(PlayFabUnityHttp.MakeApiCall))]
        private static class MakeApiCallPatch
        {
            private static bool Prefix(PlayFabUnityHttp __instance, Il2CppSystem.Object reqContainerObj)
            {
                var container = reqContainerObj.Cast<CallRequestContainer>();
                string url;
                byte[] payload;
                var headers = new List<KeyValuePair<string, string>>();
                try
                {
                    url = RewriteUrl(container.FullUrl);
                    payload = container.Payload != null ? (byte[])container.Payload : Array.Empty<byte>();
                    if ((container.ApiEndpoint ?? "").Contains("/Client/ExecuteCloudScript"))
                        payload = Replays.WithRules(WithoutDraftWarlords(payload));
                    if (container.RequestHeaders != null)
                        foreach (var kv in container.RequestHeaders)
                            headers.Add(new KeyValuePair<string, string>(kv.Key, kv.Value));
                    // the server may refuse sign-in to a mod older than it asks for
                    headers.Add(new KeyValuePair<string, string>("X-Revival-Mod", RevivalMod.Version));
                    headers.Add(new KeyValuePair<string, string>("X-Revival-Rules", RevivalMod.RulesTag));
                    // at sign-in the server may also ask that this is the only mod, and the file it hands out
                    if ((container.ApiEndpoint ?? "").Contains("/Client/Login"))
                    {
                        string others = RevivalMod.OtherMods();     // never an empty value: some senders drop those
                        headers.Add(new KeyValuePair<string, string>("X-Revival-Others", others.Length > 0 ? others : "none"));
                        headers.Add(new KeyValuePair<string, string>("X-Revival-Hash", RevivalMod.FileHash.Length > 0 ? RevivalMod.FileHash : "unknown"));
                    }
                    foreach (var h in headers)
                        if (h.Key == "X-Authorization" && !string.IsNullOrEmpty(h.Value) && h.Value != SessionTicket)
                        {
                            SessionTicket = h.Value;
                            AccountPage.SignedIn();
                        }
                }
                catch (Exception e)
                {
                    RevivalMod.Log.Error("[playfab] could not read request: " + e);
                    return true; // fall back to the game's own transport
                }

                var endpoint = container.ApiEndpoint;
                if (RevivalMod.Config.DiagnosticLogging)
                    RevivalMod.Log.Msg($"[playfab] -> {endpoint}");

                lock (InFlight) InFlight.Add(container);
                var http = __instance;
                bool login = (endpoint ?? "").Contains("/Client/Login");
                if (login)
                {
                    // a saved sign-in with the server's website account (see GameSignIn)
                    string token = GameSignIn.Token();
                    if (token != null) headers.Add(new KeyValuePair<string, string>("X-Revival-Login", token));
                }
                Task.Run(async () =>
                {
                    string body = null, error = null;
                    for (int round = 0; ; round++)
                    {
                        (body, error) = await Send(url, payload, headers, endpoint);
                        if (!login) GameSignIn.Watch(body);
                        if (login && GameSignIn.OutOfDate(body))
                        {
                            // the server needs a newer mod: update now instead of failing the sign-in
                            if (await RevivalMod.UpdateForServer()) return;      // the game closes; nothing is handed back
                            // the update could not be installed: the game shows its error popup next, with the server's words (0.15.28)
                            GameSignIn.RefusedForVersion = GameSignIn.OutOfDateMessage(body);
                            break;
                        }
                        string why = login && round < 8 ? GameSignIn.NeedsSignIn(body) : null;
                        if (why == null) break;
                        // the server wants a website account first: ask, then send the sign-in again
                        string token = await GameSignIn.Ask(why);
                        if (token == null) break;
                        headers.RemoveAll(h => h.Key == "X-Revival-Login");
                        headers.Add(new KeyValuePair<string, string>("X-Revival-Login", token));
                    }

                    MainThread.Enqueue(() =>
                    {
                        lock (InFlight) InFlight.Remove(container);
                        if (body != null)
                        {
                            if (RevivalMod.Config.DiagnosticLogging)
                                RevivalMod.Log.Msg($"[playfab] <- {endpoint} ({body.Length} bytes)");
#if ANDROID_PORT
                            // phones: show what the server answered when it is short (errors are short)
                            if (body.Length < 600) RevivalMod.Log.Msg("[playfab]    " + body);
#endif
                            DraftPacks.Watch(body);
                            http.OnResponse(body, container);
                        }
                        else
                        {
                            RevivalMod.Log.Error($"[playfab] {endpoint}: {error}");
                            http.OnError(error, container);
                        }
                    });
                });
                return false; // skip the original UnityWebRequest path
            }
        }

        private static readonly HashSet<string> DraftEntries = new HashSet<string> { "FreeDraftEntrance", "PayDraftEntrance" };
        private static readonly byte[] DraftMark = Encoding.UTF8.GetBytes("DraftEntrance");

        /// <summary>Whether the request's bytes contain this text (a quick test before reading the request as JSON).</summary>
        internal static bool Has(byte[] payload, byte[] text) => payload != null && ((ReadOnlySpan<byte>)payload).IndexOf(text) >= 0;

        /// <summary>
        /// Entering a draft, the game sends the warlords it would offer. A revival server picks a
        /// run's warlords itself, from the player's collection, so that list is left out of the request.
        /// </summary>
        private static byte[] WithoutDraftWarlords(byte[] payload)
        {
            try
            {
                if (payload.Length == 0 || payload.Length > 1 << 20) return payload;
                // M-10 (0.13.72): only a draft entry is read in full; every other call is passed on without parsing
                if (!Has(payload, DraftMark)) return payload;
                var root = System.Text.Json.Nodes.JsonNode.Parse(payload) as System.Text.Json.Nodes.JsonObject;
                string fn = root?["FunctionName"]?.GetValue<string>();
                if (fn == null || !DraftEntries.Contains(fn)) return payload;
                var param = root["FunctionParameter"] as System.Text.Json.Nodes.JsonObject;
                var data = (param?["Data"] as System.Text.Json.Nodes.JsonObject) ?? param;
                if (data == null || !data.Remove("warlords")) return payload;
                RevivalMod.Log.Msg($"[draft] {fn}: the server picks the warlords; the game's list is not sent");
                return Encoding.UTF8.GetBytes(root.ToJsonString());
            }
            catch (Exception e)
            {
                RevivalMod.Log.Warning("[draft] could not read a draft request: " + e.Message);
                return payload;
            }
        }

        private static string Why(Exception e)
        {
            string why = e.GetType().Name + ": " + e.Message;
            for (var inner = e.InnerException; inner != null; inner = inner.InnerException) why += " <- " + inner.Message;
            return why;
        }

        /// <summary>Sends one API call: (reply body, null) or (null, why it failed).</summary>
        private static async Task<(string, string)> Send(string url, byte[] payload, List<KeyValuePair<string, string>> headers, string endpoint)
        {
            string body = null, error = null;
            // Both tries of one request carry the same id: when the first did reach the server after
            // all, the server answers the second with the first one's answer instead of doing it twice.
            string requestId = Guid.NewGuid().ToString("N");
            // A connection that the server or the network closed while it sat unused fails the moment
            // it is used, before the request reaches the server. That one failure (or a try that timed out)
            // is tried once more on a new connection: both tries carry the same request id, so if the first
            // did reach the server it is not carried out twice. Anything after that is reported as it is.
            // (One loop for PC and Android since 0.13.75, code review M-23; Android also retried timeouts, PC
            // only failed connections, and their logging differed.)
            for (int attempt = 1; attempt <= 2 && body == null; attempt++)
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(payload) };
                    msg.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
                    msg.Headers.TryAddWithoutValidation("X-Revival-Request", requestId);
                    string lang = Loc.ServerLanguage();                     // the server answers its messages in it
                    if (lang != null) msg.Headers.TryAddWithoutValidation("X-Revival-Lang", lang);
                    foreach (var h in headers)
                        if (!h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                            msg.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    using var resp = await Http.SendAsync(msg);
                    body = await resp.Content.ReadAsStringAsync();
                    error = null;
                    if ((int)resp.StatusCode >= 500 && string.IsNullOrEmpty(body))
                    {
                        // the server turned the connection away before reading it (too busy): try once more
                        RevivalMod.Log.Warning($"[playfab] {endpoint}: the server answered {(int)resp.StatusCode} with no reply" + (attempt == 1 ? "; trying again" : ""));
                        body = null;
                        error = $"Revival server busy ({(int)resp.StatusCode})";
                        if (attempt == 1) await Task.Delay(1000);
                    }
                }
                catch (Exception e) when (attempt == 1 && (e is HttpRequestException || e is TaskCanceledException))
                {
                    error = $"Revival server unreachable ({e.GetType().Name}: {e.Message})";
                    RevivalMod.Log.Msg($"[playfab] {endpoint}: the connection failed after {started.ElapsedMilliseconds} ms ({Why(e)}); trying again");
                    if (started.ElapsedMilliseconds >= 1000) await Task.Delay(500);
                }
                catch (Exception e)
                {
                    error = $"Revival server unreachable ({Why(e)})";
                    break;
                }
            }
            return (body, error);
        }

        // Fire-and-forget telemetry calls (screen time, device info) would still go to the real
        // PlayFab; drop them silently.
        [HarmonyPatch(typeof(PlayFabUnityHttp), nameof(PlayFabUnityHttp.SimplePostCall))]
        private static class DropSimplePost { private static bool Prefix() => false; }

        [HarmonyPatch(typeof(PlayFabUnityHttp), nameof(PlayFabUnityHttp.SimpleGetCall))]
        private static class DropSimpleGet { private static bool Prefix() => false; }

        [HarmonyPatch(typeof(PlayFabUnityHttp), nameof(PlayFabUnityHttp.SimplePutCall))]
        private static class DropSimplePut { private static bool Prefix() => false; }
    }
}
