using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppEverguild.LiveOps;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.UI;

namespace WarpforgeRevival
{
    /// <summary>
    /// The Testers window, behind the Testers button in the menu bar. It is a separate copy of the
    /// game's Collection window (made from the same window file the Collection button opens, with
    /// its own loading handle, so the game's Collection is never touched), with its inner ribbon set
    /// up for testers:
    ///  - Play: each tester-only mode's own Play-screen tile (for now Custom Test), made the way the
    ///    Play screen makes them; clicking one opens the mode's page. (The copy's Cosmetics tab, with
    ///    its content swapped for these tiles.)
    ///  - Decks: the copy's deck tab, opened straight on the Custom Test deck list (no format picker).
    ///    Back closes the window. The window is given the mode, so other modes can be passed later.
    ///  - Its decks open in the Testers window's own deck editor (TestersEditor, 0.14.64).
    ///  - Cards: the copy's card list (the whole collection; Space Wolves were left out until 0.14.51 as a
    ///    check that the copy is separate - owner 2026-10-21: show them again).
    /// The Styles tab is hidden. The ribbon (labels, Play icon, order, Styles hidden) is set once as the window is
    /// built, by MenuLayout (0.13.51; re-applied here every 0.25 s before).
    /// The normal Collection > Decks keeps every format, Custom Test included.
    /// </summary>
    internal static class TestersCollection
    {
        private const string CopyName = "RevivalTestersWindow", PanelName = "RevivalTestersPlay";
        private static CollectionScreen copy;
        private static GameObject sourcePrefab;
        private static float nextDress, closedAt;
        private static bool placedLogged, switchOffChanged, unlitLogged;
        private static IntPtr groupPtr;


        /// <summary>True when this window is the Testers window (not the normal Collection).</summary>
        internal static bool IsUnder(Transform t)
        {
            if (!Ui.Alive(copy)) return false;
            for (; (object)t != null; t = t.parent)
                if (t.Pointer == copy.transform.Pointer) return true;
            return false;
        }

        internal static bool IsCopy(GameWindow w) => Ui.Alive(copy) && (object)w != null && w.Pointer == copy.Pointer;

        /// <summary>The Testers window is open (on screen, or under a popup or its deck editor).</summary>
        internal static bool InUse()
        {
            try
            {
                if (!Ui.Alive(copy)) return false;
                if (copy.IsOpen()) return true;
                var list = WindowsManager.Instance?.openWindows;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                        if ((object)list[i] != null && list[i].Pointer == copy.Pointer) return true;
            }
            catch { }
            return false;
        }

        internal static void Close()
        {
            try { if (Ui.Alive(copy) && copy.IsOpen()) WindowsManager.Instance.CloseWindow(copy); }
            catch (Exception e) { RevivalMod.Log.Warning("[testers] " + e.Message); }
        }

        /// <summary>Open the Testers window. False when it could not be made (the caller falls back to the plain page).</summary>
        internal static bool Open()
        {
            try
            {
                var window = Copy();
                if ((object)window == null) return false;
                var wm = WindowsManager.Instance;
                if ((object)wm == null) { RevivalMod.Log.Warning("[testers] window manager not ready"); return false; }
                if (window.IsOpen()) return true;
                wm.OpenWindow(window, DecksContext(), true, null);
                Highlight();                    // the menu bar lights Testers in the same frame, not at the next check
                RevivalMod.Log.Msg("[testers] Testers window opened");
                return true;
            }
            catch (Exception e) { RevivalMod.Log.Warning("[testers] could not open the Testers window: " + e); return false; }
        }

        /// <summary>The tester modes with decks of their own, in the Testers window's order (Custom Test, then the server's).</summary>
        private static List<IPlayEvent> OwnDeckModes()
        {
            var list = new List<IPlayEvent>();
            foreach (var id in TestersPage.TesterModes())
            {
                var ev = TestersPage.Find(id);
                var playEvent = (object)ev == null ? null : ev.TryCast<IPlayEvent>();
                if ((object)playEvent == null || !LongGame.HasOwnDecks(playEvent)) continue;
                list.Add(playEvent);
            }
            return list;
        }

        /// <summary>The mode the Decks tab opens on: the first tester mode with decks of its own. (The format picker is not
        /// used in this window: the picker tab hands its choice to the game's Collection window, not to this copy - seen
        /// 0.14.55-57 - so a mode switch button on the deck list does the job instead, see ModeSwitch.)</summary>
        private static CollectionScreen.OpenDeckCollectionContext DecksContext()
        {
            var modes = OwnDeckModes();
            if (modes.Count == 0) { RevivalMod.Log.Warning("[testers] no tester mode with decks for the Decks tab"); return null; }
            return new CollectionScreen.OpenDeckCollectionContext { eventReference = modes[0] };
        }

        /// <summary>The window, made the first time from the window file behind the menu bar's Collection button.</summary>
        private static CollectionScreen Copy()
        {
            if (Ui.Alive(copy)) return copy;
            copy = null;
            var nav = GameState.FindNav();
            var toggle = (object)nav == null ? null : nav.GetToggle(NavigationPanelToggleType.Collection);
            if ((object)toggle == null) { RevivalMod.Log.Warning("[testers] menu bar Collection button not found"); return null; }
            var opener = toggle.GetComponentInChildren<OpenWindowButton>(true);
            if ((object)opener == null) { RevivalMod.Log.Warning("[testers] Collection button has no window behind it"); return null; }
            var prefabRef = opener.windowToOpenPrefab;
            if ((object)prefabRef == null || !prefabRef.RuntimeKeyIsValid())
            {
                // a window that lives in the scene would have to be copied while in use; not done
                RevivalMod.Log.Warning("[testers] the Collection window is not a window file; Testers window not made");
                return null;
            }
            if (!Ui.Alive(sourcePrefab))
            {
                // our own handle on the file: the game's reference object (and its handle) is left alone
                var loaded = new AssetReference(prefabRef.AssetGUID).LoadAssetAsync<GameObject>().WaitForCompletion();
                if (!Ui.Alive(loaded) || (object)loaded.GetComponent<CollectionScreen>() == null)
                {
                    RevivalMod.Log.Warning("[testers] Collection window file not loaded");
                    return null;
                }
                sourcePrefab = loaded;
            }

            var placement = sourcePrefab.GetComponent<GameWindow>().WindowsPlacement;
            var anchor = WindowsManager.Instance.GetWindowAnchor(placement);
            var go = UnityEngine.Object.Instantiate(sourcePrefab, anchor, false);
            go.name = CopyName;
            copy = go.GetComponent<CollectionScreen>();
            copy.updateNavPanel = false;        // the menu bar's Collection button stays the normal Collection's
            RevivalMod.Log.Msg($"[testers] Testers window made from the Collection window file {prefabRef.AssetGUID}");
            return copy;
        }

        /// <summary>Called every frame: four times a second, the menu-bar highlight and removing the window once closed.
        /// (The Play tab is set up once as the window is built, see PlayTab; 0.13.53 and earlier swapped it in here.)</summary>
        internal static void Tick()
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (now < nextDress) return;
                nextDress = now + 0.25f;
                Highlight();
                DropWhenClosed(now);
            }
            catch (Exception e) { RevivalMod.Log.Warning("[testers] " + e.Message); }
        }

        // ---------------------------------------------------------------- the Play tab, built once

        /// <summary>
        /// As the Testers window is built (GameWindowWithTabs.SetupTabs, before it is shown): the Styles tab
        /// (CardbackCollectionTab) becomes the Play tab. Its own contents, the cosmetics list and its "owned"
        /// switch, are moved under a switched-off holder, so whatever the game turns on in there when the tab opens
        /// stays out of sight (its open code is shared by all collection tabs and too short to hook on Android).
        /// The mode tiles are built inside the tab, so the game shows and hides them with it.
        /// </summary>
        // hooked through SharedHooks (one hook per game method): HarmonyPatch(typeof(GameWindowWithTabs), nameof(GameWindowWithTabs.SetupTabs))
        internal static class PlayTab
        {
            internal static void Postfix(GameWindowWithTabs __instance)
            {
                try
                {
                    if (!IsCopy(__instance)) return;
                    var tab = __instance.GetComponentInChildren<CardbackCollectionTab>(true);
                    if ((object)tab == null) { RevivalMod.Log.Warning("[testers] no Styles tab to turn into the Play tab"); return; }
                    var hidden = new GameObject("RevivalHidden");
                    var hiddenRt = hidden.AddComponent<RectTransform>();
                    hiddenRt.SetParent(tab.transform, false);
                    hidden.SetActive(false);
                    var move = new List<Transform>();
                    for (int i = 0; i < tab.transform.childCount; i++)
                    {
                        var child = tab.transform.GetChild(i);
                        if (child.Pointer != hiddenRt.Pointer) move.Add(child);
                    }
                    foreach (var extra in new Component[] { tab.display, tab.ownedToggle })
                        if ((object)extra != null && !extra.transform.IsChildOf(tab.transform)) move.Add(extra.transform);
                    foreach (var t in move) t.SetParent(hiddenRt, false);
                    BuildPlay(tab.transform);
                    RevivalMod.Log.Msg($"[testers] Play tab set up as the window was built ({move.Count} Styles part(s) put away)");
                }
                catch (Exception e) { RevivalMod.Log.Warning("[testers] Play tab: " + e.Message); }
            }
        }

        /// <summary>
        /// Once closed, the Testers window is deleted (after a moment, so its closing animation finishes),
        /// so it never stays loaded into a match. Opening it again makes a fresh one from the loaded file.
        /// </summary>
        private static void DropWhenClosed(float now)
        {
            if (!Ui.Alive(copy)) { copy = null; closedAt = 0; return; }
            if (StillOpen()) { closedAt = 0; return; }
            if (closedAt == 0) { closedAt = now; return; }
            if (now - closedAt < 1.5f) return;
            UnityEngine.Object.Destroy(copy.gameObject);
            copy = null;
            closedAt = 0;
            RevivalMod.Log.Msg("[testers] Testers window closed and removed");
            TestersEditor.Drop();
        }

        /// <summary>The Testers window is open, on screen or underneath another window (the deck editor): it is
        /// open itself, or still in the window manager's list of open windows.</summary>
        private static bool StillOpen()
        {
            if (!Ui.Alive(copy)) return false;
            try
            {
                if (copy.IsOpen() || copy.CurrentState != WindowState.Closed) return true;
                var list = WindowsManager.Instance?.openWindows;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                        if ((object)list[i] != null && list[i].Pointer == copy.Pointer) return true;
                return false;
            }
            catch { return true; }
        }

        private static IntPtr watchedCollection;

        /// <summary>Logs when the bar's Collection button is switched on while the Testers window is open: that
        /// opens the normal Collection over it (the deck editor bug of 2026-11-01). Added once per button.</summary>
        private static void WatchCollection(Toggle toggle)
        {
            if ((object)toggle == null || toggle.Pointer == watchedCollection) return;
            watchedCollection = toggle.Pointer;
            toggle.onValueChanged.AddListener(DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction<bool>>(new Action<bool>(on =>
            {
                if (!on || !Ui.Alive(copy)) return;
                string current = "?";
                try { var w = WindowsManager.Instance?.CurrentWindow; current = (object)w == null ? "none" : w.name; } catch { }
                RevivalMod.Log.Warning($"[testers] menu bar: Collection switched on while the Testers window is open (window on screen: {current}); the normal Collection opens over it");
            })));
        }

        /// <summary>
        /// The menu bar lights the button of the window type on screen, so with the Testers window up it lit
        /// Collection. While the Testers window is the one on screen, Collection is switched off (without
        /// opening anything) and the Testers button lit instead.
        /// The bar's buttons are a group that must keep one button lit; while the Testers window is open it may
        /// have none. That stays so while the Testers window is underneath the deck editor: the editor hides the
        /// bar and shows it again as it closes, and a group that must keep one lit then switches a button on as
        /// if tapped - Collection, which opened the normal Collection over the Testers window (fixed 0.15.12).
        /// The game's own setting comes back once the Testers window is closed.
        /// </summary>
        private static void Highlight()
        {
            // nothing to do while there is no Testers window and nothing of ours to put back: the look-ups
            // below search the whole scene, and four times a second during loading that showed up as stutter
            if (!Ui.Alive(copy) && !switchOffChanged) { TestersTab.Lit(false); return; }
            bool showing = false;
            try
            {
                var wm = WindowsManager.Instance;
                var current = (object)wm == null ? null : wm.CurrentWindow;
                showing = Ui.Alive(copy) && copy.isActiveAndEnabled && (object)current != null && current.Pointer == copy.Pointer;
                var nav = GameState.Nav;               // kept, not searched for 4 times a second (M-11)
                var col = (object)nav == null ? null : nav.GetToggle(NavigationPanelToggleType.Collection);
                var toggle = (object)col == null ? null : col.toggle;
                var group = (object)toggle == null ? null : toggle.group;
                if (Ui.Alive(copy)) WatchCollection(toggle);
                if (showing && (object)toggle != null)
                {
                    // the bar's buttons are a group that keeps one button on; let it have none while Testers is up
                    if ((object)group != null && !group.allowSwitchOff)
                    {
                        if (!switchOffChanged) { switchOffChanged = true; groupPtr = group.Pointer; }
                        group.allowSwitchOff = true;
                    }
                    if (toggle.isOn)
                    {
                        toggle.SetIsOnWithoutNotify(false);
                        toggle.RefreshVisuals();
                        if (!unlitLogged) { unlitLogged = true; RevivalMod.Log.Msg($"[testers] menu bar: Collection unlit ({(toggle.isOn ? "still on" : "off")})"); }
                    }
                }
                else if (!showing && switchOffChanged && (object)group != null && group.Pointer == groupPtr && !StillOpen())
                {
                    group.allowSwitchOff = false;    // back to the game's own setting
                    switchOffChanged = false;
                }
            }
            catch (Exception e) { RevivalMod.Log.Warning("[testers] highlight: " + e.Message); }
            TestersTab.Lit(showing);
        }

        /// <summary>Where the Play screen puts its tiles: right of the inner ribbon, same top and bottom as the Play screen's tiles.</summary>
        private static void PlacePanel(RectTransform panel)
        {
            // worked out in the window's space, then set wherever the tiles sit (inside the Play tab)
            var parent = (object)copy == null ? null : copy.GetComponent<RectTransform>();
            if ((object)parent == null) return;
            var pr = parent.rect;
            float left = pr.xMin + 200, right = pr.xMax - 40, top = pr.yMax - pr.height * 0.17f, bottom = pr.yMin + pr.height * 0.045f;
            var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
            var ribbon = copy.Components?.TabButtons;
            var rib = (object)ribbon == null ? null : ribbon.GetComponent<RectTransform>();
            if ((object)rib != null)
            {
                rib.GetWorldCorners(corners);
                left = parent.InverseTransformPoint(corners[2]).x + 28;
            }
            // the Play screen's own tiles give the top and bottom
            try
            {
                var menu = WindowsManager.Instance.BaseMenu;
                var main = (object)menu == null ? null : menu.TryCast<MainMenuWindow>();
                var tiles = (object)main == null ? null : main.currentLiveOpContainers;
                if (tiles != null && tiles.Count > 0)
                {
                    float t = float.MinValue, b = float.MaxValue, r = float.MinValue;
                    for (int i = 0; i < tiles.Count; i++)
                    {
                        var tile = tiles[i];
                        var trt = (object)tile == null ? null : tile.GetComponent<RectTransform>();
                        if ((object)trt == null) continue;
                        trt.GetWorldCorners(corners);
                        t = Mathf.Max(t, parent.InverseTransformPoint(corners[1]).y);
                        b = Mathf.Min(b, parent.InverseTransformPoint(corners[0]).y);
                        r = Mathf.Max(r, parent.InverseTransformPoint(corners[2]).x);
                    }
                    if (t > b + 10) { top = t; bottom = b; right = Mathf.Max(right, r); }
                }
            }
            catch { }
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
            float scale = 1f;
            var host = panel.parent;
            if ((object)host != null && host.lossyScale.x > 0.0001f && parent.lossyScale.x > 0.0001f) scale = parent.lossyScale.x / host.lossyScale.x;
            var size = new Vector2(Mathf.Max(10, right - left), Mathf.Max(10, top - bottom));
            panel.sizeDelta = size * scale;
            panel.position = parent.TransformPoint(new Vector3((left + right) / 2, (top + bottom) / 2, 0));
            if (!placedLogged) { placedLogged = true; RevivalMod.Log.Msg($"[testers] Play tab area {size.x:0}x{size.y:0}"); }
        }

        /// <summary>The Play tab: each tester mode's own Play-screen tile, made the way the Play screen makes them.</summary>
        private static void BuildPlay(Transform tab)
        {
            var panel = new GameObject(PanelName);
            var rt = panel.AddComponent<RectTransform>();
            rt.SetParent(tab, false);           // inside the Play tab: shown and hidden with it
            rt.SetAsLastSibling();
            PlacePanel(rt);

            var row = new GameObject("Tiles");
            var rowRt = row.AddComponent<RectTransform>();
            rowRt.SetParent(rt, false);
            rowRt.anchorMin = rowRt.anchorMax = rowRt.pivot = new Vector2(0, 1);
            rowRt.anchoredPosition = Vector2.zero;
            var layout = row.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 22;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = layout.childControlHeight = false;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;
            var fit = row.AddComponent<ContentSizeFitter>();
            fit.horizontalFit = fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            int shown = 0;
            foreach (var id in TestersPage.TesterModes())
            {
                var ev = TestersPage.Find(id);
                if ((object)ev == null) { RevivalMod.Log.Warning($"[testers] mode {id} is not on this server"); continue; }
                try
                {
                    var provider = ev.TryCast<IContainerProvider>();
                    var tile = (object)provider == null ? null
                        : ContainerBuilder<LiveopMenuContainer>.MakeContainer(provider, EventComponentReferenceType.MainMenuContainer, row.transform);
                    if ((object)tile == null) { RevivalMod.Log.Warning($"[testers] {id} has no Play-screen tile"); continue; }
                    // a tile under a layout group: the layout places it, its own size is kept
                    var trt = tile.GetComponent<RectTransform>();
                    if ((object)trt != null) { trt.anchorMin = trt.anchorMax = trt.pivot = new Vector2(0, 1); }
                    var size = tile.gameObject.GetComponent<LayoutElement>() ?? tile.gameObject.AddComponent<LayoutElement>();
                    if ((object)trt != null) { size.preferredWidth = trt.rect.width; size.preferredHeight = trt.rect.height; }
                    RevivalMod.Log.Msg($"[testers] Play tab tile for {id}: {tile.name} {(object)trt?.rect.width ?? 0:0}x{(object)trt?.rect.height ?? 0:0}");
                    shown++;
                }
                catch (Exception e) { RevivalMod.Log.Warning($"[testers] tile for {id}: {e.Message}"); }
            }
            if (shown == 0)
                TestersPage.Text(panel.transform, "Empty", Loc.T("testers.noModes", "No tester modes are set up on this server right now."), 26, TextAlignmentOptions.Left, TestersPage.Dim, 0.05f, 0.6f, 0.9f, 0.75f);
            else
            {
                // lay the row out now (not at the next frame) so it can be fitted before the window is shown
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(rowRt);
                FitTiles(panel.transform);
            }
            RevivalMod.Log.Msg($"[testers] Play tab: {shown} mode(s)");
        }

        /// <summary>Tiles keep the Play screen's size; scaled down only if the area is smaller.</summary>
        private static void FitTiles(Transform panel)
        {
            var row = panel.Find("Tiles");
            var area = panel.GetComponent<RectTransform>();
            var rowRt = (object)row == null ? null : row.GetComponent<RectTransform>();
            if ((object)rowRt == null || (object)area == null) return;
            float h = rowRt.rect.height, w = rowRt.rect.width;
            if (h <= 1 || w <= 1) return;
            float scale = Mathf.Min(1f, area.rect.height / h, area.rect.width / w);
            if (scale > 0 && Mathf.Abs(rowRt.localScale.x - scale) > 0.01f) rowRt.localScale = new Vector3(scale, scale, 1);
        }

        // The window opens on Play.
        // hooked through SharedHooks (one hook per game method): HarmonyPatch(typeof(GameWindowWithTabs), nameof(GameWindowWithTabs.GetStartingTab))
        internal static class StartingTab
        {
            internal static void Postfix(GameWindowWithTabs __instance, ref WindowTabBase __result)
            {
                try
                {
                    if (!IsCopy(__instance)) return;
                    var tab = Ui.TabOf<CardbackCollectionTab>(__instance);
                    if ((object)tab != null) __result = tab;
                }
                catch (Exception e) { RevivalMod.Log.Warning("[testers] " + e.Message); }
            }
        }

        // Decks skips the format picker: straight to the tester mode's deck list.
        // The deck list of a tester mode (0.14.58): a button next to Import Deck switches to the next tester mode's list
        // ("Custom Cards (test)" while Custom Test's decks show, and the other way round). Made once per window from
        // the Import Deck button; relabelled whenever a list is shown.
        private const string SwitchName = "RevivalDeckModeSwitch";
        private static string shownModeId;

        private static string NameOf(IPlayEvent e)
        {
            try { var d = e.GetBaseData(); return (object)d == null ? "?" : d.eventId; } catch { return "?"; }
        }

        private static string LabelOf(IPlayEvent e)
        {
            try
            {
                var label = LiveOpsAssetUtility.GetLabel(e.GetBaseData(), EventLabelReferenceType.Title, new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object>(0));
                if (!string.IsNullOrEmpty(label)) return label;
            }
            catch { }
            return NameOf(e);
        }

        private static void ShowSwitch(SelectDecksTab tab, IOwnDeckPlayEvent shown)
        {
            var modes = OwnDeckModes();
            var importBtn = tab.importDeck;
            if ((object)importBtn == null) return;
            var importGo = importBtn.gameObject;
            var holder = importGo.transform.parent;
            if ((object)holder == null) return;
            var existing = holder.Find(SwitchName);
            if (modes.Count < 2) { if ((object)existing != null) existing.gameObject.SetActive(false); return; }
            var shownPlay = (object)shown == null ? null : shown.TryCast<IPlayEvent>();
            shownModeId = (object)shownPlay == null ? modes[0].GetBaseData().eventId : NameOf(shownPlay);
            int at = 0;
            for (int i = 0; i < modes.Count; i++) if (NameOf(modes[i]) == shownModeId) at = i;
            var next = modes[(at + 1) % modes.Count];
            GameObject go;
            if ((object)existing == null)
            {
                go = UnityEngine.Object.Instantiate(importGo, holder, false);
                go.name = SwitchName;
                go.transform.SetSiblingIndex(importGo.transform.GetSiblingIndex());
                var btn = go.GetComponent<UnityEngine.UI.Button>();
                if ((object)btn != null)
                {
                    btn.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
                    var tabRef = tab;
                    btn.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(new Action(() => SwitchList(tabRef))));
                }
                foreach (var loc in go.GetComponentsInChildren<Il2CppI2.Loc.Localize>(true)) loc.enabled = false;
                RevivalMod.Log.Msg("[testers] deck list: mode switch button added next to Import Deck");
            }
            else go = existing.gameObject;
            go.SetActive(true);
            var label = go.GetComponentInChildren<Il2CppTMPro.TMP_Text>(true);
            if ((object)label != null) Ui.SetText(label, LabelOf(next));
        }

        private static void SwitchList(SelectDecksTab tab)
        {
            try
            {
                var modes = OwnDeckModes();
                if (modes.Count < 2) return;
                int at = 0;
                for (int i = 0; i < modes.Count; i++) if (NameOf(modes[i]) == shownModeId) at = i;
                var next = modes[(at + 1) % modes.Count];
                var own = next.TryCast<IOwnDeckPlayEvent>();
                if ((object)own == null) return;
                RevivalMod.Log.Msg("[testers] deck list: switching to " + NameOf(next));
                tab.OnClickDeckCollection(own);
            }
            catch (Exception e) { RevivalMod.Log.Warning("[testers] deck list switch: " + e.Message); }
        }

        // hooked through SharedHooks (one hook per game method): HarmonyPatch(typeof(SelectDecksTab), nameof(SelectDecksTab.ToggleSelectDeck))
        internal static class ListShown
        {
            internal static void Postfix(SelectDecksTab __instance, IOwnDeckPlayEvent deckPlayEvent)
            {
                try { if (IsCopy(__instance.Window)) ShowSwitch(__instance, deckPlayEvent); }
                catch (Exception e) { RevivalMod.Log.Warning("[testers] deck list switch button: " + e.Message); }
            }
        }

        // Back from a tester mode's deck list closes the window instead of showing the format picker.
        [HarmonyPatch(typeof(SelectDecksTab), nameof(SelectDecksTab.BackButtonClicked))]
        private static class BackCloses
        {
            private static bool Prefix(SelectDecksTab __instance)
            {
                try
                {
                    if (!IsCopy(__instance.Window)) return true;
                    Close();
                    return false;
                }
                catch (Exception e) { RevivalMod.Log.Warning("[testers] " + e.Message); return true; }
            }
        }
    }
}
