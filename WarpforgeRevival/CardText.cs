using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using HarmonyLib;
using Il2Cpp;

namespace WarpforgeRevival
{
    /// <summary>
    /// Names and rules text for cards the revival adds. The game's own translations do not know these
    /// cards, so their text comes from the server's content/files/card-text.json
    /// ({"SW4": {"name": "Blood Claw", "text": "Ferocity: ..."}}) and is filled in wherever the game
    /// asks a card for its name or description.
    /// </summary>
    internal static class CardText
    {
        private static readonly HttpClient Http = Net.Client(TimeSpan.FromSeconds(15));
        private static volatile Dictionary<string, (string name, string text)> table = new Dictionary<string, (string, string)>();
        private static string url;
        private static DateTime lastFetch = DateTime.MinValue;
        private static volatile bool fetching;

        private static volatile Dictionary<string, string> terms = new Dictionary<string, string>();
        private static string termsUrl;

        public static void Start(string serverUrl)
        {
            url = serverUrl + "/api/v1/content/files/card-text.json";
            termsUrl = serverUrl + "/api/v1/content/files/text-terms.json";
            Refresh();
            Task.Run(async () =>
            {
                try
                {
                    using var reply = await Http.GetAsync(termsUrl);
                    if (!reply.IsSuccessStatusCode) return;
                    using var doc = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
                    var fresh = new Dictionary<string, string>();
                    foreach (var t in doc.RootElement.EnumerateObject())
                        if (t.Value.ValueKind == JsonValueKind.String) fresh[t.Name] = t.Value.GetString();
                    terms = fresh;
                    RevivalMod.Log.Msg($"[cards] {fresh.Count} extra translations loaded from the server");
                }
                catch (Exception e) { RevivalMod.Log.Warning("[cards] could not read extra translations: " + e.Message); }
            });
        }

        // Hunt Marks on the unit whose keyword list is being drawn right now (0 = none, or no unit).
        private static int liveHuntMarks;

        /// <summary>
        /// The keyword list beside a unit in battle: while it is being filled in, the Hunt Mark hint can
        /// show that unit's actual number of marks (an entry named "...huntMark#live" with {0} in the
        /// server's text file) instead of the general wording.
        /// </summary>
        [HarmonyPatch(typeof(TraitDefinitionDisplay), nameof(TraitDefinitionDisplay.DisplayCardAbilities))]
        private static class UnitKeywordList
        {
            private static void Prefix(CardScript __0)
            {
                liveHuntMarks = 0;
                try { if ((object)__0 != null && __0.Pointer != IntPtr.Zero) liveHuntMarks = __0.CurrentHuntMark; }
                catch { }
            }
            private static void Finalizer() { liveHuntMarks = 0; }
        }

        /// <summary>
        /// Fills in texts the game has no translation for (keyword names and their hover hints for
        /// Ferocity, Hunt Mark ...) from the server's content/files/text-terms.json. Texts the game does
        /// have are never touched.
        /// </summary>
        [HarmonyPatch(typeof(Il2CppI2.Loc.LocalizationManager), nameof(Il2CppI2.Loc.LocalizationManager.GetTranslation))]
        private static class MissingTranslation
        {
            private static void Postfix(string __0, ref string __result)
            {
                try
                {
                    if (!string.IsNullOrEmpty(__result) || __0 == null) return;
                    var known = terms;
                    if (known.Count == 0) return;
                    int marks = liveHuntMarks;
                    if (marks > 0 && __0 == "CardTraitDescription/huntMark" && known.TryGetValue(__0 + "#live", out var live))
                        __result = live.Replace("{0}", marks.ToString());
                    else if (known.TryGetValue(__0, out var text)) __result = text;
                    else if (__0.EndsWith("Alt") && known.TryGetValue(__0.Substring(0, __0.Length - 3), out text)) __result = text;
                }
                catch { }
            }
        }

        /// <summary>Reads the text again in the background, at most once every five minutes.</summary>
        public static void Refresh()
        {
            if (url == null || fetching || (DateTime.UtcNow - lastFetch).TotalSeconds < 300) return;
            fetching = true;
            lastFetch = DateTime.UtcNow;
            Task.Run(async () =>
            {
                try
                {
                    using var reply = await Http.GetAsync(url);
                    if (!reply.IsSuccessStatusCode) return;       // a server without added cards
                    using var doc = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
                    var fresh = new Dictionary<string, (string, string)>();
                    foreach (var card in doc.RootElement.EnumerateObject())
                    {
                        string name = card.Value.TryGetProperty("name", out var n) ? n.GetString() : null;
                        string text = card.Value.TryGetProperty("text", out var t) ? t.GetString() : null;
                        fresh[card.Name] = (name ?? "", text ?? "");
                    }
                    if (fresh.Count != table.Count) RevivalMod.Log.Msg($"[cards] text for {fresh.Count} added cards loaded from the server");
                    table = fresh;
                }
                catch (Exception e) { RevivalMod.Log.Warning("[cards] could not read card text from the server: " + e.Message); }
                finally { fetching = false; }
            });
        }

        /// <summary>
        /// The server's text uses the game's own notation ([[rally]], [[huntMark]] ...); the game turns
        /// that into the translated keyword with its icon, exactly as it does for its own cards.
        /// </summary>
        private static string Render(string text)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains("[[")) return text;
            try { return SupportMethods.ModifyLocalization(Keyword.Replace(text, NameIfUntranslated)); }
            catch (Exception e)
            {
                RevivalMod.Log.Warning("[cards] could not format card text: " + e.Message);
                return text.Replace("[[", "").Replace("]]", "");
            }
        }

        private static readonly System.Text.RegularExpressions.Regex Keyword =
            new System.Text.RegularExpressions.Regex(@"\[\[([A-Za-z]+)\]\]");
        private static readonly Dictionary<string, bool> translated = new Dictionary<string, bool>();

        /// <summary>
        /// Some keywords (Ferocity, for one) have no translation in this build of the game, which would
        /// leave them written as the bare internal word. Those are written out here instead: icon, then
        /// the keyword in bold, like the translated ones.
        /// </summary>
        private static string NameIfUntranslated(System.Text.RegularExpressions.Match m)
        {
            string trait = m.Groups[1].Value;
            if (trait == "melee" || trait == "ranged" || trait == "reanimate") return m.Value;
            if (!translated.TryGetValue(trait, out bool known))
            {
                string t = null;
                try { t = Il2CppI2.Loc.LocalizationManager.GetTranslation("Card_Trait/" + trait, true, 0, true, false, null, null, true); }
                catch { }
                known = !string.IsNullOrEmpty(t);
                translated[trait] = known;
                if (!known) RevivalMod.Log.Msg($"[cards] the game has no translation for the keyword '{trait}'; writing it out");
            }
            if (known) return m.Value;
            var name = new System.Text.StringBuilder();
            for (int i = 0; i < trait.Length; i++)
            {
                char c = trait[i];
                if (i == 0) name.Append(char.ToUpperInvariant(c));
                else { if (char.IsUpper(c)) name.Append(' '); name.Append(c); }
            }
            // the link is what the game's hover hint looks for
            return "<link=" + trait + "><nobr><sprite name=Atlas_trait_icon_" + trait + "> <b>" + name + "</b></nobr></link>";
        }

        // Texts of the custom-card package (CustomBundle, 0.14.53): {id: {lang: (name, text)}}; the game's language,
        // then English. Set by CustomBundle when the package is read; looked up before the server's card-text.json.
        private static volatile Dictionary<string, Dictionary<string, (string name, string text)>> packageTexts = new Dictionary<string, Dictionary<string, (string, string)>>();

        internal static void SetPackageTexts(Dictionary<string, Dictionary<string, (string name, string text)>> texts) => packageTexts = texts ?? new Dictionary<string, Dictionary<string, (string, string)>>();

        private static bool Find(RawCardScript card, out (string name, string text) entry)
        {
            entry = default;
            if ((object)card == null || card.Pointer == IntPtr.Zero) return false;
            string id = card.uniqueId;
            if (id == null) return false;
            var pkg = packageTexts;
            if (pkg.Count > 0 && pkg.TryGetValue(id, out var langs))
            {
                string lang = Loc.Language() ?? "en";
                if (langs.TryGetValue(lang, out entry) || langs.TryGetValue("en", out entry)) return true;
            }
            var known = table;
            if (known.Count == 0) { Refresh(); return false; }
            return known.TryGetValue(id, out entry);
        }

        [HarmonyPatch(typeof(RawCardScript), nameof(RawCardScript.GetLocalizedCardName))]
        private static class Name
        {
            private static void Postfix(RawCardScript __instance, ref string __result)
            {
                try { if (Find(__instance, out var e) && e.name.Length > 0) __result = e.name; }
                catch (Exception ex) { RevivalMod.Log.Warning("[cards] name: " + ex.Message); }
            }
        }

        [HarmonyPatch(typeof(RawCardScript), nameof(RawCardScript.GetLocalizedCardNameFull))]
        private static class FullName
        {
            private static void Postfix(RawCardScript __instance, ref string __result)
            {
                try { if (Find(__instance, out var e) && e.name.Length > 0) __result = e.name; }
                catch (Exception ex) { RevivalMod.Log.Warning("[cards] name: " + ex.Message); }
            }
        }

        [HarmonyPatch(typeof(RawCardScript), nameof(RawCardScript.GetLocalizedCardDesc))]
        private static class Description
        {
            private static void Postfix(RawCardScript __instance, ref string __result)
            {
                try { if (Find(__instance, out var e)) __result = Render(e.text); }
                catch (Exception ex) { RevivalMod.Log.Warning("[cards] text: " + ex.Message); }
            }
        }

        [HarmonyPatch(typeof(RawCardScript), nameof(RawCardScript.GetDescAndAbilityRaw))]
        private static class RawDescription
        {
            private static void Postfix(RawCardScript __instance, ref string __result)
            {
                try { if (Find(__instance, out var e)) __result = Render(e.text); }
                catch (Exception ex) { RevivalMod.Log.Warning("[cards] text: " + ex.Message); }
            }
        }
    }
}
