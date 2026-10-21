using FendaoZhengfeng.Core;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

public sealed partial class RaceModifier
{
    private sealed record CriticalAttack(AttackCommand Command, bool Critical);
    private readonly List<CriticalAttack> criticalAttacks = [];
    internal int CriticalRolls { get; private set; }
    internal bool LastAttackCritical { get; private set; }
    internal int ActiveCriticalAttacks => criticalAttacks.Count;
    private void ResetCriticalAttacks()
    { criticalAttacks.Clear(); CriticalRolls = 0; LastAttackCritical = false; }
    private bool IsGamblerDealer(Creature? creature) => creature == Player.Creature || creature?.PetOwner == Player;
    private void BeginCriticalAttack(AttackCommand command)
    {
        if (IsSuppressed || !Me.Has("gamblers_heart") || !IsGamblerDealer(command.Attacker)
            || !command.DamageProps.HasFlag(ValueProp.Move)) return;
        EndCriticalAttack(command);
        var random = new StableRandom(S.Engine.State.Seed + ":critical:" + S.LocalId + ":" + Me.ResetCount + ":" + Me.BattleKey + ":" + CriticalRolls++);
        LastAttackCritical = random.Next() < (double)Balance.CriticalChance(Me.Effects["gamblers_heart"].Rank);
        criticalAttacks.Add(new(command, LastAttackCritical));
        if (LastAttackCritical) ShowCriticalHit(command.Attacker!);
    }
    internal void EndCriticalAttack(AttackCommand command)
    { if (criticalAttacks.Count > 0) criticalAttacks.RemoveAll(entry => entry.Command == command); }
    public override Task AfterAttack(PlayerChoiceContext choiceContext, AttackCommand command)
    { EndCriticalAttack(command); return Task.CompletedTask; }
    private decimal GamblerDamageMultiplier(Creature? target, ValueProp props, Creature? dealer, CardModel? card, CardPlay? play)
    {
        if (IsSuppressed || !Me.Has("gamblers_heart")) return 1;
        // A self-hit is one damage event, so the two 10% clauses do not multiply twice.
        decimal multiplier = target == Player.Creature || IsGamblerDealer(dealer ?? GamblerPoisonOrigin.ApplierFor(target)) ? 1.1m : 1m;
        if (IsGamblerDealer(dealer) && props.HasFlag(ValueProp.Move)
            && criticalAttacks.LastOrDefault(entry => entry.Command.Attacker == dealer && entry.Command.ModelSource == card
                && entry.Command.CardPlay == play) is { Critical: true }) multiplier *= 1.5m;
        return multiplier;
    }
    private void ShowCriticalHit(Creature attacker)
    {
        if (attacker.GetCreatureNode() is not { } visual) return;
        var label = new Label { Name = "FdzfCriticalHit", Text = "暴击！", Position = Vector2.Zero,
            MouseFilter = Control.MouseFilterEnum.Ignore, ZIndex = 15 };
        label.AddThemeFontSizeOverride("font_size", 34);
        label.AddThemeColorOverride("font_color", new Color("ffd078"));
        label.AddThemeColorOverride("font_outline_color", new Color("421b18"));
        label.AddThemeConstantOverride("outline_size", 7);
        visual.AddChild(label);
        label.GlobalPosition = visual.GetTopOfHitbox() + new Vector2(-55, -55);
        var tween = label.CreateTween().SetParallel();
        tween.TweenProperty(label, "position:y", label.Position.Y - 55, 0.85);
        tween.TweenProperty(label, "modulate:a", 0, 0.5).SetDelay(0.35);
        tween.Finished += label.QueueFree;
    }
}

// Native grouped attacks use the same BeforeAttack/AfterAttack hooks. This
// finally also removes Execute frames if animation, damage, or a hook throws.
[HarmonyPatch(typeof(AttackCommand), nameof(AttackCommand.Execute))]
internal static class CriticalAttackCleanupPatch
{
    static void Postfix(AttackCommand __instance, ref Task<AttackCommand> __result)
    {
        if (RaceRuntime.Live && RaceSession.Current?.Me.Has("gamblers_heart") == true && RaceModifier.Active is { } modifier)
            __result = Finish(__result, modifier, __instance);
    }
    private static async Task<AttackCommand> Finish(Task<AttackCommand> pending, RaceModifier modifier, AttackCommand command)
    { try { return await pending; } finally { modifier.EndCriticalAttack(command); } }
}
