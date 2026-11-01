using System;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace WarpforgeRevival
{
    /// <summary>
    /// A "Testers" button in the bottom bar of the main menu, right after Social, shown only to
    /// players whose linked site account has the in-game tester role (People tab on the site;
    /// admins count as testers). The button is a copy of the Social button with Social's own
    /// behaviour taken off it. It opens the Testers window (TestersCollection).
    ///
    /// 0.13.54: built once, when the game builds the menu bar (NavigationPanelController Awake /
    /// OnInitializationComplete, the moments MenuCleanup already uses), with its picture already loaded, so it
    /// appears complete with the rest of the bar. Shown or hidden when the server says whether this player is a
    /// tester (AccountPage, right after sign-in). Before, a check once a second added it, and its picture
    /// arrived later still.
    /// </summary>
    internal static class TestersTab
    {
        private const string Name = "RevivalTestersToggle";

        private static GameObject button;
        // The button's picture: the game's own expansion-pass medallion (a servo-tool), loaded from the
        // game's files by its asset id (40k_rewards_bt_missions_expansion pass, liveopsicons bundle).
        private const string IconAsset = "89b680029c9624e6e905db2fc0a47a9d";
        private static AsyncOperationHandle<Sprite> iconLoad;
        private static bool iconAsked, iconDone;
        private static NavigationPanelController nav;
        private static bool shownLogged, hierarchyLogged;



        // The menu bar is built (a new one after every match): the button goes in with it.
        // hooked through SharedHooks (one hook per game method): HarmonyPatch(typeof(NavigationPanelController), nameof(NavigationPanelController.Awake))
        internal static class BarBuilt { internal static void Postfix(NavigationPanelController __instance) => Ensure(__instance); }

        // hooked through SharedHooks (one hook per game method): HarmonyPatch(typeof(NavigationPanelController), nameof(NavigationPanelController.OnInitializationComplete))
        internal static class BarReady { internal static void Postfix(NavigationPanelController __instance) => Ensure(__instance); }

        /// <summary>The server has said (or changed) whether this player is a tester; called on the main thread.</summary>
        internal static void RoleChanged()
        {
            try
            {
                if (AccountPage.IsTester) StartIcon();
                if (!Ui.Alive(nav)) nav = GameState.FindNav();
                Ensure(nav);
            }
            catch (Exception e) { RevivalMod.Log.Warning("[testers] " + e.Message); }
        }

        /// <summary>The button there and shown for a tester; hidden (and the Testers windows closed) otherwise.</summary>
        private static void Ensure(NavigationPanelController bar)
        {
            try
            {
                if (Ui.Alive(bar)) nav = bar;
                bool want = AccountPage.IsTester;
                if (!want)
                {
                    if (TestersPage.IsOpen) TestersPage.Close();
                    TestersCollection.Close();
                    if (Ui.Alive(button) && button.activeSelf) button.SetActive(false);
                    return;
                }
                if (Ui.Alive(button) && Ui.Alive(nav) && button.transform.parent != null && button.transform.IsChildOf(nav.transform))
                {
                    if (!button.activeSelf) button.SetActive(true);
                    return;
                }
                if (!Ui.Alive(nav)) return;
                var social = nav.GetToggle(NavigationPanelToggleType.Social);
                if ((object)social == null) return;
                button = Build(social.gameObject);
            }
            catch (Exception e) { RevivalMod.Log.Warning("[testers] " + e.Message); }
        }

        private static GameObject Build(GameObject social)
        {
            var parent = social.transform.parent;
            // Copied under a switched-off holder, so the copy's own start-up code (which would hook
            // it up to open the Social window) never runs before that code is taken off.
            var holder = new GameObject("RevivalTestersHolder");
            holder.SetActive(false);
            var copy = UnityEngine.Object.Instantiate(social, holder.transform);
            copy.name = Name;
            if (!hierarchyLogged) { hierarchyLogged = true; LogHierarchy(copy.transform, 0); }

            MenuLayout.StripBadges(copy);                // not Social's notification count
            foreach (var c in copy.GetComponentsInChildren<OpenWindowButton>(true)) UnityEngine.Object.DestroyImmediate(c);
            foreach (var c in copy.GetComponentsInChildren<NavigationPanelToggle>(true)) UnityEngine.Object.DestroyImmediate(c);
            foreach (var c in copy.GetComponentsInChildren<Il2CppI2.Loc.Localize>(true)) c.enabled = false;
            foreach (var t in copy.GetComponentsInChildren<Il2CppTMPro.TMP_Text>(true)) Loc.Bind(t, () => Loc.T("menu.testers", "Testers"));

            var toggle = copy.GetComponent<UnityEngine.UI.Toggle>();
            if ((object)toggle != null)
            {
                toggle.group = null;                     // not one of the bar's own pages
                toggle.SetIsOnWithoutNotify(false);
                toggle.onValueChanged = new UnityEngine.UI.Toggle.ToggleEvent();
                toggle.onValueChanged.AddListener(DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction<bool>>(
                    new Action<bool>(on => { if (on) { toggle.SetIsOnWithoutNotify(false); Open(); } })));
            }

            // the picture, before the button is ever shown (loaded from the game's files; waited for here if the
            // load started at sign-in has not finished, which only happens during loading)
            var pic = copy.transform.Find("Image");
            var picImage = (object)pic == null ? null : pic.GetComponent<UnityEngine.UI.Image>();
            var sprite = Icon();
            if ((object)picImage != null && (object)sprite != null) picImage.sprite = sprite;
            copy.transform.SetParent(parent, false);
            copy.transform.SetSiblingIndex(social.transform.GetSiblingIndex() + 1);
            UnityEngine.Object.Destroy(holder);
            if (!shownLogged) { shownLogged = true; RevivalMod.Log.Msg("[testers] Testers button added to the menu bar (in-game tester)"); }
            return copy;
        }

        /// <summary>Starts loading the button's picture (once).</summary>
        private static void StartIcon()
        {
            if (iconAsked) return;
            iconAsked = true;
            try { iconLoad = new AssetReference(IconAsset).LoadAssetAsync<Sprite>(); }
            catch (Exception e) { RevivalMod.Log.Warning("[testers] icon: " + e.Message); }
        }

        /// <summary>The button's picture, waiting for the load if needed; null if it cannot be had (Social's stays).</summary>
        private static Sprite Icon()
        {
            try
            {
                StartIcon();
                if (!iconLoad.IsValid()) return null;
                if (!iconLoad.IsDone) iconLoad.WaitForCompletion();
                var sprite = iconLoad.Result;
                if (!iconDone)
                {
                    iconDone = true;
                    if ((object)sprite == null) RevivalMod.Log.Warning("[testers] icon not found in the game's files; the Social picture stays");
                    else RevivalMod.Log.Msg("[testers] button picture ready");
                }
                return sprite;
            }
            catch (Exception e)
            {
                if (!iconDone) { iconDone = true; RevivalMod.Log.Warning("[testers] icon: " + e.Message); }
                return null;
            }
        }

        /// <summary>Lights the Testers button while the Testers window is on screen (the menu bar's own buttons do the same).</summary>
        internal static void Lit(bool on)
        {
            try
            {
                if (!Ui.Alive(button)) return;
                var toggle = button.GetComponent<UnityEngine.UI.Toggle>();
                if ((object)toggle == null || toggle.isOn == on) return;
                toggle.SetIsOnWithoutNotify(on);
                var ev = toggle.TryCast<EverguildToggle>();
                if ((object)ev != null) ev.RefreshVisuals();
            }
            catch (Exception e) { RevivalMod.Log.Warning("[testers] highlight: " + e.Message); }
        }

        private static void Open()
        {
            RevivalMod.Log.Msg("[testers] Testers tab opened");
            Il2CppTMPro.TMP_FontAsset gameFont = null;
            try { var label = button.GetComponentInChildren<Il2CppTMPro.TMP_Text>(true); if ((object)label != null) gameFont = label.font; }
            catch { }
            if ((object)gameFont != null) TestersPage.font = gameFont;
            if (!TestersCollection.Open()) TestersPage.Open(gameFont);   // the Testers window; the plain page if it cannot be made
        }

        // What the copied button is made of, once, so the page behind it can be built to match.
        private static void LogHierarchy(Transform t, int depth)
        {
            if (depth > 4) return;
            var names = new System.Text.StringBuilder();
            foreach (var c in t.GetComponents<Component>())
                if ((object)c != null) names.Append(c.GetIl2CppType().Name).Append(' ');
            RevivalMod.Log.Msg($"[testers] {new string(' ', depth * 2)}{t.name}: {names}");
            for (int i = 0; i < t.childCount; i++) LogHierarchy(t.GetChild(i), depth + 1);
        }
    }
}
