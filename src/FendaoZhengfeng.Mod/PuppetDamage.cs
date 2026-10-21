using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

// Native pets normally consume their owner's block. A substitute owns its
// actual block, so direct and intercepted attacks redirect that block lookup.
// An inner damage call establishes a fresh scope even if it targets somebody
// else, preventing retaliation from accidentally consuming the puppet's block.
internal static class PuppetDamageScope
{
    internal sealed class Frame
    {
        internal Creature? Target;
        internal Creature? OriginalTarget;
        internal bool Guarding;
        internal int Blocked;
        internal ValueProp Props;
    }
    internal static readonly AsyncLocal<Frame?> Current = new();
    [ThreadStatic] internal static bool EnterOriginal;
    internal static async Task<IEnumerable<DamageResult>> Damage(PlayerChoiceContext context, Creature[] targets, decimal amount,
        ValueProp props, Creature? dealer, CardModel? card, CardPlay? play)
    {
        var previous = Current.Value;
        Current.Value = new();
        try { EnterOriginal = true; return await CreatureCmd.Damage(context, targets, amount, props, dealer, card, play); }
        finally { EnterOriginal = false; Current.Value = previous; }
    }
}

[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Damage), [typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp), typeof(Creature), typeof(CardModel), typeof(CardPlay)])]
internal static class PuppetDirectDamagePatch
{
    static bool Prefix(PlayerChoiceContext choiceContext, IEnumerable<Creature>? targets, decimal amount, ValueProp props,
        Creature? dealer, CardModel? cardSource, CardPlay? cardPlay, ref Task<IEnumerable<DamageResult>> __result)
    {
        if (PuppetDamageScope.EnterOriginal) { PuppetDamageScope.EnterOriginal = false; return true; }
        if (targets == null) return true;
        if (PuppetDamageScope.Current.Value == null && (!RaceRuntime.Live || PuppetFollower.Find(RaceRuntime.State!.Players.Single()) == null)) return true;
        var list = targets.ToArray();
        if (PuppetDamageScope.Current.Value == null && !list.Any(target => target.Monster is SubstitutePuppet || PuppetFollower.AttackGuard(target, props) != null)) return true;
        __result = PuppetDamageScope.Damage(choiceContext, list, amount, props, dealer, cardSource, cardPlay); return false;
    }
}

[HarmonyPatch(typeof(Creature), nameof(Creature.DamageBlockInternal))]
internal static class PuppetOwnBlockPatch
{
    static bool Prefix(Creature __instance, decimal amount, ValueProp props, ref decimal __result)
    {
        if (PuppetDamageScope.Current.Value?.Target is not { } puppet || puppet.PetOwner?.Creature != __instance) return true;
        if (PuppetDamageScope.Current.Value!.Guarding && !puppet.IsAlive)
        {
            PuppetDamageScope.Current.Value.Target = null;
            PuppetDamageScope.Current.Value.Guarding = false;
            return true;
        }
        __result = puppet.DamageBlockInternal(amount, props);
        PuppetDamageScope.Current.Value!.Blocked = (int)__result;
        return false;
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeDamageReceived))]
internal static class PuppetDamageTargetScopePatch
{
    static void Prefix(ref Creature target, ValueProp props)
    {
        if (PuppetDamageScope.Current.Value is not { } frame) return;
        frame.OriginalTarget = target;
        frame.Target = target.Monster is SubstitutePuppet ? target : PuppetFollower.AttackGuard(target, props);
        frame.Guarding = frame.Target != null && frame.Target != target;
        frame.Blocked = 0; frame.Props = props;
        if (frame.Guarding) target = frame.Target!;
    }
}

// Keep the native redirect-and-overkill path: a lethal hit first damages the
// puppet and only its excess reaches the hero, without a second damage command.
[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyUnblockedDamageTarget))]
internal static class PuppetAttackRedirectPatch
{
    static void Postfix(Creature originalTarget, ValueProp props, ref Creature __result)
    { if (PuppetFollower.AttackGuard(originalTarget, props) is { } puppet) __result = puppet; }
}

// Defensive powers belong to the body taking the hit. This also makes native
// enemy damage previews use the puppet's vulnerability rather than the hero's.
[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
internal static class PuppetAttackModifiersPatch
{
    static void Prefix(ref Creature? target, ValueProp props)
    { if (target != null && PuppetFollower.AttackGuard(target, props) is { } puppet) target = puppet; }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyHpLost))]
internal static class PuppetPreRedirectHpModifiersPatch
{
    static void Prefix(ref Creature target, ValueProp props, HpLossHookPhase phases)
    {
        // AfterOsty is also invoked for the hero's overflow; leave that phase
        // on its actual receiver so native resurrection/HP guards still work.
        if (phases == HpLossHookPhase.BeforeOsty && PuppetFollower.AttackGuard(target, props) is { } puppet) target = puppet;
    }
}

// Vanilla assigns block events to the originally targeted hero (Osty shares
// hero block). Move them to the puppet before native history, VFX and triggers.
[HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.DamageReceived))]
internal static class PuppetDamageEventsPatch
{
    static void Prefix(DamageResult result)
    {
        if (PuppetDamageScope.Current.Value is not { Target: { } puppet } frame) return;
        if (result.Receiver == puppet)
        {
            result.BlockedDamage = frame.Blocked;
            result.WasBlockBroken = frame.Blocked > 0 && puppet.Block == 0;
            result.WasFullyBlocked = !frame.Props.HasFlag(ValueProp.Unblockable)
                && (frame.Blocked > 0 || puppet.Block > 0) && result.UnblockedDamage + result.OverkillDamage == 0;
        }
        else if (frame.Guarding && result.Receiver == frame.OriginalTarget)
        {
            result.BlockedDamage = 0; result.WasBlockBroken = false; result.WasFullyBlocked = false;
        }
    }
}
