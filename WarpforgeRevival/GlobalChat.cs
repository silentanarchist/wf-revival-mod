using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;

namespace WarpforgeRevival
{
    /// <summary>
    /// Chat runs on the Revival server. The game's own chat window, tabs and message types are
    /// used unchanged; only the transport is replaced: a message the game would publish through
    /// Photon is posted to the server instead, and the server is asked for new messages every few
    /// seconds. The server keeps the messages, which also gives the chat a history again (the
    /// game's history store, Firebase, is gone).
    /// </summary>
    internal static class GlobalChat
    {
        private const string GlobalChannel = "Global";

        private sealed class Incoming { public long Id; public string Channel, Sender, Message; public bool History; }

        private static readonly HttpClient Http = Net.Client(TimeSpan.FromSeconds(15));
        private static readonly ConcurrentQueue<Incoming> Inbox = new ConcurrentQueue<Incoming>();
        // The text of a message this game has just sent. The game draws its own message on screen
        // straight after sending it; that copy is skipped (LocalCopy), so the player sees their message
        // only as the server sends it back - with blocked words starred out, as everyone else sees it.
        private static string justSent;
        private static volatile bool pollSoon;
        private static string server;
        private static ChatGlobalManager manager;
        private static long since = -1;              // -1: nothing fetched yet, ask for the history
        private static volatile bool polling;
        private static float nextPoll, managerSeen;
        private static bool forcedChannel, pollNoted, failNoted;

        public static void Start(string serverUrl) => server = serverUrl;


        private static void Remember(ChatGlobalManager m)
        {
            if (!Ui.Alive(m) || ((object)manager != null && manager.Pointer == m.Pointer)) return;
            manager = m;
            managerSeen = UnityEngine.Time.realtimeSinceStartup;
            forcedChannel = false;
            toldConnected = false;
            since = -1;                               // a new sign-in starts from the history again
            Held.Clear();                             // and the held alliance messages come again with it
            friendShown = null;                       // the new chat manager's status table is filled again
        }

        /// <summary>Sends a chat request as the signed-in player (the server refuses chat without a session).</summary>
        private static Task<HttpResponseMessage> Post(string path, HttpContent body)
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, server + path) { Content = body };
            string ticket = PlayFabTransport.SessionTicket;
            if (!string.IsNullOrEmpty(ticket)) msg.Headers.TryAddWithoutValidation("X-Authorization", ticket);
            string lang = Loc.ServerLanguage();                             // chat refusals in the player's language
            if (lang != null) msg.Headers.TryAddWithoutValidation("X-Revival-Lang", lang);
            return Http.SendAsync(msg);
        }

        private static StringContent Body(Action<Utf8JsonWriter> write)
        {
            using var ms = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(ms)) { w.WriteStartObject(); write(w); w.WriteEndObject(); }
            return new StringContent(Encoding.UTF8.GetString(ms.ToArray()), Encoding.UTF8, "application/json");
        }

        // ---------------------------------------------------------------- reporting
        // "Report message" on a player in the chat only went to the game's analytics service; it goes
        // to the server instead, where admins see it on the creator site.
        // ChatGlobalManager.ReportMessage is a few instructions that jump straight into
        // GameAnalytics.SendReportMessage, and the hook above never ran in play (owner's test,
        // 2026-10-12: the game logged "SendReportMessage to analytics", the server got nothing).
        // So the report is taken where it ends up. The message's time is not passed this far; the
        // server matches the text against its own chat copy instead.
        [HarmonyPatch(typeof(GameAnalytics), nameof(GameAnalytics.SendReportMessage))]
        private static class ReportToAnalytics
        {
            private static bool Prefix(string playfabIdReported, string messageReported)
            {
                try
                {
                    if (server == null) return true;
                    string player = playfabIdReported ?? "", text = messageReported ?? "";
                    var body = Body(w => { w.WriteString("player", player); w.WriteString("msg", text); });
                    Task.Run(async () =>
                    {
                        try
                        {
                            using var reply = await Post("/playfab/Revival/ChatReport", body);
                            RevivalMod.Log.Msg(reply.IsSuccessStatusCode
                                ? "[chat] message reported to the server admins"
                                : "[chat] the server did not take the report (" + (int)reply.StatusCode + ")");
                        }
                        catch (Exception e) { RevivalMod.Log.Warning("[chat] could not send the report: " + e.Message); }
                    });
                }
                catch (Exception e) { RevivalMod.Log.Warning("[chat] report: " + e.Message); }
                return false;                         // nothing for the analytics service
            }
        }

        // ---------------------------------------------------------------- sending
        // On the phone PublishMessage is three instructions that jump into publishMessage - too short
        // to hook safely (see NoShortHooks) - so the one it jumps into is hooked there.
#if ANDROID_PORT
        [HarmonyPatch(typeof(Il2CppPhoton.Chat.ChatClient), nameof(Il2CppPhoton.Chat.ChatClient.publishMessage))]
#else
        [HarmonyPatch(typeof(Il2CppPhoton.Chat.ChatClient), nameof(Il2CppPhoton.Chat.ChatClient.PublishMessage))]
#endif
        private static class Publish
        {
            private static bool Prefix(string __0, Il2CppSystem.Object __1, ref bool __result)
            {
                string channelName = __0; var message = __1;
                if (server == null) return true;
                try
                {
                    string text = (object)message == null ? null : IL2CPP.Il2CppStringToManaged(message.Pointer);
                    if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(channelName)) return true;
                    string sender = "";
                    try { if (Ui.Alive(manager)) sender = manager.PlayerId ?? ""; } catch { }
                    string cid = Guid.NewGuid().ToString("N");
                    justSent = text;
                    RevivalMod.Log.Msg($"[chat] sending to {channelName} ({text.Length} characters)");
                    Task.Run(async () =>
                    {
                        try
                        {
                            using var body = Body(w => { w.WriteString("channel", channelName); w.WriteString("sender", sender); w.WriteString("message", text); w.WriteString("cid", cid); });
                            using var reply = await Post("/playfab/Revival/ChatSend", body);
                            if (!reply.IsSuccessStatusCode) RevivalMod.Log.Warning($"[chat] the server refused the message ({(int)reply.StatusCode})");
                            else pollSoon = true;             // fetch it back straight away
                        }
                        catch (Exception e) { RevivalMod.Log.Warning("[chat] message not sent: " + e.Message); }
                    });
                    __result = true;
                    return false;                     // nothing goes through Photon
                }
                catch (Exception e)
                {
                    RevivalMod.Log.Warning("[chat] send failed: " + e.Message);
                    return true;
                }
            }
        }

        // ---------------------------------------------------------------- longer messages (0.14.15)
        // The game allows 200 characters: the chat box's characterLimit (set in its prefab) and a hard-coded check
        // at the top of ChatGlobalManager.SendChatMessage(string, string) that silently drops anything longer.
        // Raised to 600 on request (owner, 2026-10-18). Nothing checks the length on the receiving side, so players
        // on older versions still see the longer messages; the server takes chat messages up to 8000 characters.
        internal const int MaxMessage = 600;

        [HarmonyPatch(typeof(ChatPanel), nameof(ChatPanel.Start))]
        private static class LongerBox
        {
            private static void Postfix(ChatPanel __instance)
            {
                try
                {
                    var input = (object)__instance == null ? null : __instance.inputField;
                    if ((object)input != null && input.characterLimit != MaxMessage)
                    {
                        input.characterLimit = MaxMessage;
                        RevivalMod.Log.Msg($"[chat] chat box takes up to {MaxMessage} characters");
                    }
                }
                catch (Exception e) { RevivalMod.Log.Warning("[chat] longer messages: " + e.Message); }
                ClearOfSend(__instance);
            }

            /// <summary>The game's Send button sits on top of the right end of the typing box, so the end of a long
            /// message (600 characters since 0.13.x) was hidden under it while typing (owner, 2026-10-27). The box is
            /// made a little narrower so it ends just left of the button.</summary>
            private static void ClearOfSend(ChatPanel panel)
            {
                try
                {
                    var input = panel.inputField;
                    var send = panel.sendMessageButton;
                    if ((object)input == null || (object)send == null) return;
                    var irt = input.GetComponent<UnityEngine.RectTransform>();
                    var srt = send.GetComponent<UnityEngine.RectTransform>();
                    if ((object)irt == null || (object)srt == null) return;
                    UnityEngine.Canvas.ForceUpdateCanvases();
                    var (it, ib, il, ir) = Ui.WorldBox(irt);
                    var (st, sb, sl, sr) = Ui.WorldBox(srt);
                    bool overlaps = sl < ir && sr > il && st > ib && sb < it;
                    if (!overlaps) { RevivalMod.Log.Msg("[chat] the Send button is clear of the typing box"); return; }
                    float scale = irt.lossyScale.x;
                    if (scale <= 0f) return;
                    float gap = 8f * scale;
                    float cut = (ir - (sl - gap)) / scale;           // in the box's own units
                    if (cut <= 0f || cut > irt.rect.width * 0.5f) { RevivalMod.Log.Msg($"[chat] typing box left as it is (would lose {cut:0} of {irt.rect.width:0})"); return; }
                    irt.offsetMax = new UnityEngine.Vector2(irt.offsetMax.x - cut, irt.offsetMax.y);
                    RevivalMod.Log.Msg($"[chat] typing box made {cut:0} narrower so it ends left of the Send button");
                }
                catch (Exception e) { RevivalMod.Log.Warning("[chat] typing box next to the Send button: " + e.Message); }
            }
        }

        /// <summary>
        /// A message of 201..600 characters is built and sent the way the game builds a normal one (type Message,
        /// sender's id, name and avatar, the text, the server time), straight to the overload that publishes it;
        /// the game's own function would drop it. Shorter ones go the game's way unchanged.
        /// </summary>
        [HarmonyPatch(typeof(ChatGlobalManager), nameof(ChatGlobalManager.SendChatMessage), new[] { typeof(string), typeof(string) })]
        private static class LongerSend
        {
            private static bool Prefix(ChatGlobalManager __instance, string chatMessage, string channelId)
            {
                if (chatMessage == null || chatMessage.Length <= 200 || chatMessage.Length > MaxMessage) return true;
                try
                {
                    var pd = __instance.playerData;
                    if ((object)pd == null) pd = PlayerDataManager.singletonManager;
                    var avatars = SingletonBehaviour<PlayerAvatarDataManager>.Instance;
                    string avatar = (object)avatars == null ? "" : avatars.PlayerAvatarID ?? "";
                    string border = (object)avatars == null ? "" : avatars.PlayerAvatarBorderID ?? "";
                    var msg = new ChatMessageData(ChatMessageType.Message, pd.playFabId, pd.playerName, avatar, border, "", "", chatMessage);
                    msg.timestamp = pd.currentServerTime;
                    __instance.SendChatMessage(msg, channelId);
                    RevivalMod.Log.Msg($"[chat] long message ({chatMessage.Length} characters) sent past the game's 200 limit");
                    return false;
                }
                catch (Exception e)
                {
                    RevivalMod.Log.Warning("[chat] long message: " + e.Message);
                    return true;
                }
            }
        }

        /// <summary>
        /// The chat window shows "x ago" from the message's timestamp, which must be Unix
        /// milliseconds; the game's server clock is not that on the Revival server.
        /// </summary>
        [HarmonyPatch(typeof(ChatGlobalManager), nameof(ChatGlobalManager.SendChatMessage), new[] { typeof(ChatMessageData), typeof(string) })]
        private static class Stamp
        {
            private static void Prefix(ChatMessageData chatMessage)
            {
                try
                {
                    if ((object)chatMessage != null && chatMessage.timestamp < 1_000_000_000_000L)
                        chatMessage.timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
                catch (Exception e) { RevivalMod.Log.Warning("[chat] timestamp: " + e.Message); }
            }
        }

        /// <summary>
        /// Right after sending, the game hands its own message to ProcessReceivedChatMessage to
        /// show it. That one call is skipped; the server's copy (filtered) is shown when it comes back.
        /// </summary>
        [HarmonyPatch(typeof(ChatGlobalManager), nameof(ChatGlobalManager.ProcessReceivedChatMessage))]
        private static class LocalCopy
        {
            private static bool Prefix(string message)
            {
                try
                {
                    string sent = justSent;
                    if (sent != null && message == sent)
                    {
                        justSent = null;
                        return false;
                    }
                }
                catch (Exception e) { RevivalMod.Log.Warning("[chat] " + e.Message); }
                return true;
            }
        }

#if ANDROID_PORT
        /// <summary>
        /// On a phone the keyboard's check mark ("done") sends the message, like Enter does on a
        /// PC. The game only listens for the Enter key, which a phone keyboard never sends; the
        /// input box reports the check mark as "submitted", so that now sends the message too.
        /// </summary>
        [HarmonyPatch(typeof(ChatPanel), nameof(ChatPanel.Start))]
        private static class KeyboardSends
        {
            private static void Postfix(ChatPanel __instance)
            {
                try
                {
                    var input = __instance.inputField;
                    if ((object)input == null) return;
                    var panel = __instance;
                    Action<string> send = text =>
                    {
                        try
                        {
                            if (string.IsNullOrWhiteSpace(text)) return;
                            panel.TrySendMessage();
                        }
                        catch (Exception e) { RevivalMod.Log.Warning("[chat] keyboard send: " + e.Message); }
                    };
                    input.onSubmit.AddListener(DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction<string>>(send));
                    RevivalMod.Log.Msg("[chat] the keyboard's check mark sends a chat message");
                }
                catch (Exception e) { RevivalMod.Log.Warning("[chat] keyboard send not set up: " + e.Message); }
            }
        }
#endif

        // ---------------------------------------------------------------- friends online
        // The green light beside a friend came from Photon Chat, which the revival does not use. The
        // server says with every chat answer which friends are online (their game is running) or in
        // a match, for friends who have added this player back; the game's own status table is
        // filled from that, and an open friend list is redrawn when anything changed.
        private static volatile Dictionary<string, string> friendStatus;
        private static Dictionary<string, string> friendShown;

        /// <summary>"online", "playing" or null (offline, or has not added this player back).</summary>
        internal static string FriendStatus(string id)
        {
            var now = friendStatus;
            return now != null && id != null && now.TryGetValue(id, out var s) ? s : null;
        }

        private static bool Same(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            foreach (var kv in a) if (!b.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
            return true;
        }

        private static void ShowFriendStatus(ChatGlobalManager m)
        {
            var now = friendStatus;
            if (now == null || ReferenceEquals(now, friendShown)) return;
            var before = friendShown;
            friendShown = now;
            bool changed = before == null || before.Count != now.Count;
            try
            {
                var table = m.cachedPlayerStatus;
                if ((object)table == null) { table = new Il2CppSystem.Collections.Generic.Dictionary<string, ChatPlayerStatus>(); m.cachedPlayerStatus = table; }
                foreach (var kv in now)
                {
                    var status = kv.Value == "online" ? ChatPlayerStatus.Connected : kv.Value == "playing" ? ChatPlayerStatus.Away : ChatPlayerStatus.Disconnected;
                    if (!changed && (!before.TryGetValue(kv.Key, out var old) || old != kv.Value)) changed = true;
                    table[kv.Key] = status;
                }
                if (changed)
                {
                    FriendsLive.Redraw();
                    // a friend the game does not list yet (they accepted your request): fetch the list
                    var list = PlayerDataManager.singletonManager?.friendsData;
                    if ((object)list != null)
                        foreach (var id in now.Keys)
                            if (!list.IsFriend(id)) { FriendsLive.RefreshNow(); break; }
                }
            }
            catch (Exception e) { if (!statusNoted) { statusNoted = true; RevivalMod.Log.Warning("[friends] online status: " + e.Message); } }
        }
        private static bool statusNoted;

        // ---------------------------------------------------------------- private messages
        // Challenging a friend (and the answer to it) is a private message between two players. The
        // game sent those through the publisher's Photon Chat, which the revival skips, so they never
        // arrived. They go to the server instead, and come back to the other player with its chat poll.
        private static readonly ConcurrentQueue<(string from, string data)> PrivateInbox = new ConcurrentQueue<(string, string)>();
        private static bool privateNoted;

        // Caught at the lowest level, the chat client's own send, which every private message goes
        // through: on Windows the game's SendPrivateMessage is built into some of its callers (the
        // online check before a challenge among them), so a patch on it was not reached from there.
        [HarmonyPatch(typeof(Il2CppPhoton.Chat.ChatClient), nameof(Il2CppPhoton.Chat.ChatClient.sendPrivateMessage))]
        private static class PrivateOut
        {
            private static bool Prefix(string __0, Il2CppSystem.Object __1, ref bool __result)
            {
                string target = __0; var message = __1;
                if (server == null || (object)message == null) return true;
                __result = true;
                try
                {
                    // The game hands over its message already as JSON text; ToString gives that text back.
                    string json = message.ToString();
                    if (string.IsNullOrEmpty(json) || json.TrimStart()[0] != '{')
                        json = Il2CppEverguild.Utils.JsonWrapper.SerializeObject(message);
                    if (string.IsNullOrEmpty(json)) return false;
                    string me = PlayerDataManager.singletonManager?.playFabId;
                    if (!string.IsNullOrEmpty(me) && string.Equals(me, target, StringComparison.OrdinalIgnoreCase))
                        return false;                         // the game's notes to itself (duplicate sign-in check)
                    string kind = Kind(json);
                    json = ChallengeRules.AddTo(target, json, kind);     // custom challenge rules ride along
                    RevivalMod.Log.Msg($"[chat] {kind} to {target}");
                    Task.Run(async () =>
                    {
                        try
                        {
                            using var body = Body(w => { w.WriteString("to", target ?? ""); w.WriteString("data", json); });
                            using var reply = await Post("/playfab/Revival/ChatPrivate", body);
                            if (!reply.IsSuccessStatusCode)
                            {
                                if (!privateNoted) { privateNoted = true; RevivalMod.Log.Warning($"[chat] the server did not pass on a {kind} message ({(int)reply.StatusCode})"); }
                                return;
                            }
                            // The server answers the online check before a challenge itself when it has
                            // seen the other player just now: the game only waits 4 seconds for that answer.
                            using var doc = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
                            if (doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
                                TakePrivate(d);
                        }
                        catch (Exception e) { RevivalMod.Log.Warning("[chat] could not send a private message: " + e.Message); }
                    });
                }
                catch (Exception e) { RevivalMod.Log.Warning("[chat] private message: " + e.Message); }
                return false;
            }
        }

        private static readonly string[] Kinds = { "RefreshInvites", "Practice", "RemovePractice", "AcceptPractice", "CheckUserOnline", "AckUserOnline", "DuplicateConnectionCheck", "ExpelForDuplicateConnection" };

        private static string Kind(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("messageType", out var t))
                {
                    if (t.ValueKind == JsonValueKind.String) return t.GetString();
                    if (t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out int i)) return i >= 0 && i < Kinds.Length ? Kinds[i] : i.ToString();
                }
            }
            catch { }
            return "private message";
        }

        private static void TakePrivate(JsonElement data)
        {
            if (data.TryGetProperty("private", out var pm) && pm.ValueKind == JsonValueKind.Array)
                foreach (var p in pm.EnumerateArray())
                    if (p.TryGetProperty("from", out var pf) && p.TryGetProperty("data", out var pd) &&
                        pf.ValueKind == JsonValueKind.String && pd.ValueKind == JsonValueKind.String)
                        PrivateInbox.Enqueue((pf.GetString(), pd.GetString()));
        }

        private static void ReceivePrivate(ChatGlobalManager m, string from, string data)
        {
            try
            {
                RevivalMod.Log.Msg($"[chat] {Kind(data)} from {from}");
                ChallengeRules.Read(from, data);
                m.OnPrivateMessage(from, new Il2CppSystem.Object(IL2CPP.ManagedStringToIl2Cpp(data)), "");
            }
            catch (Exception e) { RevivalMod.Log.Warning("[chat] could not deliver a private message: " + e.Message); }
        }

        // ---------------------------------------------------------------- receiving
        private static void Poll()
        {
            polling = true;
            long from = since;
            Task.Run(async () =>
            {
                try
                {
                    using var body = Body(w => w.WriteNumber("since", from));
                    using var reply = await Post("/playfab/Revival/ChatPoll", body);
                    reply.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
                    var data = doc.RootElement.GetProperty("data");
                    bool history = from < 0;
                    int n = 0;
                    foreach (var m in data.GetProperty("messages").EnumerateArray())
                    {
                        Inbox.Enqueue(new Incoming
                        {
                            Id = m.GetProperty("id").GetInt64(),
                            Channel = m.GetProperty("channel").GetString(),
                            Sender = m.GetProperty("sender").GetString() ?? "",
                            Message = m.GetProperty("message").GetString(),
                            History = history,
                        });
                        n++;
                    }
                    since = Math.Max(0, data.GetProperty("last").GetInt64());
                    if (data.TryGetProperty("friends", out var fr) && fr.ValueKind == JsonValueKind.Object)
                    {
                        var seen = new Dictionary<string, string>();
                        foreach (var f in fr.EnumerateObject())
                            if (f.Value.ValueKind == JsonValueKind.String) seen[f.Name] = f.Value.GetString();
                        if (!Same(seen, friendStatus)) friendStatus = seen;       // M-13: the game's table is rewritten only on a change
                    }
                    if (data.TryGetProperty("online", out var on) && on.ValueKind == JsonValueKind.Number && on.TryGetInt32(out var count))
                        OnlineCount.Set(count);
                    if (data.TryGetProperty("friendRequests", out var fq) && fq.ValueKind == JsonValueKind.Number && fq.TryGetInt32(out var waiting))
                        FriendRequests.Waiting = waiting;
                    TakePrivate(data);
                    if (!pollNoted) { pollNoted = true; RevivalMod.Log.Msg($"[chat] connected to the server chat ({n} earlier message(s))"); }
                    failNoted = false;
                }
                catch (Exception e)
                {
                    if (!failNoted) { failNoted = true; RevivalMod.Log.Warning("[chat] could not reach the server chat: " + e.Message); }
                }
                finally { polling = false; }
            });
        }

        // Message types the chat window shows; the rest are one-off signals (challenges, alliance
        // changes) that must not be replayed from the history.
        private static bool Shown(ChatMessageType t) =>
            t == ChatMessageType.Message || t == ChatMessageType.DeckShare || t == ChatMessageType.ReplayShare ||
            t == ChatMessageType.RewardShare || t == ChatMessageType.Announcement;

        // Alliance messages that came before their channel was open. The game only learns which alliance the player
        // is in once the alliance data is loaded (in play: when Alliances is first opened), well after the history
        // arrives at sign-in, and it drops messages for a channel it has not opened. They wait here and are handed
        // over when the channel opens (SyncAllianceChannel), so the alliance chat and its preview show them.
        private static readonly List<Incoming> Held = new List<Incoming>();
        private const int HeldMax = 100;

        private static bool IsOpen(ChatGlobalManager m, string channel)
        {
            var channels = m.subscribedChannels;
            if ((object)channels == null) return false;
            for (int i = 0; i < channels.Count; i++)
            {
                var c = channels[i];
                if ((object)c != null && c.ChannelId == channel) return true;
            }
            return false;
        }

        private static void Deliver(ChatGlobalManager m, Incoming msg)
        {
            if (msg.Channel != null && IsAllianceChannel(msg.Channel) && !IsOpen(m, msg.Channel))
            {
                Held.Add(msg);
                if (Held.Count > HeldMax) Held.RemoveAt(0);
                return;
            }
            Show(m, msg);
        }

        private static void Show(ChatGlobalManager m, Incoming msg)
        {
            var data = m.ParseChatMessage(msg.Message);
            if ((object)data == null) return;
            if (msg.History && !Shown(data.type)) return;
            string sender = msg.Sender.Length > 0 ? msg.Sender : (data.playerId ?? "");
            m.ProcessReceivedChatMessage(data, sender, msg.Message, msg.Channel);
        }

        /// <summary>Called every frame from the mod's update loop.</summary>
        public static void Tick()
        {
            if (server == null) return;
            var m = manager;
            if (!Ui.Alive(m)) { manager = null; return; }
            float now = UnityEngine.Time.realtimeSinceStartup;
            ShowFriendStatus(m);
            OnlineCount.Tick();
            while (PrivateInbox.TryDequeue(out var pm)) ReceivePrivate(m, pm.from, pm.data);

            // Without Photon Chat nobody tells the game "chat is connected", and it is that moment
            // that marks chat as ready and notes who the player is. Say it ourselves.
            if (photonChatSkipped && !toldConnected && now - managerSeen > 0.5f)
            {
                toldConnected = true;
                try { m.OnConnected(); }
                catch (Exception e) { RevivalMod.Log.Warning("[chat] could not mark chat as connected: " + e.Message); }
                try
                {
                    if (string.IsNullOrEmpty(m.PlayerId))
                    {
                        string id = PlayerDataManager.singletonManager?.playFabId;
                        if (!string.IsNullOrEmpty(id)) m._PlayerId_k__BackingField = id;
                    }
                    m.chatReady = true;
                }
                catch (Exception e) { RevivalMod.Log.Warning("[chat] could not set the chat player: " + e.Message); }
            }

            // The chat window builds its tabs from the channels Photon confirmed. If Photon never
            // answers, open the Global channel ourselves so the chat does not depend on it.
            if (!forcedChannel && now - managerSeen > (photonChatSkipped ? 3f : 20f))
            {
                forcedChannel = true;
                try
                {
                    var channels = m.subscribedChannels;
                    if ((object)channels == null || channels.Count == 0)
                    {
                        var names = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(new[] { GlobalChannel });
                        var results = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<bool>(new[] { true });
                        RevivalMod.Log.Msg("[chat] Photon did not open the Global channel; opening it without Photon");
                        m.OnSubscribed(names, results);
                    }
                }
                catch (Exception e) { RevivalMod.Log.Warning("[chat] could not open the Global channel: " + e.Message); }
            }

            int budget = 60;
            while (budget-- > 0 && Inbox.TryDequeue(out var msg))
            {
                try { Deliver(m, msg); }
                catch (Exception e) { RevivalMod.Log.Warning("[chat] could not show a message: " + e.Message); }
            }

            if (pollSoon && !polling) { pollSoon = false; nextPoll = 0f; }
            if (polling || now < nextPoll) return;
            string me = null;
            try { me = m.PlayerId; } catch { }
            if (string.IsNullOrEmpty(me)) { nextPoll = now + 1f; return; }       // not signed in yet
            nextPoll = now + (UnityEngine.Application.isFocused ? 3f : 12f);
            SyncAllianceChannel(m);
            Poll();
        }

        // ---------------------------------------------------------------- alliance chat (0.13.63)
        private static string allianceChannel;

        /// <summary>
        /// The alliance chat is a channel named after the alliance id. The game subscribes to it through Photon Chat,
        /// which the revival does not use, so the channel (and the chat window's alliance tab) never opened. Checked
        /// with each chat poll, no hook: in an alliance, its channel is opened the way Photon would confirm it
        /// (OnSubscribed); after leaving, the old one is closed (OnUnsubscribed). The server only hands an alliance's
        /// messages to its members.
        /// </summary>
        private static void SyncAllianceChannel(ChatGlobalManager m)
        {
            try
            {
                var alliances = SingletonBehaviour<AlliancesManager>.Instance;
                string want = null;
                if ((object)alliances != null && alliances.IsInGroup)
                {
                    string id = alliances.CurrentGroupId;
                    if (!string.IsNullOrEmpty(id)) want = id;
                }
                var open = new List<string>();
                var channels = m.subscribedChannels;
                if ((object)channels != null)
                    for (int i = 0; i < channels.Count; i++)
                    {
                        var c = channels[i];
                        if ((object)c != null && !string.IsNullOrEmpty(c.ChannelId)) open.Add(c.ChannelId);
                    }
                foreach (var name in open)
                    if (name != GlobalChannel && name != want && IsAllianceChannel(name))
                    {
                        m.OnUnsubscribed(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(new[] { name }));
                        RevivalMod.Log.Msg("[chat] alliance chat closed (no longer in that alliance)");
                    }
                if (want != null && !open.Contains(want))
                {
                    m.OnSubscribed(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(new[] { want }),
                                   new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<bool>(new[] { true }));
                    if (allianceChannel != want) RevivalMod.Log.Msg("[chat] alliance chat opened");
                    allianceChannel = want;
                    int shown = 0;
                    foreach (var msg in Held)
                        if (msg.Channel == want)
                        {
                            try { Show(m, msg); shown++; }
                            catch (Exception e) { RevivalMod.Log.Warning("[chat] could not show a held alliance message: " + e.Message); }
                        }
                    Held.RemoveAll(x => x.Channel == want);
                    if (shown > 0) RevivalMod.Log.Msg($"[chat] {shown} alliance message(s) that came before the channel was open shown");
                }
            }
            catch (Exception e) { RevivalMod.Log.Warning("[chat] alliance chat: " + e.Message); }
        }

        private static bool IsAllianceChannel(string name)
        {
            if (name.Length != 16) return false;
            foreach (char c in name) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        private static volatile bool photonChatSkipped;
        private static bool toldConnected;

        /// <summary>
        /// Chat runs on the revival server. The game's own chat connection went to its publisher's
        /// Photon Chat service; that connection is never opened.
        /// </summary>
        [HarmonyPatch(typeof(Il2CppPhoton.Chat.ChatClient), nameof(Il2CppPhoton.Chat.ChatClient.Connect))]
        private static class NoPublisherChat
        {
            private static bool Prefix(Il2CppPhoton.Chat.ChatClient __instance, string appVersion, Il2CppPhoton.Chat.AuthenticationValues authValues, ref bool __result)
            {
                if (server == null) return true;
                if (!photonChatSkipped) RevivalMod.Log.Msg("[chat] not connecting to the publisher's Photon Chat; chat runs on the revival server");
                photonChatSkipped = true;
                // The game reads the player's own id back from the chat client, so it still needs
                // to be told who is signed in even though nothing connects.
                try
                {
                    __instance._AuthValues_k__BackingField = authValues;
                    __instance._AppVersion_k__BackingField = appVersion;
                }
                catch (Exception e) { RevivalMod.Log.Warning("[chat] could not keep the sign-in details: " + e.Message); }
                __result = true;
                return false;
            }
        }

        // ---------------------------------------------------------------- game hooks
        [HarmonyPatch(typeof(ChatGlobalManager), nameof(ChatGlobalManager.Initialize))]
        private static class ManagerReady { private static void Postfix(ChatGlobalManager __instance) => Remember(__instance); }

        [HarmonyPatch(typeof(ChatGlobalManager), nameof(ChatGlobalManager.ConnectToChatServer))]
        private static class Connecting { private static void Prefix(ChatGlobalManager __instance) => Remember(__instance); }

        [HarmonyPatch(typeof(ChatGlobalManager), nameof(ChatGlobalManager.OnSubscribed))]
        private static class Subscribed { private static void Prefix(ChatGlobalManager __instance) => Remember(__instance); }

        /// <summary>The history comes from the Revival server; the dead Firebase store is not asked.</summary>
        [HarmonyPatch(typeof(ChatGlobalManager), nameof(ChatGlobalManager.DownloadChatHistory))]
        private static class History
        {
            private static bool Prefix(ChatGlobalManager __instance, string channel)
            {
                Remember(__instance);
                if (server == null) return true;
                try { __instance.downloadedChannels?.Add(channel); } catch { }
                return false;
            }
        }

        /// <summary>The main-menu chat button normally waits for a first message; show it right away.</summary>
        [HarmonyPatch(typeof(ChatPreview), nameof(ChatPreview.OnInitializationComplete))]
        private static class MenuReady
        {
            private static void Postfix(ChatPreview __instance)
            {
                try
                {
                    if ((object)__instance == null || __instance.Pointer == IntPtr.Zero) return;
                    __instance.Show();
                    RevivalMod.Log.Msg("[chat] chat button shown on the main menu");
                    OnlineCount.Attach(__instance);
                }
                catch (Exception e) { RevivalMod.Log.Warning("[chat] could not show the chat button: " + e.Message); }
            }
        }
    }
}
