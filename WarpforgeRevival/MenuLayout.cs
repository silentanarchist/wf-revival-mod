using System;
using System.Collections.Generic;
using System.Text.Json;
using HarmonyLib;
using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WarpforgeRevival
{
    /// <summary>
    /// The inner ribbons of the game's tabbed windows (owner, 2026-10-16: "preset all ribbons so they are built
    /// correctly once"), from a layout the server can send ("menuLayout" in revival-settings.json) or, without one,
    /// the layout built in here.
    ///
    /// When it happens (decompiled 2026-10-16): GameWindowWithTabs.Open runs SetupTabs once per window, the first
    /// time it opens and before it is shown (Open: if (!initialized) { SetupTabs(); initialized = true; }
    /// GameWindow.Open()). SetupTabs only wires the buttons up (TabButtons.Initialize: toggle group, click handlers,
    /// the highlight); nothing in the game sets a tab button's label, icon, order or visibility after that, other
    /// than each label's translation component (I2 Localize) when the button is enabled, which is switched off on
    /// the labels changed here. So the layout is applied in a SetupTabs postfix and never again. It replaces the
    /// 0.25 s re-dressing in ChallengeMenu and TestersCollection (0.13.50 and earlier).
    ///
    /// While testing, a check once a second looks at the open windows: if anything has changed a ribbon set
    /// here, it is logged once (what and where) and put back.
    ///
    /// Layout format (each window: a list of buttons, each named by its tab's class):
    ///   "menuLayout": { "Social":  [ {"tab": "AlliancesTab", "label": "CHALLENGE", "icon": "play", "after": "FriendsTab", "visible": true} ],
    ///                   "Testers": [ {"tab": "CardbackCollectionTab", "label": "PLAY", "icon": "play", "position": 0}, ... ] }
    /// Windows: Social (the Social window), Testers (the mod's Testers window). Tabs: the tab's class name, or the
    /// name of a tab the mod adds ("Challenge"). Icons: "play" (the menu bar's Play button picture). Omitted fields
    /// are left as the game has them.
    /// </summary>
    internal static class MenuLayout
    {
        /// <summary>
        /// Takes the notification badges (the red count circles, UiBadgeNotification) off a copied button (0.13.70):
        /// a copy keeps the badge of the button it was copied from and so showed that button's count (the
        /// Challenge tab showed the alliance invitations; the Testers button showed Social's).
        /// </summary>
        internal static int StripBadges(GameObject root)
        {
            int n = 0;
            if ((object)root == null) return 0;
            foreach (var b in root.GetComponentsInChildren<UiBadgeNotification>(true))
            {
                if ((object)b == null) continue;
                if (b.gameObject.Pointer == root.Pointer) UnityEngine.Object.DestroyImmediate(b);
                else UnityEngine.Object.DestroyImmediate(b.gameObject);
                n++;
            }
            return n;
        }

        internal sealed class Button
        {
            public string Tab, Label, Icon, After;
            public bool? Visible;
            public int? Position;
        }

        private static readonly string[] Windows = { "Social", "Testers" };

        /// <summary>A button label as shown, in the player's language: the built-in labels are translated (the game's
        /// own word where it has one); any other label from the server is shown as written. The game shows these
        /// tabs in capitals whatever the case here.</summary>
        internal static string Shown(string label)
        {
            switch (label)
            {
                case "CHALLENGE": return Loc.T("menu.challenge", "Challenge");
                case "COMMUNITY": return Loc.T("menu.community", "Community");
                case "PLAY": return Loc.Game("MainMenu/NavigationPanel/HomeButton", "PLAY");
                case "DECKS": return Loc.T("menu.decks", "Decks");
                case "CARDS": return Loc.Game("MenuShop/ShopItemType/Cards", "Cards");
                default: return label;
            }
        }
        private static readonly string[] Icons = { "play", "collection", "social" };     // the menu bar's pictures

        // ---------------------------------------------------------------- the layout

        private static Dictionary<string, List<Button>> BuiltIn() => new Dictionary<string, List<Button>>
        {
            // the mod's Challenge tab (ChallengeMenu) directly below Friends; the game's Alliances tab hidden until
            // alliances work on the server (the server's layout can show it: {"tab": "AlliancesTab", "visible": true})
            ["Social"] = new List<Button>
            {
                new Button { Tab = "Challenge", Label = "CHALLENGE", Icon = "play", After = "FriendsTab", Visible = true },
                new Button { Tab = "Community", Label = "COMMUNITY", Icon = "collection", After = "Challenge", Visible = true },
                new Button { Tab = "AlliancesTab", Visible = false },
            },
            // the Testers window (a copy of the Collection window): Play, Decks, Cards; Styles hidden
            ["Testers"] = new List<Button>
            {
                new Button { Tab = "CardbackCollectionTab", Label = "PLAY", Icon = "play", Position = 0 },
                new Button { Tab = "SelectDecksTab", Label = "DECKS", Position = 1 },
                new Button { Tab = "CardCollectionTab", Label = "CARDS", Position = 2 },
                new Button { Tab = "AlternateArtCardCollectionTab", Visible = false },
            },
        };

        private static volatile Dictionary<string, List<Button>> layout = BuiltIn();
        private static volatile string source = "built in";
        private static string lastServerText;

        /// <summary>The server's "menuLayout" (ServerSettings, on its worker thread). Null: back to the built-in layout.</summary>
        internal static void FromServer(JsonElement? e)
        {
            try
            {
                if (e == null || e.Value.ValueKind != JsonValueKind.Object)
                {
                    if (lastServerText != null) RevivalMod.Log.Msg("[menu layout] the server has no menu layout; using the built-in one");
                    lastServerText = null;
                    layout = BuiltIn();
                    source = "built in";
                    return;
                }
                string text = e.Value.GetRawText();
                if (text == lastServerText) return;
                lastServerText = text;
                var parsed = new Dictionary<string, List<Button>>();
                foreach (var w in e.Value.EnumerateObject())
                {
                    if (Array.IndexOf(Windows, w.Name) < 0) { RevivalMod.Log.Warning($"[menu layout] unknown window '{w.Name}' ignored"); continue; }
                    if (w.Value.ValueKind != JsonValueKind.Array) continue;
                    var list = new List<Button>();
                    foreach (var b in w.Value.EnumerateArray())
                    {
                        if (list.Count >= 12 || b.ValueKind != JsonValueKind.Object) break;
                        var button = new Button
                        {
                            Tab = Str(b, "tab", 64),
                            Label = Str(b, "label", 24),
                            Icon = Str(b, "icon", 16),
                            After = Str(b, "after", 64),
                        };
                        if (b.TryGetProperty("visible", out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)) button.Visible = v.GetBoolean();
                        if (b.TryGetProperty("position", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int pos) && pos >= 0 && pos < 20) button.Position = pos;
                        if (string.IsNullOrEmpty(button.Tab)) continue;
                        if (button.Icon != null && Array.IndexOf(Icons, button.Icon) < 0) { RevivalMod.Log.Warning($"[menu layout] unknown icon '{button.Icon}' ignored"); button.Icon = null; }
                        list.Add(button);
                    }
                    parsed[w.Name] = list;
                }
                // windows the server leaves out keep the built-in layout; a mod tab the server's list does not
                // mention (a tab newer than the layout) keeps its built-in button, so it is not left unlabelled
                var all = BuiltIn();
                foreach (var kv in parsed)
                {
                    var merged = new List<Button>(kv.Value);
                    if (all.TryGetValue(kv.Key, out var builtIn))
                        foreach (var b in builtIn)
                            if ((b.Tab == "Challenge" || b.Tab == "Community") && !merged.Exists(x => x.Tab == b.Tab)) merged.Add(b);
                    all[kv.Key] = merged;
                }
                layout = all;
                source = "server";
                RevivalMod.Log.Msg($"[menu layout] the server's menu layout is used ({string.Join(", ", parsed.Keys)}); windows built from now on use it");
            }
            catch (Exception ex) { RevivalMod.Log.Warning("[menu layout] could not read the server's menu layout: " + ex.Message); }
        }

        private static string Str(JsonElement o, string name, int max)
        {
            if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
            var s = v.GetString();
            if (string.IsNullOrEmpty(s)) return null;
            return s.Length > max ? s.Substring(0, max) : s;
        }

        // ---------------------------------------------------------------- applying it, once per window


        /// <summary>Which layout a window takes, or null.</summary>
        private static string WindowKey(GameWindowWithTabs w)
        {
            if ((object)w.TryCast<SocialMenuWindow>() != null) return "Social";
            if (TestersCollection.IsCopy(w)) return "Testers";
            return null;
        }

        /// <summary>What a button was set to, for the check.</summary>
        private sealed class Applied
        {
            public string Window, Tab, Label;
            public Transform Button;
            public bool? Visible;
            public int? Index;
            public Sprite Icon;
            public Image IconImage;
            public List<TMP_Text> Texts = new List<TMP_Text>();
            public HashSet<string> Reported = new HashSet<string>();
        }

        private sealed class Watched
        {
            public GameWindowWithTabs Window;
            public List<Applied> Buttons = new List<Applied>();
        }

        private static readonly List<Watched> watched = new List<Watched>();

        // hooked through SharedHooks (one hook per game method): HarmonyPatch(typeof(GameWindowWithTabs), nameof(GameWindowWithTabs.SetupTabs))
        internal static class Built
        {
            internal static void Postfix(GameWindowWithTabs __instance)
            {
                try
                {
                    if ((object)__instance == null) return;
                    string key = WindowKey(__instance);
                    if (key == null) return;
                    // the window's own pages, set up once as it is built (no hooks of their own)
                    if (key == "Social") AllianceConfig.SocialBuilt(__instance);
                    var current = layout;
                    if (!current.TryGetValue(key, out var buttons) || buttons.Count == 0) return;
                    Apply(__instance, key, buttons);
                }
                catch (Exception e) { RevivalMod.Log.Warning("[menu layout] " + e.Message); }
            }
        }

        // tabs the mod adds (by pointer), named in the layout by these names instead of their class
        private static readonly Dictionary<IntPtr, string> custom = new Dictionary<IntPtr, string>();

        /// <summary>Names a tab the mod added (ChallengeMenu: "Challenge"), so layouts can place it.</summary>
        internal static void Name(WindowTabBase tab, string name)
        {
            if ((object)tab == null) return;
            var dead = new List<IntPtr>();
            foreach (var kv in custom) if (kv.Value == name) dead.Add(kv.Key);   // one live tab per name
            foreach (var k in dead) custom.Remove(k);
            custom[tab.Pointer] = name;
        }

        private static string TabName(WindowTabBase tab)
        {
            try { return custom.TryGetValue(tab.Pointer, out var n) ? n : tab.GetIl2CppType().Name; } catch { return null; }
        }

        private static TabButtons.TabButton Find(Il2CppSystem.Collections.Generic.List<TabButtons.TabButton> options, string tab)
        {
            for (int i = 0; i < options.Count; i++)
            {
                var b = options[i];
                if (b == null || (object)b.tab == null || (object)b.toggle == null) continue;
                if (TabName(b.tab) == tab) return b;
            }
            return null;
        }

        private static void Apply(GameWindowWithTabs w, string key, List<Button> buttons)
        {
            var options = w.Components?.TabButtons?.options;
            if (options == null) { RevivalMod.Log.Warning($"[menu layout] {key}: the window has no tab buttons"); return; }
            var record = new Watched { Window = w };
            var done = new List<string>();
            foreach (var cfg in buttons)
            {
                var b = Find(options, cfg.Tab);
                if (b == null) { RevivalMod.Log.Warning($"[menu layout] {key}: no '{cfg.Tab}' tab button"); continue; }
                var a = new Applied { Window = key, Tab = cfg.Tab, Button = b.toggle.transform, Visible = cfg.Visible, Label = cfg.Label };
                var go = b.toggle.gameObject;

                if (cfg.Visible.HasValue)
                {
                    // the game's own switch on the button (the Alliances button has one, key "Alliances", wired to
                    // SetActive when the button is enabled, i.e. after this): the layout decides instead
                    foreach (var fc in go.GetComponents<FeatureChecker>()) if (fc.enabled) fc.enabled = false;
                    if (go.activeSelf != cfg.Visible.Value) go.SetActive(cfg.Visible.Value);
                }

                if (cfg.Label != null)
                {
                    a.Texts = Labels(b.toggle);
                    foreach (var t in a.Texts)
                    {
                        foreach (var loc in t.GetComponents<Il2CppI2.Loc.Localize>()) if (loc.enabled) loc.enabled = false;
                        string shown = Shown(cfg.Label);
                        if (t.text != shown) t.text = shown;
                    }
                    if (a.Texts.Count == 0) RevivalMod.Log.Warning($"[menu layout] {key}: '{cfg.Tab}' has no label to change");
                }

                if (cfg.Icon != null)
                {
                    var sprite = Icon(cfg.Icon);
                    var iconT = a.Button.Find("Icon");
                    var image = (object)iconT == null ? null : iconT.GetComponent<Image>();
                    if ((object)sprite != null && (object)image != null)
                    {
                        image.sprite = sprite;
                        a.Icon = sprite; a.IconImage = image;
                        // a toggle that swaps its picture when picked would put its own back: give it ours
                        var toggle = b.toggle.TryCast<EverguildToggle>();
                        if ((object)toggle != null && toggle.changeSpriteOnValueChange && (object)toggle.spriteToChange != null && toggle.spriteToChange.Pointer == image.Pointer)
                        {
                            toggle.onSprite = sprite;
                            toggle.offSprite = sprite;
                        }
                    }
                    else RevivalMod.Log.Warning($"[menu layout] {key}: icon '{cfg.Icon}' for '{cfg.Tab}' not set ({((object)sprite == null ? "picture not found" : "the button has no icon")})");
                }

                record.Buttons.Add(a);
                done.Add(cfg.Tab);
            }
            // order last, once every button is in its final shown/hidden state
            foreach (var cfg in buttons)
            {
                var a = record.Buttons.Find(x => x.Tab == cfg.Tab);
                if (a == null) continue;
                int want = -1;
                if (cfg.Position.HasValue) want = Math.Min(cfg.Position.Value, a.Button.parent.childCount - 1);
                else if (cfg.After != null)
                {
                    var after = Find(options, cfg.After);
                    if (after == null) RevivalMod.Log.Warning($"[menu layout] {key}: no '{cfg.After}' tab button to place '{cfg.Tab}' after");
                    else if (after.toggle.transform.parent.Pointer == a.Button.parent.Pointer)
                    {
                        int at = after.toggle.transform.GetSiblingIndex();
                        want = a.Button.GetSiblingIndex() < at ? at : at + 1;
                    }
                }
                if (want >= 0 && a.Button.GetSiblingIndex() != want) a.Button.SetSiblingIndex(want);
            }
            foreach (var a in record.Buttons) a.Index = a.Button.GetSiblingIndex();
            watched.RemoveAll(x => !Ui.Alive(x.Window) || x.Window.Pointer == w.Pointer);
            watched.Add(record);
            RevivalMod.Log.Msg($"[menu layout] {key} ribbon set as the window was built ({source} layout): {string.Join(", ", done)}");
        }

        /// <summary>The button's label texts: those the game translates, else one named like a label.</summary>
        private static List<TMP_Text> Labels(Toggle toggle)
        {
            var list = new List<TMP_Text>();
            foreach (var t in toggle.GetComponentsInChildren<TMP_Text>(true))
                if ((object)t.GetComponent<Il2CppI2.Loc.Localize>() != null) list.Add(t);
            if (list.Count == 0)
                foreach (var t in toggle.GetComponentsInChildren<TMP_Text>(true))
                    if (t.gameObject.name.IndexOf("Label", StringComparison.OrdinalIgnoreCase) >= 0) { list.Add(t); break; }
            return list;
        }

        private static readonly Dictionary<string, Sprite> icons = new Dictionary<string, Sprite>();

        private static Sprite Icon(string name)
        {
            NavigationPanelToggleType which;
            switch (name)
            {
                case "play": which = NavigationPanelToggleType.Main; break;
                case "collection": which = NavigationPanelToggleType.Collection; break;
                case "social": which = NavigationPanelToggleType.Social; break;
                default: return null;
            }
            if (icons.TryGetValue(name, out var have) && Ui.Alive(have)) return have;
            Sprite sprite = null;
            try
            {
                var nav = GameState.FindNav();
                var toggle = (object)nav == null ? null : nav.GetToggle(which);
                var img = (object)toggle == null ? null : toggle.transform.Find("Image");
                var image = (object)img == null ? null : img.GetComponent<Image>();
                sprite = (object)image == null ? null : image.sprite;
            }
            catch { sprite = null; }
            if (Ui.Alive(sprite)) icons[name] = sprite;
            return Ui.Alive(sprite) ? sprite : null;
        }

        // ---------------------------------------------------------------- pages laid out per platform

        /// <summary>
        /// Settles the game's phone/PC layout of a page now, at set-up: each PlatformBasedComponents under it runs
        /// its layout (what its Start would do the first time the page is shown) and is then switched off, so it
        /// never runs again. Changes the mod makes afterwards then stay as made: before 0.13.55 that first Start
        /// undid them as the page appeared, and a check put them back 3 frames later (Settings > Mod and Account).
        /// Returns how many were settled.
        /// </summary>
        internal static int SettlePlatformLayout(Component root)
        {
            int n = 0;
            if ((object)root == null) return 0;
            foreach (var pbc in root.GetComponentsInChildren<PlatformBasedComponents>(true))
            {
                if ((object)pbc == null || !pbc.enabled) continue;
                try { pbc.Start(); }
                catch (Exception e) { RevivalMod.Log.Warning($"[menu layout] {pbc.gameObject.name}: phone/PC layout: {e.Message}"); }
                pbc.enabled = false;
                n++;
            }
            return n;
        }

        // ---------------------------------------------------------------- the check (testing)

        private static float nextCheck;

        /// <summary>Called every frame: once a second, while a window set here is open, checks its ribbon is unchanged.</summary>
        internal static void Tick()
        {
            if (watched.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextCheck) return;
            nextCheck = now + 1f;
            try
            {
                watched.RemoveAll(x => !Ui.Alive(x.Window));
                foreach (var win in watched)
                {
                    if (!win.Window.isActiveAndEnabled) continue;
                    foreach (var a in win.Buttons)
                    {
                        if (!Ui.Alive(a.Button)) continue;
                        var go = a.Button.gameObject;
                        if (a.Visible.HasValue && go.activeSelf != a.Visible.Value) { Drift(a, "shown/hidden", go.activeSelf + " (set " + a.Visible.Value + ")"); go.SetActive(a.Visible.Value); }
                        if (a.Label != null)
                        {
                            string shown = Shown(a.Label);
                            foreach (var t in a.Texts)
                                if (Ui.Alive(t) && t.text != shown) { Drift(a, "label", "'" + t.text + "'"); t.text = shown; }
                        }
                        if ((object)a.Icon != null && Ui.Alive(a.IconImage) && (object)a.IconImage.sprite != null && a.IconImage.sprite.Pointer != a.Icon.Pointer)
                        { Drift(a, "icon", a.IconImage.sprite.name); a.IconImage.sprite = a.Icon; }
                        if (a.Index.HasValue && a.Button.GetSiblingIndex() != a.Index.Value)
                        { Drift(a, "position", a.Button.GetSiblingIndex() + " (set " + a.Index.Value + ")"); a.Button.SetSiblingIndex(a.Index.Value); }
                    }
                }
            }
            catch (Exception e) { RevivalMod.Log.Warning("[menu layout] check: " + e.Message); nextCheck = now + 10f; }
        }

        private static void Drift(Applied a, string what, string now)
        {
            if (!a.Reported.Add(what)) return;
            RevivalMod.Log.Warning($"[menu layout] {a.Window}: something changed the '{a.Tab}' button's {what} after it was set (now {now}); put back. Please send this log.");
        }
    }
}
