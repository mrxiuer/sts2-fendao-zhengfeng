using System.Threading;
using FendaoZhengfeng.Core;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace FendaoZhengfeng.Mod;

public sealed partial class RaceModifier
{
    public override decimal ModifyPowerAmountGivenMultiplicative(PowerModel power, Creature giver, decimal amount, Creature? target, CardModel? cardSource)
        => !IsSuppressed && Me.Has("evil_echo") && giver == Player.Creature && target?.IsEnemy == true
            && power.GetTypeForAmount(amount) == PowerType.Debuff && !EvilEchoScope.IsInternal(power, target, giver, cardSource)
            ? Balance.EvilEchoMultiplier(Me.Effects["evil_echo"].Rank) : 1m;
}

// Native temporary attributes apply a second, internal power. Its value is
// already scaled by the outer power; scale it only once and keep restoration
// balanced. Async scope also covers updates to an already existing power.
internal static class EvilEchoScope
{
    private sealed record Scope(Type InternalType, Creature Target, Creature? Giver, CardModel? Card, Scope? Parent);
    private static readonly AsyncLocal<Scope?> current = new();
    [ThreadStatic] internal static bool EnterApply;
    [ThreadStatic] internal static bool EnterModify;
    internal static bool NeedsScope(PowerModel power, Creature target, decimal amount, Creature? applier)
        => power is ITemporaryPower && RaceRuntime.Live && RaceModifier.Active?.IsSuppressed == false
            && RaceSession.Current?.Me.Has("evil_echo") == true && target.IsEnemy
            && applier == RaceRuntime.State!.Players.Single().Creature && power.GetTypeForAmount(amount) == PowerType.Debuff;
    internal static bool IsInternal(PowerModel power, Creature target, Creature giver, CardModel? card)
    {
        for (var scope = current.Value; scope != null; scope = scope.Parent)
            if (power.GetType() == scope.InternalType && target == scope.Target && giver == scope.Giver && card == scope.Card) return true;
        return false;
    }
    internal static async Task Apply(PlayerChoiceContext context, PowerModel power, Creature target, decimal amount, Creature? applier, CardModel? card, bool silent)
    {
        var previous = current.Value;
        current.Value = new(((ITemporaryPower)power).InternallyAppliedPower.GetType(), target, applier, card, previous);
        try { EnterApply = true; await PowerCmd.Apply(context, power, target, amount, applier, card, silent); }
        finally { EnterApply = false; current.Value = previous; }
    }
    internal static async Task<int> Modify(PlayerChoiceContext context, PowerModel power, decimal amount, Creature? applier, CardModel? card, bool silent)
    {
        var previous = current.Value;
        current.Value = new(((ITemporaryPower)power).InternallyAppliedPower.GetType(), power.Owner, applier, card, previous);
        try { EnterModify = true; return await PowerCmd.ModifyAmount(context, power, amount, applier, card, silent); }
        finally { EnterModify = false; current.Value = previous; }
    }
}
[HarmonyPatch(typeof(PowerCmd), nameof(PowerCmd.Apply), [typeof(PlayerChoiceContext), typeof(PowerModel), typeof(Creature), typeof(decimal), typeof(Creature), typeof(CardModel), typeof(bool)])]
internal static class EchoTemporaryApplyPatch
{
    static bool Prefix(PlayerChoiceContext choiceContext, PowerModel power, Creature target, decimal amount, Creature? applier, CardModel? cardSource, bool silent, ref Task __result)
    {
        if (EvilEchoScope.EnterApply) { EvilEchoScope.EnterApply = false; return true; }
        if (!EvilEchoScope.NeedsScope(power, target, amount, applier)) return true;
        __result = EvilEchoScope.Apply(choiceContext, power, target, amount, applier, cardSource, silent); return false;
    }
}
[HarmonyPatch(typeof(PowerCmd), nameof(PowerCmd.ModifyAmount))]
internal static class EchoTemporaryModifyPatch
{
    static bool Prefix(PlayerChoiceContext choiceContext, PowerModel power, decimal offset, Creature? applier, CardModel? cardSource, bool silent, ref Task<int> __result)
    {
        if (EvilEchoScope.EnterModify) { EvilEchoScope.EnterModify = false; return true; }
        if (power is not ITemporaryPower) return true;
        if (!EvilEchoScope.NeedsScope(power, power.Owner, offset, applier)) return true;
        __result = EvilEchoScope.Modify(choiceContext, power, offset, applier, cardSource, silent); return false;
    }
}
