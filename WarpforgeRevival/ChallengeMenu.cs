using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppEverguild.LiveOps;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WarpforgeRevival
{
    /// <summary>
    /// Social > Challenge: challenge a friend with rules of your choosing, on a tab of its own in the Social window
    /// (0.13.52; before, the unused Alliances tab was taken over, and the page was swapped in on a 0.25 s timer).
    ///
    /// How the tab is added (decompiled 2026-10-16): GameWindowWithTabs.SetupTabs runs once per window, before it is
    /// shown; it wires up every button in the ribbon's list (TabButtons.Initialize) and then sets up the tabs in the
    /// window's tab list. In a prefix the Alliances tab is copied (its class does nothing on its own: Start and
    /// Refresh, which load alliances, are blocked for the copy only) and a copy of its button is added to the ribbon
    /// with the game's own TabButtons.AssignTabButton, so the game wires its click like any other. In a postfix the
    /// copy is set up and added to the tab list after the game has filed its tabs by class, so the game's lookups
    /// (GetTab&lt;AlliancesTab&gt;) still find the real Alliances tab. From then on the game opens and closes the
    /// Challenge tab itself; the page lives inside it and is drawn when the tab is picked (the window's OnTabChanged
    /// event), in the same frame. The button's
    /// label, icon and place come from MenuLayout ("Challenge"). The real Alliances tab is the game's own again
    /// (hidden by MenuLayout until alliances work on the server).
    ///
    /// The page: a friend to challenge, a game mode as the preset (its deck rules, and the starting values of the
    /// match rules) and the match rules themselves. Sending goes through the game's own friend challenge, with the
    /// rules added (see ChallengeRules).
    ///
    /// 0.14.17: a player can also be picked by friend code (a box under the friend list; the server finds the player
    /// and lets the two games exchange the challenge although they are not friends), and the game's own duel popup
    /// (Classic / Skirmish on a profile or the alliance member list), whose challenge cannot carry the mode or any
    /// rules, now opens this page with that player and mode picked instead.
    /// </summary>
    internal static class ChallengeMenu
    {
        private const string PanelName = "RevivalChallengeMenu", TabName = "RevivalChallengeTab";
        private static SocialMenuWindow window => SocialTabs.Window;
        private static AlliancesTab tab => entry?.Tab;
        private static readonly SocialTabs.Entry entry = SocialTabs.Register(new SocialTabs.Entry
        {
            Name = "Challenge", TabName = TabName, ButtonName = "Challenge Tab Button",
            Build = (w, host) => Build(w, host),
        });
        private static bool builtLogged;

        // menu state, kept while the game runs
        private static string friendId, friendName;
        private static string codeId, codeName, codeState;    // the player found by friend code (shown under the list)
        private static TMP_InputField codeBox;
        private static string codeText = "";                  // what was typed, kept across redraws
        private static bool codeBusy;
        private static int presetIndex;
        private static ChallengeRules.RuleSet values;
        private static string status = "";
        private static bool dirty = true;


        /// <summary>Modes offered as presets: Classic and Skirmish (the two the game's own challenge can carry).</summary>
        private static List<string> Presets()
        {
            var ids = new List<string>();
            foreach (var id in new[] { "RevivalClassic", "RevivalSkirmish" })
                if ((object)ChallengeRules.FindEvent(id) != null) ids.Add(id);
            // Custom Test is left out for now: the friend's game picks its deck from the game's own Classic /
            // Skirmish choice, so a Custom Test challenge would pit a Custom Test deck against a Classic one.
            return ids;
        }

        private static string ModeName(string id)
        {
            // the two presets carry the game's own mode names (the server's event titles are English only)
            if (id == "RevivalClassic") return Loc.Game("MainMenu/GameMode/Classic", "Classic");
            if (id == "RevivalSkirmish") return Loc.Game("MainMenu/GameMode/Skirmish", "Skirmish");
            var ev = ChallengeRules.FindEvent(id);
            return (object)ev == null ? id : TestersPage.Label(ev.TryCast<LiveOpsEvent>(), EventLabelReferenceType.Title, id);
        }

        /// <summary>True for the Challenge tab (the copy), not the real Alliances tab.</summary>
        internal static bool IsChallengeTab(UnityEngine.Object t) => Ui.Alive(tab) && (object)t != null && t.Pointer == tab.Pointer;

        private static int drawnLoc;
        private static readonly List<RectTransform> scrollContents = new List<RectTransform>();
        private static int replaceBars;
        // the page is drawn afresh after every click, so each list's scroll position is kept and put back (0.14.19)
        private static readonly Dictionary<string, float> scrollAt = new Dictionary<string, float>();
        /// <summary>
        /// The page's text larger than first drawn (x1.7). A text that would not fit its place shrinks back, down to its
        /// first size at the least.
        /// </summary>
        private static void Enlarge(Transform panel)
        {
            const float scale = 1.7f;
            foreach (var t in panel.GetComponentsInChildren<TMP_Text>(true))
            {
                if (InFriendRow(t.transform)) continue;        // the game's own rows keep the game's text size
                float size = t.fontSize;
                t.enableAutoSizing = true;
                t.fontSizeMin = size;
                t.fontSizeMax = size * scale;
                t.fontSize = size * scale;
            }
        }

        private static bool InFriendRow(Transform t)
        {
            for (int i = 0; i < 6 && (object)t != null; i++, t = t.parent)
                if (t.name.StartsWith("Friend_", StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Called every frame: redraws the page after a click on it (picking a friend, a mode, a rule) or a change of language.</summary>
        internal static void Tick()
        {
            if (drawnLoc != Loc.Version) { drawnLoc = Loc.Version; dirty = true; }     // the language changed: draw it again in the new one
            if (openSince >= 0) FinishOpen();
            if (replaceBars > 0 && --replaceBars == 0)
                foreach (var c in scrollContents)
                {
                    if (!Ui.Alive(c)) continue;
                    Scrolling.Improve(c);
                    try
                    {
                        var sr = c.GetComponentInParent<ScrollRect>();       // the position again, now that the sizes are known
                        if ((object)sr != null && scrollAt.TryGetValue(sr.name, out float at)) sr.verticalNormalizedPosition = at;
                    }
                    catch { }
                }
            if (!dirty) return;
            try
            {
                if (!Ui.Alive(window) || !Ui.Alive(tab) || !tab.gameObject.activeInHierarchy) return;
                Build(window, tab.transform);
            }
            catch (Exception e) { RevivalMod.Log.Warning("[challenge] menu: " + e.Message); dirty = false; }
        }

        // ---------------------------------------------------------------- the tab

        // ---------------------------------------------------------------- the menu

        private static void Build(GameWindowWithTabs w, Transform host)
        {
            dirty = false;
            scrollContents.Clear();
            var old = host.Find(PanelName);
            if ((object)old != null)
            {
                try { foreach (var sr in old.GetComponentsInChildren<ScrollRect>(true)) scrollAt[sr.name] = sr.verticalNormalizedPosition; } catch { }
            }
            if ((object)old != null) { old.gameObject.SetActive(false); old.name = PanelName + " (old)"; UnityEngine.Object.Destroy(old.gameObject); }
            if ((object)TestersPage.font == null)
                try { var any = w.GetComponentInChildren<TMP_Text>(true); if ((object)any != null) TestersPage.font = any.font; } catch { }

            var presets = Presets();
            if (presets.Count == 0) return;
            presetIndex = Math.Clamp(presetIndex, 0, presets.Count - 1);
            string mode = presets[presetIndex];
            var ev = ChallengeRules.FindEvent(mode);
            if (values == null || values.EventId != mode) values = ChallengeRules.Preset(ev, mode);
            if (pendingDeal && AccountPage.IsTester) { values.Values["serverDeal"] = 1; pendingDeal = false; }     // from the Testers window

            var go = new GameObject(PanelName);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(host, false);
            rt.SetAsLastSibling();
            Place(w, rt);
            var area = go.transform;
            TestersPage.Box(area, "Back", new Color(0, 0, 0, 0.35f), 0, 0, 1, 1);

            // left: friends
            TestersPage.Text(area, "FriendsTitle", Loc.T("challenge.title", "Challenge a friend"), 34, TextAlignmentOptions.Left, Color.white, 0.02f, 0.9f, 0.34f, 0.99f);
            var friends = Friends();
            // the list scrolls on its own (0.14.17): rows of a fixed height inside a scroll area with a bar
            const float friendRow = 0.075f, friendGap = 0.01f;
            // under the list: the code box (with its label), or, once a player was found by code or picked from a profile,
            // that player's row in the box's place; the list takes the room that leaves
            float listBottom = codeId != null ? 0.15f : 0.2f;
            var list = ScrollArea(area, "FriendList", 0.02f, listBottom, 0.34f, 0.88f, Math.Max(1, friends.Count) * friendRow, out float listH);
            int row = 0;
            foreach (var (id, name, state) in friends)
            {
                float top = 1f - row * friendRow / listH, bottom = top - (friendRow - friendGap) / listH;
                string fid = id, fname = name;
                FriendRow(w, list, "Friend_" + id, name, state, id == friendId, 0f, bottom, 1f, top, () => { friendId = fid; friendName = fname; status = ""; dirty = true; });
                row++;
            }
            if (friends.Count == 0)
                TestersPage.Text(list, "NoFriends", Loc.T("challenge.noFriends", "Add friends in the Friends tab first."), 24, TextAlignmentOptions.Left, TestersPage.Dim, 0f, 0f, 1f, 1f);
            CodeBox(w, area);

            // right: mode and rules
            float x0 = 0.38f, x1 = 0.98f;
            TestersPage.Text(area, "ModeTitle", Loc.T("challenge.mode", "Mode"), 26, TextAlignmentOptions.Left, TestersPage.Dim, x0, 0.92f, 0.5f, 0.99f);
            var prev = TestersPage.Button(area, "ModePrev", "<", 0.5f, 0.92f, 0.55f, 0.99f, TestersPage.Tile);
            TestersPage.Text(area, "Mode", ModeName(mode), 30, TextAlignmentOptions.Center, Color.white, 0.55f, 0.92f, 0.85f, 0.99f);
            var next = TestersPage.Button(area, "ModeNext", ">", 0.85f, 0.92f, 0.9f, 0.99f, TestersPage.Tile);
            TestersPage.OnClick(prev, () => { presetIndex = (presetIndex + presets.Count - 1) % presets.Count; values = null; dirty = true; });
            TestersPage.OnClick(next, () => { presetIndex = (presetIndex + 1) % presets.Count; values = null; dirty = true; });
            TestersPage.Text(area, "DeckRules", ChallengeRules.DeckRules(ev, mode) + " " + Loc.T("challenge.yourDeck", "You play your deck for this mode."), 20, TextAlignmentOptions.Left, TestersPage.Dim, x0, 0.86f, x1, 0.92f);

            var preset = ChallengeRules.Preset(ev, mode);
            // the rules scroll on their own (0.14.17), so more can be added and the text is larger; the buttons stay at the bottom
            const float ruleRow = 0.066f;
            var shownFields = new List<ChallengeRules.Field>();
            foreach (var f in ChallengeRules.Fields)
                if (!ChallengeRules.TestersOnly(f.Key) || (ShowTesterRules && AccountPage.IsTester)) shownFields.Add(f);
            var rules = ScrollArea(area, "RuleList", x0, 0.17f, 0.96f, 0.85f, shownFields.Count * ruleRow, out float rulesH);
            int r = 0;
            rows.Clear();
            foreach (var f in shownFields)
            {
                float top = 1f - r * ruleRow / rulesH, bottom = top - ruleRow / rulesH, pad = 0.004f / rulesH;
                var rr = new RuleRow { Field = f };
                rr.Label = TestersPage.Text(rules, "L_" + f.Key, f.Text(), 26, TextAlignmentOptions.Left, Color.white, 0.01f, bottom, 0.6f, top);
                rr.Value = TestersPage.Text(rules, "V_" + f.Key, "", 27, TextAlignmentOptions.Center, Color.white, 0.7f, bottom, 0.87f, top);
                var field = f;
                // the buttons are always there; one that would change nothing (at the lowest or highest value) is hidden
                rr.Minus = TestersPage.Button(rules, "M_" + f.Key, "-", 0.61f, bottom + pad, 0.69f, top - pad, TestersPage.Tile);
                TestersPage.OnClick(rr.Minus, () => Step(field, -1));
                rr.Plus = TestersPage.Button(rules, "P_" + f.Key, "+", 0.88f, bottom + pad, 0.96f, top - pad, TestersPage.Tile);
                TestersPage.OnClick(rr.Plus, () => Step(field, +1));
                rows.Add(rr);
                r++;
            }
            RefreshRows(preset);

            const float by = 0.1f;
            var reset = TestersPage.Button(area, "Reset", Loc.T("challenge.reset", "Mode's own rules"), x0, by - 0.08f, 0.62f, by, TestersPage.Tile);
            TestersPage.OnClick(reset, () =>
            {
                status = "";
                try { values = ChallengeRules.Preset(ev, mode); RefreshRows(ChallengeRules.Preset(ev, mode)); }
                catch { values = null; dirty = true; }
                var st = area.Find("Status"); if ((object)st != null) st.gameObject.SetActive(false);
            });
            var send = TestersPage.Button(area, "Send", friendId == null ? Loc.T("challenge.pickFriend", "Pick a friend") : Loc.T("challenge.send", "Challenge {name}", ("name", friendName)), 0.64f, by - 0.08f, x1, by,
                                          friendId == null ? TestersPage.Tile : new Color(0.45f, 0.12f, 0.08f, 1f));
            TestersPage.OnClick(send, () => Send(mode, ev));
            if (!string.IsNullOrEmpty(status))
                TestersPage.Text(area, "Status", status, 22, TextAlignmentOptions.Left, TestersPage.Dim, x0, by + 0.01f, x1, 0.16f);
            Enlarge(go.transform);
            if (!builtLogged) { builtLogged = true; RevivalMod.Log.Msg($"[challenge] menu built: {friends.Count} friend(s), presets {string.Join(", ", presets)}"); }
        }

        /// <summary>
        /// A scroll area (wheel, drag, and a bar down its right edge) at the given place in the panel. Its content is
        /// contentFrac of the panel's height tall (at least the area itself); rows are placed in it with anchors as
        /// fractions of the content height, which is returned in contentH (in the same panel-height units).
        /// </summary>
        private static Transform ScrollArea(Transform panel, string name, float x0, float y0, float x1, float y1, float contentFrac, out float contentH)
        {
            float viewH = y1 - y0;
            contentH = Math.Max(viewH, contentFrac);
            var view = TestersPage.Box(panel, name, new Color(0f, 0f, 0f, 0.18f), x0, y0, x1, y1);
            view.gameObject.AddComponent<RectMask2D>();
            var scroll = view.gameObject.AddComponent<ScrollRect>();
            var content = new GameObject("Content");
            var crt = content.AddComponent<RectTransform>();
            crt.SetParent(view, false);
            crt.anchorMin = new Vector2(0f, 1f); crt.anchorMax = new Vector2(1f, 1f); crt.pivot = new Vector2(0.5f, 1f);
            float panelH = panel.GetComponent<RectTransform>().rect.height;
            crt.sizeDelta = new Vector2(0f, panelH * contentH);
            crt.anchoredPosition = Vector2.zero;
            scroll.content = crt;
            scroll.viewport = view.GetComponent<RectTransform>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 60f;
            if (scrollAt.TryGetValue(name, out float at)) scroll.verticalNormalizedPosition = at;
            if (contentH > viewH + 0.0001f)
            {
                // the bar sits just outside the area's right edge (Scrolling attaches it to the panel around the list);
                // placed again next frame, once the new objects have their sizes
                Scrolling.Improve(crt);
                scrollContents.Add(crt);
                replaceBars = 2;
            }
            return crt;
        }

        /// <summary>What one press of - (dir -1) or + (dir +1) would set; the same value when the press would do nothing.</summary>
        private static int Next(ChallengeRules.Field f, int dir)
        {
            int v = values?.Get(f.Key) ?? f.Min;
            int next = Math.Clamp(v + dir * f.Step, f.Min, f.Max);
            // the starting hand cannot go past the hand limit (nor the second player's hand with its extra cards)
            if (f.Key == "startingHand" && next > (values?.Get("handLimit") ?? 99)) return v;
            if (f.Key == "secondPlayerExtraCards" && (values?.Get("startingHand") ?? 0) + next > (values?.Get("handLimit") ?? 99)) return v;
            return next;
        }

        private sealed class RuleRow { public ChallengeRules.Field Field; public TMP_Text Label, Value; public Button Minus, Plus; }
        private static readonly List<RuleRow> rows = new List<RuleRow>();

        /// <summary>The rule rows brought up to date in place: value, the changed-from-preset colour, which buttons show.
        /// No redraw, so the lists keep their scroll (0.14.20).</summary>
        private static void RefreshRows(ChallengeRules.RuleSet preset)
        {
            foreach (var row in rows)
            {
                try
                {
                    if (!Ui.Alive(row.Value)) continue;
                    int v = values.Get(row.Field.Key) ?? row.Field.Min;
                    bool changed = preset.Get(row.Field.Key) != v;
                    row.Value.text = ChallengeRules.Show(row.Field.Key, v);
                    row.Label.color = changed ? new Color(1f, 0.85f, 0.45f, 1f) : Color.white;
                    row.Minus.gameObject.SetActive(Next(row.Field, -1) != v);
                    row.Plus.gameObject.SetActive(Next(row.Field, +1) != v);
                }
                catch (Exception e) { RevivalMod.Log.Warning("[challenge] row: " + e.Message); }
            }
        }

        private static void Step(ChallengeRules.Field f, int dir)
        {
            if (values == null) return;
            int v = values.Get(f.Key) ?? f.Min;
            int next = Next(f, dir);
            if (next == v) return;
            values.Values[f.Key] = next;
            ChallengeRules.CapHand(values.Values);       // a lower hand limit pulls the starting hand down with it
            try
            {
                var ev = ChallengeRules.FindEvent(values.EventId);
                RefreshRows(ChallengeRules.Preset(ev, values.EventId));
            }
            catch (Exception e) { RevivalMod.Log.Warning("[challenge] " + e.Message); dirty = true; }
        }

        private static void Send(string mode, IPlayEvent ev)
        {
            try
            {
                if (friendId == null) { status = Loc.T("challenge.pickFriendFirst", "Pick a friend on the left first."); dirty = true; return; }
                var manager = UnityEngine.Object.FindObjectOfType<ChallengeManager>();
                if ((object)manager == null) { status = Loc.T("challenge.unavailable", "Challenges are not available right now."); dirty = true; return; }
                var rules = new ChallengeRules.RuleSet { EventId = mode, Values = new Dictionary<string, int>(values.Values) };
                ChallengeRules.Outgoing(friendId, rules);
                // who goes first: the rules' choice (1 the challenger, 2 the friend), otherwise a coin toss as before;
                // MatchOptions makes sure of it on both sides once the match starts
                int fp = rules.Get("firstPlayer") ?? 0;
                bool first = fp == 1 ? true : fp == 2 ? false : UnityEngine.Random.value < 0.5f;
                RevivalMod.Log.Msg($"[challenge] challenging {friendId} in {mode}");
                manager.StartChallengeWithUser(friendName ?? friendId, friendId, first, ev);
                status = Loc.T("challenge.sent", "Challenge sent to {name}. They see your rules when it arrives.", ("name", friendName));
                dirty = true;
            }
            catch (Exception e) { status = Loc.T("challenge.sendFailed", "Could not send the challenge: {error}", ("error", e.Message)); dirty = true; RevivalMod.Log.Warning("[challenge] send: " + e); }
        }

        private static FriendUIElementWarpforge rowPrefab;

        /// <summary>
        /// A player row drawn like the Friends tab's own (a copy of its entry without the three buttons): the status
        /// light, the name, and the whole row as a button. Picked rows are tinted. Falls back to a plain tile when the
        /// Friends tab's entry cannot be found.
        /// </summary>
        private static void FriendRow(GameWindowWithTabs w, Transform parent, string objName, string name, string state, bool picked,
                                      float x0, float y0, float x1, float y1, Action onClick)
        {
            try
            {
                if (!Ui.Alive(rowPrefab))
                {
                    rowPrefab = null;
                    foreach (var f in w.GetComponentsInChildren<FriendsTab>(true)) { rowPrefab = f.friendElementPrefab; break; }
                }
                if ((object)rowPrefab == null) throw new Exception("no friend entry to copy");
                var go = UnityEngine.Object.Instantiate(rowPrefab.gameObject, parent, false);
                go.name = objName;
                foreach (var le in go.GetComponents<LayoutElement>()) le.ignoreLayout = true;
                var rt = go.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(x0, y0); rt.anchorMax = new Vector2(x1, y1);
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                rt.localScale = Vector3.one;
                var entry = go.GetComponent<FriendUIElementWarpforge>();
                var label = entry.userLabel;
                var on = entry.connectedImage; var off = entry.notConnected;
                Color onColor = entry.connectedColor, away = entry.awayColor;
                foreach (var b in new[] { entry.challengeButton, entry.deleteUserButton, entry.showProfileButton })
                    if ((object)b != null) b.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(entry);
                if ((object)label != null)
                {
                    Ui.SetText(label, name);
                    // one line: a long name shrinks a little rather than wrapping, then is cut with ...
                    label.enableWordWrapping = false;
                    label.overflowMode = TextOverflowModes.Ellipsis;
                    float size = label.fontSize;
                    label.enableAutoSizing = true;
                    label.fontSizeMax = size;
                    label.fontSizeMin = size * 0.7f;
                    // the label's room was laid out next to the three buttons: let it run to the row's right edge instead
                    try
                    {
                        var lrt = label.GetComponent<RectTransform>();
                        var rowRt = go.GetComponent<RectTransform>();
                        var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
                        lrt.GetWorldCorners(corners);
                        float left = rowRt.InverseTransformPoint(corners[0]).x - rowRt.rect.xMin;     // keep the gap for the status light
                        foreach (var le in label.GetComponents<LayoutElement>()) le.ignoreLayout = true;
                        lrt.SetParent(go.transform, false);
                        lrt.anchorMin = new Vector2(0f, 0f); lrt.anchorMax = new Vector2(1f, 1f); lrt.pivot = new Vector2(0f, 0.5f);
                        lrt.offsetMin = new Vector2(Mathf.Max(0f, left) + 10f, 0f); lrt.offsetMax = new Vector2(-16f, 0f);   // a little air after the light
                        lrt.localScale = Vector3.one;
                        label.alignment = TextAlignmentOptions.Left;
                    }
                    catch (Exception e) { RevivalMod.Log.Warning("[challenge] row label: " + e.Message); }
                }
                bool offline = state != "online" && state != "playing";       // FriendStatus gives null for offline
                if ((object)on != null) { on.gameObject.SetActive(!offline); on.color = state == "playing" ? away : onColor; }
                if ((object)off != null) off.gameObject.SetActive(offline);
                // the row itself is the button; picked, its background and border pieces are tinted gold
                var bg = go.GetComponent<Image>();
                var button = go.GetComponent<Button>() ?? go.AddComponent<Button>();
                button.onClick = new Button.ButtonClickedEvent();
                button.interactable = true;
                if ((object)bg != null) button.targetGraphic = bg;
                if (picked)
                {
                    var gold = new Color(0.95f, 0.7f, 0.3f, 1f);
                    foreach (var img in go.GetComponentsInChildren<Image>(true))
                    {
                        if ((object)img == null || ((object)on != null && img.Pointer == on.Pointer) || ((object)off != null && img.Pointer == off.Pointer)) continue;
                        img.color = new Color(gold.r, gold.g, gold.b, img.color.a);
                    }
                }
                // clicks: the entry's own pieces may not catch them (0.14.25: a click did nothing), so a clear
                // full-size layer on top does, and nothing in the copy may block it
                foreach (var cg in go.GetComponentsInChildren<CanvasGroup>(true)) { cg.blocksRaycasts = true; cg.interactable = true; }
                var hit = TestersPage.Box(go.transform, "Hit", new Color(0f, 0f, 0f, 0f), 0, 0, 1, 1);
                hit.SetAsLastSibling();
                if ((object)bg == null) button.targetGraphic = hit.GetComponent<Image>();
                TestersPage.OnClick(button, onClick);
                go.SetActive(true);
            }
            catch (Exception e)
            {
                if (!rowFallbackLogged) { rowFallbackLogged = true; RevivalMod.Log.Warning("[challenge] friend rows drawn plainly: " + e.Message); }
                string hint = "  (" + (state == "online" ? Loc.T("challenge.online", "online") : state == "playing" ? Loc.T("challenge.playing", "in a match") : Loc.Game("MainMenu/General/Offline", "Offline")) + ")";
                var b = TestersPage.Button(parent, objName, "", x0, y0, x1, y1, picked ? new Color(0.35f, 0.25f, 0.08f, 1f) : TestersPage.Tile);
                TestersPage.Text(b.transform, "Name", name + hint, 24, TextAlignmentOptions.Left, state == "online" ? Color.white : TestersPage.Dim, 0.04f, 0, 0.98f, 1);
                TestersPage.OnClick(b, onClick);
            }
        }
        private static bool rowFallbackLogged;

        /// <summary>Under the friend list: the player found by friend code (if any), a box for a code and a Find button.</summary>
        private static void CodeBox(GameWindowWithTabs w, Transform area)
        {
            try
            {
                const float top = 0.19f;        // label 0.14-0.19, box 0.07-0.13; or the found player's row at 0.06-0.13
                if (codeId != null)
                {
                    FriendRow(w, area, "Friend_code", codeName, codeState, codeId == friendId, 0.02f, 0.06f, 0.29f, 0.13f, () => { friendId = codeId; friendName = codeName; status = ""; dirty = true; });
                    // the x puts the code box back
                    var clear = TestersPage.Button(area, "ClearCode", "x", 0.3f, 0.06f, 0.34f, 0.13f, TestersPage.Tile);
                    TestersPage.OnClick(clear, () => { if (friendId == codeId) { friendId = null; friendName = null; } codeId = codeName = codeState = null; codeText = ""; status = ""; dirty = true; });
                    return;
                }
                TestersPage.Text(area, "CodeTitle", Loc.T("challenge.byCode", "Or by friend code"), 24, TextAlignmentOptions.Left, TestersPage.Dim, 0.02f, top - 0.05f, 0.34f, top);
                // the box: a copy of the Friends tab's add-friend box (the game's own look and keyboard handling)
                FriendsTab friends = null;
                foreach (var f in w.GetComponentsInChildren<FriendsTab>(true)) { friends = f; break; }
                var input = (object)friends == null ? null : friends.inputFieldUserName;
                if ((object)input == null) return;
                var boxGo = UnityEngine.Object.Instantiate(input.gameObject, area, false);
                boxGo.name = "Friend code box";
                foreach (var le in boxGo.GetComponents<LayoutElement>()) le.ignoreLayout = true;
                var rt = boxGo.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.02f, 0.07f); rt.anchorMax = new Vector2(0.24f, 0.13f);
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                codeBox = boxGo.GetComponent<TMP_InputField>();
                if ((object)codeBox != null)
                {
                    codeBox.onSubmit = new TMP_InputField.SubmitEvent();
                    codeBox.onEndEdit = new TMP_InputField.SubmitEvent();
                    codeBox.onValueChanged = new TMP_InputField.OnChangeEvent();
                    codeBox.characterLimit = 24;
                    codeBox.SetTextWithoutNotify(codeText ?? "");
                    var ph = codeBox.placeholder?.TryCast<TMP_Text>();
                    if ((object)ph != null) Ui.SetText(ph, Loc.T("challenge.codePlaceholder", "Friend code"));
                    codeBox.onValueChanged.AddListener(Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction<string>>(new Action<string>(t => codeText = t)));
                    codeBox.onSubmit.AddListener(Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction<string>>(new Action<string>(_ => FindByCode())));
                }
                boxGo.SetActive(true);
                var find = TestersPage.Button(area, "FindCode", codeBusy ? "..." : Loc.T("challenge.find", "Find"), 0.25f, 0.07f, 0.34f, 0.13f, TestersPage.Tile);
                TestersPage.OnClick(find, FindByCode);
            }
            catch (Exception e) { RevivalMod.Log.Warning("[challenge] friend code box: " + e.Message); }
        }

        private static void FindByCode()
        {
            string code = (codeText ?? "").Trim();
            if (codeBusy) return;
            if (code.Length == 0) { status = Loc.T("challenge.typeCode", "Type a friend code first."); dirty = true; return; }
            codeBusy = true;
            status = ""; dirty = true;
            LookUp(code);
        }

        /// <summary>Asks the server for the player behind a friend code, name or id; the answer picks them.</summary>
        private static void LookUp(string code, bool quiet = false)
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                string id = null, name = null, state = null, error = null;
                try
                {
                    var reply = await Net.PostJson("/playfab/Revival/ChallengeByCode", Net.Json(w => w.WriteString("code", code)), TimeSpan.FromSeconds(10));
                    var d = reply.Data;
                    if (reply.Ok && d.ValueKind == System.Text.Json.JsonValueKind.Object && d.TryGetProperty("id", out var i))
                    {
                        id = i.GetString();
                        name = d.TryGetProperty("name", out var n) ? n.GetString() : id;
                        state = d.TryGetProperty("state", out var st) ? st.GetString() : "offline";
                    }
                    else error = reply.Error ?? Loc.T("challenge.noSuchCode", "No player with that friend code.");
                }
                catch (Exception e) { error = e.Message; }
                PlayFabTransport.OnMainThread(() =>
                {
                    codeBusy = false;
                    if (id != null)
                    {
                        codeId = id; codeName = name; codeState = state;
                        friendId = id; friendName = name;
                        RevivalMod.Log.Msg($"[challenge] friend code found: {id} ({state})");
                    }
                    else if (quiet) RevivalMod.Log.Msg("[challenge] look-up of " + code + ": " + error);
                    else status = error;
                    dirty = true;
                });
            });
        }

        /// <summary>
        /// The game's own duel popup (Classic / Skirmish on a profile or an alliance member): its challenge can carry
        /// neither the mode nor any rules (the friend's game plays it as a default challenge), so it opens this page
        /// instead, with that player and mode picked.
        /// </summary>
        private static float openSince = -1f;
        private static bool? pendingSkirmish;
        private static bool pendingDeal;
        // the tester-only rules (the server-deal test) are off the page for now (owner, 2026-10-21): the test runs as
        // its own mode in the Testers window; the rule stays in the rule set so every game reads the same fields
        private const bool ShowTesterRules = false;

        /// <summary>From the Testers window: the Challenge page with the Classic preset and the server-deal rule on.</summary>
        internal static void OpenForServerDeal()
        {
            pendingSkirmish = false;
            pendingDeal = true;
            values = null;
            status = ""; dirty = true;
            RevivalMod.Log.Msg("[challenge] opening the Challenge page for a server-deal test");
            OpenPage();
        }

        private static void FromDuelPopup(DuelPopupWindow popup, bool? skirmish)
        {
            try
            {
                var data = popup.playerDuelData;
                string id = (object)data == null ? null : data.PlayerId, name = (object)data == null ? null : data.PlayerName;
                if (string.IsNullOrEmpty(id)) { RevivalMod.Log.Warning("[challenge] duel popup without a player"); return; }
                codeId = friendId = id; codeName = friendName = string.IsNullOrEmpty(name) ? id : name; codeState = GlobalChat.FriendStatus(id) ?? "offline";
                LookUp(id, quiet: true);      // the server says whether they are on (FriendStatus only knows friends)
                pendingSkirmish = skirmish;
                status = ""; dirty = true;
                try { popup.OnCloseButtonPressed(); } catch { try { popup.Close(); } catch { } }
                RevivalMod.Log.Msg($"[challenge] duel popup skipped: opening the Challenge page for {id}" + (skirmish == null ? "" : skirmish.Value ? " (Skirmish)" : " (Classic)"));
                OpenPage();
            }
            catch (Exception e) { RevivalMod.Log.Warning("[challenge] duel popup: " + e.Message); }
        }

        /// <summary>The popup is never shown: as the game opens it (the Challenge button on a profile or an alliance
        /// member), it is closed again and the Challenge page opens with that player picked and the mode as it was.</summary>
        // hooked through SharedHooks (one hook per game method): HarmonyPatch(typeof(DuelPopupWindow), nameof(DuelPopupWindow.Open))
        internal static class DuelOpened { internal static void Postfix(DuelPopupWindow __instance) => FromDuelPopup(__instance, null); }

        private static void OpenPage()
        {
            openSince = Time.realtimeSinceStartup;
            try
            {
                if (Ui.Alive(window) && window.IsOpen()) { FinishOpen(); return; }
                var wm = WindowsManager.Instance;
                SocialMenuWindow social = null;
                foreach (var m in Resources.FindObjectsOfTypeAll<SocialMenuWindow>())
                    if ((object)m != null && m.gameObject.scene.IsValid()) { social = m; break; }
                if ((object)social != null) { wm.OpenWindow(social, false, null); return; }
                // not opened yet this session (the menu loads it from a prefab when its button is pressed): press the
                // menu's own Social button - the one whose window is the Social window, or else the one named for it
                OpenWindowButton named = null;
                string all = "";
                foreach (var b in UnityEngine.Object.FindObjectsOfType<OpenWindowButton>())
                {
                    var scene = b.windowToOpenScene;
                    if ((object)scene != null && (object)scene.TryCast<SocialMenuWindow>() != null) { b.OpenWindow(); return; }
                    string path = RecoveryReminder.Path(b.transform);
                    all += " | " + path;
                    if (named == null && path.IndexOf("social", StringComparison.OrdinalIgnoreCase) >= 0) named = b;
                }
                if (named != null) { RevivalMod.Log.Msg("[challenge] pressing the Social button " + RecoveryReminder.Path(named.transform)); named.OpenWindow(); return; }
                RevivalMod.Log.Warning("[challenge] the Social window was not found; window buttons:" + all);
                openSince = -1f;
            }
            catch (Exception e) { RevivalMod.Log.Warning("[challenge] could not open the Social window: " + e.Message); openSince = -1f; }
        }

        /// <summary>Once the Social window is open: switch to the Challenge tab with the picked mode.</summary>
        private static void FinishOpen()
        {
            if (openSince < 0) return;
            if (Ui.Alive(window) && window.IsOpen() && Ui.Alive(tab))
            {
                openSince = -1f;
                var presets = Presets();
                int want = pendingSkirmish == null ? -1 : presets.IndexOf(pendingSkirmish.Value ? "RevivalSkirmish" : "RevivalClassic");
                if (want >= 0 && want != presetIndex) { presetIndex = want; values = null; }
                dirty = true;
                try { window.ChangeTab(tab); } catch (Exception e) { RevivalMod.Log.Warning("[challenge] could not switch to the Challenge tab: " + e.Message); }
                return;
            }
            if (Time.realtimeSinceStartup - openSince > 5f) { openSince = -1f; RevivalMod.Log.Warning("[challenge] the Social window did not open"); }
        }

        [HarmonyPatch(typeof(DuelPopupWindow), nameof(DuelPopupWindow.OnClassicButtonPressed))]
        private static class DuelClassic { private static bool Prefix(DuelPopupWindow __instance) { FromDuelPopup(__instance, false); return false; } }

        [HarmonyPatch(typeof(DuelPopupWindow), nameof(DuelPopupWindow.OnSkirmishButtonPressed))]
        private static class DuelSkirmish { private static bool Prefix(DuelPopupWindow __instance) { FromDuelPopup(__instance, true); return false; } }

        private static List<(string id, string name, string state)> Friends()
        {
            var list = new List<(string, string, string)>();
            try
            {
                var fl = PlayerDataManager.singletonManager?.friendsData?.friendList;
                if (fl != null)
                    for (int i = 0; i < fl.Count; i++)
                    {
                        var f = fl[i];
                        if ((object)f == null || string.IsNullOrEmpty(f.id)) continue;
                        list.Add((f.id, string.IsNullOrEmpty(f.name) ? f.id : f.name, GlobalChat.FriendStatus(f.id)));
                    }
            }
            catch { }
            int Rank(string s) => s == "online" ? 0 : s == "playing" ? 1 : 2;
            list.Sort((a, b) => Rank(a.Item3) != Rank(b.Item3) ? Rank(a.Item3).CompareTo(Rank(b.Item3)) : string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        /// <summary>The menu's area: right of the inner ribbon, below the window's top bar (worked out in the
        /// window's space, then set wherever the page sits, here inside the Challenge tab).</summary>
        private static void Place(GameWindowWithTabs w, RectTransform panel)
        {
            var parent = w.GetComponent<RectTransform>();
            if ((object)parent == null) return;
            var pr = parent.rect;
            float left = pr.xMin + 360, right = pr.xMax - 60, top = pr.yMax - pr.height * 0.09f, bottom = pr.yMin + pr.height * 0.015f;   // 0.14.7: taller, for the larger text
            try
            {
                var rib = w.Components?.TabButtons?.GetComponent<RectTransform>();
                if ((object)rib != null)
                {
                    var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
                    rib.GetWorldCorners(corners);
                    left = parent.InverseTransformPoint(corners[2]).x + 28;
                }
            }
            catch { }
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
            // size in the window's units, scaled to the page's own parent (the same canvas: normally 1)
            float scale = 1f;
            var host = panel.parent;
            if ((object)host != null && host.lossyScale.x > 0.0001f && parent.lossyScale.x > 0.0001f) scale = parent.lossyScale.x / host.lossyScale.x;
            panel.sizeDelta = new Vector2(Mathf.Max(10, right - left), Mathf.Max(10, top - bottom)) * scale;
            panel.position = parent.TransformPoint(new Vector3((left + right) / 2, (top + bottom) / 2, 0));
        }

        // The copy's own alliance loading is blocked; the real Alliances tab runs as the game made it.
        // (ToFocus is not patched: in this build it is a single jump into Refresh, and a hook on a 4-byte function
        // is the one thing that made the Android loader place code at an address of its own choosing.)

        [HarmonyPatch(typeof(AlliancesTab), nameof(AlliancesTab.Start))]
        private static class NoAllianceStart { private static bool Prefix(AlliancesTab __instance) => !SocialTabs.IsCopy(__instance); }

        [HarmonyPatch(typeof(AlliancesTab), nameof(AlliancesTab.Refresh))]
        private static class NoAllianceRefresh
        {
            // the real tab: AllianceConfig.ShouldRefresh breaks the game's refresh loop (0.13.60)
            private static bool Prefix(AlliancesTab __instance) => !SocialTabs.IsCopy(__instance) && AllianceConfig.ShouldRefresh();
        }
    }
}
