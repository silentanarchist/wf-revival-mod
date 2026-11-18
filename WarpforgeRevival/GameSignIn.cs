using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using UnityEngine;

namespace WarpforgeRevival
{
    /// <summary>
    /// Signing in with an account from the server's website.
    ///
    /// The game signs in by itself (Steam, the phone's device id, or the saved id when Steam is off).
    /// A server can ask for more: "requireAccount" in its settings. The mod then sends the saved
    /// game login (UserData/WarpforgeRevival/game-login.txt) with every sign-in, and when there is
    /// none - or the server no longer accepts it - shows a small window asking for the account's
    /// name and password. The server answers a new game login, which is saved to that file, and
    /// the sign-in goes ahead. The password itself is never stored.
    ///
    /// The window is drawn with Unity's simple built-in GUI (OnGUI), because it has to appear while
    /// the game is still loading, before any of the game's own menus exist.
    /// </summary>
    internal static class GameSignIn
    {
        private const string Prefix = "wfr-login-";

        private static string FilePath => Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "WarpforgeRevival", "game-login.txt");

        /// <summary>The saved game login, or null.</summary>
        internal static string Token()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                string text = File.ReadAllText(FilePath).Trim();
                if (text.StartsWith(Prefix) && text.Length > 40 && text.Length < 200) return text;
                RevivalMod.Log.Warning("[signin] game-login.txt is not a game login - ignored");
            }
            catch (Exception e) { RevivalMod.Log.Warning("[signin] could not read game-login.txt: " + e.Message); }
            return null;
        }

        private static void Save(string token)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, token);
            }
            catch (Exception e) { RevivalMod.Log.Warning("[signin] could not save the game login: " + e.Message); }
        }

        /// <summary>Removes a saved game login the server no longer accepts.</summary>
        internal static void Forget()
        {
            // the saved game sign-in must not stay behind unnoticed (M-20): a file that cannot be deleted is logged
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch (Exception e) { RevivalMod.Log.Warning("[sign-in] could not delete the saved game sign-in file: " + e.Message); }
        }

        /// <summary>
        /// What to do with the server's answer to a sign-in: null to hand it to the game as it is,
        /// otherwise the message to show in the sign-in window before trying again.
        /// </summary>
        internal static string NeedsSignIn(string body)
        {
            if (string.IsNullOrEmpty(body) || body.Length > 2000 || !body.Contains("\"error\"")) return null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;
                string error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
                string message = root.TryGetProperty("errorMessage", out var m) ? m.GetString() : null;
                switch (error)
                {
                    // the mod's own words (in the player's language) rather than the server's English; the window
                    // draws them in the language of the moment (see Known)
                    case "AccountRequired": return KnownMark + error;
                    case "InvalidGameLoginFile": Forget(); return KnownMark + error;
                    case "AccountNotApproved": Forget(); return KnownMark + error;
                }
            }
            catch { }
            return null;
        }

        private const string KnownMark = "\u0001";

        /// <summary>The window's text for one of the server's reasons, in the current language.</summary>
        private static string Known(string code)
        {
            switch (code)
            {
                case "AccountRequired": return Loc.T("signin.required", "This server needs you to sign in with an account from its website.");
                case "InvalidGameLoginFile": return Loc.T("signin.invalidSaved", "Your saved sign-in is no longer valid. Sign in again.");
                case "AccountNotApproved": return Loc.T("signin.notApproved", "Your account is waiting for an admin to approve it.");
                default: return code;
            }
        }

        /// <summary>The message line as shown: a reason from the server is put in words at drawing time.</summary>
        private static string Shown(string text) => text != null && text.StartsWith(KnownMark) ? Known(text.Substring(KnownMark.Length)) : text ?? "";

        // ------------------------------------------------------------------ the window
        private static volatile bool open;
        private static TaskCompletionSource<string> waiting;
        private static string userName = "", password = "", password2 = "", message = "", serverNote = "";
        private static bool busy, problem, drawn;
        // Creating an account: the password twice and a tick box agreeing to the server's Terms and
        // Privacy notice (its /terms page, open to everyone). The same tick box appears when signing
        // in to an account that has not agreed to the current version yet (the server answers
        // "TermsNotAgreed"); "termsBox" tells the server this mod can show it.
        private static bool creating, agreed, termsAsked;
        private static float askedAt;

        /// <summary>
        /// Shows the window (from any thread) and waits for a new game login; null when the window
        /// could not be shown, in which case the game gets the server's answer as it was.
        /// </summary>
        internal static Task<string> Ask(string why)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            PlayFabTransport.OnMainThread(() =>
            {
                waiting?.TrySetResult(null);
                waiting = tcs;
                message = why ?? "";
                problem = false;
                busy = false;
                password = "";
                password2 = "";
                creating = false;
                agreed = false;
                termsAsked = false;
                drawn = false;
                askedAt = Time.realtimeSinceStartup;
                serverNote = null;          // drawn in the current language when shown
                open = true;
                RevivalMod.Log.Msg("[signin] the server asks for an account: " + message);
            });
            return tcs.Task;
        }

        /// <summary>The server's Terms and Privacy notice: /terms on the website (the "creatorUrl" setting's site when known).</summary>
        internal static string TermsUrl()
        {
            try
            {
                string site = ServerSettings.CreatorUrl;
                if (!string.IsNullOrEmpty(site)) return new Uri(site).GetLeftPart(UriPartial.Authority) + "/terms";
            }
            catch { }
            return RevivalMod.Config.ServerUrl.TrimEnd('/') + "/terms";
        }

        private static void OpenTerms()
        {
            string url = TermsUrl();
            try { Ui.OpenUrl(url); RevivalMod.Log.Msg("[signin] opened " + url); }
            catch (Exception e)
            {
                RevivalMod.Log.Warning("[signin] could not open the terms page: " + e.Message);
                Say(Loc.T("signin.openTerms", "Open {url} in a browser to read them.", ("url", url)), false);
            }
        }

        private static string ServerName()
        {
            try { return new Uri(RevivalMod.Config.ServerUrl).Host; } catch { return RevivalMod.Config.ServerUrl; }
        }

        /// <summary>Called every frame from OnUpdate: gives up if the window never gets drawn.</summary>
        // ------------------------------------------------------------------ signed out while playing
        // When the server ends this game's session (the account was unlinked, its password changed,
        // or it was banned) the game cannot carry on: the window says why and the game closes.
        // The next start asks to sign in again.
        private static volatile bool closing;
        private static string closingWhy = "", closingTitle;
        private static bool closingButton = true;
        private static float closeAt;
        private const float CloseAfter = 20f;

        /// <summary>From any thread: the server signed this game out.</summary>
        internal static void SignedOut(string why)
        {
            if (closing) return;
            closing = true;
            Forget();
            PlayFabTransport.OnMainThread(() =>
            {
                closingWhy = string.IsNullOrEmpty(why) ? Loc.T("signin.signedOutByServer", "You were signed out by the server.") : why;
                closeAt = Time.realtimeSinceStartup + CloseAfter;
                RevivalMod.Log.Msg("[signin] signed out by the server: " + closingWhy);
            });
        }

        // ------------------------------------------------------------------ out of date
        /// <summary>True when the server refused a sign-in because this mod is not the one it hands out.</summary>
        internal static bool OutOfDate(string body)
        {
            if (string.IsNullOrEmpty(body) || body.Length > 2000 || !(body.Contains("\"ModOutOfDate\"") || body.Contains("\"ModChanged\""))) return false;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                string e = doc.RootElement.TryGetProperty("error", out var v) ? v.GetString() : null;
                return e == "ModOutOfDate" || e == "ModChanged";
            }
            catch { return false; }
        }

        /// <summary>The server's own words (in the player's language) when it refused the sign-in because this mod is
        /// too old and the update could not be installed; shown on the game's error popup instead of its "Account not
        /// found" (PopupNotes, 0.15.28). Null otherwise.</summary>
        internal static volatile string RefusedForVersion;

        /// <summary>The errorMessage of an out-of-date answer (see OutOfDate), or a general sentence.</summary>
        internal static string OutOfDateMessage(string body)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("errorMessage", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(m.GetString()))
                    return m.GetString().Trim();
            }
            catch { }
            return Loc.T("update.needed", "This server needs a newer WF Revival mod. Downloading it...");
        }

        /// <summary>From any thread: shows "Updating" while the update downloads (no closing yet).</summary>
        internal static void Updating(string text)
        {
            closing = true;
            PlayFabTransport.OnMainThread(() =>
            {
                closingTitle = Loc.T("signin.updatingTitle", "Updating the mod");
                closingWhy = text;
                closingButton = false;
                closeAt = 0f;
            });
        }

        /// <summary>From any thread: the update is in place; the game closes after a short wait.</summary>
        internal static void Updated(string text, float seconds)
        {
            closing = true;
            PlayFabTransport.OnMainThread(() =>
            {
                closingTitle = Loc.T("signin.updatedTitle", "Mod updated");
                closingWhy = text;
                closingButton = false;          // "Close now" froze the game on every system; the countdown closes it
                closeAt = Time.realtimeSinceStartup + seconds;
            });
        }

        /// <summary>From any thread: the update could not be installed; the game goes on to show the server's message.</summary>
        internal static void UpdateFailed()
        {
            PlayFabTransport.OnMainThread(() => { closing = false; closeAt = 0f; });
        }

        /// <summary>Spots the server's "signed out" answer to any request.</summary>
        internal static void Watch(string body)
        {
            if (closing || string.IsNullOrEmpty(body) || body.Length > 2000 || !body.Contains("\"SignedOut\"")) return;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var e) && e.GetString() == "SignedOut")
                    SignedOut(root.TryGetProperty("errorMessage", out var m) ? m.GetString() : null);
            }
            catch { }
        }

        private static void DrawClosing()
        {
            float left = Mathf.Max(0f, closeAt - Time.realtimeSinceStartup);
            if (closeAt > 0 && left <= 0f) { RevivalMod.QuitGame(); return; }
            float scale = Scale();
            float w = Screen.width / scale, h = Screen.height / scale;
            GUI.DrawTexture(R(0, 0, w, h), shade);
            float pw = 640, ph = 300, x = (w - pw) / 2, y = (h - ph) / 2;
            GUI.Box(R(x, y, pw, ph), "", box);
            GUI.Label(R(x + 40, y + 24, pw - 80, 34), closingTitle ?? Loc.T("signin.signedOutTitle", "Signed out"), label);
            GUI.Label(R(x + 40, y + 70, pw - 80, 90), closingWhy, small);
            if (closeAt > 0)
                GUI.Label(R(x + 40, y + 160, pw - 80, 30), Loc.T("signin.closesIn", "The game closes in {seconds} s.", ("seconds", Mathf.CeilToInt(left))), small);
            if (closingButton && GUI.Button(R(x + 40, y + 210, 220, 50), Loc.T("signin.closeNow", "Close now"), button)) RevivalMod.QuitGame();
        }

        private static float lastFrame;

        internal static void Tick()
        {
            // The countdown only runs while the game is drawing. The game can stall for seconds while it
            // loads; time spent stalled does not count, so the message is on screen for the full wait
            // (a phone once closed straight after a stall, without the message ever being seen).
            float now = Time.realtimeSinceStartup;
            float gap = lastFrame > 0f ? now - lastFrame : 0f;
            lastFrame = now;
            if (closing && closeAt > 0 && gap > 0.5f) closeAt += gap;
            // closes even if the window cannot be drawn on this device
            if (closing && closeAt > 0 && Time.realtimeSinceStartup > closeAt + 1f) { RevivalMod.QuitGame(); return; }
            if (!open || drawn || Time.realtimeSinceStartup - askedAt < 10f) return;
            RevivalMod.Log.Error("[signin] the sign-in window could not be shown on this device");
            Close(null);
        }

        private static void Close(string token)
        {
            open = false;
            password = "";
            password2 = "";
            var tcs = waiting;
            waiting = null;
            tcs?.TrySetResult(token);
        }

        private static GUIStyle box, label, small, field, button, nowrap;

        /// <summary>
        /// How wide a text is in this style, in the 1280 x 720 page's units. Measured by Unity where it can; where the
        /// game's build lacks that (or it fails), estimated from the number of letters.
        /// </summary>
        private static float Width(GUIStyle style, string text)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            try { return style.CalcSize(new GUIContent(text)).x / Mathf.Max(0.01f, sc); }
            catch { return text.Length * 22f * 0.6f; }
        }
        private static Texture2D shade, panel;


        // Laid out on a 1280 x 720 page and scaled to the screen by hand: the phone's game build lacks
        // parts of Unity's GUI that would do this (GUI.matrix, GUI.depth), and using them fails there.
        private static float sc = 1f;
        private static Rect R(float x, float y, float w, float h) => new Rect(x * sc, y * sc, w * sc, h * sc);

        private static float Scale()
        {
            sc = Ui.GuiScale();
            label.fontSize = Mathf.RoundToInt(22 * sc);
            small.fontSize = Mathf.RoundToInt(16 * sc);
            field.fontSize = Mathf.RoundToInt(24 * sc);
            button.fontSize = Mathf.RoundToInt(22 * sc);
            nowrap.fontSize = Mathf.RoundToInt(22 * sc);
            return sc;
        }

        private static void Styles()
        {
            if (box != null) return;
            shade = Ui.Solid(new Color(0f, 0f, 0f, 0.75f));
            panel = Ui.Solid(new Color(0.05f, 0.11f, 0.09f, 0.97f));
            box = new GUIStyle(GUI.skin.box);
            box.normal.background = panel;
            label = new GUIStyle(GUI.skin.label) { fontSize = 22, wordWrap = true };
            label.normal.textColor = new Color(0.85f, 0.95f, 0.9f);
            small = new GUIStyle(label) { fontSize = 16 };
            small.normal.textColor = new Color(0.6f, 0.75f, 0.7f);
            field = new GUIStyle(GUI.skin.textField) { fontSize = 24 };
            button = new GUIStyle(GUI.skin.button) { fontSize = 22 };
            nowrap = new GUIStyle(label) { wordWrap = false };          // the field labels: one line, the column made to fit
        }

        /// <summary>Called from the mod's OnGUI.</summary>
        internal static void Draw()
        {
            if (closing)
            {
                try { Styles(); DrawClosing(); }
                catch (Exception e) { RevivalMod.Log.Error("[signin] " + e.Message); RevivalMod.QuitGame(); }
                return;
            }
            if (!open) return;
            try
            {
                Styles();
                drawn = true;
                // Lay out on a 1280 x 720 page scaled to the screen, so it reads the same on a phone.
                float scale = Scale();
                float w = Screen.width / scale, h = Screen.height / scale;
                GUI.DrawTexture(R(0, 0, w, h), shade);

                bool showTerms = creating || termsAsked;
                float pw = 720, ph = (creating ? 560 : 440) + (termsAsked && !creating ? 64 : 0);
                float x = (w - pw) / 2, y = Mathf.Max(10, (h - ph) / 2 - 60);
                GUI.Box(R(x, y, pw, ph), "", box);
                float cx = x + 40, cw = pw - 80, cy = y + 24;
                // the game's language, here too (new players meet this window before they can reach Settings):
                // a button in the window's bottom right corner, beside the server's name
                string langName = LanguageName(Loc.Language());
                if (GUI.Button(R(x + pw - 40 - 220, y + ph - 52, 220, 40), langName + "      ", button)) picking = !picking;
                // the arrow is drawn, not typed: a phone's font may not have the sign (it showed a box)
                GUI.DrawTexture(R(x + pw - 40 - 34, y + ph - 52 + 15, 14, 10), picking ? ArrowUp : ArrowDown);
                if (picking) { DrawLanguages(x, y + 24, pw, ph - 84); return; }
                GUI.Label(R(cx, cy, cw, 34), creating ? Loc.T("signin.createOn", "Create an account on {server}", ("server", ServerName())) : Loc.T("signin.signInTo", "Sign in to {server}", ("server", ServerName())), label); cy += 40;
                GUI.Label(R(cx, cy, cw, 48), creating ? Loc.T("signin.createRules", "Pick a username (5 to 30 letters, digits, . ' - or _) and a password (at least 6 characters, no spaces).")
                                                      : Loc.T("signin.intro", "Use the account you made on the server's website, or create one here."), small); cy += 50;

                // the label column as wide as the longest label in this language (some do not fit 140)
                string tUser = Loc.T("signin.username", "Username"), tPass = Loc.T("signin.password", "Password"), tAgain = Loc.T("signin.again", "Again");
                float lw = Mathf.Clamp(Mathf.Max(Width(label, tUser), Width(label, tPass), creating ? Width(label, tAgain) : 0f) + 12f, 140f, cw * 0.45f);
                GUI.Label(R(cx, cy, lw, 42), tUser, nowrap);
                GUI.SetNextControlName("wfrName");
                userName = GUI.TextField(R(cx + lw + 10, cy, cw - lw - 10, 42), userName ?? "", 30, field); cy += 54;
                GUI.Label(R(cx, cy, lw, 42), tPass, nowrap);
                password = GUI.PasswordField(R(cx + lw + 10, cy, cw - lw - 10, 42), password ?? "", '*', 200, field); cy += 54;
                if (creating)
                {
                    GUI.Label(R(cx, cy, lw, 42), tAgain, nowrap);
                    password2 = GUI.PasswordField(R(cx + lw + 10, cy, cw - lw - 10, 42), password2 ?? "", '*', 200, field); cy += 54;
                }
                if (showTerms)
                {
                    // a button drawn as a tick box: plain buttons work on every build of the game
                    if (GUI.Button(R(cx, cy, 44, 44), agreed ? "X" : "", button)) agreed = !agreed;
                    if (GUI.Button(R(cx + 52, cy, cw - 52 - 130, 44), Loc.T("signin.agree", "I am 16 or older and agree to the Terms and Privacy notice"), small)) agreed = !agreed;
                    if (GUI.Button(R(cx + cw - 120, cy, 120, 44), Loc.T("signin.read", "Read"), button)) OpenTerms();
                    cy += 54;
                }
                cy += 4;

                var keepColor = GUI.color;
                if (problem) GUI.color = new Color(1f, 0.55f, 0.5f);
                GUI.Label(R(cx, cy, cw, 52), busy ? (creating ? Loc.T("signin.creating", "Creating the account...") : Loc.T("signin.signingIn", "Signing in...")) : Shown(message), small);
                GUI.color = keepColor;
                cy += 58;

                GUI.enabled = !busy;
                // the buttons as wide as their words (and at least as wide as before), Quit at the right
                string tCreate = Loc.T("signin.createAccount", "Create account"), tQuit = Loc.T("signin.quit", "Quit");
                string tFirst = creating ? tCreate : Loc.T("signin.signIn", "Sign in"), tSecond = creating ? Loc.T("signin.back", "Back") : tCreate;
                float w1 = Mathf.Max(creating ? 230f : 170f, Width(button, tFirst) + 30f), w2 = Mathf.Max(creating ? 140f : 230f, Width(button, tSecond) + 30f),
                      w3 = Mathf.Max(140f, Width(button, tQuit) + 30f);
                if (creating)
                {
                    bool match = (password ?? "").Length > 0 && password == password2;
                    GUI.enabled = !busy && agreed && match;
                    if (GUI.Button(R(cx, cy, w1, 50), tFirst, button)) Submit(true);
                    GUI.enabled = !busy;
                    if (GUI.Button(R(cx + w1 + 12, cy, w2, 50), tSecond, button)) { creating = false; password2 = ""; agreed = false; Say("", false); }
                    if (!busy && message.Length == 0 && (password2 ?? "").Length > 0 && !match) Say(NoMatch, true);
                }
                else
                {
                    if (GUI.Button(R(cx, cy, w1, 50), tFirst, button)) Submit(false);
                    if (GUI.Button(R(cx + w1 + 12, cy, w2, 50), tSecond, button)) { creating = true; agreed = false; password2 = ""; Say("", false); }
                }
                if (GUI.Button(R(cx + cw - w3, cy, w3, 50), tQuit, button)) RevivalMod.QuitGame();
                GUI.enabled = true;
                cy += 62;
                GUI.Label(R(cx, cy, cw, 30), serverNote ?? (RevivalMod.Config.Secure ? ServerName() : Loc.T("signin.notEncrypted", "{server}  -  this connection is NOT encrypted", ("server", ServerName()))), small);
                if (problem && message == NoMatch && password == password2) Say("", false);

                var e = Event.current;
                if (!busy && !creating && e != null && e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)) Submit(false);
            }
            catch (Exception e)
            {
                RevivalMod.Log.Error("[signin] window: " + e.Message);
                Close(null);
            }
        }

        private static void Submit(bool create)
        {
            string name = (userName ?? "").Trim(), pass = password ?? "";
            if (name.Length == 0 || pass.Length == 0) { Say(Loc.T("signin.needBoth", "Enter your username and password."), true); return; }
            if (create && pass != password2) { Say(NoMatch, true); return; }
            if ((create || termsAsked) && !agreed) { Say(Loc.T("signin.tickTerms", "Tick the box to agree to the Terms and Privacy notice."), true); return; }
            bool agree = (create || termsAsked) && agreed;
            busy = true;
            Task.Run(async () =>
            {
                string token = null, why, error = null;
                try
                {
                    string body = Net.Json(w =>
                    {
                        w.WriteString("name", name);
                        w.WriteString("passphrase", pass);
                        w.WriteBoolean("create", create);
                        w.WriteBoolean("termsBox", true);
                        if (agree) w.WriteBoolean("agreeTerms", true);
                    });
                    var reply = await Net.PostJson("/playfab/Revival/GameAccountSignIn", body, TimeSpan.FromSeconds(20),
                        new[] { new System.Collections.Generic.KeyValuePair<string, string>("X-Revival-Mod", RevivalMod.Version) }, session: false);
                    if (reply.Ok && reply.Data.ValueKind == System.Text.Json.JsonValueKind.Object && reply.Data.TryGetProperty("token", out var t))
                    {
                        token = t.GetString();
                        why = null;
                    }
                    else
                    {
                        error = reply.Code;
                        why = reply.Error ?? Loc.T("signin.refused", "The server did not accept that.");
                    }
                }
                catch (Exception e)
                {
                    why = Loc.T("common.unreachableDot", "Could not reach the server.");
                    RevivalMod.Log.Warning("[signin] " + e.GetType().Name + ": " + e.Message);
                }
                PlayFabTransport.OnMainThread(() =>
                {
                    busy = false;
                    if (token != null)
                    {
                        Save(token);
                        try { RevivalMod.Cards?.SyncAgainAfterSignIn(); } catch (Exception e) { RevivalMod.Log.Warning("[signin] content: " + e.Message); }
                        creating = false;
                        termsAsked = false;
                        RevivalMod.Log.Msg("[signin] signed in as " + name + (create ? " (new account)" : ""));
                        Close(token);
                    }
                    else if (error == "TermsNotAgreed" && !create)
                    {
                        termsAsked = true;
                        agreed = false;
                        Say(Loc.T("signin.termsChanged", "The Terms and Privacy notice have changed. Read them, tick the box and sign in again."), true);
                    }
                    else Say(why, true);
                });
            });
        }

        // ---- choosing the game's language from this window (0.14.9)
        private static bool picking;
        private static readonly string[,] Languages =
        {
            { "en", "English" }, { "es", "Español" }, { "ru", "Русский" }, { "fr", "Français" }, { "de", "Deutsch" },
            { "pt-BR", "Português (Brasil)" }, { "it", "Italiano" }, { "ko", "한국어" }, { "zh-CN", "简体中文" },
            { "ja", "日本語" }, { "pl", "Polski" }, { "cs", "Čeština" }, { "tr", "Türkçe" },
        };

        private static string LanguageName(string code)
        {
            for (int i = 0; i < Languages.GetLength(0); i++) if (Languages[i, 0] == code) return Languages[i, 1];
            return code;
        }

        /// <summary>The languages as a grid of buttons over the form; a click sets the game's own language.</summary>
        private static void DrawLanguages(float x, float y, float pw, float ph)
        {
            int n = Languages.GetLength(0), cols = 2;
            float gap = 10, bw = (pw - 80 - gap) / cols, bh = 40;
            for (int i = 0; i < n; i++)
            {
                float bx = x + 40 + (i % cols) * (bw + gap), by = y + (i / cols) * (bh + 6);
                if (GUI.Button(R(bx, by, bw, bh), Languages[i, 1], button)) { SetGameLanguage(Languages[i, 0]); picking = false; }
            }
        }

        /// <summary>
        /// Sets the game's own language, as Settings > General > Language does: I2's language, kept for the next start
        /// (the game's Settings shows it too), and the mod's texts follow.
        /// </summary>
        private static void SetGameLanguage(string code)
        {
            try
            {
                string name = Il2CppI2.Loc.LocalizationManager.GetLanguageFromCode(code, true);
                if (string.IsNullOrEmpty(name)) name = Il2CppI2.Loc.LocalizationManager.GetLanguageFromCode(code, false);
                if (string.IsNullOrEmpty(name)) { RevivalMod.Log.Warning("[signin] the game has no language " + code); return; }
                Il2CppI2.Loc.LocalizationManager.SetLanguageAndCode(name, code, true, true);
                Loc.LookNow();
                RevivalMod.Log.Msg($"[signin] language set to {name} ({code}) from the sign-in window");
            }
            catch (Exception e) { RevivalMod.Log.Warning("[signin] language: " + e.Message); }
        }

        private static Texture2D arrowDown, arrowUp;
        private static Texture2D ArrowDown => arrowDown != null ? arrowDown : (arrowDown = Arrow(false));
        private static Texture2D ArrowUp => arrowUp != null ? arrowUp : (arrowUp = Arrow(true));

        /// <summary>A small light triangle pointing down (or up), 28 x 20, transparent around it.</summary>
        private static Texture2D Arrow(bool up)
        {
            const int w = 28, h = 20;
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var on = new Color(0.85f, 0.95f, 0.9f, 1f); var off = new Color(0f, 0f, 0f, 0f);
            for (int py = 0; py < h; py++)
            {
                float row = up ? py : h - 1 - py;                // texture rows go up from the bottom
                float half = (w / 2f) * (1f - row / (h - 1f));   // wide at the top of a down arrow, a point at its bottom
                for (int px = 0; px < w; px++) t.SetPixel(px, py, Mathf.Abs(px + 0.5f - w / 2f) <= half ? on : off);
            }
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        private static string NoMatch => Loc.T("signin.noMatch", "The two passwords do not match.");

        private static void Say(string text, bool bad)
        {
            message = text ?? "";
            problem = bad;
        }
    }
}
