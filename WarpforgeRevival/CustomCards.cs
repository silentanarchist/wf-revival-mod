using System;
using System.Linq;
using HarmonyLib;
using Il2Cpp;

namespace WarpforgeRevival
{
    /// <summary>
    /// Custom cards (ids "CUSTOM-NNNN", from the card creator; the tester bundle, claude/custom-bundle-plan.md).
    /// Step 1 (0.14.52): where they may go. They are shown only in the Testers window's Cards tab and in the deck
    /// editor of a mode the server lists in "customCardModes" (the custom-card test mode), and DeckRules lets
    /// them into such a mode's decks only. The normal Collection and every other mode's editor leave them out,
    /// so a player who somehow has one loaded never sees it elsewhere. Since 0.14.64 the editor that offers them is
    /// only the Testers window's own copy of the deck editor (TestersEditor).
    /// </summary>
    internal static class CustomCards
    {
        internal const string Prefix = "CUSTOM-";

        internal static bool IsCustom(RawCardScript card)
        {
            try { string id = (object)card == null ? null : card.uniqueId; return id != null && id.StartsWith(Prefix, StringComparison.Ordinal); }
            catch { return false; }
        }

        /// <summary>Whether this deck belongs to a mode that allows custom cards.</summary>
        internal static bool AllowedIn(CardDeck deck)
        {
            try
            {
                string format = LongGame.FormatOfDeck(deck);
                return format != null && ServerSettings.CustomCardModes.Contains(format);
            }
            catch { return false; }
        }

        private static Il2CppSystem.Collections.Generic.List<RawCardScript> Without(Il2CppSystem.Collections.Generic.List<RawCardScript> list, out int left)
        {
            left = 0;
            var kept = new Il2CppSystem.Collections.Generic.List<RawCardScript>();
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (IsCustom(c)) { left++; continue; }
                kept.Add(c);
            }
            return kept;
        }

        private static int notedCollection = -1, notedEditor = -1;

        /// <summary>The Collection's card list: no custom cards, except in the Testers window's copy.</summary>
        [HarmonyPatch(typeof(CardCollectionTab), "GetCollection")]
        private static class CollectionList
        {
            private static void Postfix(CardCollectionTab __instance, ref Il2CppSystem.Collections.Generic.List<RawCardScript> __result)
            {
                try
                {
                    if (__result == null || TestersCollection.IsCopy(__instance.Window)) return;
                    var kept = Without(__result, out int left);
                    if (left == 0) return;
                    __result = kept;
                    if (left != notedCollection) { notedCollection = left; RevivalMod.Log.Msg($"[custom] Collection: {left} custom card(s) left out (Testers window shows them)"); }
                }
                catch (Exception e) { RevivalMod.Log.Warning("[custom] collection: " + e.Message); }
            }
        }

        /// <summary>The deck editor's card list: custom cards only in the Testers window's own deck editor (TestersEditor,
        /// 0.14.64), and there only when editing a deck of a mode that allows them. The game's shared editor never offers them.</summary>
        // hooked through SharedHooks (one hook per game method): HarmonyPatch(typeof(DeckEditingWindow), nameof(DeckEditingWindow.GetCardCollection))
        internal static class EditorList
        {
            internal static void Postfix(DeckEditingWindow __instance, ref Il2CppSystem.Collections.Generic.List<RawCardScript> __result)
            {
                try
                {
                    if (__result == null) return;
                    var panel = __instance.editingPanel;
                    var deck = (object)panel == null ? null : panel.editingDeck;
                    if ((object)deck == null) deck = __instance.EditingDeck;
                    bool testers = TestersEditor.IsCopy(__instance);
                    if (testers && AllowedIn(deck)) return;
                    var kept = Without(__result, out int left);
                    if (left == 0) return;
                    __result = kept;
                    if (left != notedEditor) { notedEditor = left; RevivalMod.Log.Msg($"[custom] deck editor: {left} custom card(s) left out ({(testers ? "not a custom-card mode's deck" : "the game's own editor")})"); }
                }
                catch (Exception e) { RevivalMod.Log.Warning("[custom] editor: " + e.Message); }
            }
        }
    }
}
