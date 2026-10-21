using FendaoZhengfeng.Core;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace FendaoZhengfeng.Mod;

internal static class FlightWings
{
    internal static bool Applies(AttackCommand attack)
    {
        if (!RaceRuntime.Live || RaceSession.Current?.Me.Has("flight_wings") != true) return false;
        var player = RaceRuntime.State!.Players.Single();
        return attack.Attacker == player.Creature || attack.Attacker?.PetOwner == player;
    }
    internal static Creature? Highest() => RaceRuntime.State?.Players.Single().Creature.CombatState?.Enemies
        .Where(enemy => enemy.IsAlive).MaxBy(enemy => enemy.CurrentHp);
    internal static void RetargetCard(CardPlay play)
    {
        if (RaceSession.Current?.Me.Has("flight_wings") == true && play.Card.Type == CardType.Attack && play.Target?.IsEnemy == true
            && play.Player == RaceRuntime.State!.Players.Single() && Highest() is { } target)
        {
            AccessTools.PropertySetter(typeof(CardPlay), nameof(CardPlay.Target)).Invoke(play, [target]);
            AccessTools.PropertySetter(typeof(CardModel), nameof(CardModel.CurrentTarget)).Invoke(play.Card, [target]);
        }
    }
}

public sealed partial class RaceModifier
{
    public override Task BeforeAttack(AttackCommand command)
    {
        BeginCriticalAttack(command);
        if (FlightWings.Applies(command))
        {
            // Forced targeting also replaces random targeting, without rolling
            // a random target or rejecting a repeated healthiest enemy.
            AccessTools.PropertySetter(typeof(AttackCommand), nameof(AttackCommand.IsRandomlyTargeted)).Invoke(command, [false]);
            AccessTools.PropertySetter(typeof(AttackCommand), nameof(AttackCommand.TargetSide)).Invoke(command, [CombatSide.Enemy]);
        }
        return Task.CompletedTask;
    }
}

[HarmonyPatch(typeof(AttackCommand), "GetPossibleTargets")]
internal static class FlightAttackTargetsPatch
{
    static bool Prefix(AttackCommand __instance, ref IReadOnlyList<Creature> __result)
    {
        if (!FlightWings.Applies(__instance)) return true;
        __result = FlightWings.Highest() is { } enemy ? [enemy] : [];
        return false;
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.IsValidTarget))]
internal static class FlightCardTargetPatch
{
    static void Postfix(CardModel __instance, Creature? target, ref bool __result)
    {
        if (__result && target?.IsEnemy == true && !__instance.IsCanonical && __instance.Type == CardType.Attack
            && RaceRuntime.Live && RaceSession.Current?.Me.Has("flight_wings") == true
            && __instance.Owner == RaceRuntime.State!.Players.Single() && target != FlightWings.Highest()) __result = false;
    }
}
