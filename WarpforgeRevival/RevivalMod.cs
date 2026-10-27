using System;
using System.IO;
using MelonLoader;

[assembly: MelonInfo(typeof(WarpforgeRevival.RevivalMod), "WF Revival", WarpforgeRevival.RevivalMod.Version, "WF Revival community")]
// The old name next to the version (0.15.3, owner 2026-10-27): mods 0.14.0-0.14.42 find a download's version by
// looking for "Warpforge Revival" followed by it, and refused every build named only "WF Revival" ("the download names
// no version"), so they could not update or sign in. With this they take the current build like any update. Its
// signature must be made under the old file name (WarpforgeRevival.dll / WarpforgeRevival.Android.dll), the name those
// mods check it under; newer mods accept either name. Keep as long as the rename migration is kept.
[assembly: System.Reflection.AssemblyMetadata("Warpforge Revival", WarpforgeRevival.RevivalMod.Version)]
[assembly: MelonGame("Everguild", "Warpforge")]
// The mod applies its hooks itself, one class at a time (SafePatch): MelonLoader's automatic PatchAll stops
// at the first hook that fails. The phone build also pauses the game's garbage collector meanwhile (PatchQuietly).
[assembly: MelonLoader.HarmonyDontPatchAll]

namespace WarpforgeRevival
{
    /// <summary>
    /// Entry point. Milestone 1:
    ///  - reads the server address from UserData/WarpforgeRevival.cfg (MelonPreferences)
    ///  - downloads / caches the card pack from the revival server
    ///  - logs every step of the game's login flow so we can see where it stalls offline
    /// Later milestones add the offline backend, card injection and direct connect.
    /// </summary>
    public class RevivalMod : MelonMod
    {
        #if ANDROID_PORT
        public const string Version = "0.15.5-a";
#else
        public const string Version = "0.15.5-w";
#endif

        /// <summary>
        /// Raised whenever a release changes something both players of a match must agree on (how
        /// the mod alters rules, cards or the match itself). Players are only matched with players
        /// on the same number, so an older mod never meets a newer one in a match. It is the same
        /// for Windows and Android, which is what keeps the two playing each other.
        /// </summary>
        public const int RulesVersion = 1;

        /// <summary>The rules a match is played under: the mod's rules number plus the server's own ("rulesVersion" in its settings).</summary>
        internal static string RulesTag
        {
            get { string server = ServerSettings.Rules; return "r" + RulesVersion + (string.IsNullOrEmpty(server) ? "" : "-" + server); }
        }

        internal static MelonLogger.Instance Log;
        internal static RevivalConfig Config;
        internal static CardPackClient Cards;
        private static bool backgroundNoted;

        /// <summary>The number part of a version: 0.11.0-w -> 0.11.0 (-w is Windows, -a is Android).</summary>
        internal static System.Version Number(string text)
        {
            text = text ?? "";
            int dash = text.IndexOf('-');
            return System.Version.TryParse(dash < 0 ? text : text.Substring(0, dash), out var v) ? v : null;
        }

        /// <summary>SHA-256 of this mod's own file as it was when the game started ("" if it could not be read).</summary>
        internal static string FileHash = "";

        /// <summary>
        /// Names of every other mod and plugin the loader has running, comma separated ("" when this
        /// mod is alone). A server can refuse to sign a game in when others are loaded, so that
        /// everyone on it plays by the same rules.
        /// </summary>
        internal static string OtherMods()
        {
            var names = new System.Collections.Generic.List<string>();
            try
            {
                foreach (var m in MelonBase.RegisteredMelons)
                {
                    if (m == null || m is RevivalMod) continue;
                    string name = null;
                    try { name = m.Info?.Name; } catch { }
                    if (string.IsNullOrWhiteSpace(name)) { try { name = m.MelonAssembly?.Assembly?.GetName().Name; } catch { } }
                    names.Add(string.IsNullOrWhiteSpace(name) ? "unnamed" : name.Replace(',', ' ').Trim());
                }
            }
            catch (Exception e) { Log?.Warning("[mods] could not list the loaded mods: " + e.Message); names.Add("unknown"); }
            return string.Join(", ", names);
        }

#if ANDROID_PORT
        /// <summary>
        /// Applies the mod's hooks with the game's own garbage collector paused. The game's collector and the
        /// loader's .NET each stop every thread with signals to clean up memory; on a Pixel 6 Pro the loader's
        /// .NET crashed while allocating memory in the middle of applying the hooks (a SIGSEGV in
        /// mono_gc_alloc_obj during PatchAll, 2026-10-10), which froze the game on its loading screen. Applying
        /// the ~215 hooks is a few seconds of heavy allocation while the game is loading, so the game's collector
        /// is kept from running during it. It is switched back on straight after, whatever happens.
        /// </summary>
        private void PatchQuietly()
        {
            bool paused = false;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (!Il2CppInterop.Runtime.IL2CPP.il2cpp_gc_is_disabled()) { Il2CppInterop.Runtime.IL2CPP.il2cpp_gc_disable(); paused = true; }
            }
            catch (Exception e) { LoggerInstance.Warning("[startup] could not pause the game's memory clean-up: " + e.Message); }
            try { SafePatch.All(HarmonyInstance, MelonAssembly.Assembly); }
            catch (Exception e) { LoggerInstance.Error("[startup] applying the hooks failed: " + e); }
            finally
            {
                if (paused) try { Il2CppInterop.Runtime.IL2CPP.il2cpp_gc_enable(); } catch { }
            }
            NoNotifications.Apply(HarmonyInstance);      // on their own, after the rest (see NoNotifications)
            LoggerInstance.Msg($"[startup] hooks applied in {clock.ElapsedMilliseconds} ms{(paused ? " with the game's memory clean-up paused" : "")}");
        }
#endif

        /// <summary>This copy stays switched off for this run (another copy of the mod runs; the WF Revival rename, Migration).</summary>
        private static bool inactive;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            // first: the rename (0.15.0) - a copy started from the old file name moves itself, and a second copy stays off
            string here = "";
            try { here = MelonAssembly.Location ?? ""; } catch { }
            if (!Migration.Run(here, Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "WarpforgeRevival")))
            {
                inactive = true;
                LoggerInstance.Msg($"WF Revival {Version} at {here}: switched off for this run (another copy runs)");
                return;
            }
            ScreenCheck.CheckRuntime();      // first: the loader sometimes fails to load parts of .NET (seen on a Pixel 6 Pro)
#if ANDROID_PORT
            RuntimeSettings.Apply();         // before the hooks: they are where the runtime has crashed
            if (!ScreenCheck.RuntimeBroken) PatchQuietly();
#else
            // what MelonLoader's automatic PatchAll did at this point, one hook class at a time (SafePatch)
            try { SafePatch.All(HarmonyInstance, MelonAssembly.Assembly); }
            catch (Exception e) { LoggerInstance.Error("[startup] applying the hooks failed: " + e); }
#endif
            Config = RevivalConfig.Load();
            try
            {
                string file = MelonAssembly.Location ?? "";
                if (file.Length > 0 && System.IO.File.Exists(file))
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                        FileHash = Convert.ToHexString(sha.ComputeHash(System.IO.File.ReadAllBytes(file))).ToLowerInvariant();
            }
            catch (Exception e) { Log.Warning("[mods] could not read this mod's own file: " + e.Message); }
            try { Updater.LoadedFrom = MelonAssembly.Location ?? ""; } catch { }
            Log.Msg($"WF Revival {Version} - server: {Config.ServerUrl}" + (Config.Secure ? " (encrypted)" : " (not encrypted)"));

            string dataDir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "WarpforgeRevival");
            Directory.CreateDirectory(dataDir);

            // First of all: make sure nothing can delete the original game files in the cache.
#if !ANDROID_PORT   // the phone version is given its content files by the mod, so its cache holds nothing to protect
            try { CacheGuard.Apply(HarmonyInstance, dataDir); }
            catch (Exception e) { Log.Warning("[cache] " + e.Message); }
#endif

            BugReport.Start();               // crash/freeze tracking: as early as possible after the settings
            Cards = new CardPackClient(Config.ServerUrl, dataDir);
            Cards.StartSync();
#if !ANDROID_PORT   // Windows only
            NoSteam.Apply(HarmonyInstance);
#endif
            DraftPacks.Apply(HarmonyInstance);
            ServerSettings.Start(Config.ServerUrl);
            CardText.Start(Config.ServerUrl);
            CustomBundle.Start(Config.ServerUrl);
            Loc.Start(Config.ServerUrl, dataDir);
            GlobalChat.Start(Config.ServerUrl);
            Updater.Start(Config.ServerUrl, dataDir);

            if (Config.DiagnosticLogging)
                Diagnostics.Install(HarmonyInstance);
            // PlayFabTransport's patches, like every [HarmonyPatch] class, are applied by SafePatch.All.
        }

        private float nextQuitCheck;
        private bool loggerNoted;

        /// <summary>Closes the game for real (on a phone the game's own exit only puts it in the background).</summary>
        internal static void QuitGame()
        {
            BugReport.End();                 // the mod closing the game is not a crash
#if ANDROID_PORT
            AndroidUpdater.CloseApp();
#else
            UnityEngine.Application.Quit();
#endif
        }

        /// <summary>
        /// The server refused to sign in because this mod is out of date: fetch and install its build
        /// now, without asking, and close the game so the next start uses it. False when that could
        /// not be done (no update offered, updates switched off, download failed).
        /// </summary>
        internal static System.Threading.Tasks.Task<bool> UpdateForServer()
        {
            return Updater.UpdateNow();
        }

        /// <summary>Settings > Mod > Check for updates: what to show, or null when an update has started.</summary>
        internal static System.Threading.Tasks.Task<string> CheckForUpdates()
        {
            return Updater.CheckNow();
        }

        public override void OnGUI()
        {
            if (inactive) return;
            ScreenCheck.OnGUI();
            if (ScreenCheck.RuntimeBroken) return;
            GameSignIn.Draw();
            BugReport.OnGUI();
            PlayerReports.OnGUI();
        }

        public override void OnApplicationQuit() { if (!inactive) BugReport.End(); }

#if ANDROID_PORT
        private static bool graphicsNoted;
#endif
        public override void OnUpdate()
        {
            if (inactive) return;
            if (ScreenCheck.RuntimeBroken) return;     // nothing of the mod can run; the screen says to restart
            // The game freezes when its window loses focus, and a frozen game drops out of the match
            // service within seconds: the room you were waiting in closes and nobody can find you.
#if !ANDROID_PORT   // a phone has no "window in the background", and its game build lacks this setting
            if (Config.RunInBackground && !UnityEngine.Application.runInBackground)
            {
                UnityEngine.Application.runInBackground = true;
                if (!backgroundNoted) { backgroundNoted = true; Log.Msg("[game] the game now keeps running in the background"); }
            }
#endif
            PlayFabTransport.Pump();
#if ANDROID_PORT
            if (!graphicsNoted)
            {
                // Which drawing system the engine really started (the graphics plugin only asks for one).
                graphicsNoted = true;
                try { Log.Msg("[graphics] the game draws with " + UnityEngine.SystemInfo.graphicsDeviceType + " (" + UnityEngine.SystemInfo.graphicsDeviceVersion + ", " + UnityEngine.SystemInfo.graphicsDeviceName + ")"); }
                catch (Exception e) { Log.Msg("[graphics] could not read the drawing system: " + e.Message); }
            }
#endif
            FrameWatch.Tick();
            MatchCounts.Tick();
            CustomBundle.Tick();
            BugReport.Tick();
            ScreenCheck.Tick();
#if ANDROID_PORT
            IntroVideo.Tick();
#endif
            TestersCollection.Tick();
            ChallengeMenu.Tick();
            CommunityMenu.Tick();
            MenuLayout.Tick();
            ChallengeRules.Tick();
            SupportPage.Tick();
            CardArtExport.Tick();
            MulliganPick.Tick();
            Replays.Tick();
            Replays.NoteCards();
            AntiCheat.Tick();
            AccountPage.Tick();
            RecoveryReminder.Tick();
            GameSignIn.Tick();
            FriendsLive.Tick();
            GlobalChat.Tick();
            ProfileRetry.Tick();
            AllianceInvite.Tick();
            FriendRequests.Tick();
            Loc.Tick();
            AlternateArts.Tick();

            // Dev helper: creating UserData/WarpforgeRevival/quit closes the game cleanly
            // (lets the tester restart it without touching the game window).
            if (UnityEngine.Time.realtimeSinceStartup >= nextQuitCheck)
            {
                nextQuitCheck = UnityEngine.Time.realtimeSinceStartup + 1f;
                // The game turns Unity's logger off, which also hides crashes inside menu screens.
                // Keep it on so they are written to Player.log (LocalLow/Everguild/Warpforge).
#if !ANDROID_PORT   // on a phone this would send every game log line to the system log and slow loading down
                try
                {
                    var logger = UnityEngine.Debug.unityLogger;
                    if (logger != null && !logger.logEnabled)
                    {
                        logger.logEnabled = true;
                        if (!loggerNoted) { loggerNoted = true; Log.Msg("Unity logging re-enabled (errors go to Player.log)"); }
                    }
                }
                catch (Exception e) { if (!loggerNoted) { loggerNoted = true; Log.Warning("could not enable Unity logging: " + e.Message); } }
#endif
                var flag = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "WarpforgeRevival", "quit");
                if (File.Exists(flag))
                {
                    try { File.Delete(flag); } catch { }
                    Log.Msg("Quit requested via quit flag");
                    UnityEngine.Application.Quit();
                }
            }
        }
    }
}
