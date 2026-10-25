using System;
using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;

namespace WarpforgeRevival
{
    /// <summary>
    /// "N online" next to the main menu's chat preview (0.15.1, owner 2026-10-25), shown only while that preview is
    /// the Global one and is on screen. The number comes with every chat poll (the server counts the players whose game
    /// asked for chat in the last 45 seconds, the same rule as a friend's "online" light). No hook of its own: the label
    /// is made when the preview is set up (GlobalChat's existing ChatPreview.OnInitializationComplete postfix), and the
    /// chat's per-frame Tick keeps it beside the box, hidden with it, and its number current.
    /// </summary>
    internal static class OnlineCount
    {
        private static ChatPreview preview;
        private static RectTransform box;               // the preview's message box
        private static TextMeshProUGUI label;
        private static int online = -1;                 // from the last chat poll; -1 = not known (older server)
        private static int shown = int.MinValue;
        private static Vector3 placedFor;
        private static bool placedLogged;

        /// <summary>From the chat poll (any thread).</summary>
        internal static void Set(int count) => System.Threading.Volatile.Write(ref online, count);

        /// <summary>Called when the main menu's chat preview is ready.</summary>
        internal static void Attach(ChatPreview p)
        {
            try
            {
                if ((object)p == null || p.channelType != ChatRoom.Global) return;      // the alliance page's preview: no count
                var container = p.messageContainer;
                if ((object)container == null) return;
                box = container.rectTransform;
                preview = p;
                if (Ui.Alive(label)) UnityEngine.Object.Destroy(label.gameObject);
                // a sibling of the box, not a child: the box may clip what lies outside it
                var go = new GameObject("RevivalOnlineCount");
                go.transform.SetParent(box.parent, false);
                label = go.AddComponent<TextMeshProUGUI>();
                var like = p.GetComponentInChildren<TMP_Text>(true);           // the preview's own text, for font and colour
                if ((object)like != null)
                {
                    label.font = like.font;
                    label.fontSharedMaterial = like.fontSharedMaterial;
                    label.color = like.color;
                    label.fontSize = Math.Max(16f, like.fontSize * 0.9f);
                }
                else label.fontSize = 20f;
                label.enableWordWrapping = false;
                label.alignment = TextAlignmentOptions.MidlineLeft;
                label.raycastTarget = false;
                var rt = label.rectTransform;
                rt.pivot = new Vector2(0f, 0.5f);
                rt.sizeDelta = new Vector2(260f, 36f);
                go.SetActive(false);
                shown = int.MinValue;
                placedFor = Vector3.positiveInfinity;
                RevivalMod.Log.Msg("[chat] online count added next to the main-menu chat");
            }
            catch (Exception e) { RevivalMod.Log.Warning("[chat] online count: " + e.Message); }
        }

        /// <summary>Every frame, from GlobalChat.Tick: cheap checks only.</summary>
        internal static void Tick()
        {
            if ((object)label == null) return;
            try
            {
                if (!Ui.Alive(label) || !Ui.Alive(box) || !Ui.Alive(preview)) { label = null; box = null; preview = null; return; }
                int n = System.Threading.Volatile.Read(ref online);
                bool want = n > 0 && box.gameObject.activeInHierarchy;
                if (label.gameObject.activeSelf != want) label.gameObject.SetActive(want);
                if (!want) return;
                if (n != shown)
                {
                    shown = n;
                    Ui.SetText(label, Loc.T("chat.online", "{count} online", ("count", n)));
                }
                // just right of the box, level with its top line; again if the menu moves it
                var (top, bottom, left, right) = Ui.WorldBox(box);
                var at = new Vector3(right, top, 0f);
                if (at != placedFor)
                {
                    placedFor = at;
                    var rt = label.rectTransform;
                    float half = (top - bottom) > 0f ? Math.Min(rt.rect.height * rt.lossyScale.y * 0.5f, (top - bottom) * 0.5f) : 0f;
                    float gap = 12f * rt.lossyScale.x;
                    rt.position = new Vector3(right + gap, top - half, box.position.z);
                    if (!placedLogged)
                    {
                        placedLogged = true;
                        RevivalMod.Log.Msg($"[chat] online count placed right of the chat box (box {left:0.##}..{right:0.##} x {bottom:0.##}..{top:0.##})");
                    }
                }
            }
            catch (Exception e) { label = null; RevivalMod.Log.Warning("[chat] online count: " + e.Message); }
        }
    }
}
