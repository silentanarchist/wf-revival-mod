using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;

namespace WarpforgeRevival
{
    /// <summary>
    /// Settings > Support pointed at the publisher's closed help pages. It becomes "Mod": the mod's
    /// version, a button ("Open") that opens the revival server's website (its home page) in the browser, and
    /// a "Check for updates" button (a copy of the first one) that asks the server for a newer mod
    /// now. The contact button, the support e-mail text and the privacy policy link are hidden.
    /// </summary>
    internal static class SupportPage
    {
        private const string CheckName = "WFR Check for updates";
        private static string TabName => Loc.T("modTab.title", "Mod");
        private static string ButtonText => Loc.T("modTab.open", "Open");
        private static string CheckText => Loc.T("modTab.checkUpdates", "Check for updates");
        private static string Intro => Loc.T("modTab.intro", "WF Revival mod {version}. The website: design your own cards, manage your account and share decks.", ("version", RevivalMod.Version));

        private static bool reported;

        /// <summary>The website's home page: the site's address without the /creator part.</summary>
        private static string SiteHome()
        {
            string site = ServerSettings.CreatorUrl;
            try { return new Uri(site).GetLeftPart(UriPartial.Authority) + "/"; } catch { return site; }
        }

        private static bool Under(Transform t, Transform ancestor)
        {
            for (; (object)t != null; t = t.parent)
                if (t.Pointer == ancestor.Pointer) return true;
            return false;
        }

        /// <summary>
        /// Hides something for good. The window switches its rows back on when the tab opens, so
        /// switching the object off is not enough: everything it draws is turned off as well, which
        /// also stops it catching clicks.
        /// </summary>
        private static void Hide(Component c)
        {
            if ((object)c == null) return;
            foreach (var g in c.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
                if ((object)g != null) g.enabled = false;
            foreach (var b in c.GetComponentsInChildren<UnityEngine.UI.Selectable>(true))
                if ((object)b != null) b.enabled = false;
            c.gameObject.SetActive(false);
        }

        /// <summary>Switches on everything between a part of the page and the page itself. Returns how many were off.</summary>
        private static int Show(Transform t, Transform root)
        {
            int n = 0;
            for (; (object)t != null && t.Pointer != root.Pointer; t = t.parent)
                if (!t.gameObject.activeSelf) { t.gameObject.SetActive(true); n++; }
            return n;
        }

        private static void Apply(SupportTab tab)
        {
            var faq = tab.faqButton;
            if ((object)faq == null) return;
            var root = tab.transform;
            var faqT = faq.transform;

            // The game's phone/PC layout of this page, settled now (0.13.55), so it cannot undo what follows when
            // the page is first shown (see MenuLayout.SettlePlatformLayout).
            int settled = MenuLayout.SettlePlatformLayout(tab);
            if (settled > 0 && !reported) RevivalMod.Log.Msg($"[support] phone/PC layout settled at set-up ({settled})");

            // Everything that pointed at the publisher goes.
            Hide(tab.contactButton);
            Hide(tab.privacyPolicyButton);
            Hide(tab.termsOfServiceButton);
            Hide(tab.supportButton);

            // The game's own set-up has already given the button its click handler (which opens Url);
            // only the address changes. Initialize would add a second handler: two browser tabs per click.
            faq.Url = SiteHome();

            // The line introducing the button: its nearest earlier neighbour that holds text and no button.
            TMP_Text intro = null;
            var parent = faqT.parent;
            if ((object)parent != null)
                for (int i = faqT.GetSiblingIndex() - 1; i >= 0 && (object)intro == null; i--)
                {
                    var c = parent.GetChild(i);
                    if ((object)c.GetComponentInChildren<UrlButton>(true) != null || (object)c.GetComponentInChildren<EverguildButton>(true) != null) continue;
                    intro = c.GetComponentInChildren<TMP_Text>(true);
                }

            // The page heading is the first text on the page; every other loose line of text is the
            // publisher's ("Do you need help from us?", the support e-mail) and is hidden.
            TMP_Text title = null;
            string oldTitle = null;
            var seen = new List<string>();
            var gone = new List<Transform>();
            foreach (var label in root.GetComponentsInChildren<TMP_Text>(true))
            {
                if ((object)label == null) continue;
                if ((object)check != null && check.Pointer != IntPtr.Zero && Under(label.transform, check)) continue;
                bool inFaq = Under(label.transform, faqT);
                bool isIntro = (object)intro != null && label.Pointer == intro.Pointer;
                seen.Add($"{label.name}='{label.text}'" + (inFaq ? " [button]" : isIntro ? " [intro]" : "")
                    + (label.gameObject.activeInHierarchy ? "" : " (not shown)"));
                if (inFaq) { Loc.Bind(label, () => ButtonText); continue; }
                if (isIntro) { Loc.Bind(label, () => Intro); continue; }
                if ((object)title == null && (object)label.GetComponentInParent<EverguildButton>() == null)
                {
                    title = label;
                    oldTitle = label.text;
                    Loc.Bind(label, () => TabName);
                    continue;
                }
                Hide(label);
                gone.Add(label.transform);
            }

            // What was hidden often sits in a row with its own icon (the link arrow beside the old
            // privacy policy text). Hide the whole row: the largest part of the page around it that
            // holds none of the three things this page keeps.
            foreach (var extra in new Component[] { tab.contactButton, tab.privacyPolicyButton, tab.termsOfServiceButton, tab.supportButton })
                if ((object)extra != null) gone.Add(extra.transform);
            int rows = 0;
            foreach (var t in gone)
            {
                var row = t;
                while ((object)row.parent != null && row.parent.Pointer != root.Pointer && row.Pointer != root.Pointer
                       && !Under(faqT, row.parent)
                       && ((object)intro == null || !Under(intro.transform, row.parent))
                       && ((object)title == null || !Under(title.transform, row.parent)))
                    row = row.parent;
                if (row.Pointer == t.Pointer || row.Pointer == root.Pointer) continue;
                Hide(row);
                rows++;
            }
            if (rows > 0 && !reported) RevivalMod.Log.Msg($"[support] hid {rows} leftover row(s) around the publisher's links");

            AddCheckButton(faq, faqT);
            AddReportButton(faq, faqT);
            AddExportButton(faq, faqT);
            AddCompressBox();
            // "Watch a replay code" lives on Social > Community since 0.14.28 (AddWatchButton kept, unused)

            // The phone layout of this page keeps the button and its introduction switched off and
            // shows its own wording instead (which was just hidden with the rest of the publisher's
            // text). Make sure the two things this page is for can be seen.
            int shown = Show(faqT, root);
            if ((object)intro != null) shown += Show(intro.transform, root);
            if ((object)check != null) shown += Show(check, root);
            ReportVisible(root);
            if ((object)export != null && export.Pointer != IntPtr.Zero) shown += Show(export, root);
            if ((object)compressRow != null && compressRow.Pointer != IntPtr.Zero) { shown += Show(compressRow, root); ShowTick(); }
            if ((object)watch != null && watch.Pointer != IntPtr.Zero) { shown += Show(watch, root); PlaceWatch(faqT); }
            if (shown > 0 && !reported) RevivalMod.Log.Msg($"[support] switched on {shown} hidden part(s) of the page so the button and its text show");

            // The tab's button down the side of the window carries the same word as the heading.
            int renamed = 0;
            var menu = tab.GetComponentInParent<SettingsMenu>();
            if ((object)menu != null && !string.IsNullOrEmpty(oldTitle))
            {
                string term = null;
                var titleLoc = title.GetComponent<Il2CppI2.Loc.Localize>();
                if ((object)titleLoc != null) term = titleLoc.mTerm;
                foreach (var label in menu.GetComponentsInChildren<TMP_Text>(true))
                {
                    if ((object)label == null || Under(label.transform, root)) continue;
                    var loc = label.GetComponent<Il2CppI2.Loc.Localize>();
                    bool same = label.text == oldTitle || ((object)loc != null && !string.IsNullOrEmpty(term) && loc.mTerm == term);
                    if (!same) continue;
                    Loc.Bind(label, () => TabName);
                    renamed++;
                }
            }

            // the Login tab of the same window needs the same care (see AccountPage.Tick)
            try
            {
                var window = tab.GetComponentInParent<SettingsMenu>();
                if ((object)window != null) AccountPage.Remember(window.GetComponentInChildren<AccountTab>(true));
            }
            catch (Exception e) { RevivalMod.Log.Warning("[account] " + e.Message); }

            // remembered for Tick: on a phone the layout changes again when the tab is opened
            page = tab; pageRoot = root; pageButton = faqT; pageIntro = (object)intro != null ? intro.transform : null;
            settledFrames = 0;

            if (reported) return;
            reported = true;
            RevivalMod.Log.Msg($"[support] page now opens {SiteHome()}; heading was '{oldTitle}', side tab labels renamed: {renamed}; texts found: {string.Join(" | ", seen)}");
        }

        // ---- Check for updates
        private static Transform check;
        private static TMP_Text checkLabel;
        private static bool checking;

        /// <summary>A copy of the website button under it, which checks the server for a newer mod.</summary>
        private static void AddCheckButton(UrlButton faq, Transform faqT)
        {
            var parent = faqT.parent;
            if ((object)parent == null) return;
            var old = parent.Find(CheckName);
            if ((object)old != null) { check = old; return; }        // set up already (the page was set up again)
            var go = CopyButton(faq, parent, CheckName, CheckText, OnCheck, "update", out checkLabel);
            if ((object)go == null) return;
            Loc.Bind(checkLabel, () => CheckText);
            check = go.transform;
            check.SetSiblingIndex(faqT.GetSiblingIndex() + 1);
            // no layout group to stack them: put the copy below by hand
            if ((object)parent.GetComponent<UnityEngine.UI.LayoutGroup>() == null)
            {
                var rt = check.Cast<RectTransform>();
                var src = faqT.Cast<RectTransform>();
                rt.anchoredPosition = src.anchoredPosition - new Vector2(0f, src.rect.height + 24f);
            }
            RevivalMod.Log.Msg("[support] Check for updates button added" + ((object)parent.GetComponent<UnityEngine.UI.LayoutGroup>() == null ? " (placed by hand)" : ""));
        }

        private static void Label(string text)
        {
            try { if ((object)checkLabel != null && checkLabel.Pointer != IntPtr.Zero) Ui.SetText(checkLabel, text); } catch { }
        }

        private static void OnCheck()
        {
            if (checking) return;
            checking = true;
            Label(Loc.T("modTab.checking", "Checking..."));
            System.Threading.Tasks.Task.Run(async () =>
            {
                string answer;
                try { answer = await RevivalMod.CheckForUpdates(); }
                catch (Exception e) { RevivalMod.Log.Warning("[update] check button: " + e.Message); answer = Loc.T("common.unreachableDot", "Could not reach the server."); }
                PlayFabTransport.OnMainThread(() =>
                {
                    Label(answer ?? Loc.T("modTab.updating", "Updating..."));
                    if (answer != null) RevivalMod.Log.Msg("[update] check button: " + answer);
                    var at = Time.realtimeSinceStartup;
                    resetAt = answer != null ? at + 4f : 0f;
                    checking = answer == null;      // while updating, the button stays busy
                });
            });
        }

        private static float resetAt;

        // ---- Report a bug (testers): a second copy, under Check for updates. Outside matches this is
        // where testers start a report (F8 also works on PC; in a match there is a tab on the screen edge).
        private const string ReportName = "WFR Report a bug";
        private static Transform report;

        private static void AddReportButton(UrlButton faq, Transform faqT)
        {
            var parent = faqT.parent;
            if ((object)parent == null || (object)check == null) return;
            var old = parent.Find(ReportName);
            if ((object)old != null) { report = old; return; }
            var go = CopyButton(faq, parent, ReportName, Loc.T("modTab.reportBug", "Report a bug"), BugReport.Request, "report", out var reportLabel);
            if ((object)go == null) return;
            Loc.Bind(reportLabel, () => Loc.T("modTab.reportBug", "Report a bug"));
            report = go.transform;
            report.SetSiblingIndex(check.GetSiblingIndex() + 1);
            if ((object)parent.GetComponent<UnityEngine.UI.LayoutGroup>() == null)
            {
                var rt = report.Cast<RectTransform>();
                var src = check.Cast<RectTransform>();
                rt.anchoredPosition = src.anchoredPosition - new Vector2(0f, src.rect.height + 24f);
            }
            report.gameObject.SetActive(AccountPage.CanReportBugs);
        }

        // ---- Export card art: the card pictures from this copy of the game, as a zip for the website
        // (CardArtExport). Under Check for updates and Report a bug; the button's text shows the progress.
        private const string ExportName = "WFR Export card art";
        private static string ExportText => Loc.T("modTab.exportArt", "Export card art");
        private static Transform export;
        private static TMP_Text exportLabel;

        private static void AddExportButton(UrlButton faq, Transform faqT)
        {
            var parent = faqT.parent;
            if ((object)parent == null || (object)check == null) return;
            var old = parent.Find(ExportName);
            if ((object)old != null) { export = old; exportLabel = old.GetComponentInChildren<TMP_Text>(true); return; }
            var go = CopyButton(faq, parent, ExportName, CardArtExport.Running ? Loc.T("modTab.exporting", "Exporting card art...") : ExportText, OnExport, "export", out exportLabel);
            if ((object)go == null) return;
            Loc.Bind(exportLabel, () => CardArtExport.Running ? exportLabel.text : ExportText);
            export = go.transform;
            var after = (object)report != null && report.Pointer != IntPtr.Zero ? report : check;
            export.SetSiblingIndex(after.GetSiblingIndex() + 1);
            if ((object)parent.GetComponent<UnityEngine.UI.LayoutGroup>() == null)
            {
                var rt = export.Cast<RectTransform>();
                var src = check.Cast<RectTransform>();
                rt.anchoredPosition = src.anchoredPosition - new Vector2(0f, 2f * (src.rect.height + 24f));
            }
        }

        // ---- Watch a replay code (0.13.37): the first button of a second column, right of the website
        // button. Plays the replay whose code is on the clipboard (ReplayCodes); the button's text says
        // what is happening.
        private const string WatchName = "WFR Watch replay code";
        private static string WatchText => Loc.T("modTab.watchReplay", "Watch a replay code");
        private static Transform watch;
        private static TMP_Text watchLabel;
        private static float watchResetAt;
        private static bool watchLogged;

        private static void AddWatchButton(UrlButton faq, Transform faqT)
        {
            var parent = faqT.parent;
            if ((object)parent == null) return;
            var old = parent.Find(WatchName);
            if ((object)old != null) { watch = old; watchLabel = old.GetComponentInChildren<TMP_Text>(true); return; }
            var go = CopyButton(faq, parent, WatchName, WatchText, () =>
            {
                ReplayCodes.WatchStatus = text => { WatchLabel(string.IsNullOrEmpty(text) ? WatchText : text); watchResetAt = Time.realtimeSinceStartup + 4f; };
                ReplayCodes.WatchFromClipboard();
            }, "watch", out watchLabel);
            if ((object)go == null) return;
            watch = go.transform;
            Loc.Bind(watchLabel, () => WatchText);
            var le = go.GetComponent<UnityEngine.UI.LayoutElement>() ?? go.AddComponent<UnityEngine.UI.LayoutElement>();
            le.ignoreLayout = true;                     // not stacked with the first column
            PlaceWatch(faqT);
        }

        private static void WatchLabel(string text)
        {
            try { if ((object)watchLabel != null && watchLabel.Pointer != IntPtr.Zero) Ui.SetText(watchLabel, text); } catch { }
        }

        /// <summary>Same row as the website button, one button's width plus a gap to its right.</summary>
        private static void PlaceWatch(Transform faqT)
        {
            try
            {
                if ((object)watch == null || watch.Pointer == IntPtr.Zero || (object)faqT == null) return;
                var src = faqT.Cast<RectTransform>();
                var rt = watch.Cast<RectTransform>();
                var c = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
                src.GetWorldCorners(c);
                float width = Math.Abs(c[2].x - c[0].x);
                watch.position = faqT.position + new Vector3(width * 1.12f, 0f, 0f);
                rt.sizeDelta = src.sizeDelta;
                if (!watchLogged) { watchLogged = true; RevivalMod.Log.Msg($"[support] Watch a replay code button placed right of the website button (width {width:0.00})"); }
            }
            catch (Exception e) { RevivalMod.Log.Warning("[support] watch button: " + e.Message); }
        }

        private static void OnExport()
        {
            if (CardArtExport.Running) return;
            RevivalMod.Log.Msg("[support] Export card art pressed, Compress Card Art " + (compressArt ? "on" : "off"));
            CardArtExport.Start(text =>
            {
                try { if ((object)exportLabel != null && exportLabel.Pointer != IntPtr.Zero) Ui.SetText(exportLabel, text); } catch { }
            }, compressArt);
        }

        // ---- "Compress Card Art" tick box under the export button (0.13.27): on (the default) the art is
        // 512 x 512 as before; off it is written at the game's own size, which takes much longer.
        private const string CompressName = "WFR Compress card art";
        private static string CompressWarning => Loc.T("modTab.compressWarning", "Warning: Slower devices may take 5 minutes or longer if you disable this option.");
        private static bool compressArt = true;
        private static Transform compressRow;
        private static UnityEngine.UI.Image compressTick;

        private static void AddCompressBox()
        {
            if ((object)export == null || export.Pointer == IntPtr.Zero) return;
            var parent = export.parent;
            if ((object)parent == null) return;
            var old = parent.Find(CompressName);
            if ((object)old != null)
            {
                compressRow = old;
                var tick = old.Find("Box/Tick");
                compressTick = (object)tick == null ? null : tick.GetComponent<UnityEngine.UI.Image>();
                ShowTick();
                return;
            }
            var src = export.Cast<RectTransform>();
            float bw = Mathf.Max(200f, src.rect.width), bh = Mathf.Max(40f, src.rect.height);
            float rowH = bh * 1.5f;
            var go = new GameObject(CompressName);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = src.anchorMin; rt.anchorMax = src.anchorMax; rt.pivot = src.pivot;
            rt.sizeDelta = new Vector2(src.sizeDelta.x, rowH);
            go.transform.SetSiblingIndex(export.GetSiblingIndex() + 1);
            var le = go.AddComponent<UnityEngine.UI.LayoutElement>();
            le.preferredHeight = rowH; le.minHeight = rowH; le.preferredWidth = bw;
            if ((object)parent.GetComponent<UnityEngine.UI.LayoutGroup>() == null)
                rt.anchoredPosition = src.anchoredPosition - new Vector2(0f, (bh + rowH) / 2f + 12f);
            compressRow = go.transform;

            var font = (object)exportLabel != null ? exportLabel.font : null;
            float size = (object)exportLabel != null && exportLabel.fontSize > 0 ? exportLabel.fontSize : 28f;
            float boxS = Mathf.Round(bh * 0.5f);

            // the clickable part: the box and its label, the top half of the row
            var hit = new GameObject("Hit");
            var hrt = hit.AddComponent<RectTransform>();
            hrt.SetParent(go.transform, false);
            hrt.anchorMin = new Vector2(0f, 0.45f); hrt.anchorMax = new Vector2(1f, 1f);
            hrt.offsetMin = Vector2.zero; hrt.offsetMax = Vector2.zero;
            var hitImg = hit.AddComponent<UnityEngine.UI.Image>();
            hitImg.color = new Color(0f, 0f, 0f, 0f);                  // invisible, but catches the click
            var btn = hit.AddComponent<UnityEngine.UI.Button>();
            btn.targetGraphic = hitImg;
            btn.onClick.AddListener(Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(new Action(() =>
            {
                if (CardArtExport.Running) return;
                compressArt = !compressArt;
                ShowTick();
                RevivalMod.Log.Msg("[support] Compress Card Art " + (compressArt ? "on" : "off"));
            })));

            var box = new GameObject("Box");
            var brt = box.AddComponent<RectTransform>();
            brt.SetParent(hit.transform, false);
            brt.anchorMin = brt.anchorMax = new Vector2(0f, 0.5f); brt.pivot = new Vector2(0f, 0.5f);
            brt.sizeDelta = new Vector2(boxS, boxS); brt.anchoredPosition = new Vector2(4f, 0f);
            var frame = box.AddComponent<UnityEngine.UI.Image>();
            frame.color = new Color(0.85f, 0.75f, 0.5f, 1f);
            frame.raycastTarget = false;
            var inner = new GameObject("Inner");
            var irt = inner.AddComponent<RectTransform>();
            irt.SetParent(box.transform, false);
            irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one; irt.offsetMin = new Vector2(3f, 3f); irt.offsetMax = new Vector2(-3f, -3f);
            var innerImg = inner.AddComponent<UnityEngine.UI.Image>();
            innerImg.color = new Color(0.08f, 0.07f, 0.06f, 1f);
            innerImg.raycastTarget = false;
            var tickGo = new GameObject("Tick");
            var trt = tickGo.AddComponent<RectTransform>();
            trt.SetParent(box.transform, false);
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = new Vector2(7f, 7f); trt.offsetMax = new Vector2(-7f, -7f);
            compressTick = tickGo.AddComponent<UnityEngine.UI.Image>();
            compressTick.color = new Color(0.95f, 0.8f, 0.35f, 1f);
            compressTick.raycastTarget = false;

            var label = new GameObject("Label");
            var lrt = label.AddComponent<RectTransform>();
            lrt.SetParent(hit.transform, false);
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(boxS + 16f, 0f); lrt.offsetMax = Vector2.zero;
            var lt = label.AddComponent<TextMeshProUGUI>();
            if ((object)font != null) lt.font = font;
            Loc.Bind(lt, () => Loc.T("modTab.compress", "Compress Card Art")); lt.fontSize = size * 0.85f; lt.color = Color.white;
            lt.alignment = TextAlignmentOptions.MidlineLeft; lt.enableWordWrapping = false; lt.raycastTarget = false;

            var warn = new GameObject("Warning");
            var wrt = warn.AddComponent<RectTransform>();
            wrt.SetParent(go.transform, false);
            wrt.anchorMin = new Vector2(0f, 0f); wrt.anchorMax = new Vector2(1f, 0.45f);
            wrt.offsetMin = new Vector2(4f, 0f); wrt.offsetMax = Vector2.zero;
            var wt = warn.AddComponent<TextMeshProUGUI>();
            if ((object)font != null) wt.font = font;
            Loc.Bind(wt, () => CompressWarning); wt.fontSize = size * 0.55f; wt.color = new Color(1f, 0.8f, 0.45f, 1f);
            wt.alignment = TextAlignmentOptions.TopLeft; wt.enableWordWrapping = true; wt.raycastTarget = false;

            ShowTick();
            RevivalMod.Log.Msg("[support] Compress Card Art box added" + ((object)parent.GetComponent<UnityEngine.UI.LayoutGroup>() == null ? " (placed by hand)" : ""));
        }

        private static void ShowTick()
        {
            try { if ((object)compressTick != null && compressTick.Pointer != IntPtr.Zero) compressTick.enabled = compressArt; } catch { }
        }

        /// <summary>The report button only for players the server lets send reports (switched on by the phone layout fix like the others).</summary>
        private static void ReportVisible(Transform root)
        {
            try
            {
                if ((object)report == null || report.Pointer == IntPtr.Zero) return;
                if (AccountPage.CanReportBugs) Show(report, root);
                else if (report.gameObject.activeSelf) report.gameObject.SetActive(false);
            }
            catch { }
        }

        private static SupportTab page;
        private static Transform pageRoot, pageButton, pageIntro;
        private static int settledFrames;
        private static float pageNext;
        private static bool tickReported;

        /// <summary>
        /// Called every frame. The phone layout switches the button and its introduction off when the
        /// tab is opened (after the set-up above has run) and shows its own wording, which this page
        /// hides. So each time the tab comes into view, wait two frames for the layout to finish and
        /// then switch the page's two parts back on.
        /// </summary>
        internal static void Tick()
        {
            if (resetAt > 0f && Time.realtimeSinceStartup >= resetAt) { resetAt = 0f; checking = false; Label(CheckText); }
            if (watchResetAt > 0f && Time.realtimeSinceStartup >= watchResetAt) { watchResetAt = 0f; WatchLabel(WatchText); }
            if ((object)page == null) return;
            // M-11: while the tab is shut or already set up, looked at 4 times a second instead of every frame
            if (settledFrames <= 0 && Time.realtimeSinceStartup < pageNext) return;
            pageNext = Time.realtimeSinceStartup + 0.25f;
            try
            {
                if (page.Pointer == IntPtr.Zero || page.m_CachedPtr == IntPtr.Zero) { page = null; return; }   // the window was closed for good
                if (!page.gameObject.activeInHierarchy) { settledFrames = 0; return; }
                if (settledFrames < 0) return;                  // done for this opening of the tab
                if (++settledFrames < 3) return;
                settledFrames = -1;
                int shown = Show(pageButton, pageRoot);
                if ((object)pageIntro != null) shown += Show(pageIntro, pageRoot);
                if ((object)check != null && check.Pointer != IntPtr.Zero) shown += Show(check, pageRoot);
                ReportVisible(pageRoot);
                if ((object)export != null && export.Pointer != IntPtr.Zero) shown += Show(export, pageRoot);
                if ((object)watch != null && watch.Pointer != IntPtr.Zero) { shown += Show(watch, pageRoot); PlaceWatch(pageButton); }
                if (!tickReported)
                {
                    tickReported = true;
                    var states = new List<string>();
                    foreach (var label in pageRoot.GetComponentsInChildren<TMP_Text>(true))
                        if ((object)label != null)
                            states.Add($"{label.name}='{(label.text ?? "").Replace("\n", " ")}' {(label.gameObject.activeInHierarchy ? "on screen" : "off")}{(label.enabled ? "" : " (hidden by the mod)")}");
                    // since 0.13.55 the layout is settled at set-up, so nothing should need switching on here any more
                    if (shown > 0) RevivalMod.Log.Warning($"[support] tab opened: something switched {shown} part(s) off after set-up; put back. Please send this log. Texts now: {string.Join(" | ", states)}");
                    else RevivalMod.Log.Msg("[support] tab opened: page unchanged since set-up");
                }
            }
            catch (Exception e)
            {
                page = null;
                RevivalMod.Log.Warning("[support] " + e.Message);
            }
        }

        [HarmonyPatch(typeof(SupportTab), nameof(SupportTab.OnSetup))]
        private static class OnSetup
        {
            private static void Postfix(SupportTab __instance)
            {
                try { Apply(__instance); }
                catch (Exception e) { RevivalMod.Log.Warning("[support] " + e.Message); }
            }
        }

        /// <summary>
        /// A copy of the page's website button for one of the mod's own buttons (0.13.75: one routine for the four,
        /// code review M-27): named, its website link removed (the game added the link's click in code, so the copy
        /// never has it; its UrlButton goes too), its text set, and <paramref name="click"/> as its click. Null when
        /// the copy has no button to click (logged).
        /// </summary>
        private static GameObject CopyButton(Component faq, Transform parent, string name, string text, Action click, string what, out TMP_Text label)
        {
            var go = UnityEngine.Object.Instantiate(faq.gameObject, parent, false);
            go.name = name;
            var url = go.GetComponent<UrlButton>();
            if ((object)url != null) UnityEngine.Object.DestroyImmediate(url);
            label = go.GetComponentInChildren<TMP_Text>(true);
            Ui.SetText(label, text);
            var button = go.GetComponent<UnityEngine.UI.Button>() ?? go.GetComponentInChildren<UnityEngine.UI.Button>(true);
            if ((object)button == null) { RevivalMod.Log.Warning($"[support] the {what} button has no button to click"); return null; }
            button.onClick.AddListener(Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(new Action(click)));
            return go;
        }
    }
}
