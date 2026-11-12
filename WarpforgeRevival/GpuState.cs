using System;
using System.Runtime.InteropServices;

namespace WarpforgeRevival
{
    /// <summary>
    /// Makes Unity forget what it thinks is bound on the GPU, so it binds everything afresh on the next draw.
    /// Phones only.
    ///
    /// Why: on a Pixel 6 Pro the menu sometimes comes up black after the loading screen while the game
    /// reports drawing normally (0.12.45-0.12.57 logs: same cameras, same sizes, same settings as a good
    /// start; nothing in the phone's log either). Only rebuilding the drawing surface (a resize, or locking
    /// and unlocking the phone) brings the picture back; making the pipeline's in-between pictures anew
    /// (render scale, HDR) does not. On OpenGL ES that is what a stale state cache looks like: Unity skips
    /// re-binding a texture it believes is already bound, the driver has something else there, and every
    /// draw samples black. A surface rebuild throws that cache away. The loading screen's hardware video
    /// player uses an external texture that is detached from the GL context when the player is destroyed,
    /// which is the kind of thing that leaves the cache wrong (any decoder: it happened with the WebM too).
    ///
    /// GL.InvalidateState() is the direct call for this, but the game's build has it stripped out. The
    /// other way Unity drops its cache is after a native plugin render event (it must assume the plugin
    /// changed GL state). CommandBuffer.IssuePluginEvent survived, so the event's "plugin function" is
    /// libc's getpid: it takes the event number in its first register, ignores it, and returns.
    /// </summary>
    internal static class GpuState
    {
#if ANDROID_PORT
        private static IntPtr fn;
        private static bool failed;
        private static int count;

        [DllImport("libdl", EntryPoint = "dlsym")] private static extern IntPtr dlsym(IntPtr handle, string name);

        /// <summary>Issues the plugin event. Returns false (and says why, once) when it cannot.</summary>
        internal static bool Refresh(string why)
        {
            if (failed) return false;
            try
            {
                if (fn == IntPtr.Zero)
                {
                    try { fn = NativeLibrary.GetExport(NativeLibrary.Load("libc.so"), "getpid"); } catch { }
                    if (fn == IntPtr.Zero) fn = dlsym(IntPtr.Zero, "getpid");      // RTLD_DEFAULT
                    if (fn == IntPtr.Zero) throw new Exception("getpid not found");
                }
                var cb = new UnityEngine.Rendering.CommandBuffer();
                cb.name = "WarpforgeRevival state refresh";
                cb.IssuePluginEvent(fn, 0);
                UnityEngine.Graphics.ExecuteCommandBuffer(cb);
                cb.Dispose();
                count++;
                if (count <= 6) RevivalMod.Log.Msg($"[screen] drawing state refreshed ({why})");
                return true;
            }
            catch (Exception e)
            {
                failed = true;
                RevivalMod.Log.Warning("[screen] could not refresh the drawing state (" + why + "): " + e.Message);
                return false;
            }
        }
#else
        internal static bool Refresh(string why) => false;
#endif
    }
}
