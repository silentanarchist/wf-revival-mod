using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace WarpforgeRevival
{
    /// <summary>
    /// The custom-card package (claude/custom-bundle-plan.md; built by the kit's build_custom.py from approved creator
    /// cards): custom-tester.json next to custom-cards_&lt;platform&gt;.bundle (the RawCardScript objects) and
    /// custom-art_&lt;platform&gt;.bundle (a texture and sprite per card), in UserData/WarpforgeRevival/content/custom/.
    ///
    /// Loading (0.14.61): the mod opens the two bundles itself (AssetBundle.LoadFromFile) once the game has loaded its
    /// own cards, and hands each custom card to the game's card collection the way the game's own card library does
    /// (AddressableCardLibrary.AssetCompletedLoading with the card's key, as AlternateArts does for card styles), so
    /// it is in the one card list every screen reads. The picture: each card's cardImageReference names an official
    /// picture of its army (so every place that preloads it succeeds), and RawCardScript.cardSprite - the property
    /// the card displays ask for the picture - answers the custom card's own sprite from the art bundle.
    /// (0.14.53-0.14.60 made the package known to Addressables instead; Unity's bundle loader refused the mod-made
    /// bundle location every time - "Invalid path in AssetBundleProvider" - so that route is dropped.)
    /// Texts come from the package too (CardText.SetPackageTexts; per language, English fallback). Where the custom
    /// cards may be seen or used is CustomCards.cs.
    ///
    /// The package comes from the server (0.14.54): at start-up a game with a saved sign-in asks
    /// /api/v1/content/custom/custom-tester.json (the server answers only a tester's or admin's sign-in) and fetches
    /// the files for this platform whose checksum differs from what is in the folder; the manifest is written last, so
    /// a half-finished fetch leaves the previous package usable. A game without the package (not a tester, server
    /// unreachable) plays as before.
    /// </summary>
    internal static class CustomBundle
    {
        private static string Folder => Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "WarpforgeRevival", "content", "custom");
        private static string Platform =>
#if ANDROID_PORT
            "android";
#else
            "pc";
#endif

        /// <summary>The package's version (0 when none is loaded): on the match ticket, and in replays of custom-card modes (0.15.32).</summary>
        internal static int Version { get; private set; }
        internal static int CardCount { get; private set; }

        private static System.Threading.Tasks.Task fetching;
        private static string server;

        /// <summary>Mod start-up: fetch the package in the background when the game has a saved sign-in (the server decides
        /// whether it is a tester's).</summary>
        internal static void Start(string serverUrl)
        {
            server = serverUrl;
            string login = null;
            try { login = GameSignIn.Token(); } catch { }
            if (string.IsNullOrEmpty(login)) { RevivalMod.Log.Msg("[custom] no saved sign-in yet: the custom-card package is not asked for this start"); return; }
            fetching = System.Threading.Tasks.Task.Run(() => Fetch(login));
        }

        private static async System.Threading.Tasks.Task Fetch(string login)
        {
            try
            {
                using var http = Net.Client(TimeSpan.FromSeconds(60));
                http.DefaultRequestHeaders.TryAddWithoutValidation("X-Revival-Login", login);
                using var reply = await http.GetAsync(server + "/api/v1/content/custom/custom-tester.json");
                if (reply.StatusCode == System.Net.HttpStatusCode.Forbidden) { RevivalMod.Log.Msg("[custom] the server's custom-card package is for testers; none fetched"); return; }
                if (reply.StatusCode == System.Net.HttpStatusCode.NotFound) { RevivalMod.Log.Msg("[custom] the server has no custom-card package"); return; }
                if (!reply.IsSuccessStatusCode) { RevivalMod.Log.Warning($"[custom] package manifest: HTTP {(int)reply.StatusCode}"); return; }
                string manifest = await reply.Content.ReadAsStringAsync();
                string folder = Folder;
                Directory.CreateDirectory(folder);
                var wanted = new List<(string name, string sha)>();
                using (var doc = JsonDocument.Parse(manifest))
                {
                    if (doc.RootElement.TryGetProperty("files", out var files) && files.TryGetProperty(Platform, out var mine))
                        foreach (var f in mine.EnumerateObject())
                            wanted.Add((f.Name, f.Value.TryGetProperty("sha256", out var sh) ? sh.GetString() ?? "" : ""));
                }
                if (wanted.Count == 0) { RevivalMod.Log.Warning("[custom] the package manifest lists no files for " + Platform); return; }
                int fetched = 0;
                foreach (var (name, sha) in wanted)
                {
                    if (name.IndexOfAny(new[] { '/', '\\' }) >= 0 || !name.StartsWith("custom-")) continue;
                    string path = Path.Combine(folder, name);
                    if (File.Exists(path) && Sha256(path) == sha) continue;
                    // keeps going for as long as data arrives (0.15.28)
                    var fileReq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, server + "/api/v1/content/custom/" + name);
                    fileReq.Headers.TryAddWithoutValidation("X-Revival-Login", login);
                    var file = await Net.Download(fileReq);
                    if (!file.Ok) { RevivalMod.Log.Warning($"[custom] {name}: HTTP {(int)file.Status}; package not updated"); return; }
                    var data = file.Data;
                    string got = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
                    if (got != sha) { RevivalMod.Log.Warning($"[custom] {name} does not match the server's checksum; package not updated"); return; }
                    File.WriteAllBytes(path + ".new", data);
                    File.Move(path + ".new", path, true);
                    fetched++;
                }
                string manifestPath = Path.Combine(folder, "custom-tester.json");
                if (fetched > 0 || !File.Exists(manifestPath) || File.ReadAllText(manifestPath) != manifest)
                    File.WriteAllText(manifestPath, manifest);
                RevivalMod.Log.Msg($"[custom] custom-card package: {(fetched > 0 ? fetched + " file(s) fetched" : "up to date")}");
            }
            catch (Exception e) { RevivalMod.Log.Warning("[custom] package fetch: " + e.Message); }
        }

        private static string Sha256(string path)
        {
            using var f = File.OpenRead(path);
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(f)).ToLowerInvariant();
        }

        private static bool done;
        private static AssetBundle cardsBundle, artBundle;
        private static readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();

        /// <summary>Called every frame: once the game's own cards are in, the package's cards are added (once).</summary>
        internal static void Tick()
        {
            if (done) return;
            if (fetching != null && !fetching.IsCompleted) return;
            if (!AlternateArts.CardsReady) return;
            done = true;
            try { Load(); } catch (Exception e) { RevivalMod.Log.Warning("[custom] package: " + e); }
        }

        private static void Load()
        {
            string folder = Folder;
            string manifest = Path.Combine(folder, "custom-tester.json");
            if (!File.Exists(manifest)) { RevivalMod.Log.Msg("[custom] no custom-card package in " + folder); return; }
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = doc.RootElement;
            int version = root.TryGetProperty("version", out var v) && v.TryGetInt32(out int vi) ? vi : 0;
            string cardsFile = Path.Combine(folder, "custom-cards_" + Platform + ".bundle");   // the package files for this platform
            string artFile = Path.Combine(folder, "custom-art_" + Platform + ".bundle");
            // a package of updates to official cards only (0.15.32) has no pictures, so no art bundle
            bool newCards = root.TryGetProperty("cards", out var listed) && listed.ValueKind == JsonValueKind.Array && listed.GetArrayLength() > 0;
            foreach (var f in newCards ? new[] { cardsFile, artFile } : new[] { cardsFile })
                if (!File.Exists(f)) { RevivalMod.Log.Warning("[custom] package file missing: " + f); return; }

            var keys = new Dictionary<string, string>();      // card id -> the card's key (its GUID in the inventory)
            foreach (var card in root.GetProperty("cards").EnumerateArray())
            {
                string id = card.GetProperty("id").GetString(), guid = card.GetProperty("guid").GetString();
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(guid)) keys[id] = guid;
            }

            // AssetBundle.LoadAllAssets is stripped from this game's engine (MissingMethodException in 0.14.61), so each asset
            // is asked for by its name in the bundle (the GUID it is listed under), with LoadAllAssetsAsync as the fallback
            var arts = new Dictionary<string, string>();       // card id -> its picture's name in the art bundle
            foreach (var card in root.GetProperty("cards").EnumerateArray())
            {
                string id = card.GetProperty("id").GetString(), art = card.TryGetProperty("art", out var a) ? a.GetString() : null;
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(art)) arts[id] = art;
            }

            if (newCards)
            {
                artBundle = AssetBundle.LoadFromFile(artFile);
                if ((object)artBundle == null) { RevivalMod.Log.Warning("[custom] the art bundle could not be opened"); return; }
            }
            foreach (var kv in arts)
            {
                Sprite sp = null;
                try { sp = artBundle.LoadAsset(kv.Value, Il2CppType.Of<Sprite>())?.TryCast<Sprite>(); } catch (Exception e) { RevivalMod.Log.Warning("[custom] picture " + kv.Key + ": " + e.Message); }
                if ((object)sp == null) sp = FromAll<Sprite>(artBundle, "Custom_" + kv.Key);
                if ((object)sp == null) { RevivalMod.Log.Warning("[custom] no picture for " + kv.Key + " in the art bundle"); continue; }
                sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
                sprites[kv.Key] = sp;
            }

            cardsBundle = AssetBundle.LoadFromFile(cardsFile);
            if ((object)cardsBundle == null) { RevivalMod.Log.Warning("[custom] the card bundle could not be opened"); return; }
            var library = new AddressableCardLibrary();
            int added = 0;
            foreach (var kv in keys)
            {
                RawCardScript card = null;
                try { card = cardsBundle.LoadAsset(kv.Value, Il2CppType.Of<RawCardScript>())?.TryCast<RawCardScript>(); } catch (Exception e) { RevivalMod.Log.Warning("[custom] card " + kv.Key + ": " + e.Message); }
                if ((object)card == null) card = FromAll<RawCardScript>(cardsBundle, null, kv.Key);
                if ((object)card == null) { RevivalMod.Log.Warning("[custom] card " + kv.Key + " is not in the card bundle"); continue; }
                card.hideFlags = HideFlags.DontUnloadUnusedAsset;
                UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle handle =
                    Addressables.ResourceManager.CreateCompletedOperation<RawCardScript>(card, "");
                library.AssetCompletedLoading(handle, kv.Value);
                added++;
            }
            if (root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.Object)
            {
                var texts = new Dictionary<string, Dictionary<string, (string name, string text)>>();
                foreach (var entry in text.EnumerateObject())
                {
                    var langs = new Dictionary<string, (string, string)>();
                    foreach (var lang in entry.Value.EnumerateObject())
                    {
                        string name = lang.Value.TryGetProperty("name", out var n) ? n.GetString() : null;
                        string body = lang.Value.TryGetProperty("text", out var t) ? t.GetString() : null;
                        if (!string.IsNullOrEmpty(name)) langs[lang.Name] = (name, body ?? "");
                    }
                    if (langs.Count > 0) texts[entry.Name] = langs;
                }
                CardText.SetPackageTexts(texts);
            }
            CardUpdates.Load(root, cardsBundle);
            Version = version; CardCount = added;
            int total = 0;
            try { total = PlayerDataManager.singletonManager.allCardCollection.Count; } catch { }
            RevivalMod.Log.Msg($"[custom] custom-card package v{version}: {added} card(s) added to the collection ({total} cards now), {sprites.Count} picture(s), {CardUpdates.Count} update(s) to official cards");
            MatchTicket.Renew();      // the match ticket says which package this game has (0.15.32)
        }

        /// <summary>The fallback: every asset of a type in a bundle (loaded asynchronously, waited for), the one with that name
        /// (or, for cards, that unique id).</summary>
        private static T FromAll<T>(AssetBundle bundle, string name, string cardId = null) where T : UnityEngine.Object
        {
            try
            {
                var req = bundle.LoadAllAssetsAsync(Il2CppType.Of<T>());
                var all = req.allAssets;
                if (all == null) return null;
                foreach (var o in all)
                {
                    var t = o?.TryCast<T>();
                    if ((object)t == null) continue;
                    if (name != null && t.name == name) return t;
                    if (cardId != null && t.TryCast<RawCardScript>()?.uniqueId == cardId) return t;
                }
            }
            catch (Exception e) { RevivalMod.Log.Warning("[custom] loading from the bundle: " + e.Message); }
            return null;
        }

        /// <summary>The picture a card display shows: a custom card's own, from the art bundle.</summary>
        [HarmonyPatch(typeof(RawCardScript), nameof(RawCardScript.cardSprite), MethodType.Getter)]
        private static class Picture
        {
            private static void Postfix(RawCardScript __instance, ref Sprite __result)
            {
                if (sprites.Count == 0) return;
                try
                {
                    string id = __instance.uniqueId;
                    if (id != null && id.StartsWith(CustomCards.Prefix, StringComparison.Ordinal) && sprites.TryGetValue(id, out var sp) && Ui.Alive(sp))
                        __result = sp;
                }
                catch { }
            }
        }
    }
}
