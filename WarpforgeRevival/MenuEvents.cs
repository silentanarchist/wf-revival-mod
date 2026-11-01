using System;
using Il2Cpp;
using Il2CppInterop.Runtime;

namespace WarpforgeRevival
{
    /// <summary>
    /// The game's own "a window or tab changed" event, passed on to the mod's features (0.15.12). No hook.
    ///
    /// The game's WindowsManager raises OnWindowOrTabChanged(isTab, window, tab) whenever the window on screen
    /// changes - a window opened, closed, "back", a popup closed, the last window closed and the main menu came back -
    /// and whenever the window on screen changes tab (from the decompiled WindowsManager.set_CurrentWindow and
    /// TabChanged). It is raised before the window or tab has finished opening (OpenWindowCO sets the window, then
    /// opens it; OpenTabs raises the tab change before the tab opens), so the mod's events are raised afterwards,
    /// from the mod's next update, with the state as it then is: the window on screen and its current tab.
    ///
    /// Features subscribe to WindowShown / TabShown / TabHidden / BackAtMainMenu instead of checking on a timer
    /// whether their page is on screen. Each subscriber is called inside its own try.
    /// </summary>
    internal static class MenuEvents
    {
        /// <summary>A different window is now on screen (also one shown again after the window over it closed).</summary>
        internal static event Action<GameWindow> WindowShown;
        /// <summary>A tab is now the one on screen (its window is the window on screen).</summary>
        internal static event Action<GameWindowWithTabs, WindowTabBase> TabShown;
        /// <summary>A tab is no longer the one on screen (another tab, another window over it, or closed).</summary>
        internal static event Action<GameWindowWithTabs, WindowTabBase> TabHidden;
        /// <summary>The main menu is on screen again (no other window open).</summary>
        internal static event Action BackAtMainMenu;

        private static IntPtr subscribedTo;
        private static bool pending, subscribedLogged;
        private static IntPtr shownWindow, shownTab;
        private static GameWindowWithTabs shownTabWindow;
        private static WindowTabBase shownTabObject;

        /// <summary>The window on screen now (null when there is none, or no window manager in this scene).</summary>
        internal static GameWindow Current
        {
            get
            {
                try { var wm = Manager(); return (object)wm == null ? null : wm.CurrentWindow; }
                catch { return null; }
            }
        }

        /// <summary>The window manager of this scene, without the game's own search (its Instance getter searches the
        /// whole scene and logs a warning when there is none).</summary>
        private static WindowsManager Manager()
        {
            var wm = SingletonBehaviour<WindowsManager>.instance;
            return Ui.Alive(wm) ? wm : null;
        }

        /// <summary>The menu bar was built (SharedHooks): the menu's window manager exists by now.</summary>
        internal static void NavBuilt(NavigationPanelController nav) => Subscribe();

        /// <summary>A scene was loaded: a new window manager may have come with it.</summary>
        internal static void SceneReady() { try { Subscribe(); } catch (Exception e) { RevivalMod.Log.Warning("[menu events] " + e.Message); } }

        private static void Subscribe()
        {
            var wm = Manager();
            if ((object)wm == null || wm.Pointer == subscribedTo) return;
            var handler = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool, GameWindow, WindowTabBase>>(
                new Action<bool, GameWindow, WindowTabBase>((isTab, window, tab) => pending = true));
            wm.OnWindowOrTabChanged = wm.OnWindowOrTabChanged == null
                ? handler
                : Il2CppSystem.Delegate.Combine(wm.OnWindowOrTabChanged, handler).Cast<Il2CppSystem.Action<bool, GameWindow, WindowTabBase>>();
            subscribedTo = wm.Pointer;
            shownWindow = IntPtr.Zero; shownTab = IntPtr.Zero; shownTabWindow = null; shownTabObject = null;
            pending = true;
            if (!subscribedLogged) { subscribedLogged = true; RevivalMod.Log.Msg("[menu events] following the game's window and tab changes"); }
        }

        /// <summary>Called every frame: nothing to do unless the game said something changed.</summary>
        internal static void Tick()
        {
            if (!pending) return;
            pending = false;
            try { Dispatch(); }
            catch (Exception e) { RevivalMod.Log.Warning("[menu events] " + e.Message); }
        }

        private static void Dispatch()
        {
            var wm = Manager();
            var window = (object)wm == null ? null : wm.CurrentWindow;
            if (!Ui.Alive(window)) window = null;
            var tabbed = (object)window == null ? null : window.TryCast<GameWindowWithTabs>();
            var tab = (object)tabbed == null ? null : tabbed.CurrentTab;
            if (!Ui.Alive(tab)) tab = null;

            IntPtr w = (object)window == null ? IntPtr.Zero : window.Pointer;
            IntPtr t = (object)tab == null ? IntPtr.Zero : tab.Pointer;
            bool windowChanged = w != shownWindow, tabChanged = t != shownTab || windowChanged;
            if (!windowChanged && !tabChanged) return;

            if (tabChanged && shownTab != IntPtr.Zero && Ui.Alive(shownTabObject))
                Raise(TabHidden, shownTabWindow, shownTabObject);
            shownWindow = w;
            shownTab = t;
            shownTabWindow = tabbed;
            shownTabObject = tab;

            if (windowChanged && (object)window != null)
            {
                Log.Shown(window, tab);
                foreach (Action<GameWindow> h in WindowShown?.GetInvocationList() ?? Array.Empty<Delegate>())
                    try { h(window); } catch (Exception e) { RevivalMod.Log.Warning("[menu events] window shown: " + e.Message); }
                bool atMain = false;
                try { var main = wm.CurrentBaseMenu; atMain = Ui.Alive(main) && main.Pointer == w; } catch { }
                if (atMain)
                    foreach (Action h in BackAtMainMenu?.GetInvocationList() ?? Array.Empty<Delegate>())
                        try { h(); } catch (Exception e) { RevivalMod.Log.Warning("[menu events] main menu: " + e.Message); }
            }
            else if (tabChanged && (object)tab != null) Log.Shown(window, tab);
            if (tabChanged && (object)tab != null) Raise(TabShown, tabbed, tab);
        }

        private static void Raise(Action<GameWindowWithTabs, WindowTabBase> ev, GameWindowWithTabs window, WindowTabBase tab)
        {
            if (ev == null) return;
            foreach (Action<GameWindowWithTabs, WindowTabBase> h in ev.GetInvocationList())
                try { h(window, tab); } catch (Exception e) { RevivalMod.Log.Warning("[menu events] tab: " + e.Message); }
        }

        /// <summary>One line per change of the window or tab on screen: in a bug report this shows what the player
        /// was looking at, in order.</summary>
        private static class Log
        {
            internal static void Shown(GameWindow window, WindowTabBase tab)
            {
                string w = "?", t = null;
                try { w = window.name; } catch { }
                try { if ((object)tab != null) t = tab.GetIl2CppType().Name; } catch { }
                RevivalMod.Log.Msg("[menu events] on screen: " + w + (t == null ? "" : " / " + t));
            }
        }
    }
}
