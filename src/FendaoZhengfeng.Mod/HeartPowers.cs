using FendaoZhengfeng.Core;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

public abstract class HeartPower : PowerModel
{
    public abstract string HeartKey { get; }
    public override PowerType Type => PowerType.Buff;
    public override PowerStackType StackType => PowerStackType.Counter;
    public override LocString Title => new(ModEntry.Id, "heart." + HeartKey + ".title");
    public override LocString Description => new(ModEntry.Id, "heart." + HeartKey + ".description");
    public override bool ShouldPlayVfx => false;
    public abstract string Detail { get; }
}
public sealed class HeartWardPower : HeartPower
{
    public override string HeartKey => "ward";
    // Amount encodes HP spent + 1, so the native power is never removed at an exhausted budget.
    public int Remaining => Math.Max(0, HeartRules.RoundBudget(Owner.MaxHp) - (Amount - 1));
    public override int DisplayAmount => Remaining;
    public override string Detail => $"本轮最多损失最大生命的25%（{HeartRules.RoundBudget(Owner.MaxHp)}），剩余{Remaining}。伤害、反伤和直接生命流失共用额度，在玩家回合开始时重置。灾厄与直接处决按原生规则处理。";
    public override decimal ModifyHpLostAfterOstyLate(Creature target, decimal amount, ValueProp props, Creature? dealer, CardModel? cardSource)
        => target == Owner ? Math.Min(amount, Remaining) : amount;
    public override Task AfterCurrentHpChanged(Creature creature, decimal delta)
    {
        if (creature == Owner && delta < 0) { SetAmount(Amount + (int)-delta); InvokeDisplayAmountChanged(); }
        return Task.CompletedTask;
    }
    public override Task BeforeSideTurnStart(PlayerChoiceContext context, CombatSide side, IReadOnlyList<Creature> participants, ICombatState state)
    {
        if (side == CombatSide.Player) { SetAmount(1); InvokeDisplayAmountChanged(); }
        return Task.CompletedTask;
    }
}
public sealed class HeartBeatPower : HeartPower
{
    public override string HeartKey => "beat";
    public override string Detail => $"玩家每次实际打出卡牌，在卡牌结算后受到{Amount}点伤害，可被格挡。重放也触发；生成卡牌不触发。不受心脏力量、幕数及R键的伤害倍率放大。";
    public override async Task AfterCardPlayed(PlayerChoiceContext context, CardPlay play)
    {
        if (Owner.IsDead || play.Card.Owner?.Creature is not { IsAlive: true } target || CombatManager.Instance.IsOverOrEnding) return;
        HeartPresentation.Beat(Owner);
        await CreatureCmd.Damage(context, target, Amount, ValueProp.Unpowered, null, null, null);
    }
}
public sealed class HeartStabsPower : HeartPower
{
    public override string HeartKey => "stabs";
    public override int DisplayAmount => 3;
    public override string Detail => "心脏攻击每次造成未被格挡的生命伤害时，向玩家弃牌堆加入1张伤口；同次攻击行动最多3张。";
    public override async Task AfterDamageGiven(PlayerChoiceContext context, Creature? dealer, DamageResult result, ValueProp props, Creature target, CardModel? cardSource)
    {
        if (dealer != Owner || !target.IsPlayer || result.UnblockedDamage <= 0 || Owner.Monster is not TowerHeart heart || heart.WoundsThisAction >= 3) return;
        heart.WoundsThisAction++;
        await CardPileCmd.AddGeneratedCardToCombat(Owner.CombatState!.CreateCard(ModelDb.Card<Wound>(), target.Player!), MegaCrit.Sts2.Core.Entities.Cards.PileType.Discard, null);
    }
}

public sealed class HeartEchoPower : HeartPower
{
    public override string HeartKey => "copy";
    public override async Task AfterRemoved(Creature owner)
    {
        var heart = owner.CombatState?.Enemies.Select(enemy => enemy.Monster).OfType<TowerHeart>().FirstOrDefault();
        if (heart != null && owner.Player is { } player) await heart.EchoDeck.Restore(player);
    }
    public override PowerType Type => PowerType.Debuff;
    public override string Detail
    {
        get
        {
            var heart = Owner.CombatState?.Enemies.Select(enemy => enemy.Monster).OfType<TowerHeart>().FirstOrDefault();
            return $"正在使用{heart?.EchoDeck.DonorName ?? "对手"}牌组的临时副本，剩余{Amount}个玩家回合。第7回合抽牌前恢复自己的各牌堆；新增状态牌保留。对手和永久牌组不受影响。";
        }
    }
}
