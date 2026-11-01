using System;
using HarmonyLib;
using Il2Cpp;

namespace WarpforgeRevival
{
    /// <summary>
    /// The shop and the rewards track have nothing behind them on a revival server.
    /// With HideShopAndRewards on (default) their buttons are removed from the main menu's
    /// navigation bar, leaving Play, Collection and Social.
    /// (Patched methods verified to have unique native code.)
    /// </summary>
    internal static class MenuCleanup
    {
        private static void Apply(NavigationPanelController nav)
        {
            try
            {
                var toggles = nav?.navToggles;
                if (toggles == null) return;
                for (int i = 0; i < toggles.Count; i++)
                {
                    var t = toggles[i];
                    if ((object)t == null) continue;
                    if (RevivalMod.Config.HideShopAndRewards &&
                        (t.toggleType == NavigationPanelToggleType.Shop || t.toggleType == NavigationPanelToggleType.Rewards))
                        t.gameObject.SetActive(false);
                    // The game keeps Social locked until alliances unlock; friends do not need alliances.
                    if (t.toggleType == NavigationPanelToggleType.Social && (object)t.toggle != null)
                        t.toggle.interactable = true;
                }
            }
            catch (Exception e) { RevivalMod.Log.Warning("[menu] could not hide shop/rewards buttons: " + e.Message); }
        }

        // The currency counters at the top right: nothing is bought or earned on a revival server.
        [HarmonyPatch(typeof(ResourcesBarController), nameof(ResourcesBarController.Initialize), new[] { typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<GameCurrency>) })]
        private static class NoCurrencyBar
        {
            private static void Postfix(ResourcesBarController __instance)
            {
                if (!RevivalMod.Config.HideShopAndRewards) return;
                try
                {
                    var c = __instance.container;
                    if ((object)c != null) c.gameObject.SetActive(false);
                }
                catch (Exception e) { RevivalMod.Log.Warning("[menu] could not hide the currency counters: " + e.Message); }
            }
        }

        // This is where the game greys the Social button out after login.
        [HarmonyPatch(typeof(AlliancesManager), nameof(AlliancesManager._Initialize_b__27_0))]
        private static class AfterAllianceCheck
        {
            private static void Postfix()
            {
                try { Apply(GameState.FindNav()); }
                catch (Exception e) { RevivalMod.Log.Warning("[menu] " + e.Message); }
            }
        }

        // hooked through SharedHooks (one hook per game method): HarmonyPatch(typeof(NavigationPanelController), nameof(NavigationPanelController.Awake))
        internal static class OnAwake { internal static void Postfix(NavigationPanelController __instance) { GameState.SawNav(__instance); Apply(__instance); } }

        // hooked through SharedHooks (one hook per game method): HarmonyPatch(typeof(NavigationPanelController), nameof(NavigationPanelController.OnInitializationComplete))
        internal static class OnReady
        {
            internal static void Postfix(NavigationPanelController __instance)
            {
                Apply(__instance);
            }
        }

        [HarmonyPatch(typeof(NavigationPanelController), nameof(NavigationPanelController.OnEventsRefreshed))]
        private static class OnRefresh { private static void Postfix(NavigationPanelController __instance) => Apply(__instance); }
    }
}
