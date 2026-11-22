using System;
using System.IO;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace WarpforgeRevival
{
    /// <summary>
    /// The game's content catalog points some locations at Unity's cloud storage (CCD), which is
    /// gone. We rewrite those locations:
    ///  - the remote catalog *hash* -> the catalog hash shipped with the game, so Addressables
    ///    concludes "no catalog update" and keeps using the local catalog;
    ///  - remote content bundles -> UserData/WarpforgeRevival/content/&lt;name&gt; when the mod has
    ///    placed a replacement there (left unchanged otherwise).
    /// Local file paths also sidestep Unity's block on plain-http downloads.
    /// </summary>
    internal static class AddressablesRedirect
    {
        private static bool installed;
        private static string contentDir;

        public static void Install()
        {
            if (installed) return;
            installed = true;
            // Make sure replacement bundles have finished downloading before Addressables needs them.
            if (!RevivalMod.Cards.WaitReady(RevivalMod.Config.TimeoutSeconds * 1000))
                RevivalMod.Log.Warning("[addressables] content sync not finished; continuing with what is on disk");
            contentDir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "WarpforgeRevival", "content");
            Directory.CreateDirectory(contentDir);

            Func<IResourceLocation, string> transform = Transform;
            Addressables.InternalIdTransformFunc =
                DelegateSupport.ConvertDelegate<Il2CppSystem.Func<IResourceLocation, string>>(transform);
            RevivalMod.Log.Msg("[addressables] location redirect installed");
        }

        /// <summary>How often the game asked where a piece of content is (for timing work).</summary>
        internal static long Lookups;

        private static string Transform(IResourceLocation location)
        {
            Lookups++;
            string id = location.InternalId;
            try
            {
                if (id == null || !id.Contains("unity3dusercontent.com")) return id;

                int p = id.IndexOf("path=/", StringComparison.Ordinal);
                string name = p >= 0 ? id.Substring(p + 6) : Path.GetFileName(id);

                if (name.EndsWith(".hash", StringComparison.OrdinalIgnoreCase))
                {
                    // Same hash as the local catalog -> nothing to update.
                    string local = Path.Combine(UnityEngine.Application.streamingAssetsPath, "aa", "catalog.hash");
                    RevivalMod.Log.Msg($"[addressables] {name} -> local catalog hash");
                    return local;
                }

                string replacement = Path.Combine(contentDir, name);
                if (File.Exists(replacement))
                {
                    // Our rebuilt bundle won't match the original CRC; load it from disk without CRC check.
                    var opts = location.Data?.TryCast<UnityEngine.ResourceManagement.ResourceProviders.AssetBundleRequestOptions>();
                    if (opts != null)
                    {
                        opts.Crc = 0;
                        opts.m_UseCrcForCachedBundles = false;
                        opts.m_UseUWRForLocalBundles = false;
                    }
                    RevivalMod.Log.Msg($"[addressables] {name} -> {replacement}");
                    CardsCheck.Noted(name, replacement);           // which card data this run plays with (0.15.30)
                    return replacement;
                }

                RevivalMod.Log.Warning($"[addressables] no replacement for remote content '{name}'");
                CardsCheck.Noted(name, null);
                return id;
            }
            catch (Exception e)
            {
                RevivalMod.Log.Error("[addressables] transform failed: " + e);
                return id;
            }
        }

        // Install just before the game initialises Addressables.
        [HarmonyPatch(typeof(Il2Cpp.AddressablesManager), nameof(Il2Cpp.AddressablesManager.Init))]
        private static class InitPatch
        {
            private static void Prefix() => Install();
        }
    }
}
