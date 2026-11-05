using System;
using HarmonyLib;
using Il2Cpp;

namespace WarpforgeRevival
{
    /// <summary>
    /// An email (or Google Play) login saved by the original game (0.15.18).
    ///
    /// A player who once signed in to the original game with an email address, or with Google Play on a phone, still
    /// has that login saved (GameStaticData.loginName / loginPassword / loginWithGoogle) if the game was never
    /// uninstalled. LoginManager.TryLoginToPlayfab prefers it over the device sign-in, the server refuses it ("This
    /// server only signs in through the game"), and the game shows "Invalid Parameters." - only its OK button clears the
    /// saved login and retries with the device sign-in, which then works. Players took the popup for a dead end.
    /// The saved login (and the game's backup copy of it) is now cleared before the first try, so the game goes
    /// straight to the device sign-in. Those logins belonged to the original servers and cannot work here.
    /// </summary>
    internal static class OldGameLogin
    {
        [HarmonyPatch(typeof(LoginManager), "TryLoginToPlayfab")]
        private static class ClearOldLogin
        {
            private static void Prefix()
            {
                try
                {
                    bool email = !string.IsNullOrEmpty(GameStaticData.loginName) || !string.IsNullOrEmpty(GameStaticData.loginPassword);
                    bool google = GameStaticData.loginWithGoogle;
                    bool backup = !string.IsNullOrEmpty(GameStaticData.previousLoginName) || GameStaticData.previousLoginWithGoogle;
                    if (!email && !google && !backup) return;
                    GameStaticData.loginName = "";
                    GameStaticData.loginPassword = "";
                    GameStaticData.loginWithGoogle = false;
                    GameStaticData.previousLoginName = "";
                    GameStaticData.previousLoginPassword = "";
                    GameStaticData.previousLoginWithGoogle = false;
                    RevivalMod.Log.Msg("[signin] cleared a login saved by the original game (" +
                                       (google ? "Google Play" : email ? "email" : "backup copy") + "): it cannot sign in here; using the normal sign-in");
                }
                catch (Exception e) { RevivalMod.Log.Warning("[signin] old saved login: " + e.Message); }
            }
        }
    }
}
