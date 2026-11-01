using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace WarpforgeRevival
{
    /// <summary>
    /// The tabs the mod adds to the Social window (Challenge since 0.13.52, Community since 0.14.28), each a copy of
    /// the game's Alliances tab with its pages taken out and a copy of its ribbon button.
    ///
    /// How (decompiled 2026-10-16): GameWindowWithTabs.SetupTabs runs once per window, before it is shown; it wires up
    /// every button in the ribbon's list (TabButtons.Initialize) and then sets up the tabs in the window's tab list.
    /// In a prefix each registered tab is copied (the Alliances class does nothing on its own: Start and Refresh,
    /// which load alliances, are blocked for the copies in ChallengeMenu) and a copy of the Alliances button is added
    /// to the ribbon with the game's own TabButtons.AssignTabButton, so the game wires its click like any other. In a
    /// postfix the copies are set up and added to the tab list after the game has filed its tabs by class, so the
    /// game's lookups (GetTab&lt;AlliancesTab&gt;) still find the real Alliances tab. From then on the game opens and
    /// closes the tabs itself; each page lives inside its tab and is drawn when the tab is picked (the window's
    /// OnTabChanged event). Labels, icons and places come from MenuLayout (by the entry's Name).
    /// </summary>
    internal static class SocialTabs
    {
        internal sealed class Entry
        {
            public string Name, TabName, ButtonName;
            public Action<GameWindowWithTabs, Transform> Build;     // draws the page into the tab
            public AlliancesTab Tab;
        }

        private static readonly List<Entry> entries = new List<Entry>();
        internal static SocialMenuWindow Window { get; private set; }

        internal static Entry Register(Entry e) { entries.Add(e); return e; }

        /// <summary>True for any of the mod's copies, not the real Alliances tab.</summary>
        internal static bool IsCopy(UnityEngine.Object t)
        {
            if ((object)t == null) return false;
            foreach (var e in entries) if (Ui.Alive(e.Tab) && t.Pointer == e.Tab.Pointer) return true;
            return false;
        }

        internal static Entry Of(UnityEngine.Object t)
        {
            if ((object)t == null) return null;
            foreach (var e in entries) if (Ui.Alive(e.Tab) && t.Pointer == e.Tab.Pointer) return e;
            return null;
        }

        // hooked through SharedHooks (one hook per game method): HarmonyPatch(typeof(GameWindowWithTabs), nameof(GameWindowWithTabs.SetupTabs))
        internal static class AddTabs
        {
            /// <summary>Before the game wires up the ribbon: the tabs and their buttons.</summary>
            internal static void Prefix(GameWindowWithTabs __instance)
            {
                try
                {
                    var social = __instance.TryCast<SocialMenuWindow>();
                    if ((object)social == null) return;
                    var alliances = __instance.GetComponentInChildren<AlliancesTab>(true);
                    var buttons = __instance.Components?.TabButtons;
                    if ((object)alliances == null || (object)buttons == null || buttons.options == null)
                    { RevivalMod.Log.Warning("[social tabs] Social window has no Alliances tab or ribbon to copy; no mod tabs"); return; }
                    TabButtons.TabButton source = null;
                    for (int i = 0; i < buttons.options.Count; i++)
                    {
                        var b = buttons.options[i];
                        if (b != null && (object)b.tab != null && b.tab.Pointer == alliances.Pointer && (object)b.toggle != null) { source = b; break; }
                    }
                    if (source == null) { RevivalMod.Log.Warning("[social tabs] the Alliances tab has no button to copy; no mod tabs"); return; }
                    Window = social;
                    int place = alliances.transform.GetSiblingIndex();
                    foreach (var e in entries)
                    {
                        try
                        {
                            // Both copies are made under an inactive holder, so nothing in them (the alliance pages, the
                            // button's alliances switch) starts up before it is taken out.
                            var holder = new GameObject("Revival" + e.Name + "Holder");
                            holder.SetActive(false);

                            var tabGo = UnityEngine.Object.Instantiate(alliances.gameObject, holder.transform, false);
                            tabGo.name = e.TabName;
                            var copyTab = tabGo.GetComponent<AlliancesTab>();
                            for (int i = tabGo.transform.childCount - 1; i >= 0; i--)
                                UnityEngine.Object.DestroyImmediate(tabGo.transform.GetChild(i).gameObject);
                            tabGo.SetActive(false);
                            tabGo.transform.SetParent(alliances.transform.parent, false);
                            tabGo.transform.SetSiblingIndex(++place);

                            var buttonGo = UnityEngine.Object.Instantiate(source.toggle.gameObject, holder.transform, false);
                            buttonGo.name = e.ButtonName;
                            foreach (var fc in buttonGo.GetComponents<FeatureChecker>()) UnityEngine.Object.DestroyImmediate(fc);
                            MenuLayout.StripBadges(buttonGo);            // not the alliance invitations' count
                            buttonGo.SetActive(true);
                            buttonGo.transform.SetParent(source.toggle.transform.parent, false);
                            UnityEngine.Object.Destroy(holder);
                            var toggle = buttonGo.GetComponent<EverguildToggle>();
                            if ((object)toggle == null) { UnityEngine.Object.Destroy(buttonGo); UnityEngine.Object.Destroy(tabGo); RevivalMod.Log.Warning($"[social tabs] {e.Name}: copied button is not a tab button"); continue; }
                            toggle.isOn = false;
                            buttons.AssignTabButton(copyTab, toggle);       // wired up by the game in TabButtons.Initialize, next
                            e.Tab = copyTab;
                            MenuLayout.Name(copyTab, e.Name);
                        }
                        catch (Exception ex) { RevivalMod.Log.Warning($"[social tabs] could not add the {e.Name} tab: " + ex); }
                    }
                }
                catch (Exception e) { RevivalMod.Log.Warning("[social tabs] " + e); }
            }

            /// <summary>After the game has filed its tabs: the copies set up, listed, closed, and their pages drawn.</summary>
            private static void Postfix(GameWindowWithTabs __instance)
            {
                try
                {
                    if (!Ui.Alive(Window) || __instance.Pointer != Window.Pointer) return;
                    bool any = false;
                    foreach (var e in entries)
                    {
                        if (!Ui.Alive(e.Tab)) continue;
                        e.Tab.Setup(__instance);
                        var tabs = __instance.tabs;
                        if (tabs != null && !tabs.Contains(e.Tab)) tabs.Add(e.Tab);     // after the game filed them by class
                        if (e.Tab.gameObject.activeSelf) e.Tab.gameObject.SetActive(false);
                        any = true;
                    }
                    if (!any) return;
                    var onChange = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<WindowTabBase>>(new Action<WindowTabBase>(TabChanged));
                    __instance.OnTabChanged = __instance.OnTabChanged == null
                        ? onChange
                        : Il2CppSystem.Delegate.Combine(__instance.OnTabChanged, onChange).Cast<Il2CppSystem.Action<WindowTabBase>>();
                    foreach (var e in entries)
                        if (Ui.Alive(e.Tab)) { try { e.Build(Window, e.Tab.transform); } catch (Exception ex) { RevivalMod.Log.Warning($"[social tabs] {e.Name}: " + ex.Message); } }
                    RevivalMod.Log.Msg("[social tabs] added to the Social window: " + string.Join(", ", entries.ConvertAll(x => x.Name)));
                }
                catch (Exception e) { RevivalMod.Log.Warning("[social tabs] set-up: " + e.Message); }
            }
        }

        /// <summary>The window's own "tab changed" event (it lights the ribbon): the picked copy's page is drawn again
        /// in the same frame (its contents may have changed). No hook: 0.13.52 hooked WindowTabBase.TryOpenTab, which
        /// crashed the Android loader (its code starts 8 bytes before a page end).</summary>
        private static void TabChanged(WindowTabBase to)
        {
            try
            {
                var e = Of(to);
                if (e != null && Ui.Alive(Window)) e.Build(Window, e.Tab.transform);
            }
            catch (Exception ex) { RevivalMod.Log.Warning("[social tabs] " + ex.Message); }
        }
    }
}
