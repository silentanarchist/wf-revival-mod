using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using UnityEngine;

namespace WarpforgeRevival
{
    /// <summary>
    /// Settings (gear) > Account on a revival server: the game's e-mail login form is reused to link
    /// this game player to an account made on the Revival site (optional). The display name and the
    /// friend code are on the profile page; the helpers for them live here.
    /// </summary>
    internal static class AccountPage
    {
        /// <summary>Checks and saves a new display name; reports (saved, message).</summary>
        internal static void ChangeName(string typed, Action<bool, string> done)
        {
            var pd = PlayerDataManager.singletonManager;
            string wanted = string.Join(" ", (typed ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            if (wanted.Length < MinName || wanted.Length > MaxName) { done(false, Loc.T("account.nameLength", "Display name must be {min}-{max} characters long.", ("min", MinName), ("max", MaxName))); return; }
            if (wanted == CurrentName(pd)) { done(false, Loc.T("account.nameSame", "That is already your name.")); return; }
            Action<bool, Il2CppPlayFab.PlayFabError> answered = (ok, error) =>
            {
                try
                {
                    if (ok)
                    {
                        pd.playerName = wanted;
                        try { PlayerDataManager.OnPlayerNameChanged?.Invoke(wanted); } catch { }
                        RevivalMod.Log.Msg($"[account] display name changed to '{wanted}'");
                        done(true, Loc.T("account.nameSaved", "Name saved."));
                    }
                    else
                    {
                        string why = (object)error != null ? error.ErrorMessage : null;
                        RevivalMod.Log.Msg($"[account] name change refused: {why}");
                        done(false, string.IsNullOrEmpty(why) ? Loc.T("account.nameRefused", "The server did not accept that name.") : why);
                    }
                }
                catch (Exception e) { RevivalMod.Log.Warning("[account] " + e.Message); }
            };
            pd.SetPlayfabDisplayName(wanted, DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool, Il2CppPlayFab.PlayFabError>>(answered));
        }

        internal static string FriendCode(string playFabId)
        {
            if (string.IsNullOrEmpty(playFabId) || playFabId.Length < 8) return "";
            return (playFabId.Substring(0, 4) + "-" + playFabId.Substring(4, 4)).ToUpperInvariant();
        }

        private static void Hide(Component c)
        {
            if ((object)c != null && c.Pointer != IntPtr.Zero) c.gameObject.SetActive(false);
        }

        private static void SetPlaceholder(TMP_InputField input, string text)
        {
            var ph = input.placeholder;
            if ((object)ph == null) return;
            var label = ph.TryCast<TMP_Text>();
            if ((object)label != null) { label.text = text; Ui.BrighterHint(label); }
        }

        private static void Label(Transform form, string child, string text)
        {
            var t = form.Find(child);
            if ((object)t == null) return;
            var loc = t.GetComponent<Il2CppI2.Loc.Localize>();
            if ((object)loc != null) loc.enabled = false;     // otherwise it puts "E-mail"/"Password" back
            var label = t.GetComponent<TMP_Text>();
            if ((object)label != null) label.text = text;
        }

        internal const int MinName = 3, MaxName = 20;

        internal static string CurrentName(PlayerDataManager pd)
        {
            string n = pd.playerName;
            if (string.IsNullOrEmpty(n)) n = pd.playerNameSocial;
            return n ?? "";
        }

        // ------------------------------------------------------------------ account link
        // Settings > Account: the game's e-mail login form, used to link this game player to an
        // account made on the Revival site (username + passphrase). Linking is optional; it lets the
        // server apply the account's permissions (in-game tester) to this player. The passphrase is
        // sent once to make the link and is not kept anywhere on this computer.

        private static volatile string linkedName;          // null: not asked yet, "": not linked
        private static volatile bool tester, busy;
        private static volatile int bugs = -1;              // -1: the server did not say (before 2026-11-05); 0 / 1
        private static bool described;

        /// <summary>Site account this player is linked to ("" when none or not known yet).</summary>
        internal static string LinkedAccount => linkedName ?? "";
        /// <summary>True when the linked site account is an in-game tester.</summary>
        internal static bool IsTester => tester;
        /// <summary>True when this player may send bug reports (the server's "bugReports" setting: testers, everyone or
        /// off); servers that do not say keep the old rule, testers only (0.15.19).</summary>
        internal static bool CanReportBugs => bugs < 0 ? tester : bugs == 1;
        /// <summary>Whether the linked site account has a recovery code: null until the server has said (servers before 2026-10-16 do not).</summary>
        internal static bool? HasRecovery => hasRecovery;
        private static bool? hasRecovery;
        /// <summary>A recovery code was just made in the game (RecoveryCode).</summary>
        internal static void RecoveryMade() => hasRecovery = true;
        /// <summary>True once the server has answered whether this player is linked (and so whether it is a tester).</summary>
        internal static bool StatusKnown => linkedName != null;

        /// <summary>Called when the game has signed in: ask the server whether this player is linked.</summary>
        internal static void SignedIn()
        {
            StatusWithRetry(1);
            ProfileStats.Prefetch();       // your own record, ready before your profile is opened
            // M-13 (0.13.74): what the first match connection needs is asked for now, in the background, so the
            // first match normally has nothing to wait for (settings, the encrypted route, the match ticket)
            try { ServerSettings.Refresh(); MatchTunnel.Probe(); MatchTicket.Refresh(); }
            catch (Exception e) { RevivalMod.Log.Warning("[match] getting ready for matches: " + e.Message); }
        }

        // The answer decides whether this player sees tester-only things (the Testers button). Asked while the
        // game is busiest loading, the request sometimes failed to get through, and the button then stayed
        // away for the whole session (seen on a Pixel 6 Pro, 2026-10-10), so an unreachable server is asked again.
        private static void StatusWithRetry(int attempt) => Send("status", null, null, (ok, message) =>
        {
            if (ok || message != Unreachable || attempt >= 5) return;
            int wait = 2 * attempt;
            RevivalMod.Log.Msg($"[account] asking the server again in {wait} s (try {attempt + 1} of 5)");
            System.Threading.Tasks.Task.Delay(wait * 1000).ContinueWith(_ => PlayFabTransport.OnMainThread(() => StatusWithRetry(attempt + 1)));
        });

        private static string Unreachable => Loc.T("common.unreachableDot", "Could not reach the server.");

        private static string Inner(Exception e)
        {
            string text = e.Message;
            for (var i = e.InnerException; i != null; i = i.InnerException) text += " / " + i.GetType().Name + ": " + i.Message;
            return text;
        }

        private static void Send(string op, string name, string passphrase, Action<bool, string> done)
        {
            string ticket = PlayFabTransport.SessionTicket;
            if (string.IsNullOrEmpty(ticket)) { done?.Invoke(false, Loc.T("account.notSignedInYet", "Not signed in to the server yet.")); return; }
            System.Threading.Tasks.Task.Run(async () =>
            {
                bool ok = false;
                string message;
                try
                {
                    string body = Net.Json(w =>
                    {
                        w.WriteString("op", op);
                        if (name != null) w.WriteString("name", name);
                        if (passphrase != null) w.WriteString("passphrase", passphrase);
                    });
                    string login = op == "unlink" ? GameSignIn.Token() : null;      // the one saved sign-in to cancel
                    var extra = login != null ? new[] { new System.Collections.Generic.KeyValuePair<string, string>("X-Revival-Login", login) } : null;
                    var reply = await Net.PostJson("/playfab/Revival/AccountLink", body, TimeSpan.FromSeconds(15), extra, retry: true);
                    if (reply.Ok)
                    {
                        var data = reply.Data;
                        string linked = data.TryGetProperty("linked", out var l) ? l.GetString() ?? "" : "";
                        bool isTester = data.TryGetProperty("account", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Object &&
                                        a.TryGetProperty("tester", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.True;
                        if (data.TryGetProperty("signedOut", out var so) && so.ValueKind == System.Text.Json.JsonValueKind.True)
                            GameSignIn.SignedOut(op == "unlink" && linked.Length > 0
                                ? Loc.T("account.signedOutOf", "Signed out of {name} on this device. Start the game again to sign in - with this account or another one.", ("name", linked))
                                : Loc.T("account.unlinked", "This game was unlinked from its account. Start the game again and sign in."));
                        if (data.TryGetProperty("recovery", out var rc) && (rc.ValueKind == System.Text.Json.JsonValueKind.True || rc.ValueKind == System.Text.Json.JsonValueKind.False))
                            hasRecovery = linked.Length > 0 && rc.ValueKind == System.Text.Json.JsonValueKind.True;
                        int canBugs = data.TryGetProperty("bugReports", out var br) && (br.ValueKind == System.Text.Json.JsonValueKind.True || br.ValueKind == System.Text.Json.JsonValueKind.False)
                            ? (br.ValueKind == System.Text.Json.JsonValueKind.True ? 1 : 0) : -1;
                        bool changed = linked != linkedName || isTester != tester || canBugs != bugs;
                        linkedName = linked;
                        tester = isTester;
                        bugs = canBugs;
                        ok = true;
                        message = linked.Length > 0 ? (isTester ? Loc.T("account.linkedTester", "Linked to {name} - in-game tester.", ("name", linked)) : Loc.T("account.linked", "Linked to {name}.", ("name", linked))) : Loc.T("account.notLinked", "Not linked to a site account.");
                        if (changed)
                        {
                            RevivalMod.Log.Msg("[account] " + message);
                            PlayFabTransport.OnMainThread(TestersTab.RoleChanged);      // the Testers button follows the role
                        }
                    }
                    else if (reply.Unreachable) message = Unreachable;
                    else
                    {
                        message = reply.Error;
                        RevivalMod.Log.Msg("[account] " + op + " refused: " + message);
                    }
                }
                catch (Exception e)
                {
                    message = Unreachable;
                    RevivalMod.Log.Warning("[account] " + op + ": " + Inner(e));
                }
                if (done != null) PlayFabTransport.OnMainThread(() => done(ok, message));
            });
        }


        private static void Apply(AccountTab tab)
        {
            try
            {
                if (!Ui.Alive(tab)) return;
                bool linked = !string.IsNullOrEmpty(linkedName);

                var user = tab.emailInput;
                var pass = tab.passwordInput;
                if ((object)user != null)
                {
                    user.gameObject.SetActive(true);
                    user.contentType = TMP_InputField.ContentType.Standard;
                    user.characterLimit = 30;
                    user.readOnly = linked;
                    user.interactable = !linked;
                    SetPlaceholder(user, Loc.T("account.siteUsername", "Site username"));
                    if (linked) user.SetTextWithoutNotify(linkedName);
                    user.ForceLabelUpdate();
                    var form = user.transform.parent;
                    if ((object)form != null)
                    {
                        Label(form, "EmailText", Loc.Game("MenuLogin/Login/Username", "Username"));
                        Label(form, "PasswordText", linked ? "" : Loc.Game("MenuLogin/Login/Password", "Password"));
                    }
                }
                if ((object)pass != null)
                {
                    pass.gameObject.SetActive(!linked);          // nothing to type once linked
                    pass.contentType = TMP_InputField.ContentType.Password;
                    pass.characterLimit = 200;
                    pass.interactable = true;
                    SetPlaceholder(pass, Loc.Game("MenuLogin/Login/Password", "Password"));
                    pass.ForceLabelUpdate();
                }

                Hide(tab.loginButton); Hide(tab.logoutButton); Hide(tab.switchAccountButton);
                Hide(tab.resetPasswordButton); Hide(tab.forgotPasswordButton); Hide(tab.deleteaccountButton);
                Hide(tab.newsletterLink); Hide(tab.twitchLink); Hide(tab.socialMediaLinks);

                var button = tab.registerButton;
                if ((object)button != null)
                {
                    button.gameObject.SetActive(true);
                    var group = button.transform.parent;
                    if ((object)group != null) group.gameObject.SetActive(true);
                    button.interactable = !busy;
                    var label = button.GetComponentInChildren<TMP_Text>(true);
                    if ((object)label != null)
                    {
                        Ui.SetText(label, !linked ? Loc.T("account.link", "Link account") : SignedInWithAccount ? Loc.Game("Settings/Account/Logout", "Sign out") : Loc.T("account.unlink", "Unlink account"));
                    }
                }

                try { Arrange(tab, linked); } catch (Exception e) { RevivalMod.Log.Warning("[account] layout: " + e.Message); }

                if (!described)
                {
                    described = true;
                    RevivalMod.Log.Msg($"[account] page set up: {(linked ? "linked to " + linkedName : "not linked")}");
                }
            }
            catch (Exception e) { RevivalMod.Log.Warning("[account] " + e.Message); }
        }

        // ---- "Open profile on website" button once linked (0.13.29; placed on the button's row in 0.13.31).
        private const string ProfileName = "WFR Open profile";
        private static void Arrange(AccountTab tab, bool linked)
        {
            // The page's own layout places the buttons (and puts them back whenever it is laid out again,
            // e.g. when the pointer moves over one), so the Sign out button is left where it puts it. The
            // "Open profile on website" button is kept out of that layout (ignoreLayout) and placed on the
            // same row, starting at the left edge of the username box - well away from Sign out on the right.
            // The message line is not part of that layout; it goes up just under the username box.
            var user = tab.emailInput;
            var button = tab.registerButton;
            if ((object)user == null || (object)button == null) return;
            var userRt = user.GetComponent<RectTransform>();
            var btnT = button.transform;
            if ((object)userRt == null || (object)btnT.parent == null) return;

            var profile = btnT.parent.Find(ProfileName);
            if ((object)profile == null && linked)
            {
                var go = UnityEngine.Object.Instantiate(button.gameObject, btnT.parent, false);
                go.name = ProfileName;
                profile = go.transform;
                var le = go.GetComponent<UnityEngine.UI.LayoutElement>() ?? go.AddComponent<UnityEngine.UI.LayoutElement>();
                le.ignoreLayout = true;
                var copy = go.GetComponent<UnityEngine.UI.Button>();
                if ((object)copy != null)
                {
                    copy.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();     // not the original's "link / sign out"
                    copy.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(new Action(OpenProfile)));
                }
                var label = go.GetComponentInChildren<TMP_Text>(true);
                if ((object)label != null)
                {
                    Loc.Bind(label, () => Loc.T("account.openProfile", "Open profile on website"));
                    label.enableAutoSizing = true;
                }
                RevivalMod.Log.Msg("[account] Open profile button added");
            }
            if ((object)profile == null) return;
            profile.gameObject.SetActive(linked);
            if (!linked) { RecoveryCode.Place(tab, false, userRt, button, ""); return; }
            // the message line (which the page's layout leaves alone) goes up just under the username box,
            // into the space the hidden password row left
            var msg = tab.errorMessage;
            var msgRt = (object)msg == null ? null : msg.GetComponent<RectTransform>();
            if ((object)msgRt != null)
            {
                var u = Ui.WorldBox(userRt);
                var m = Ui.WorldBox(msgRt);
                float target = u.bottom - (u.top - u.bottom) * 0.5f;
                if (Math.Abs(m.top - target) > 0.001f) msg.transform.position += new Vector3(0f, target - m.top, 0f);

                // and the row of buttons goes up under the message line. The row is the buttons' own holder, moved
                // as a whole (the page's layout arranges the buttons inside it, not the holder itself); it is only
                // moved when it holds nothing else of the page (not the username box nor the message line).
                var row = btnT.parent;
                var rowRt = row.GetComponent<RectTransform>();
                if ((object)rowRt != null && !Under(userRt.transform, row) && !Under(msg.transform, row))
                {
                    var brt = button.GetComponent<RectTransform>();
                    if ((object)brt != null)
                    {
                        var b = Ui.WorldBox(brt);
                        m = Ui.WorldBox(msgRt);
                        float lineH = Math.Max(0.0001f, (u.top - u.bottom) * 0.35f);
                        float want = m.bottom - lineH;                      // the buttons' top, a little under the text
                        if (Math.Abs(b.top - want) > 0.001f)
                        {
                            row.position += new Vector3(0f, want - b.top, 0f);
                            if (!rowLogged) { rowLogged = true; RevivalMod.Log.Msg("[account] button row '" + row.name + "' moved up under the message line"); }
                        }
                    }
                }
                else if (!rowLogged) { rowLogged = true; RevivalMod.Log.Msg("[account] button row not moved: its holder '" + row.name + "' also holds other parts of the page"); }
            }
            var prt = profile.GetComponent<RectTransform>();
            if ((object)prt == null) return;
            // same row as the button; its left edge on the username box's left edge
            float userLeft = Ui.WorldBox(userRt).left, half = profile.position.x - Ui.WorldBox(prt).left;
            profile.position = new Vector3(userLeft + half, btnT.position.y, btnT.position.z);
            var c = profile.GetComponent<UnityEngine.UI.Button>();
            if ((object)c != null) c.interactable = true;
            // under this row: password box and "Create recovery code" (0.13.35)
            try { RecoveryCode.Place(tab, true, userRt, button, linkedName ?? ""); }
            catch (Exception e) { RevivalMod.Log.Warning("[account] recovery code part: " + e.Message); }
        }

        /// <summary>Places the page's added parts again (RecoveryCode, after a code was made).</summary>
        internal static void Rearrange(AccountTab tab) => Arrange(tab, !string.IsNullOrEmpty(linkedName));

        private static bool rowLogged;

        private static bool Under(Transform t, Transform ancestor)
        {
            for (; (object)t != null; t = t.parent) if (t.Pointer == ancestor.Pointer) return true;
            return false;
        }

        private static void OpenProfile()
        {
            try
            {
                string site = ServerSettings.CreatorUrl;
                string url;
                try { url = new Uri(site).GetLeftPart(UriPartial.Authority) + "/profile"; } catch { url = site; }
                RevivalMod.Log.Msg("[account] opening the website profile: " + url);
                Application.OpenURL(url);
            }
            catch (Exception e) { RevivalMod.Log.Warning("[account] open profile: " + e.Message); }
        }

        /// <summary>
        /// True when this game signs in with a site account (a saved game login): the account and
        /// its game player are then one thing, and the button only signs this computer or phone out.
        /// </summary>
        private static bool SignedInWithAccount => GameSignIn.Token() != null;

        private static string Status()
        {
            if (linkedName == null) return Loc.T("account.checking", "Checking the link...");
            if (linkedName.Length == 0) return Loc.T("account.optional", "Optional: link an account made on the Revival site. Enter its username and password.");
            return tester ? Loc.T("account.linkedTester", "Linked to {name} - in-game tester.", ("name", linkedName)) : Loc.T("account.linked", "Linked to {name}.", ("name", linkedName));
        }

        private static void Say(AccountTab tab, string text, bool problem = false)
        {
            try
            {
                if (!Ui.Alive(tab)) return;
                var msg = tab.errorMessage;
                if ((object)msg == null) return;
                msg.gameObject.SetActive(true);
                msg.color = problem ? new Color(1f, 0.25f, 0.2f) : new Color(0.75f, 0.85f, 0.8f);
                msg.text = text;
            }
            catch { }
        }

        // ---------------------------------------------------------------- when the tab comes into view
        private static AccountTab seen;
        private static int seenFrames;
        private static float seenNext;
        private static bool seenReported;

        /// <summary>Told about the tab as soon as the Settings window is built (see SupportPage).</summary>
        internal static void Remember(AccountTab tab)
        {
            if (!Ui.Alive(tab)) return;
            // the button row's phone/PC layout, settled at set-up (0.13.55): see MenuLayout.SettlePlatformLayout
            int settled = MenuLayout.SettlePlatformLayout(tab);
            if (settled > 0) RevivalMod.Log.Msg($"[account] phone/PC layout settled at set-up ({settled})");
            // (the page itself is filled in by the Refresh / OnOpen postfixes, in the frame the game opens it)
            seen = tab;
            seenFrames = 0;
        }

        /// <summary>
        /// Called every frame. The game lays this tab out again whenever it is opened - on a phone
        /// quite differently from a PC - and that can undo the changes made above. So each time the
        /// tab comes into view, wait two frames for the layout to finish and apply them once more.
        /// </summary>
        private static int seenLoc;

        internal static void Tick()
        {
            if ((object)seen == null) return;
            // M-11: while the tab is shut or already set up, looked at 4 times a second instead of every frame
            // (the 3-frame settle after it opens still runs frame by frame)
            float now = Time.realtimeSinceStartup;
            if (seenFrames <= 0 && now < seenNext) return;
            seenNext = now + 0.25f;
            try
            {
                if (!Ui.Alive(seen)) { seen = null; return; }
                if (!seen.gameObject.activeInHierarchy) { seenFrames = 0; return; }
                if (seenLoc != Loc.Version && seenFrames < 0) { seenLoc = Loc.Version; Apply(seen); if (!busy) Say(seen, Status()); }   // the language changed while open
                if (seenFrames < 0) return;
                if (++seenFrames < 3) return;
                seenFrames = -1;
                var tab = seen;
                Apply(tab);
                Say(tab, Status());
                if (seenReported) return;
                seenReported = true;
                string State(Component c) => (object)c == null ? "missing" : c.gameObject.activeInHierarchy ? "on screen" : "off";
                RevivalMod.Log.Msg("[account] tab opened: username box " + State(tab.emailInput) + ", password box " + State(tab.passwordInput)
                    + ", link button " + State(tab.registerButton) + ", message line " + State(tab.errorMessage)
                    + ", sign-in button " + State(tab.loginButton) + ", sign-out button " + State(tab.logoutButton));
            }
            catch (Exception e)
            {
                seen = null;
                RevivalMod.Log.Warning("[account] " + e.Message);
            }
        }

        private static void Opened(AccountTab tab)
        {
            Apply(tab);
            Say(tab, Status());
            // the link may have been changed from the site since the game started
            // if this re-check fails but the link is already known (from the sign-in), keep showing it
            Send("status", null, null, (ok, message) => { Apply(tab); bool keep = !ok && StatusKnown; Say(tab, ok || keep ? Status() : message, !ok && !keep); });
        }

#if ANDROID_PORT
        // On the phone "open the page" is a single jump into "refresh the page" - too short to hook
        // safely (see NoShortHooks) - so opening is noticed through the refresh it always causes.
        private static float lastOpened = -100f;

        [HarmonyPatch(typeof(AccountTab), nameof(AccountTab.Refresh))]
        private static class OnRefresh
        {
            private static void Postfix(AccountTab __instance)
            {
                float now = Time.realtimeSinceStartup;
                if (now - lastOpened < 3f) { Apply(__instance); return; }
                lastOpened = now;
                Opened(__instance);
            }
        }
#else
        [HarmonyPatch(typeof(AccountTab), nameof(AccountTab.Refresh))]
        private static class OnRefresh
        {
            private static void Postfix(AccountTab __instance) => Apply(__instance);
        }

        [HarmonyPatch(typeof(AccountTab), nameof(AccountTab.OnOpen))]
        private static class OnOpen
        {
            private static void Postfix(AccountTab __instance) => Opened(__instance);
        }
#endif

        // The page's main button links or unlinks.
        [HarmonyPatch(typeof(AccountTab), nameof(AccountTab.Register))]
        private static class LinkOrUnlink
        {
            private static bool Prefix(AccountTab __instance)
            {
                var tab = __instance;
                try
                {
                    if (busy) return false;
                    bool linked = !string.IsNullOrEmpty(linkedName);
                    string name = null, pass = null;
                    if (!linked)
                    {
                        name = (tab.emailInput.text ?? "").Trim();
                        pass = tab.passwordInput.text ?? "";
                        if (name.Length == 0 || pass.Length == 0) { Say(tab, Loc.T("account.enterBoth", "Enter your site username and password."), true); return false; }
                    }
                    busy = true;
                    Apply(tab);
                    bool signOut = linked && SignedInWithAccount;
                    Say(tab, !linked ? Loc.T("account.linking", "Linking...") : signOut ? Loc.T("account.signingOut", "Signing out...") : Loc.T("account.unlinking", "Unlinking..."));
                    Send(linked ? "unlink" : "link", name, pass, (ok, message) =>
                    {
                        busy = false;
                        try { if (Ui.Alive(tab) && (object)tab.passwordInput != null) tab.passwordInput.SetTextWithoutNotify(""); } catch { }
                        if (ok && !linked) { try { tab.emailInput.SetTextWithoutNotify(linkedName ?? ""); } catch { } }
                        if (ok && linked) { try { tab.emailInput.SetTextWithoutNotify(""); } catch { } }
                        Apply(tab);
                        Say(tab, ok ? Status() : message, !ok);
                    });
                }
                catch (Exception e)
                {
                    busy = false;
                    RevivalMod.Log.Warning("[account] " + e);
                    Say(tab, Loc.T("account.failed", "Could not do that."), true);
                }
                return false;
            }
        }

        // Friend codes contain a dash; the server decides whether a name or code exists.
        [HarmonyPatch(typeof(SupportMethods), nameof(SupportMethods.IsValidFriendName))]
        private static class AnyFriendName
        {
            private static bool Prefix(string friendName, ref bool __result)
            {
                __result = !string.IsNullOrWhiteSpace(friendName);
                return false;
            }
        }
    }
}
