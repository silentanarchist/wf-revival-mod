using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;

namespace WarpforgeRevival
{
    /// <summary>
    /// The gear menu in a match (BattleSettingsWindow): for testers, Resign moves left and a
    /// "Report bug" button made from a copy of it sits on the right. Pressing it closes the menu
    /// and, half a second later (so the menu is not in the screenshot), opens the bug report window.
    /// The copy gets a fresh click event, so it can never resign.
    /// </summary>
    internal static class BattleMenuBug
    {
        private const string Name = "WFR Report bug";

        [HarmonyPatch(typeof(BattleSettingsWindow), nameof(BattleSettingsWindow.Open))]
        private static class OpenPatch
        {
            private static void Postfix(BattleSettingsWindow __instance)
            {
                try { Add(__instance); }
                catch (Exception e) { RevivalMod.Log.Warning("[bugs] battle menu button: " + e.Message); }
            }
        }

        private static void Add(BattleSettingsWindow window)
        {
            if (!AccountPage.CanReportBugs) return;
            var resign = window.resignButton;
            if ((object)resign == null) return;
            var rt = resign.transform.Cast<RectTransform>();
            var parent = rt.parent;
            if ((object)parent == null || (object)parent.Find(Name) != null) return;     // added already (the window is reused)

            var go = UnityEngine.Object.Instantiate(resign.gameObject, parent, false);
            go.name = Name;
            var copy = go.GetComponent<UnityEngine.UI.Button>();
            if ((object)copy == null) { UnityEngine.Object.Destroy(go); return; }
            copy.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();          // nothing of Resign's comes along
            foreach (var label in go.GetComponentsInChildren<TMP_Text>(true))
            {
                Loc.Bind(label, () => Loc.T("bugReport.battleButton", "Report bug"));     // the window is reused: follows a language change
            }
            foreach (var loc in go.GetComponentsInChildren<Il2CppI2.Loc.Localize>(true)) loc.enabled = false;

            // side by side: Resign to the left, Report bug to the right
            float shift = rt.rect.width / 2f + 16f;
            var crt = go.transform.Cast<RectTransform>();
            crt.anchoredPosition = rt.anchoredPosition + new Vector2(shift, 0f);
            rt.anchoredPosition = rt.anchoredPosition - new Vector2(shift, 0f);

            copy.onClick.AddListener(Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(new Action(() =>
            {
                try { window.CloseButtonOnClick(); } catch (Exception e) { RevivalMod.Log.Warning("[bugs] closing the battle menu: " + e.Message); }
                BugReport.RequestAfter(0.5f);
            })));
            RevivalMod.Log.Msg("[bugs] Report bug button added to the battle menu");
        }
    }
}
