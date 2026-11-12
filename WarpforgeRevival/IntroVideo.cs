using System;
using System.IO;
using HarmonyLib;
using Il2Cpp;

namespace WarpforgeRevival
{
    /// <summary>
    /// Phones only. The loading screen's video, and stopping videos cleanly before a scene change.
    ///
    /// The black menu on a Pixel 6 Pro (2026-10-10, 4 starts in about 20): the picture stays black after
    /// the loading screen while the game reports drawing normally; only rebuilding the screen surface
    /// (a resize, or locking the phone) brings it back. Stopping the video before the scene change
    /// (0.12.54), a WebM copy of the clip (0.12.57) and refreshing Unity's drawing state (0.12.58) did
    /// not stop it. The decoder's teardown error turned out to appear in good starts too. What is left
    /// to test is whether the video is involved at all, so the LoadingVideo setting has three modes:
    ///   Normal  - the game's own player, drawing straight onto the camera's far plane;
    ///   Texture - the player draws into a texture, shown through a plain UI image on the loading
    ///             screen's canvas, so the camera's far-plane video path is never used (candidate fix);
    ///   Off     - the video is stopped as soon as it starts (the test: the loading screen stays dark);
    ///   Still   - as Off, but the clip's last frame (from "6 - fix loading video.bat") is shown as a picture.
    /// Test results 2026-10-11 on the Pixel 6 Pro: Normal and Texture both black about 1 start in 5 (Texture
    /// included), Off 10 of 10 clean - so decoding the video at all is what does it on that phone.
    /// </summary>
    internal static class IntroVideo
    {
#if ANDROID_PORT
        internal static void StopVideos(string why)
        {
            try
            {
                int stopped = 0;
                var names = new System.Collections.Generic.List<string>();
                foreach (var vp in UnityEngine.Object.FindObjectsOfType<UnityEngine.Video.VideoPlayer>())
                {
                    if ((object)vp == null) continue;
                    bool playing = false;
                    try { playing = vp.isPlaying; } catch { }
                    if (!playing) continue;
                    vp.Stop();
                    stopped++;
                    names.Add(vp.name);
                }
                if (stopped > 0) RevivalMod.Log.Msg($"[video] stopped {stopped} playing video(s) before {why}: {string.Join(", ", names)}");
            }
            catch (Exception e) { RevivalMod.Log.Warning("[video] could not stop the videos: " + e.Message); }
            DropTexture();
        }

        // ---------------------------------------------------------------- Texture mode
        private static UnityEngine.RenderTexture texture;
        private static UnityEngine.GameObject textureImage;

        private static string Mode()
        {
            try { return (RevivalMod.Config.LoadingVideo ?? "").Trim(); } catch { return ""; }
        }

        /// <summary>True when the loading video is not decoded at all (LoadingVideo = Still or Off), so the phones'
        /// black menu (from decoding the clip) cannot happen and ScreenCheck has nothing to watch (0.15.24).</summary>
        internal static bool VideoSkipped
        {
            get
            {
                string m = Mode();
                return m.Equals("Still", StringComparison.OrdinalIgnoreCase) || m.Equals("Off", StringComparison.OrdinalIgnoreCase);
            }
        }

        private static void DropTexture()
        {
            try
            {
                if ((object)textureImage != null) { UnityEngine.Object.Destroy(textureImage); textureImage = null; }
                if ((object)texture != null) { texture.Release(); UnityEngine.Object.Destroy(texture); texture = null; }
                if ((object)still != null) { UnityEngine.Object.Destroy(still); still = null; }
            }
            catch { textureImage = null; texture = null; still = null; }
        }

        /// <summary>Points the player at a texture and shows that texture through a UI image behind the loading screen's text.</summary>
        private static UnityEngine.Canvas LoadingCanvas()
        {
            foreach (var c in UnityEngine.Object.FindObjectsOfType<UnityEngine.Canvas>())
            {
                if ((object)c == null || !c.isActiveAndEnabled) continue;
                try { if (c.gameObject.scene.name != "Intro") continue; } catch { continue; }
                if ((object)c.rootCanvas != null && c.rootCanvas.GetInstanceID() != c.GetInstanceID()) continue;
                return c;
            }
            return null;
        }

        /// <summary>A plain image behind the loading screen's text, fitted inside the screen keeping the picture's shape (what the game's player does: FitInside).</summary>
        private static UnityEngine.UI.RawImage ImageOnCanvas(UnityEngine.Canvas canvas, UnityEngine.Texture tex, int w, int h, string name)
        {
            var go = new UnityEngine.GameObject(name);
            go.layer = canvas.gameObject.layer;
            go.transform.SetParent(canvas.transform, false);
            go.transform.SetAsFirstSibling();                       // drawn first: behind the loading text
            var img = go.AddComponent<UnityEngine.UI.RawImage>();
            img.texture = tex;
            img.raycastTarget = false;
            var rt = img.rectTransform;
            rt.anchorMin = rt.anchorMax = new UnityEngine.Vector2(0.5f, 0.5f);
            rt.pivot = new UnityEngine.Vector2(0.5f, 0.5f);
            var canvasRect = canvas.transform.TryCast<UnityEngine.RectTransform>();      // a plain C# cast is not valid on interop objects
            var size = (object)canvasRect != null ? canvasRect.rect.size : new UnityEngine.Vector2(UnityEngine.Screen.width, UnityEngine.Screen.height);
            float scale = Math.Min(size.x / w, size.y / h);
            rt.sizeDelta = new UnityEngine.Vector2(w * scale, h * scale);
            rt.anchoredPosition = UnityEngine.Vector2.zero;
            textureImage = go;
            return img;
        }

        /// <summary>Points the player at a texture and shows that texture through a UI image behind the loading screen's text.</summary>
        private static bool ThroughTexture(UnityEngine.Video.VideoPlayer vp)
        {
            var canvas = LoadingCanvas();
            if ((object)canvas == null) { RevivalMod.Log.Warning("[video] Texture mode: no canvas in the loading scene; the game's own player is left as it is"); return false; }

            int w = 1440, h = 810;
            try { var clip = vp.clip; if ((object)clip != null && clip.width > 0 && clip.height > 0) { w = (int)clip.width; h = (int)clip.height; } } catch { }
            DropTexture();
            texture = new UnityEngine.RenderTexture(w, h, 0);
            texture.name = "WarpforgeRevival intro video";
            texture.Create();
            ImageOnCanvas(canvas, texture, w, h, "WarpforgeRevival intro video");

            vp.Stop();
            vp.renderMode = UnityEngine.Video.VideoRenderMode.RenderTexture;
            vp.targetTexture = texture;
            vp.targetCamera = null;
            return true;
        }

        // ---------------------------------------------------------------- Still mode
        // The clip's last frame as a picture, so the loading screen looks like the end of the video for the
        // whole load while no video is decoded at all. "6 - fix loading video.bat" writes it to the phone as
        // content/intro-last.rgba: raw 32-bit RGBA, 1440x810, bottom row first (what Unity's textures expect).
        private const int StillW = 1440, StillH = 810;
        private static UnityEngine.Texture2D still;

        private static bool ShowStill()
        {
            string path = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "WarpforgeRevival", "content", "intro-last.rgba");
            if (!File.Exists(path) || new FileInfo(path).Length != (long)StillW * StillH * 4)
            {
                RevivalMod.Log.Msg("[video] Still mode: no content/intro-last.rgba on this phone (run \"6 - fix loading video.bat\"); the loading screen stays dark");
                return false;
            }
            var canvas = LoadingCanvas();
            if ((object)canvas == null) { RevivalMod.Log.Warning("[video] Still mode: no canvas in the loading scene"); return false; }
            byte[] bytes = File.ReadAllBytes(path);
            DropTexture();
            still = new UnityEngine.Texture2D(StillW, StillH, UnityEngine.TextureFormat.RGBA32, false);
            still.name = "WarpforgeRevival intro still";
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { still.LoadRawTextureData(handle.AddrOfPinnedObject(), bytes.Length); }
            finally { handle.Free(); }
            still.Apply(false, true);
            ImageOnCanvas(canvas, still, StillW, StillH, "WarpforgeRevival intro still");
            return true;
        }

        // ---------------------------------------------------------------- the loading-screen video
        // Playing the loading screen's H.264 clip goes through the phone's hardware decoder and the screen
        // surface, and on a Pixel 6 Pro that path leaves the picture black (0.12.52-0.12.55 logs). A VP8
        // WebM copy of the same clip is decoded by Unity itself, in software, and never touches that path.
        // "6 - fix loading video.bat" makes that copy from the game's own files and puts it on the phone
        // as UserData/WarpforgeRevival/content/intro.webm. When that file exists, the loading screen's
        // player is pointed at it instead of the built-in clip; otherwise the clip plays as it always did.
        // Deleting the file puts the original back.
        private static float nextLook;
        private static string webmPath;
        private static bool webmChecked, webmExists, swapped, swapLogged;

        private static string WebmPath()
        {
            if (!webmChecked)
            {
                webmChecked = true;
                try
                {
                    webmPath = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "WarpforgeRevival", "content", "intro.webm");
                    webmExists = File.Exists(webmPath) && new FileInfo(webmPath).Length > 100_000;
                    RevivalMod.Log.Msg(webmExists ? "[video] loading-screen video: the WebM copy on this phone will be used (content/intro.webm)"
                                                  : "[video] loading-screen video: the game's own clip (no content/intro.webm on this phone)");
                }
                catch (Exception e) { RevivalMod.Log.Warning("[video] " + e.Message); }
            }
            return webmExists ? webmPath : null;
        }

        // After the loading scene is gone (its video player destroyed with it), Unity's drawing state cache
        // is refreshed a few times over the next seconds (see GpuState), covering the scene unload, the
        // transition and the menu's first draws. Cheap, invisible, and so far the only thing short of a
        // surface rebuild that could stop the black menu.
        // (0.12.58-0.12.61 did that preventive refresh; it was switched off in 0.12.62 and the code removed in 0.13.49.)
        private static string lastScene;

        internal static void Tick()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            // 0.12.62: the three refreshes after the loading scene closed are off. They never prevented a
            // black menu on the Pixel 6 Pro (4 black menus in 14 starts with them), and on a Pixel 8 Pro a
            // one-frame black was seen at the transition right when they ran. The refresh stays as the
            // first fix step in ScreenCheck, where it brought the picture back on the Pixel 8 Pro (00:23).
            if (now < nextLook) return;
            nextLook = now + 0.25f;
            try
            {
                string scene = GameState.Scene;
                if (scene != lastScene) lastScene = scene;
                if (scene != "Intro") { swapped = false; return; }
                if (swapped) return;
                string mode = Mode();
                string webm = WebmPath();
                bool off = mode.Equals("Off", StringComparison.OrdinalIgnoreCase), stillMode = mode.Equals("Still", StringComparison.OrdinalIgnoreCase);
                if (webm == null && !mode.Equals("Texture", StringComparison.OrdinalIgnoreCase) && !off && !stillMode) { swapped = true; return; }
                foreach (var vp in UnityEngine.Object.FindObjectsOfType<UnityEngine.Video.VideoPlayer>())
                {
                    if ((object)vp == null) continue;
                    if (vp.source == UnityEngine.Video.VideoSource.Url) { swapped = true; continue; }
                    bool wasPlaying = vp.isPlaying;
                    if (off || stillMode)
                    {
                        vp.Stop();
                        try { vp.targetCamera = null; } catch { }
                        swapped = true;
                        bool shown = false;
                        if (stillMode) { try { shown = ShowStill(); } catch (Exception e) { RevivalMod.Log.Warning("[video] Still mode: " + e.Message); } }
                        if (!swapLogged)
                        {
                            swapLogged = true;
                            RevivalMod.Log.Msg(stillMode ? $"[video] '{vp.name}' stopped: LoadingVideo = Still ({(shown ? "the clip's last frame is shown instead" : "no picture; the loading screen stays dark")})"
                                                         : $"[video] '{vp.name}' stopped: LoadingVideo = Off (test mode, the loading screen stays dark)");
                        }
                        continue;
                    }
                    bool viaTexture = mode.Equals("Texture", StringComparison.OrdinalIgnoreCase) && ThroughTexture(vp);
                    if (webm != null)
                    {
                        vp.Stop();
                        vp.source = UnityEngine.Video.VideoSource.Url;
                        vp.url = webm;
                    }
                    vp.Play();
                    swapped = true;
                    if (!swapLogged)
                    {
                        swapLogged = true;
                        RevivalMod.Log.Msg($"[video] '{vp.name}' plays {(webm != null ? "the WebM copy" : "the game's clip")}{(viaTexture ? " through a texture (LoadingVideo = Texture)" : "")}{(wasPlaying ? " (the clip had started)" : "")}");
                    }
                }
            }
            catch (Exception e) { nextLook = now + 5f; swapped = true; RevivalMod.Log.Warning("[video] could not switch the loading-screen video: " + e.Message); }
        }

        [HarmonyPatch(typeof(EverguildSceneManager), nameof(EverguildSceneManager.LoadScene), new[] { typeof(string), typeof(string) })]
        private static class LoadFromTo
        {
            private static void Prefix(string from, string to) => StopVideos($"the scene change {from} -> {to}");
        }

        [HarmonyPatch(typeof(EverguildSceneManager), nameof(EverguildSceneManager.LoadScene), new[] { typeof(string) })]
        private static class LoadOne
        {
            private static void Prefix(string scene) => StopVideos("loading the scene " + scene);
        }

        [HarmonyPatch(typeof(EverguildSceneManager), nameof(EverguildSceneManager.UnloadScene))]
        private static class Unload
        {
            private static void Prefix(string scene) => StopVideos("unloading the scene " + scene);
        }
#endif
    }
}
