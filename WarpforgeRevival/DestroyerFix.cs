using System;
using HarmonyLib;
using Il2Cpp;

namespace WarpforgeRevival
{
    /// <summary>
    /// DRAFT (2026-10-27, not in any build yet - owner: "come up with a fix but don't update the mod with it yet").
    /// The Destroyer keyword (trait frenzied, "always attacks a unit it can destroy before any other unit") judged by the
    /// attack actually being made. See claude/destroyer-keyword.md for the original-game gaps:
    ///  - BattleManager.CheckIfAttackKillsTarget counts melee OR ranged damage, so a 4 melee / 1 ranged Destroyer could
    ///    shoot a 3-health unit (it survives) while a 1-health unit was on the board, and a flier that only the ranged
    ///    attack can reach counted as "destroyable" by the melee damage;
    ///  - when looking for other units it must attack, the game checked reachability with the attacker's default attack
    ///    type, not the chosen one.
    ///
    /// The rule (owner, 2026-10-27):
    ///  1. the chosen target is allowed when the selected attack type alone destroys it;
    ///  2. otherwise the attack is refused when any other enemy can be destroyed by melee (and melee can reach it) or by
    ///     ranged (and ranged can reach it), each checked on its own, so switching attack type cannot dodge a legal kill.
    ///
    /// How: a prefix on IsValidAttackTarget passes isCheckingDestroyerTargets = true for a Destroyer, which makes the
    /// game's own check run every rule except its Destroyer step (the game itself uses that flag to skip it). A postfix
    /// then applies the rule above, using the same IsValidAttackTarget (with the flag) for "can this attack reach that
    /// unit" and the game's own damage check (EnoughPendingDamageToDieWithDamageValues, as CheckIfAttackKillsTarget does)
    /// for "would it die", one attack type at a time. The refusal shows the game's own tip (Battle/Tips/CantAttackDestroyer).
    /// IsValidAttackTarget is also what the AI uses to list targets, so the computer follows the same rule.
    /// Android: IsValidAttackTarget is 2220 bytes at RVA 0x328DCC0 (not near a page end): safe to hook.
    /// To enable: remove the #if below (the file is left out of builds while DESTROYER_FIX is not defined).
    /// </summary>
#if DESTROYER_FIX
    internal static class DestroyerFix
    {
        private const DefinedTrait Destroyer = DefinedTrait.frenzied;
        private const DefinedTrait Flying = DefinedTrait.flying;

        [ThreadStatic] private static bool inner;      // our own calls into IsValidAttackTarget are left alone

        /// <summary>Whether one attack of this type from the attacker destroys the target (the game's own damage check).</summary>
        private static bool Destroys(CardScript attacker, CardScript target, AttackTypes type)
        {
            int dmg = attacker.GetCombatDamageWithType(type);
            if (dmg <= 0) return false;
            var values = new Il2CppSystem.Collections.Generic.List<int>();
            values.Add(dmg);
            var kinds = new Il2CppSystem.Collections.Generic.List<DamageType>();
            kinds.Add(DamageType.Attack);                          // what CheckIfAttackKillsTarget passes (4)
            return target.EnoughPendingDamageToDieWithDamageValues(values, kinds);
        }

        /// <summary>The game's own rules for this attack type, Destroyer step left out.</summary>
        private static bool Reaches(BattleManager bm, CardScript attacker, CardScript target, AttackTypes type, bool allowAuto)
        {
            inner = true;
            try { return bm.IsValidAttackTarget(attacker, target, false, type, allowAuto, true); }
            finally { inner = false; }
        }

        private static bool CanDestroyAnotherUnit(BattleManager bm, CardScript attacker, CardScript chosen, bool allowAuto)
        {
            bool enemyIsPlayer = chosen.isPlayer;
            var list = new System.Collections.Generic.List<CardScript>();
            try
            {
                var units = bm.GetMinionManager(enemyIsPlayer)?.GetUnitList();
                if (units != null) foreach (var u in units) if ((object)u != null) list.Add(u);
            }
            catch { }
            try { var hero = bm.GetHero(enemyIsPlayer); if ((object)hero != null) list.Add(hero); } catch { }
            foreach (var other in list)
            {
                if (other.Pointer == chosen.Pointer || !other.IsInPlay()) continue;
                foreach (var type in new[] { AttackTypes.Melee, AttackTypes.Ranged })
                    if (Reaches(bm, attacker, other, type, allowAuto) && Destroys(attacker, other, type)) return true;
            }
            return false;
        }

        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.IsValidAttackTarget))]
        private static class Check
        {
            private static void Prefix(CardScript attacker, ref bool isCheckingDestroyerTargets, out bool __state)
            {
                __state = false;
                try
                {
                    if (inner || isCheckingDestroyerTargets || (object)attacker == null || !attacker.HasCurrentTrait(Destroyer)) return;
                    isCheckingDestroyerTargets = true;          // the game's own Destroyer step is skipped; ours runs after
                    __state = true;
                }
                catch { }
            }

            private static void Postfix(BattleManager __instance, CardScript attacker, CardScript target, bool showTips,
                                        AttackTypes attType, bool allowAutoActions, bool __state, ref bool __result)
            {
                if (!__state || !__result) return;              // not a Destroyer check of ours, or already refused by another rule
                try
                {
                    var type = attType != AttackTypes.None ? attType : attacker.currentAttackType;
                    if (type != AttackTypes.Melee && type != AttackTypes.Ranged) return;
                    if (Destroys(attacker, target, type)) return;   // rule 1: the selected attack destroys it
                    if (!CanDestroyAnotherUnit(__instance, attacker, target, allowAutoActions)) return;   // nothing it must attack instead
                    __result = false;                                // rule 2
                    if (showTips)
                    {
                        var tips = __instance._battleTipController;
                        if ((object)tips != null) tips.NotifyCantDoAction(Loc.Game("Battle/Tips/CantAttackDestroyer", "A Destroyer must attack a unit it can destroy"));
                    }
                    RevivalMod.Log.Msg($"[destroyer] {attacker.cardName} may not attack {target.cardName} with {type}: another unit can be destroyed");
                }
                catch (Exception e) { RevivalMod.Log.Warning("[destroyer] " + e.Message); }
            }
        }
    }
#endif
}
