using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Il2Cpp;

namespace WarpforgeRevival
{
    /// <summary>
    /// Which card data this run plays with, and telling the player when the server has newer card data (0.15.30).
    ///
    /// The game loads its card file once, at start. When the server's card file changes (2026-11-22: Everstorm's
    /// "minimum of 1" fixed in the card data), a game started before the change keeps the old cards until it is
    /// restarted, and a match between an old and a new copy breaks as soon as the cards differ.
    ///  - InUse: the SHA-256 of the card file the game actually loaded (taken when AddressablesRedirect hands it the
    ///    file), or "game" when the game used its own copy. It goes with the match ticket request (X-Revival-Cards),
    ///    and the match service only pairs players whose cards are the same (photon_emu SAME_CARDS_ONLY) and refuses
    ///    a friend's join into a room whose players have different cards.
    ///  - Back at the main menu (at most every 5 minutes) the mod asks the server for its current card file; when it
    ///    differs from InUse it says once to restart the game.
    /// </summary>
    internal static class CardsCheck
    {
        /// <summary>SHA-256 (lower-case hex) of the card file this run loaded, "game" for the game's own, or null before it loaded.</summary>
        internal static volatile string InUse;
        private static bool told, started;
        private static DateTime lastAsk = DateTime.MinValue;
        private static volatile bool asking;

        internal static void Start()
        {
            if (started) return;
            started = true;
            MenuEvents.BackAtMainMenu += Ask;
        }

        /// <summary>From AddressablesRedirect: the game asked for content `name`, answered with `file` (null = its own copy).</summary>
        internal static void Noted(string name, string file)
        {
            if (InUse != null || name == null || !name.StartsWith("allcards", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                if (file == null) InUse = "game";
                else
                {
                    using var s = File.OpenRead(file);
                    using var sha = SHA256.Create();
                    InUse = Convert.ToHexString(sha.ComputeHash(s)).ToLowerInvariant();
                }
                RevivalMod.Log.Msg($"[cards] this run plays with card file {(InUse == "game" ? "the game's own copy" : InUse.Substring(0, 12))}");
            }
            catch (Exception e) { RevivalMod.Log.Warning("[cards] could not read the card file in use: " + e.Message); }
        }

        private static void Ask()
        {
            if (told || asking || InUse == null) return;
            if ((DateTime.UtcNow - lastAsk).TotalMinutes < 5) return;
            lastAsk = DateTime.UtcNow;
            asking = true;
            Task.Run(async () =>
            {
                try
                {
                    string current = await CurrentOnServer();
                    if (current == null || current == InUse) return;
                    RevivalMod.Log.Msg($"[cards] the server has newer card data ({current.Substring(0, 12)}); this run plays with " +
                                       (InUse == "game" ? "the game's own copy" : InUse.Substring(0, 12)) + ": asking the player to restart");
                    PlayFabTransport.OnMainThread(Tell);
                }
                catch (Exception e) { RevivalMod.Log.Warning("[cards] could not check the server's card data: " + e.Message); }
                finally { asking = false; }
            });
        }

        private static void Tell()
        {
            if (told) return;
            told = true;
            try
            {
                WindowsManager.Instance?.ShowPopUp(Loc.T("cards.restart",
                    "The card data was updated on the server. Restart the game to play online: until then you are only matched with players who have not restarted either."),
                    false, true, Loc.Game("MainMenu/General/OK", "OK"), (Il2CppSystem.Action)null);
            }
            catch (Exception e) { RevivalMod.Log.Warning("[cards] " + e.Message); }
        }

        /// <summary>The SHA-256 of the server's current card file for this platform, or null.</summary>
        private static async Task<string> CurrentOnServer()
        {
            using var http = Net.Client(TimeSpan.FromSeconds(15), gzip: true);
#if ANDROID_PORT
            string url = RevivalMod.Config.ServerUrl + "/api/v1/content/manifest?platform=android";
#else
            string url = RevivalMod.Config.ServerUrl + "/api/v1/content/manifest";
#endif
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("bundles", out var bundles) || bundles.ValueKind != JsonValueKind.Array) return null;
            foreach (var b in bundles.EnumerateArray())
            {
                if (b.ValueKind != JsonValueKind.Object) continue;
                if (!b.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
                if (!(n.GetString() ?? "").StartsWith("allcards", StringComparison.OrdinalIgnoreCase)) continue;
                if (b.TryGetProperty("sha256", out var s) && s.ValueKind == JsonValueKind.String) return s.GetString()?.ToLowerInvariant();
            }
            return null;
        }
    }
}
