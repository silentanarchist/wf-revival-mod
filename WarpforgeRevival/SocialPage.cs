using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;

namespace WarpforgeRevival
{
    /// <summary>
    /// Social on a revival server: the friend list (worded for adding people by friend code or name)
    /// and the Challenge tab, which takes the place of the unused Alliances tab (see ChallengeMenu; its ribbon
    /// button is set by MenuLayout).
    /// </summary>
    internal static class SocialPage
    {
        // hooked through SharedHooks (one hook per game method): HarmonyPatch(typeof(GameWindowWithTabs), nameof(GameWindowWithTabs.GetStartingTab))
        internal static class StartOnFriends
        {
            internal static void Postfix(GameWindowWithTabs __instance, ref WindowTabBase __result)
            {
                try
                {
                    if ((object)__instance.TryCast<SocialMenuWindow>() == null) return;
                    var friends = Ui.TabOf<FriendsTab>(__instance);
                    if ((object)friends != null) __result = friends;
                }
                catch (Exception e) { RevivalMod.Log.Warning("[social] " + e.Message); }
            }
        }

        /// <summary>Friends tab wording: add by friend code, no "instant duel" button.</summary>
        internal static void DressFriendsTab(FriendsTab tab)
        {
            try
            {
                var input = tab.inputFieldUserName;
                if ((object)input != null)
                {
                    var ph = input.placeholder;
                    if ((object)ph != null)
                    {
                        var text = ph.TryCast<TMP_Text>();
                        if ((object)text != null) Loc.Bind(text, () => Loc.T("friends.enterCode", "Enter friend code"));
                    }
                    var panel = input.transform.parent;
                    var title = (object)panel != null ? panel.Find("Search Player") : null;
                    if ((object)title != null)
                    {
                        var text = title.GetComponent<TMP_Text>();
                        // the game's own "Add as a friend"
                        if ((object)text != null) Loc.Bind(text, () => Loc.Game("MainMenu/Chat/Add", "Add friend"));
                    }
                }
                var duel = tab.instantDuelButton;
                if ((object)duel != null) duel.gameObject.SetActive(false);
            }
            catch (Exception e) { RevivalMod.Log.Warning("[social] " + e.Message); }
        }
    }
}
