using System;
using HarmonyLib;
using Il2Cpp;

namespace WarpforgeRevival
{
    /// <summary>
    /// Notes every popup the game shows in the log, with the text (or text key) it was given.
    /// A popup that comes up blank is one whose wording is missing; the key in the log says which,
    /// so the wording can be supplied from the server's text-terms.json. Since 0.13.60 such a popup shows readable
    /// text instead of nothing (Readable).
    /// </summary>
    internal static class PopupNotes
    {
        private const string EverguildMail = "support@everguild.com", RevivalMail = "wf-revival@silentanarchist.com";

        /// <summary>Wording for a text key that has none: known server errors in plain words, otherwise the key's last
        /// part split into words ("PlayfabErrors/AccountNotFound" -> "Account not found").</summary>
        internal static string Readable(string key)
        {
            string last = key;
            int slash = last.LastIndexOf('/');
            if (slash >= 0 && slash < last.Length - 1) last = last.Substring(slash + 1);
            switch (last)
            {
                case "APIClientRequestRateLimitExceeded":
                case "APIRequestRateLimitExceeded":
                    return Loc.T("popup.rateLimited", "Too many requests to the server. Wait a minute and try again.");
                case "ServiceUnavailable":
                case "InternalServerError":
                    return Loc.T("popup.serverBusy", "The server could not do that right now. Try again in a moment.");
            }
            // otherwise the key's own words, which stay in English (they come from the key, not from the mod)
            var words = System.Text.RegularExpressions.Regex.Replace(last, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|_", " ").Trim();
            if (words.Length == 0) return Loc.T("popup.somethingWrong", "Something went wrong.");
            words = words.Substring(0, 1).ToUpperInvariant() + words.Substring(1).ToLowerInvariant();
            return words.EndsWith(".") ? words : words + ".";
        }

        [HarmonyPatch(typeof(WindowsManager), nameof(WindowsManager.LoadPopUpAndShow))]
        private static class Shown
        {
            private static void Prefix(ref string text, ref bool localizeTexts, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GameWindowButton> gameWindowButton)
            {
                try
                {
                    text = ChallengeRules.Decorate(text);        // a custom-rules challenge: its rules on the game's popup
                    // The sign-in was refused because the mod is too old and the update could not be downloaded
                    // (2026-11-12: a player on a slow connection saw only "Account not found" and thought the account
                    // was gone). The game shows a PlayFab error popup by its error code; show the server's own words
                    // and how to update instead (0.15.28).
                    string refused = GameSignIn.RefusedForVersion;
                    if (refused != null && localizeTexts && text != null && text.StartsWith("PlayfabErrors/", StringComparison.Ordinal))
                    {
                        GameSignIn.RefusedForVersion = null;
                        RevivalMod.Log.Msg($"[popup] '{text}' after an out-of-date sign-in: the server's message is shown instead");
                        text = refused + "\n\n" + Loc.T("popup.updateByHand",
                            "The mod could not download the update (the connection to the server was too slow or was cut). Your account is fine. Update with the Patcher app or download the new version from the Discord, then start the game again.");
                        localizeTexts = false;
                    }
                    string shown = text;
                    if (localizeTexts && !string.IsNullOrEmpty(text))
                    {
                        try { shown = Il2CppI2.Loc.LocalizationManager.GetTranslation(text, true, 0, true, false, null, null, true); } catch { }
                    }
                    // (the game's English wording is matched here, so this is not translated)
                    // The game's "Error connecting to server" after a match connection that could
                    // not be encrypted: say why instead of pointing at the player's internet.
                    string why = MatchTunnel.Problem;
                    if (why != null && (shown ?? text ?? "").IndexOf("Error connecting to server", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        text = Loc.T("popup.matchConnect", "Could not connect to the match: {reason}.", ("reason", why));
                        localizeTexts = false;
                        shown = text;
                    }
                    // the game's "Error loading match replay" for a replay the mod refused: say why
                    string replayWhy = Replays.Refusal;
                    if (replayWhy != null && (shown ?? text ?? "").IndexOf("loading match replay", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        text = replayWhy;
                        localizeTexts = false;
                        shown = text;
                    }
                    // the game's error popups end "contact support@everguild.com": the publisher is gone, so point at the revival
                    if ((shown ?? "").IndexOf(EverguildMail, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        text = System.Text.RegularExpressions.Regex.Replace(shown, System.Text.RegularExpressions.Regex.Escape(EverguildMail), RevivalMail,
                                                                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        localizeTexts = false;
                        shown = text;
                    }
                    // a text key with no wording would show an empty popup (2026-10-16: the server's rate limit,
                    // "PlayfabErrors/APIClientRequestRateLimitExceeded"): show readable text instead
                    if (localizeTexts && string.IsNullOrEmpty(shown) && !string.IsNullOrEmpty(text))
                    {
                        string original = text;
                        text = Readable(text);
                        localizeTexts = false;
                        shown = text;
                        RevivalMod.Log.Msg($"[popup] no wording for '{original}'; shown as: {text}");
                    }
                    int buttons = (object)gameWindowButton != null ? gameWindowButton.Length : 0;
                    RevivalMod.Log.Msg($"[popup] text {(text == null ? "null" : "'" + text + "'")}, {(localizeTexts ? "a text key" : "literal text")}, {buttons} button(s)" +
                                       (localizeTexts ? $"; wording found: {(string.IsNullOrEmpty(shown) ? "NONE (popup will be blank)" : "'" + shown + "'")}" : ""));
                }
                catch (Exception e) { RevivalMod.Log.Warning("[popup] " + e.Message); }
            }
        }
    }
}
