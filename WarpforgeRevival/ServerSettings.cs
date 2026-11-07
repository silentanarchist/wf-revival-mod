using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace WarpforgeRevival
{
    /// <summary>
    /// Rules the server owner decides for everyone, read from the server's
    /// content/files/revival-settings.json (for example {"turnSeconds": 120}).
    /// Fetched when the game starts and again, in the background, before battles, so every player
    /// on a server uses the same values without editing anything locally.
    /// </summary>
    internal static class ServerSettings
    {
        private static readonly HttpClient Http = Net.Client(TimeSpan.FromSeconds(10));
        private static string url;
        private static volatile int turnSeconds;          // 0 = the server did not say
        private static DateTime lastFetch = DateTime.MinValue;
        private static volatile bool fetching;
        private static bool failNoted;

        private static volatile bool offenseCards;      // off until the server says otherwise

        // "longGame": {"eventId": "RevivalLongGame", "warlordHealth": 1.6, "classicDeckSize": 30,
        //              "copies": {"Common": 4, "Rare": 3, "Epic": 2, "Legendary": 1}}
        private static volatile string longGameEvent;
        private static double longGameHealth = 1.0;
        private static volatile int longGameHand;            // cards the first player starts with (0 = game default)
        private static volatile int longGameSecondExtra = -1; // extra cards for the second player (-1 = game default)
        public static int LongGameHand => longGameHand;
        public static int LongGameSecondExtra => longGameSecondExtra;

        // "startingHands": {"RevivalSkirmish": {"startingHand": 4, "secondPlayerExtraCards": 0}} - added by
        // the server from each mode's gameplayVariables in its GameModes.json.
        private static volatile System.Collections.Generic.Dictionary<string, (int hand, int extra)> modeHands =
            new System.Collections.Generic.Dictionary<string, (int, int)>();

        /// <summary>The starting hand a game mode sets: (cards, extra for the second player); -1 where it sets none.</summary>
        public static (int hand, int extra) ModeHand(string eventId) =>
            eventId != null && modeHands.TryGetValue(eventId, out var h) ? h : (-1, -1);
        private static readonly int[] longGameCopies = new int[6];     // by CardRarity value (1 Common .. 4 Legendary)

        /// <summary>Event id of the Long Game mode, or null when the server has none.</summary>
        public static string LongGameEvent => longGameEvent;
        private static volatile System.Collections.Generic.List<string> testerModes = new System.Collections.Generic.List<string>();
        private static volatile System.Collections.Generic.Dictionary<string, int> deckModes = new System.Collections.Generic.Dictionary<string, int>();
        /// <summary>Modes with a deck list of their own, as sent in "deckModes": {event id: the play-mode number its decks are stored under}
        /// (0.14.52; the Long Game is not listed here, it has "longGame").</summary>
        public static System.Collections.Generic.IReadOnlyDictionary<string, int> DeckModes => deckModes;
        private static volatile System.Collections.Generic.List<string> customCardModes = new System.Collections.Generic.List<string>();
        /// <summary>Modes whose decks may hold custom (CUSTOM-) cards, as sent in "customCardModes" (0.14.52).</summary>
        public static System.Collections.Generic.IReadOnlyList<string> CustomCardModes => customCardModes;
        /// <summary>Event ids of the modes the server shows only to testers (besides the Long Game), as sent in "testerModes".</summary>
        public static System.Collections.Generic.IReadOnlyList<string> TesterModes => testerModes;
        private static volatile System.Collections.Generic.List<string> dealModes = new System.Collections.Generic.List<string>();
        /// <summary>Event ids of the server-deal test modes, as sent in "dealModes".</summary>
        public static System.Collections.Generic.IReadOnlyList<string> DealModes => dealModes;
        private static volatile System.Collections.Generic.List<string> noAiModes = new System.Collections.Generic.List<string>();
        /// <summary>Event ids of the modes whose search waits for a person instead of starting an AI match ("noAiModes").</summary>
        public static System.Collections.Generic.IReadOnlyList<string> NoAiModes => noAiModes;
        public static double LongGameHealth => longGameHealth;

        private static volatile int practiceClassicLife;
        /// <summary>
        /// Health added to both warlords in a practice match against the AI when its Classic/Skirmish
        /// switch is on Classic (the server's "practiceClassicWarlordLife"; 0 = nothing added). Those
        /// matches all run under one event, so the mode rules on the server cannot tell the two apart.
        /// </summary>
        public static int PracticeClassicLife => practiceClassicLife;
        public static int LongGameCopies(int rarity) => rarity >= 0 && rarity < longGameCopies.Length ? longGameCopies[rarity] : 0;

        private static volatile bool loaded;
        private static volatile int matchPort;

        // Reconnects in matches (0.15.20; see Reconnects): 0 = the server did not say (the mod's defaults apply)
        private static volatile int reconnectSeconds, reconnectDrops, hiccupSeconds;
        /// <summary>Seconds a dropped player has to get back into the match ("reconnectSeconds", 15-180).</summary>
        public static int ReconnectSeconds => reconnectSeconds;
        /// <summary>Drops of one player in one match that are still retried ("reconnectDrops", 1-30).</summary>
        public static int ReconnectDrops => reconnectDrops;
        /// <summary>Seconds without an answer before the match connection counts as lost ("reconnectHiccupSeconds", 10-30).</summary>
        public static int HiccupSeconds => hiccupSeconds;

        private static int Clamped(JsonElement root, string name, int min, int max)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number ||
                !v.TryGetInt32(out int n)) return 0;
            return Math.Max(min, Math.Min(max, n));
        }

        /// <summary>True once the server has answered (with settings or without) at least once.</summary>
        public static bool Loaded => loaded;
        /// <summary>Port of the server's match service ("matchPort"); 0 = the server's usual port.</summary>
        public static int MatchPort => matchPort;

        private static volatile string creatorUrl;

        private static volatile string rules = "";
        /// <summary>
        /// The server's own rules version ("rulesVersion" in its settings; "" when it has none). The
        /// owner changes it when the cards or rules served change, so players who have not picked
        /// the change up yet are not matched with players who have.
        /// </summary>
        public static string Rules => rules;

        /// <summary>
        /// Address of the card creator site: the server's "creatorUrl" setting when it has one
        /// (for example an https address), otherwise the creator page on the game server itself.
        /// </summary>
        public static string CreatorUrl => !string.IsNullOrEmpty(creatorUrl) ? creatorUrl : RevivalMod.Config.ServerUrl + "/creator";


        // "offenseCardModes": {eventId: true/false} - each game mode's own Offence card switch (from its
        // gameplayVariables on the server); null when the server does not send it (older server)
        private static volatile System.Collections.Generic.Dictionary<string, bool> offenceModes;

        /// <summary>
        /// Whether a match in this game mode has the Offence card pick: the mode's own switch on the
        /// server; a mode the server does not list (a new mode, a tutorial) has none. A server that does
        /// not send the switches per mode: its single "offenseCards" switch for everything.
        /// </summary>
        public static bool OffenceFor(string eventId)
        {
            var modes = offenceModes;
            if (modes == null) return offenseCards;
            return eventId != null && modes.TryGetValue(eventId, out bool on) && on;
        }

        /// <summary>Turn length chosen by the server, or null when it has not set one.</summary>
        public static int? TurnSeconds => turnSeconds > 0 ? turnSeconds : (int?)null;

        public static void Start(string serverUrl)
        {
            url = serverUrl + "/api/v1/content/files/revival-settings.json";
            Refresh();
        }

        /// <summary>Re-reads the settings in the background if the last read is more than a minute old.</summary>
        public static void Refresh()
        {
            if (url == null || fetching || (DateTime.UtcNow - lastFetch).TotalSeconds < 60) return;
            fetching = true;
            lastFetch = DateTime.UtcNow;
            Task.Run(async () =>
            {
                try
                {
                    using var reply = await Http.GetAsync(url);
                    if (!reply.IsSuccessStatusCode)
                    {
                        // M-19 (0.13.72): a passing error (the server busy or restarting) is "no news": every setting
                        // keeps its last value, so they never end up half reset (the turn timer and Offence cards
                        // reset while Long Game and the rules version stayed). Only "not found" means the server
                        // has no settings any more, and then all of them go back to the defaults together.
                        if (reply.StatusCode != System.Net.HttpStatusCode.NotFound)
                        {
                            if (!failNoted) { failNoted = true; RevivalMod.Log.Warning($"[settings] the server did not send its settings ({(int)reply.StatusCode}); keeping the last ones"); }
                            loaded = true;
                            return;
                        }
                        if (loaded && (turnSeconds != 0 || longGameEvent != null || rules.Length > 0))
                            RevivalMod.Log.Msg("[settings] the server no longer provides settings; using local values");
                        matchPort = 0;
                        reconnectSeconds = reconnectDrops = hiccupSeconds = 0;
                        turnSeconds = 0;
                        offenseCards = false;
                        offenceModes = null;
                        rules = "";
                        longGameEvent = null;
                        modeHands = new System.Collections.Generic.Dictionary<string, (int, int)>();
                        practiceClassicLife = 0;
                        creatorUrl = null;
                        MenuLayout.FromServer(null);
                        loaded = true;
                        return;
                    }
                    failNoted = false;
                    using var doc = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
                    int seconds = 0;
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("turnSeconds", out var t) && t.ValueKind == JsonValueKind.Number)
                        seconds = t.GetInt32();
                    string creator = null;
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("creatorUrl", out var cu) &&
                        cu.ValueKind == JsonValueKind.String)
                    {
                        string v = (cu.GetString() ?? "").Trim();
                        if (v.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) creator = v;
                    }
                    creatorUrl = creator;
                    string rv = "";
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("rulesVersion", out var rve) &&
                        (rve.ValueKind == JsonValueKind.String || rve.ValueKind == JsonValueKind.Number))
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (char ch in rve.ToString())
                            if (sb.Length < 16 && (char.IsLetterOrDigit(ch) || ch == '.' || ch == '-') && ch < 128) sb.Append(ch);
                        rv = sb.ToString();
                    }
                    if (rv != rules) RevivalMod.Log.Msg("[settings] server settings: rules version " + (rv.Length > 0 ? rv : "not set"));
                    rules = rv;
                    int port = 0;
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("matchPort", out var mp) && mp.ValueKind == JsonValueKind.Number)
                        port = mp.GetInt32();
                    matchPort = port > 0 && port < 65536 ? port : 0;
                    int rs = Clamped(doc.RootElement, "reconnectSeconds", 15, 180), rd = Clamped(doc.RootElement, "reconnectDrops", 1, 30),
                        hs = Clamped(doc.RootElement, "reconnectHiccupSeconds", 10, 30);
                    if (rs != reconnectSeconds || rd != reconnectDrops || hs != hiccupSeconds)
                        RevivalMod.Log.Msg($"[settings] server settings: reconnect window {(rs > 0 ? rs + " s" : "default")}, " +
                                           $"drops retried {(rd > 0 ? rd.ToString() : "default")}, connection timeout {(hs > 0 ? hs + " s" : "default")}");
                    reconnectSeconds = rs; reconnectDrops = rd; hiccupSeconds = hs;
                    loaded = true;
                    int life = 0;
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("practiceClassicWarlordLife", out var pl) && pl.ValueKind == JsonValueKind.Number)
                        life = pl.GetInt32();
                    if (life != practiceClassicLife) RevivalMod.Log.Msg($"[settings] server settings: practice on Classic adds {life} warlord health");
                    practiceClassicLife = life;
                    bool offence = false;
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("offenseCards", out var o) &&
                        (o.ValueKind == JsonValueKind.True || o.ValueKind == JsonValueKind.False))
                        offence = o.GetBoolean();
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("longGame", out var lg) && lg.ValueKind == JsonValueKind.Object)
                    {
                        string id = lg.TryGetProperty("eventId", out var le) ? le.GetString() : null;
                        if (lg.TryGetProperty("warlordHealth", out var lh) && lh.ValueKind == JsonValueKind.Number) longGameHealth = lh.GetDouble();
                        longGameHand = lg.TryGetProperty("startingHand", out var sh) && sh.ValueKind == JsonValueKind.Number ? sh.GetInt32() : 0;
                        longGameSecondExtra = lg.TryGetProperty("secondPlayerExtraCards", out var se) && se.ValueKind == JsonValueKind.Number ? se.GetInt32() : -1;
                        if (lg.TryGetProperty("copies", out var cp) && cp.ValueKind == JsonValueKind.Object)
                        {
                            string[] names = { "", "Common", "Rare", "Epic", "Legendary" };
                            for (int i = 1; i < names.Length; i++)
                                longGameCopies[i] = cp.TryGetProperty(names[i], out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0;
                        }
                        if (id != longGameEvent) RevivalMod.Log.Msg($"[settings] server settings: Long Game mode '{id}', warlord health x{longGameHealth}");
                        longGameEvent = id;
                    }
                    else longGameEvent = null;
                    var hands = new System.Collections.Generic.Dictionary<string, (int, int)>();
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("startingHands", out var shs) && shs.ValueKind == JsonValueKind.Object)
                        foreach (var m in shs.EnumerateObject())
                        {
                            if (m.Value.ValueKind != JsonValueKind.Object) continue;
                            int h = m.Value.TryGetProperty("startingHand", out var hv) && hv.ValueKind == JsonValueKind.Number ? hv.GetInt32() : -1;
                            int x = m.Value.TryGetProperty("secondPlayerExtraCards", out var xv) && xv.ValueKind == JsonValueKind.Number ? xv.GetInt32() : -1;
                            hands[m.Name] = (h, x);
                        }
                    if (hands.Count != modeHands.Count)
                        RevivalMod.Log.Msg($"[settings] server settings: starting hand set by {hands.Count} game mode(s)");
                    modeHands = hands;
                    if (offence != offenseCards) RevivalMod.Log.Msg($"[settings] server settings: Offence cards {(offence ? "on" : "off")} (used when the server has no per-mode switch)");
                    offenseCards = offence;
                    System.Collections.Generic.Dictionary<string, bool> om = null;
                    // the menus' inner ribbons (MenuLayout); without it the built-in layout is used
                    MenuLayout.FromServer(doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("menuLayout", out var ml) && ml.ValueKind == JsonValueKind.Object
                        ? ml.Clone() : (JsonElement?)null);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("offenseCardModes", out var oms) && oms.ValueKind == JsonValueKind.Object)
                    {
                        om = new System.Collections.Generic.Dictionary<string, bool>();
                        foreach (var m in oms.EnumerateObject())
                            if (m.Value.ValueKind == JsonValueKind.True || m.Value.ValueKind == JsonValueKind.False) om[m.Name] = m.Value.GetBoolean();
                    }
                    string Describe(System.Collections.Generic.Dictionary<string, bool> d) =>
                        d == null ? "none" : string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.OrderBy(d, kv => kv.Key), kv => kv.Key + (kv.Value ? " on" : " off")));
                    if (Describe(om) != Describe(offenceModes)) RevivalMod.Log.Msg("[settings] server settings: Offence cards per mode: " + Describe(om));
                    offenceModes = om;
                    // "testerModes": event ids of the modes the server hands only to testers' games (0.14.34: the
                    // server-deal test); they get a tile in the Testers window
                    var tm = new System.Collections.Generic.List<string>();
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("testerModes", out var tms) && tms.ValueKind == JsonValueKind.Array)
                        foreach (var m in tms.EnumerateArray())
                            if (m.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(m.GetString())) tm.Add(m.GetString());
                    if (string.Join(",", tm) != string.Join(",", testerModes)) RevivalMod.Log.Msg("[settings] server settings: tester modes: " + (tm.Count == 0 ? "none" : string.Join(", ", tm)));
                    testerModes = tm;
                    // "deckModes": {event id: play-mode number} for modes with their own deck list (LongGame.cs)
                    var dmodes = new System.Collections.Generic.Dictionary<string, int>();
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("deckModes", out var dmo) && dmo.ValueKind == JsonValueKind.Object)
                        foreach (var m in dmo.EnumerateObject())
                            if (m.Value.ValueKind == JsonValueKind.Number && m.Value.TryGetInt32(out int pm) && pm > 0) dmodes[m.Name] = pm;
                    if (string.Join(",", System.Linq.Enumerable.Select(System.Linq.Enumerable.OrderBy(dmodes, kv => kv.Key), kv => kv.Key + "=" + kv.Value)) !=
                        string.Join(",", System.Linq.Enumerable.Select(System.Linq.Enumerable.OrderBy(deckModes, kv => kv.Key), kv => kv.Key + "=" + kv.Value)))
                        RevivalMod.Log.Msg("[settings] server settings: modes with their own decks: " + (dmodes.Count == 0 ? "none" : string.Join(", ", System.Linq.Enumerable.Select(dmodes, kv => kv.Key + " (play mode " + kv.Value + ")"))));
                    deckModes = dmodes;
                    // "customCardModes": modes whose decks may hold custom cards (DeckRules.cs)
                    var ccm = new System.Collections.Generic.List<string>();
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("customCardModes", out var ccms) && ccms.ValueKind == JsonValueKind.Array)
                        foreach (var m in ccms.EnumerateArray())
                            if (m.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(m.GetString())) ccm.Add(m.GetString());
                    customCardModes = ccm;
                    // "dealModes": the modes that are server-deal tests (ServerDeal.cs: no AI opponent in them)
                    var dm = new System.Collections.Generic.List<string>();
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("dealModes", out var dms) && dms.ValueKind == JsonValueKind.Array)
                        foreach (var m in dms.EnumerateArray())
                            if (m.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(m.GetString())) dm.Add(m.GetString());
                    dealModes = dm;
                    // "noAiModes": modes whose search never falls back to the AI ("aiFallback": false on the server)
                    var na = new System.Collections.Generic.List<string>();
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("noAiModes", out var nas) && nas.ValueKind == JsonValueKind.Array)
                        foreach (var m in nas.EnumerateArray())
                            if (m.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(m.GetString())) na.Add(m.GetString());
                    if (string.Join(",", na) != string.Join(",", noAiModes)) RevivalMod.Log.Msg("[settings] server settings: no AI opponent in: " + (na.Count == 0 ? "no mode" : string.Join(", ", na)));
                    noAiModes = na;
                    // the pick is switched on or off for each match as it starts (MatchOptions), not here:
                    // a refresh during a match must not change it
                    if (seconds != turnSeconds) RevivalMod.Log.Msg($"[settings] server settings: turn timer {(seconds > 0 ? seconds + "s" : "not set")}");
                    turnSeconds = seconds;
                }
                catch (Exception e) { RevivalMod.Log.Warning("[settings] could not read server settings: " + e.Message); }
                finally { fetching = false; }
            });
        }
    }
}
