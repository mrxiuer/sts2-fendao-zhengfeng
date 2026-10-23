using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

internal static class CharmTargeting
{
    internal static bool IsActive(Creature? creature)
        => creature is { IsAlive: true, IsEnemy: true }
            && creature.GetPower<CharmRedirectPower>() is { Amount: > 0 };

    internal static IReadOnlyList<Creature> SelectRecipients(Creature source, IEnumerable<Creature> candidates, bool group)
    {
        Creature[] others = candidates
            .Where(candidate => candidate.IsAlive && candidate.IsEnemy && candidate != source)
            .ToArray();
        if (others.Length == 0) return source.IsAlive ? [source] : [];
        return group ? others : [others[0]];
    }

    internal static IReadOnlyList<Creature> Recipients(Creature source, bool group)
        => SelectRecipients(source, source.CombatState?.Enemies ?? [], group);

    internal static Creature SingleRecipient(Creature source)
        => Recipients(source, group: false).FirstOrDefault() ?? source;

    internal static IReadOnlyList<Creature>? AttackTargets(AttackCommand attack)
    {
        Creature? source = attack.Attacker;
        if (!attack.DamageProps.HasFlag(ValueProp.Move) || attack.TargetSide != CombatSide.Player || !IsActive(source))
            return null;
        return Recipients(source!, attack.IsMultiTargeted);
    }

    internal static IReadOnlyList<Creature>? DirectAttackTargets(IEnumerable<Creature>? original, ValueProp props, Creature? dealer)
    {
        if (original == null || !props.HasFlag(ValueProp.Move) || !IsActive(dealer)) return null;
        Creature[] targets = original.ToArray();
        if (!targets.Any(target => target.IsPlayer)) return null;
        return Recipients(dealer!, group: targets.Length > 1);
    }

    internal static PowerType PowerTypeFor<T>(decimal amount) where T : PowerModel
        => ModelDb.Power<T>().GetTypeForAmount(amount);

    internal static Creature RoutePowerTarget(Creature target, Creature? applier, PowerType type)
    {
        // Charm redirect takes precedence; a protected player remains protected only if
        // the redirected target is still that player.
        Creature routed = RedirectPowerTargetForCharm(target, applier, type);
        if (type == PowerType.Debuff && routed.IsPlayer
            && PuppetFollower.Guard(routed) is { IsAlive: true } puppet)
            return puppet;
        return routed;
    }

    internal static IEnumerable<Creature>? RoutePowerTargets(IEnumerable<Creature>? targets, Creature? applier, PowerType type)
    {
        if (targets == null) return null;
        Creature[] routed = RedirectPowerTargetsForCharm(targets, applier, type).ToArray();
        if (type == PowerType.Debuff)
        {
            routed = routed.Select(target => target.IsPlayer && PuppetFollower.Guard(target) is { IsAlive: true } puppet
                    ? puppet
                    : target)
                .Distinct()
                .ToArray();
        }
        return routed;
    }

    private static Creature RedirectPowerTargetForCharm(Creature target, Creature? applier, PowerType type)
    {
        if (!IsActive(applier)) return target;
        if (type == PowerType.Debuff && target.IsPlayer)
            return SingleRecipient(applier!);
        if (type == PowerType.Buff && target == applier
            && applier!.GetPower<CharmRedirectPower>()?.Applier is { IsPlayer: true } caster)
            return caster;
        return target;
    }

    private static IEnumerable<Creature> RedirectPowerTargetsForCharm(IEnumerable<Creature> targets, Creature? applier, PowerType type)
    {
        Creature[] original = targets.ToArray();
        if (!IsActive(applier)) return original;
        if (type == PowerType.Debuff && original.Any(target => target.IsPlayer))
            return Recipients(applier!, group: true);
        if (type == PowerType.Buff && original.Contains(applier)
            && applier!.GetPower<CharmRedirectPower>()?.Applier is { IsPlayer: true } caster)
            return original.Select(target => target == applier ? caster : target).Distinct().ToArray();
        return original;
    }
}

[HarmonyPatch(typeof(AttackCommand), "GetPossibleTargets")]
internal static class CharmAttackTargetsPatch
{
    private static bool Prefix(AttackCommand __instance, ref IReadOnlyList<Creature> __result)
    {
        if (CharmTargeting.AttackTargets(__instance) is not { } targets) return true;
        __result = targets;
        return false;
    }
}

// Feed the native move delegate its redirected opponents. This covers moves
// that use their target argument directly instead of constructing an AttackCommand.
[HarmonyPatch(typeof(MoveState), nameof(MoveState.PerformMove))]
internal static class CharmMoveTargetsPatch
{
    private static void Prefix(ref IEnumerable<Creature> targets)
    {
        Creature[] original = targets.ToArray();
        if (!original.Any(target => target.IsPlayer)) return;
        Creature? source = original.FirstOrDefault()?.CombatState?.Enemies
            .FirstOrDefault(creature => creature.Monster?.IsPerformingMove == true && CharmTargeting.IsActive(creature));
        if (source != null) targets = CharmTargeting.Recipients(source, group: true);
    }
}

[HarmonyPatch(typeof(MonsterModel), nameof(MonsterModel.RollMove))]
internal static class CharmMonsterRollTargetsPatch
{
    private static void Prefix(MonsterModel __instance, ref IEnumerable<Creature> targets)
    {
        Creature source = __instance.Creature;
        if (targets.Any(target => target.IsPlayer) && CharmTargeting.IsActive(source))
            targets = CharmTargeting.Recipients(source, group: true);
    }
}

[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Damage), [typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp), typeof(Creature), typeof(CardModel), typeof(CardPlay)])]
internal static class CharmDirectDamageTargetsPatch
{
    private static void Prefix(ref IEnumerable<Creature>? targets, ValueProp props, Creature? dealer)
    {
        if (CharmTargeting.DirectAttackTargets(targets, props, dealer) is { } redirected)
            targets = redirected;
    }
}

[HarmonyPatch(typeof(PowerCmd), nameof(PowerCmd.Apply), [typeof(PlayerChoiceContext), typeof(PowerModel), typeof(Creature), typeof(decimal), typeof(Creature), typeof(CardModel), typeof(bool)])]
internal static class CharmPowerModelTargetPatch
{
    private static void Prefix(PowerModel power, ref Creature target, decimal amount, Creature? applier)
        => target = CharmTargeting.RoutePowerTarget(target, applier, power.GetTypeForAmount(amount));
}
