using FendaoZhengfeng.Core;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

public sealed partial class RaceModifier
{
    internal bool CycleUsed { get; private set; }
    internal bool CorneredUsed { get; private set; }
    internal bool DisorderUsed { get; private set; }
    internal bool HeavyUsed { get; private set; }
    internal int HeavyBonusUsed { get; private set; }
    internal bool EchoUsed { get; private set; }
    private bool sawAttack;
    private bool initialShuffleComplete;
    private CardModel? echoCard;
    private int RankValue(string id, int basis, int perRank) => Me.Has(id) ? basis + Me.Effects[id].Rank * perRank : 0;
    private void ResetTierCombat()
    {
        CorneredUsed = false; DisorderUsed = false; HeavyBonusUsed = 0; initialShuffleComplete = false;
        ResetTierTurn();
    }
    private void ResetTierTurn()
    { CycleUsed = false; HeavyUsed = false; EchoUsed = false; sawAttack = false; echoCard = null; }
    private async Task TierOpening()
    {
        await Power<StrengthPower>(Player.Creature, RankValue("sharpen", 1, 1) + (Me.Has("lone_crown") ? 2 : 0));
        await Power<DexterityPower>(Player.Creature, RankValue("light_step", 1, 1) + (Me.Has("lone_crown") ? 2 : 0));
        await Power<FrailPower>(Player.Creature, N("thin_armor"));
        await Power<VulnerablePower>(Player.Creature, N("exposed"));
        if (N("abrasion") > 0) await PayCurrentHp(1 + N("abrasion"));
    }
    private async Task PayCurrentHp(int amount)
    {
        // A direct resource payment, not combat damage: cannot farm damage hooks,
        // Ghost charges or ordinary "when damaged" benefits.
        if (Player.Creature.CurrentHp > 1)
            await CreatureCmd.SetCurrentHp(Player.Creature, Math.Max(1, Player.Creature.CurrentHp - amount));
    }
    private async Task TierTurnStart(PlayerChoiceContext context)
    {
        if (Turn == 1 && Me.Has("prepared"))
            await CreatureCmd.GainBlock(Player.Creature, RankValue("prepared", 6, 3), ValueProp.Unpowered, null);
        if (Me.Has("cornered") && !CorneredUsed && Player.Creature.CurrentHp * 2 <= Player.Creature.MaxHp)
        {
            CorneredUsed = true;
            await CreatureCmd.GainBlock(Player.Creature, RankValue("cornered", 8, 4), ValueProp.Unpowered, null);
            await CardPileCmd.Draw(context, 1, Player);
        }
    }
    internal bool ShouldEcho(CardModel card) => Me.Has("desperate_echo") && !EchoUsed && card.Owner == Player
        && card.Type == CardType.Attack && !card.EnergyCost.CostsX && !CardPlayScope.IsAutomatic(card) && CardPlayScope.EnergySpent(card) >= 2;
    private void TierBeforePlay(CardPlay play)
    {
        if (play.Player != Player || play.IsAutoPlay || !play.IsFirstInSeries) return;
        if (ShouldEcho(play.Card)) { EchoUsed = true; echoCard = play.Card; }
    }
    private async Task TierAfterPlay(PlayerChoiceContext context, CardPlay play)
    {
        if (play.Player != Player || play.IsAutoPlay || !play.IsLastInSeries) return;
        bool attack = play.Card.Type == CardType.Attack;
        if (Me.Has("combat_cycle") && !CycleUsed && sawAttack && play.Card.Type == CardType.Skill)
        { CycleUsed = true; await CreatureCmd.GainBlock(Player.Creature, RankValue("combat_cycle", 4, 2), ValueProp.Unpowered, null); }
        if (attack) sawAttack = true;
        if (Me.Has("heavy_followup") && !HeavyUsed && attack && play.Resources.EnergySpent >= 2)
        {
            HeavyUsed = true; int count = 1;
            if (HeavyBonusUsed < Me.Effects["heavy_followup"].Rank) { HeavyBonusUsed++; count++; }
            await CardPileCmd.Draw(context, count, Player);
        }
        if (N("combo_tax") > 0 && ManualCards % 4 == 0)
            foreach (var enemy in Player.Creature.CombatState!.Enemies.Where(enemy => enemy.IsAlive).ToList())
                await CreatureCmd.GainBlock(enemy, 1 + 2 * N("combo_tax"), ValueProp.Unpowered, null);
        if (echoCard == play.Card) { echoCard = null; await PayCurrentHp(2); }
    }
    public override void ModifyShuffleOrder(Player player, List<CardModel> cards, bool isInitialShuffle)
    { if (player == Player && !isInitialShuffle) initialShuffleComplete = true; }
    public override async Task AfterShuffle(PlayerChoiceContext context, Player shuffler)
    {
        if (shuffler != Player || !initialShuffleComplete || DisorderUsed || N("disorder") == 0) return;
        // BeforeCombatStart runs after the initial draw-pile shuffle. Turn zero is
        // the initial setup and must never be counted as the first reshuffle.
        if (Turn == 0) return;
        DisorderUsed = true; suppressPropagation = true;
        try { for (int i = 0; i < 1 + N("disorder"); i++) await CardPileCmd.AddGeneratedCardToCombat(Player.Creature.CombatState!.CreateCard<Dazed>(Player), PileType.Draw, Player); }
        finally { suppressPropagation = false; }
    }
    private async Task AddExpeditionRewards(RewardsSet rewards)
    {
        foreach (var ticket in Me.ExpeditionRewards.ToList())
        {
            var relic = new RelicReward(Player); relic.Populate();
            RaceRuntime.BindRewardKey(relic, ticket);
            rewards.Rewards.Add(relic);
            await S.Submit(S.New("expedition_emit", ticket));
        }
    }
}

[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Heal))]
internal static class LoneCrownHealingPatch
{
    static void Prefix(Creature creature, ref decimal amount)
    {
        if (RaceRuntime.Live && creature == RaceRuntime.State!.Players.Single().Creature && creature.CurrentHp > 0
            && RaceSession.Current!.Me.Has("lone_crown") && RaceRuntime.State.CurrentRoom?.RoomType != RoomType.RestSite)
            amount = Math.Floor(amount / 2);
    }
}
