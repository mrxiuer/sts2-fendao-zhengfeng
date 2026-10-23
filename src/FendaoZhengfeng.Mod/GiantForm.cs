using FendaoZhengfeng.Core;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

internal static class GiantForm
{
    internal const string EffectId = "giant_form";

    internal static int Rank
    {
        get
        {
            var effects = RaceSession.Current?.Me.Effects;
            return effects != null && effects.TryGetValue(EffectId, out var holding) ? holding.Rank : -1;
        }
    }

    internal static decimal ExecutionRatio(int rank) => 1m + Math.Max(0, rank) * 0.1m;
    internal static float ScaleMultiplier(int rank) => Math.Min(3f, 2f + Math.Max(0, rank) * 0.1f);

    internal static bool CanExecute(int targetMaxHp, int playerCurrentHp, int rank)
        => playerCurrentHp > 0 && (decimal)targetMaxHp < (decimal)playerCurrentHp * ExecutionRatio(rank);

    internal static decimal ModifyHpLoss(
        Creature target,
        decimal originalLoss,
        ValueProp props,
        Creature? dealer,
        HpLossHookPhase phase)
    {
        if (phase != HpLossHookPhase.AfterOsty
            || dealer == null
            || !props.HasFlag(ValueProp.Move)
            || !RaceRuntime.Live
            || !CombatManager.Instance.IsInProgress
            || CombatManager.Instance.IsEnding
            || Rank < 0)
            return originalLoss;

        var player = RaceRuntime.State?.Players.FirstOrDefault();
        if (player == null
            || dealer != player.Creature
            || !dealer.IsAlive
            || target.IsDead
            || !target.IsEnemy
            || target.CombatState == null
            || target.CombatState != dealer.CombatState
            || !CanExecute(target.MaxHp, dealer.CurrentHp, Rank))
            return originalLoss;

        // Keep native damage, DamageResult, hooks and CreatureCmd.Kill settlement.
        // The final HP-loss hook runs after block absorption, so this guarantees
        // lethal unblocked damage even when the hit is fully blocked.
        return target.CurrentHp;
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyHpLost))]
internal static class GiantExecutionPatch
{
    static void Postfix(Creature target, ValueProp props, Creature? dealer, HpLossHookPhase phases, ref decimal __result)
        => __result = GiantForm.ModifyHpLoss(target, __result, props, dealer, phases);
}

[HarmonyPatch(typeof(NCreature), nameof(NCreature._Ready))]
internal static class GiantScaleAttachmentPatch
{
    static void Postfix(NCreature __instance)
    {
        if (!RaceRuntime.Live
            || !__instance.Entity.IsPlayer
            || RaceRuntime.State?.Players.FirstOrDefault() != __instance.Entity.Player
            || __instance.GetNodeOrNull<GiantScaleFollower>("GiantScaleFollower") != null)
            return;

        __instance.AddChild(new GiantScaleFollower(__instance) { Name = "GiantScaleFollower" });
    }
}

internal partial class GiantScaleFollower : Node
{
    private readonly NCreature creature;
    private SceneTree? tree;
    private float? baseScale;
    internal float NaturalScale => baseScale ?? creature.Visuals.DefaultScale;

    internal GiantScaleFollower(NCreature creature)
    {
        this.creature = creature;
        TreeEntered += OnTreeEntered;
        TreeExiting += OnTreeExiting;
    }

    private void OnTreeEntered()
    {
        tree = GetTree();
        tree.ProcessFrame += Frame;
        Frame();
    }

    private void OnTreeExiting()
    {
        if (GodotObject.IsInstanceValid(tree))
            tree!.ProcessFrame -= Frame;
        Restore();
        tree = null;
    }

    private void Frame()
    {
        if (!GodotObject.IsInstanceValid(creature) || creature.Visuals == null
            || !GodotObject.IsInstanceValid(creature.Visuals))
            return;

        float current = creature.Visuals.DefaultScale;
        if (current <= 0f || !float.IsFinite(current))
            return;
        baseScale ??= current;

        int rank = GiantForm.Rank;
        float multiplier = rank < 0 ? 1f : GiantForm.ScaleMultiplier(rank);
        float desired = baseScale.Value * multiplier;
        if (Mathf.Abs(current - desired) > 0.001f)
            creature.SetDefaultScaleTo(desired, 0.2f);
    }

    private void Restore()
    {
        if (baseScale is { } original
            && GodotObject.IsInstanceValid(creature)
            && creature.Visuals != null
            && GodotObject.IsInstanceValid(creature.Visuals))
            creature.SetDefaultScaleTo(original, 0.1f);
    }
}

