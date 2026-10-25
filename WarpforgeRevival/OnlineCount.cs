using System;
using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;

namespace WarpforgeRevival
{
    /// <summary>
    /// "N Players Online" with a green dot on the main menu (0.15.1, owner 2026-10-25): just left of the settings gear
    /// above the chat box, on every main-menu tab (it sits with the gear, so it shows wherever the gear does). The number
    /// comes with every chat poll: the server counts the players whose game asked for chat in the last 45 seconds (the
    /// same rule as a friend's "online" light). No hook of its own: it is put up when the main menu's chat preview is
    /// set up (GlobalChat's existing ChatPreview.OnInitializationComplete postfix), and the chat's per-frame Tick keeps
    /// it beside the gear, hidden with it, and its number current.
    /// </summary>
    internal static class OnlineCount
    {
        private static RectTransform gear;              // the menu's settings button
        private static RectTransform holder;            // the dot and the text, right edge at the gear
        private static TextMeshProUGUI label;
        private static RectTransform dot;
        private static Sprite dotSprite;
        private static int online = -1;                 // from the last chat poll; -1 = not known (older server)
        private static int shown = int.MinValue;
        private static Vector3 placedFor;
        private static bool placedLogged;
        private static readonly Color Green = new Color(0.24f, 0.86f, 0.31f, 1f);
        private const float DotSize = 22f, Gap = 10f, GearGap = 16f;

        /// <summary>From the chat poll (any thread).</summary>
        internal static void Set(int count) => System.Threading.Volatile.Write(ref online, count);

        /// <summary>The settings gear: the menu button whose window is the settings (or, before that window was ever
        /// loaded, the button with "setting" or "option" in its name).</summary>
        private static RectTransform FindGear()
        {
            RectTransform byName = null;
            foreach (var b in UnityEngine.Object.FindObjectsOfType<OpenWindowButton>())
            {
                if ((object)b == null) continue;
                var scene = b.windowToOpenScene;
                if ((object)scene != null && (object)scene.TryCast<SettingsMenu>() != null) return b.GetComponent<RectTransform>();
                string path = RecoveryReminder.Path(b.transform);
                if (byName == null && (path.IndexOf("setting", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       path.IndexOf("option", StringComparison.OrdinalIgnoreCase) >= 0))
                    byName = b.GetComponent<RectTransform>();
            }
            return byName;
        }

        /// <summary>A soft-edged round dot, made once.</summary>
        private static Sprite Dot()
        {
            if (Ui.Alive(dotSprite)) return dotSprite;
            const int n = 64;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            float r = n / 2f - 1f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x + 0.5f - n / 2f) * (x + 0.5f - n / 2f) + (y + 0.5f - n / 2f) * (y + 0.5f - n / 2f));
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(r - d)));
                }
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            dotSprite = Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
            dotSprite.hideFlags = HideFlags.HideAndDontSave;
            return dotSprite;
        }

        /// <summary>Called when the main menu's chat preview is ready (each time the main menu loads).</summary>
        internal static void Attach(ChatPreview p)
        {
            try
            {
                if ((object)p == null || p.channelType != ChatRoom.Global) return;
                var g = FindGear();
                if ((object)g == null) { RevivalMod.Log.Warning("[chat] online count: settings button not found"); return; }
                if (Ui.Alive(holder)) UnityEngine.Object.Destroy(holder.gameObject);
                gear = g;

                var go = new GameObject("RevivalOnlineCount");
                holder = go.AddComponent<RectTransform>();
                holder.SetParent(gear.parent, false);                       // with the gear: shown wherever it is
                holder.pivot = new Vector2(1f, 0.5f);
                holder.sizeDelta = new Vector2(400f, 48f);

                var dg = new GameObject("Dot");
                dot = dg.AddComponent<RectTransform>();
                dot.SetParent(holder, false);
                var img = dg.AddComponent<UnityEngine.UI.Image>();
                img.sprite = Dot();
                img.color = Green;
                img.raycastTarget = false;
                dot.anchorMin = dot.anchorMax = dot.pivot = new Vector2(1f, 0.5f);
                dot.sizeDelta = new Vector2(DotSize, DotSize);
                dot.anchoredPosition = Vector2.zero;

                var tg = new GameObject("Text");
                var trt = tg.AddComponent<RectTransform>();
                trt.SetParent(holder, false);
                label = tg.AddComponent<TextMeshProUGUI>();
                var like = p.GetComponentInChildren<TMP_Text>(true);           // the chat's own text, for the font
                if ((object)like != null) { label.font = like.font; label.fontSharedMaterial = like.fontSharedMaterial; }
                label.color = Color.white;
                label.fontSize = Math.Max(30f, ((object)like != null ? like.fontSize : 22f) * 1.5f);
                label.enableWordWrapping = false;
                label.alignment = TextAlignmentOptions.MidlineRight;
                label.raycastTarget = false;
                trt.anchorMin = trt.anchorMax = trt.pivot = new Vector2(1f, 0.5f);
                trt.sizeDelta = new Vector2(400f, 48f);
                trt.anchoredPosition = new Vector2(-(DotSize + Gap), 0f);

                go.SetActive(false);
                shown = int.MinValue;
                placedFor = Vector3.positiveInfinity;
                RevivalMod.Log.Msg("[chat] online count added left of the settings button " + RecoveryReminder.Path(gear));
            }
            catch (Exception e) { RevivalMod.Log.Warning("[chat] online count: " + e.Message); }
        }

        /// <summary>Every frame, from GlobalChat.Tick: cheap checks only.</summary>
        internal static void Tick()
        {
            if ((object)holder == null) return;
            try
            {
                if (!Ui.Alive(holder) || !Ui.Alive(gear) || !Ui.Alive(label)) { holder = null; gear = null; label = null; return; }
                int n = System.Threading.Volatile.Read(ref online);
                bool want = n > 0 && gear.gameObject.activeInHierarchy;
                if (holder.gameObject.activeSelf != want) holder.gameObject.SetActive(want);
                if (!want) return;
                if (n != shown)
                {
                    shown = n;
                    Ui.SetText(label, n == 1 ? Loc.T("chat.playerOnline", "{count} Player Online", ("count", n))
                                             : Loc.T("chat.playersOnline", "{count} Players Online", ("count", n)));
                }
                // right edge a little left of the gear, level with its middle; again if the menu moves it
                var (top, bottom, left, right) = Ui.WorldBox(gear);
                var at = new Vector3(left, (top + bottom) / 2f, gear.position.z);
                if (at != placedFor)
                {
                    placedFor = at;
                    holder.position = new Vector3(left - GearGap * holder.lossyScale.x, at.y, at.z);
                    if (!placedLogged)
                    {
                        placedLogged = true;
                        RevivalMod.Log.Msg($"[chat] online count placed left of the settings button (button {left:0.##}..{right:0.##} x {bottom:0.##}..{top:0.##})");
                    }
                }
            }
            catch (Exception e) { holder = null; RevivalMod.Log.Warning("[chat] online count: " + e.Message); }
        }
    }
}
