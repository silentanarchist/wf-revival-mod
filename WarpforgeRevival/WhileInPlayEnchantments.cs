using System;
using HarmonyLib;
using Il2Cpp;

namespace WarpforgeRevival
{
    /// <summary>
    /// "While in play" abilities on enchantments (Space Wolves' Everstorm, SW64: "All units have -1 Ranged, down to a
    /// minimum of 1"; only the two warlords ever got it). Two gaps in the game, both needed for the fix
    /// (fix outline: project doc claude/fix-while-in-play-enchantments.md; 0.13.46 fixed only the first, and that
    /// one with the wrong test, so nothing changed in play):
    ///
    ///  1. The refresh (BattleManagerSupport.BroadcastWhileInPlay) only reaches cards whose
    ///     CardScript.HasWhileInPlayAbility() is true, and that looks at the card's own abilities only. A warlord that
    ///     carries Everstorm's enchantment was never refreshed. Here a card also counts when one of its active
    ///     (not spent) enchantments has a "while in play" ability (trigger WhileInPlay or WhileInPlayVariable).
    ///     EntityScript.HasEnchantmentWhileInPlay() looks at the enchantment's "while in play" animation flag instead,
    ///     which Everstorm's enchantment does not set, so it is not relied on. A jammed unit still has no effects.
    ///
    ///  2. CardScript.UpdateWhileInPlay(unitJustSummoned) passes the card itself as the acting card only right after
    ///     it was summoned, and nothing otherwise. Everstorm's ability has a subject condition (acting card: your own
    ///     warlord, this card), which then checks an empty card ("Null evaluated card") and fails, so the ability ran
    ///     only at the opening, when the warlords were the only units. For a card whose "while in play" comes only from
    ///     an enchantment, the card itself is passed as the acting card on every refresh. Cards with their own "while
    ///     in play" abilities are left exactly as the game runs them.
    ///
    /// Everstorm is the only official card affected; custom enchantments can now use "while in play" too. Both
    /// players need this (the server only hands out the latest mod).
    /// </summary>
    internal static class WhileInPlayEnchantments
    {
        private static int loggedFound, loggedActing;

        /// <summary>True when one of the card's active enchantments has a "while in play" ability.</summary>
        private static bool FromEnchantment(CardScript card)
        {
            var list = card.activeEnchantments;
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                var raw = (object)e == null ? null : e.enchantmentCard;
                if ((object)raw == null || raw.m_CachedPtr == IntPtr.Zero) continue;
                try { if (card.IsSpentEnchantment(raw)) continue; } catch { }
                if (raw.HasAbilityType(AbilityTrigger.WhileInPlay) || raw.HasAbilityType(AbilityTrigger.WhileInPlayVariable)) return true;
            }
            return false;
        }

        private static string Name(CardScript card)
        {
            try { return (card.rawCardData?.uniqueId ?? "?") + " #" + card.uniqueInGameID; } catch { return "?"; }
        }

        // ---------------------------------------------------------------- 1. the refresh reaches the card

        [HarmonyPatch(typeof(CardScript), nameof(CardScript.HasWhileInPlayAbility))]
        private static class Reach
        {
            private static void Postfix(CardScript __instance, ref bool __result)
            {
                if (__result) return;
                try
                {
                    if ((object)__instance == null || __instance.m_CachedPtr == IntPtr.Zero) return;
                    if (__instance.HasCurrentTrait(DefinedTrait.jam)) return;          // the game's rule: jammed units have no effects
                    if (!FromEnchantment(__instance)) return;
                    __result = true;
                    if (loggedFound < 5)
                    {
                        loggedFound++;
                        RevivalMod.Log.Msg($"[whileinplay] {Name(__instance)} carries an enchantment with a while-in-play ability: refreshed with the board");
                    }
                }
                catch (Exception e) { RevivalMod.Log.Warning("[whileinplay] " + e.Message); }
            }
        }

        // ---------------------------------------------------------------- diagnostics (0.15.22)
        // B-0015: on 0.15.21 Everstorm again gave no -1 Ranged to troops entering later, and neither patch above logged
        // anything in that match, so the refresh never asked about the card that holds the enchantment. The refresh
        // (BroadcastWhileInPlay) only walks manager.unitsInPlay and both hands. This notes, a few times a match, every
        // card that carries a "while in play" enchantment, its state, and whether it is in unitsInPlay at all.
        private static IntPtr diagMatch;
        private static int diagLines;
        private static float nextDiag;
        private static string lastDiag;
        private static BattleCardManager diagCards;

        [HarmonyPatch(typeof(BattleManagerSupport), nameof(BattleManagerSupport.BroadcastWhileInPlay))]
        private static class Diagnose
        {
            private static void Postfix(BattleManager manager)
            {
                try
                {
                    if ((object)manager == null || manager.m_CachedPtr == IntPtr.Zero) return;
                    if (manager.Pointer != diagMatch) { diagMatch = manager.Pointer; diagLines = 0; lastDiag = null; diagCards = null; nextDiag = 0; }
                    if (diagLines >= 8) return;
                    float now = UnityEngine.Time.realtimeSinceStartup;
                    if (now < nextDiag) return;
                    nextDiag = now + 5f;
                    if ((object)diagCards == null || diagCards.m_CachedPtr == IntPtr.Zero) diagCards = UnityEngine.Object.FindObjectOfType<BattleCardManager>();
                    var all = (object)diagCards == null ? null : diagCards.GetAllCardsInGame();
                    if (all == null) return;
                    var inPlay = manager.unitsInPlay;
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < all.Count; i++)
                    {
                        var c = all[i];
                        if ((object)c == null || c.m_CachedPtr == IntPtr.Zero || !FromEnchantment(c)) continue;
                        bool listed = inPlay != null && inPlay.Contains(c);
                        bool reached = false;
                        try { reached = c.HasWhileInPlayAbility(); } catch { }
                        sb.Append(sb.Length > 0 ? "; " : "").Append($"{Name(c)} ({(c.isPlayer ? "ours" : "theirs")}, {c.cardState}, " +
                            $"in unitsInPlay {(listed ? "yes" : "NO")}, IsInPlay {c.IsInPlay()}, refreshed {(reached ? "yes" : "no")})");
                    }
                    string line = sb.Length > 0 ? sb.ToString() : "none";
                    if (line == lastDiag) return;
                    lastDiag = line;
                    diagLines++;
                    RevivalMod.Log.Msg("[whileinplay] cards carrying a while-in-play enchantment: " + line);
                }
                catch (Exception e) { if (diagLines < 8) { diagLines++; RevivalMod.Log.Warning("[whileinplay] diagnostics: " + e.Message); } }
            }
        }

        // ---------------------------------------------------------------- 2. the card is its own acting card

        [HarmonyPatch(typeof(RawCardScript), nameof(RawCardScript.UpdateWhileInPlay))]
        private static class Acting
        {
            private static void Prefix(RawCardScript __instance, CardScript thisCard, ref CardScript actingCard)
            {
                if ((object)actingCard != null) return;
                try
                {
                    if ((object)thisCard == null || thisCard.m_CachedPtr == IntPtr.Zero || (object)__instance == null) return;
                    // the card's own "while in play" abilities keep the game's behaviour
                    if (__instance.HasAbilityType(AbilityTrigger.WhileInPlay) || __instance.HasAbilityType(AbilityTrigger.WhileInPlayVariable)) return;
                    if (!FromEnchantment(thisCard)) return;
                    actingCard = thisCard;
                    if (loggedActing < 5)
                    {
                        loggedActing++;
                        RevivalMod.Log.Msg($"[whileinplay] refreshing {Name(thisCard)}'s enchantment with the card itself as the acting card");
                    }
                }
                catch (Exception e) { RevivalMod.Log.Warning("[whileinplay] " + e.Message); }
            }
        }
    }
}
