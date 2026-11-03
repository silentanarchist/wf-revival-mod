using System;
using HarmonyLib;
using Il2Cpp;

namespace WarpforgeRevival
{
    /// <summary>
    /// Two fixes to Oath (Ultramarines) actions (0.15.16; findings in the project doc
    /// claude/ferren-areios-oath-check.md, read from the game's code).
    ///
    ///  1. Ferren Areios (UM106: "Oath abilities of friendly troops can be activated up to 3 times each turn") together
    ///     with Vico Therbeus (UM93: "... may be activated on later turns"). Ferren gives friendly troops
    ///     oathTripleActivation (1278), Vico gives oathInAllTurns (1276). The game's limit check
    ///     (CardScript.CanUseOathAbility, and the same code inlined into CardTraitOath.IsActive for the keyword icon)
    ///     ends with "oathInAllTurns and used once this turn: no", which wins over the triple limit, so with both
    ///     cards on the board every Oath troop could act only once per turn. Now a troop with both keywords may act
    ///     up to 3 times per turn, on any turn. Every other combination keeps the game's own answer (it is only ever
    ///     turned from "no" to "yes" in exactly that case).
    ///
    ///  2. The Oath cost was never checked before an action was started by the bot (and by the ability button):
    ///     BattleManager.CanUseActiveAbility checks Energy against currentAbilityCost, which the game always returns
    ///     as 0, and never the Oath number. A player's troop drag checks the Oath cost separately
    ///     (CardScript.CanUseAbilityNow), but the bot's list of moves (GetAvailableActions) does not, so a bot used
    ///     Oath actions it could not pay for (the game then logs "UseNormalEnergy ... over available energy" and sets
    ///     Energy to 0); with Ferren up to 3 times per troop. Now CanUseActiveAbility also says no when the owner
    ///     cannot pay the Oath cost, with the same Energy test CanUseAbilityNow uses.
    ///
    /// Neither changes how a move already sent is played out on the other device (that side runs
    /// ReadyToUseActiveAbility, which checks neither), and replays only re-run recorded moves, so RulesVersion and
    /// old replays are left alone.
    /// </summary>
    internal static class OathFixes
    {
        private static int loggedTriple, loggedCost;

        private static string Name(CardScript card)
        {
            try { return (card.rawCardData?.uniqueId ?? "?") + " #" + card.uniqueInGameID; } catch { return "?"; }
        }

        /// <summary>The fixed answer for a troop that has both Ferren's and Vico's keywords; null when it has not.</summary>
        private static bool? TripleOnAllTurns(CardScript card)
        {
            if ((object)card == null || card.m_CachedPtr == IntPtr.Zero) return null;
            if (!card.HasCurrentTrait(DefinedTrait.oathTripleActivation) || !card.HasCurrentTrait(DefinedTrait.oathInAllTurns)) return null;
            return card.usedActiveAbilityTimesPerTurn < 3;
        }

        private static void Allow(CardScript card, ref bool result)
        {
            if (result) return;
            bool? fixedAnswer = TripleOnAllTurns(card);
            if (fixedAnswer != true) return;
            result = true;
            if (loggedTriple < 5)
            {
                loggedTriple++;
                RevivalMod.Log.Msg($"[oath] {Name(card)} has Oath on all turns and up to 3 times: use {card.usedActiveAbilityTimesPerTurn + 1} of 3 this turn allowed");
            }
        }

        // ---------------------------------------------------------------- 1. the use limit

        [HarmonyPatch(typeof(CardScript), nameof(CardScript.CanUseOathAbility))]
        private static class Limit
        {
            private static void Postfix(CardScript __instance, ref bool __result)
            {
                try { Allow(__instance, ref __result); }
                catch (Exception e) { RevivalMod.Log.Warning("[oath] " + e.Message); }
            }
        }

        /// <summary>The Oath keyword icon's "still usable" state: the same check, inlined by the compiler.</summary>
        [HarmonyPatch(typeof(CardTraitOath), nameof(CardTraitOath.IsActive))]
        private static class Icon
        {
            private static void Postfix(CardScript card, ref bool __result)
            {
                try { Allow(card, ref __result); }
                catch (Exception e) { RevivalMod.Log.Warning("[oath] " + e.Message); }
            }
        }

        // ---------------------------------------------------------------- 2. the Oath cost

        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.CanUseActiveAbility))]
        private static class Cost
        {
            private static void Postfix(BattleManager __instance, CardScript parentCard, ref bool __result)
            {
                if (!__result) return;
                try
                {
                    if ((object)parentCard == null || parentCard.m_CachedPtr == IntPtr.Zero || (object)__instance == null) return;
                    if (!parentCard.HasCurrentTrait(DefinedTrait.oath)) return;
                    PlayerManager owner = parentCard.isPlayer ? __instance.playerManager : __instance.enemyManager;
                    if ((object)owner == null) return;
                    int cost = parentCard.GetCurrentTraitValue(DefinedTrait.oath);
                    if (owner.HasEnoughMana(cost, parentCard.AbilityUsesPsychicEnergy(), parentCard.AbilityUsesPlasmaEnergy(), false)) return;
                    __result = false;
                    if (loggedCost < 5)
                    {
                        loggedCost++;
                        RevivalMod.Log.Msg($"[oath] {Name(parentCard)} cannot pay its Oath cost {cost}: action not offered");
                    }
                }
                catch (Exception e) { RevivalMod.Log.Warning("[oath] " + e.Message); }
            }
        }
    }
}
