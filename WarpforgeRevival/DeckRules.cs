using System;
using HarmonyLib;
using Il2Cpp;

namespace WarpforgeRevival
{
    /// <summary>
    /// Offensive cards (given to the attacker by the battlefield) are marked "can't add to deck" in
    /// the card data, but the deck check only refuses defensive cards, relying on players not owning
    /// the offensive ones. The revival server grants every card, so enforce the mark here.
    /// </summary>
    internal static class DeckRules
    {
        [HarmonyPatch(typeof(CardDeck), nameof(CardDeck.CanAddCard))]
        private static class NoBattlefieldCards
        {
            private static bool noted;

            private static void Postfix(CardDeck __instance, RawCardScript card, ref string error, ref bool __result)
            {
                try
                {
                    if (!__result || (object)card == null) return;
                    // Custom cards (the tester bundle, 0.14.52) go only into decks of a mode the server allows them in
                    if (CustomCards.IsCustom(card) && !CustomCards.AllowedIn(__instance))
                    {
                        __result = false;
                        error = Loc.T("decks.customOnlyTest", "Custom cards can only be added to a deck of the custom-card test mode.");
                        return;
                    }
                    // Defensive cards are chosen in the deck editor (they fill the deck's defensive slot),
                    // so they must stay addable even though some carry the "can't add to deck" mark.
                    if (card.spellType == SpellType.DefensiveCard) return;
                    // Dark Angels Secrets only come into play through other cards (see OffenceInCollection).
                    if (card.spellType == SpellType.Secret && card.inventoryOptions == CardInventoryOptions.NotInInventory)
                    {
                        __result = false;
                        error = Loc.T("decks.noSecrets", "Secrets cannot be added to a deck.");
                        return;
                    }
                    if (card.inventoryOptions != CardInventoryOptions.CantAddToDeck && card.spellType != SpellType.OffensiveCard) return;
                    __result = false;
                    error = Loc.T("decks.noOffensive", "Offensive cards cannot be added to a deck.");
                    if (!noted) { noted = true; RevivalMod.Log.Msg($"[decks] refused '{card.cardName}' ({card.spellType}, {card.inventoryOptions}): not a deck card"); }
                }
                catch (Exception e) { RevivalMod.Log.Warning("[decks] " + e.Message); }
            }
        }

        // A game mode whose "assignDefensiveCardsInEditMode" switch is off (Classic since 0.14.42) has no Defence card
        // in its decks: the player going second picks one after the mulligan, as in a draft (BattlefieldCards).
        // The game itself already keeps Defence cards out of the editor's collection for such a mode
        // (DeckEditingWindow.GetCardCollection asks the deck's mode); what it still shows is the deck list's
        // Defence slot (empty, or holding a card saved before the switch), which is hidden here.
        private static void HideDefenceSlot(DeckListDrawer drawer)
        {
            try
            {
                if ((object)drawer == null) return;
                var deck = drawer.currentCardDeck;
                if ((object)deck == null) return;
                if (BattlefieldCards.DefenceInEditor(deck.CustomGameModeEvent)) return;
                var slot = drawer.defensiveCardInfoItem;
                if ((object)slot == null || slot.Pointer == IntPtr.Zero) return;
                var go = slot.gameObject;
                if ((object)go != null && go.activeSelf) go.SetActive(false);
            }
            catch (Exception e) { RevivalMod.Log.Warning("[decks] Defence slot: " + e.Message); }
        }

        [HarmonyPatch(typeof(DeckListDrawer), nameof(DeckListDrawer.ShowEmptySlotsIfNeeded))]
        private static class NoDefenceSlot
        {
            private static void Postfix(DeckListDrawer __instance) => HideDefenceSlot(__instance);
        }

        [HarmonyPatch(typeof(DeckListDrawer), nameof(DeckListDrawer.SpawnCardInfo))]
        private static class NoSavedDefenceCard
        {
            private static void Postfix(DeckListDrawer __instance) => HideDefenceSlot(__instance);
        }

        // Queuing with a deck that has no Defence card asks "you have not assigned a Defence card" (0.15.17).
        // The game's PlayEvent.StartMatch shows that popup when DeckUtility.HasDefensiveCards says the player owns
        // Defence cards of the warlord's faction but the deck has none (HaveItInInventory), unless the mode is a
        // draft. On this server everyone owns every card, and a mode whose "assignDefensiveCardsInEditMode" switch
        // is off (Classic) has no Defence slot - the card is picked after the mulligan - so it asked on every
        // queue. For such a mode the deck counts as having its Defence card. StartMatch is the only caller.
        [HarmonyPatch(typeof(DeckUtility), nameof(DeckUtility.HasDefensiveCards))]
        private static class NoDefenceWarning
        {
            private static bool noted;

            private static bool Prefix(CardDeck deck, ref DeckUtility.DefensiveCardInventoryCheck __result)
            {
                try
                {
                    if ((object)deck == null || BattlefieldCards.DefenceInEditor(deck.CustomGameModeEvent)) return true;
                    __result = DeckUtility.DefensiveCardInventoryCheck.HaveItAssigned;
                    if (!noted) { noted = true; RevivalMod.Log.Msg("[decks] no Defence card warning: this mode picks the Defence card at the start of the match"); }
                    return false;
                }
                catch (Exception e) { RevivalMod.Log.Warning("[decks] Defence card warning: " + e.Message); return true; }
            }
        }

        // Offensive and defensive cards unlock with a faction's Forge level. The revival server has
        // no Forge, so treat every faction as fully levelled: all of them are available.
        [HarmonyPatch(typeof(EnviromentalEffectCardsSO), nameof(EnviromentalEffectCardsSO.GetEnvCardList))]
        private static class ForgeMaxed
        {
            private static bool noted;

            private static void Prefix(CardArmy useArmy, bool shouldUseOffensive, ref int currentForgeLevelObtained)
            {
                if (!noted)
                {
                    noted = true;
                    RevivalMod.Log.Msg($"[forge] battlefield cards requested for {useArmy} (offensive={shouldUseOffensive}) at forge level {currentForgeLevelObtained}; treating as max");
                }
                currentForgeLevelObtained = 100000;
            }

            private static void Postfix(CardArmy useArmy, bool shouldUseOffensive, Il2CppSystem.Collections.Generic.List<RawCardScript> __result)
            {
                try { RevivalMod.Log.Msg($"[forge] {(shouldUseOffensive ? "offensive" : "defensive")} cards available for {useArmy}: {(__result == null ? 0 : __result.Count)}"); }
                catch { }
            }
        }
    }
}
