using FendaoZhengfeng.Core;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Rooms;

namespace FendaoZhengfeng.Mod;

public sealed class TowerHeartEncounter : EncounterModel
{
    public override RoomType RoomType => RoomType.Boss;
    public override IEnumerable<MonsterModel> AllPossibleMonsters => [ModelDb.Monster<TowerHeart>()];
    // Preload native map scaffolding; HeartMapIconPatch supplies the actual heart texture.
    public override string BossNodePath => ModelDb.Encounter<MegaCrit.Sts2.Core.Models.Encounters.CeremonialBeastBoss>().BossNodePath;
    protected override IReadOnlyList<(MonsterModel, string?)> GenerateMonsters() => [(ModelDb.Monster<TowerHeart>().ToMutable(), null)];
}

public sealed class TowerHeart : MonsterModel
{
    public override LocString Title => new(ModEntry.Id, "heart.name");
    public override int MinInitialHp => HeartRules.BaseHp;
    public override int MaxInitialHp => HeartRules.BaseHp;
    public override bool HasDeathSfx => false;
    public override float DeathAnimLengthOverride => 3.2f;
    public override bool ShouldFadeAfterDeath => false;
    public override bool ShouldDisappearFromDoom => false;
    public override bool ShouldShowInCompendium => false;
    public override IEnumerable<string> AssetPaths => [];
    internal HeartEchoDeck EchoDeck { get; private set; } = new();
    protected override void DeepCloneFields() { base.DeepCloneFields(); EchoDeck = new HeartEchoDeck(); }
    private string Seed => RaceRuntime.State?.Rng.Seed.ToString() ?? "heart";
    internal int Round => Creature.CombatState?.RoundNumber ?? 1;

    protected override MonsterMoveStateMachine GenerateMoveStateMachine()
    {
        var debilitate = new MoveState("DEBILITATE_MOVE", Debilitate, new DebuffIntent());
        var blood = new MoveState("BLOOD_SHOTS_MOVE", BloodShots, new MultiAttackIntent(HeartRules.BloodDamage, HeartRules.BloodHits));
        var echo = new MoveState("ECHO_MOVE", Echo, new SingleAttackIntent(HeartRules.EchoDamage));
        var buff = new MoveState("STRENGTHEN_MOVE", Strengthen, new BuffIntent());
        var copy = new MoveState("COPY_MOVE", Strengthen, new BuffIntent(), new HeartCopyIntent());
        var branch = new ConditionalBranchState("HEART_NEXT");
        branch.AddState(blood, () => HeartRules.ActionAt(Seed, Round) == HeartAction.BloodShots);
        branch.AddState(echo, () => HeartRules.ActionAt(Seed, Round) == HeartAction.Echo);
        branch.AddState(copy, () => Round == HeartRules.CopyAtRound);
        branch.AddState(buff, () => true);
        debilitate.FollowUpState = branch; blood.FollowUpState = branch; echo.FollowUpState = branch; buff.FollowUpState = branch;
        copy.FollowUpState = branch;
        return new MonsterMoveStateMachine([debilitate, blood, echo, buff, copy, branch], debilitate);
    }
    public override async Task BeforeCombatStartLate()
    {
        var ctx = new ThrowingPlayerChoiceContext();
        await PowerCmd.Apply<HeartWardPower>(ctx, Creature, 1, Creature, null);
        await PowerCmd.Apply<HeartBeatPower>(ctx, Creature, 1, Creature, null);
        HeartPresentation.Install(this);
        await HeartPresentation.Intro(this);
    }
    private async Task Debilitate(IReadOnlyList<Creature> targets)
    {
        var ctx = new ThrowingPlayerChoiceContext();
        await HeartPresentation.Attack(this, false);
        await PowerCmd.Apply<VulnerablePower>(ctx, targets, 2, Creature, null);
        await PowerCmd.Apply<WeakPower>(ctx, targets, 2, Creature, null);
        await PowerCmd.Apply<FrailPower>(ctx, targets, 2, Creature, null);
        foreach (var target in targets.Where(target => target.IsPlayer))
            foreach (var card in new CardModel[] { ModelDb.Card<Dazed>(), ModelDb.Card<Slimed>(), ModelDb.Card<Wound>(), ModelDb.Card<Burn>(), ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.Void>() })
                await CardPileCmd.AddGeneratedCardToCombat(Creature.CombatState!.CreateCard(card, target.Player!), MegaCrit.Sts2.Core.Entities.Cards.PileType.Draw, null);
    }
    private async Task BloodShots(IReadOnlyList<Creature> targets)
    {
        await HeartPresentation.Attack(this, false);
        WoundsThisAction = 0;
        await DamageCmd.Attack(HeartRules.BloodDamage).FromMonster(this).WithNoAttackerAnim().WithHitCount(HeartRules.BloodHits).Execute(null);
    }
    private async Task Echo(IReadOnlyList<Creature> targets)
    {
        await HeartPresentation.Attack(this, true);
        WoundsThisAction = 0;
        await DamageCmd.Attack(HeartRules.EchoDamage).FromMonster(this).WithNoAttackerAnim().Execute(null);
    }
    // The damage listener records actual unblocked hits, not just the final HP delta of an action.
    internal int WoundsThisAction { get; set; }
    private async Task Strengthen(IReadOnlyList<Creature> targets)
    {
        int number = (Round - 1) / 3;
        var ctx = new ThrowingPlayerChoiceContext();
        var strength = Creature.GetPower<StrengthPower>();
        if (strength?.Amount < 0) await PowerCmd.Remove(strength);
        await PowerCmd.Apply<StrengthPower>(ctx, Creature, HeartRules.StrengthForBuff(number), Creature, null);
        if (number == 1) await PowerCmd.Apply<ArtifactPower>(ctx, Creature, 2, Creature, null);
        if (number == 2) await PowerCmd.Apply<HeartBeatPower>(ctx, Creature, 1, Creature, null);
        if (number == 3) await PowerCmd.Apply<HeartStabsPower>(ctx, Creature, 1, Creature, null);
        await HeartPresentation.Overload(this, number);
        if (Round == HeartRules.CopyAtRound) await EchoDeck.CopyOpponent(this);
    }
    public override async Task BeforeHandDraw(MegaCrit.Sts2.Core.Entities.Players.Player player, PlayerChoiceContext context, ICombatState combatState)
    {
        if (combatState.RoundNumber == HeartRules.CopyAtRound - 1)
            RaceUi.Notify("众生回响预警：心脏将在第4回合结束时复制另一名玩家的牌组，供你在第5、6回合使用；人工制品可以抵挡。");
        if (combatState.RoundNumber >= HeartRules.RestoreAtRound) await EchoDeck.Restore(player);
        else if (EchoDeck.Active && player.Creature.GetPower<HeartEchoPower>() is { } marker) marker.SetAmount(HeartRules.RestoreAtRound - combatState.RoundNumber);
    }
    public override Task AfterCombatEnd(CombatRoom room) => EchoDeck.Cleanup();
    public override Task BeforeDeath(Creature creature)
    {
        if (creature == Creature) DeathVisual = HeartPresentation.CaptureDeath(this);
        return Task.CompletedTask;
    }
    internal HeartDeathVisual? DeathVisual { get; private set; }
}

public sealed partial class RaceModifier
{
    public override async Task AfterDeath(PlayerChoiceContext context, Creature creature, bool wasRemovalPrevented, float deathAnimLength)
    {
        await AfterPuppetDeath(creature, wasRemovalPrevented);
        if (wasRemovalPrevented || creature.Monster is not TowerHeart heart) return;
        if (RunState.CurrentActIndex == 4 && FiveActCampaign.IsFinalBoss(RunState))
            await S.Submit(S.New("heart_finish", Me.BattleKey));
        HeartPresentation.PlayDeath(heart.DeathVisual);
        // Rank is already committed. Restoring cards or playing death art cannot change it.
        await heart.EchoDeck.Cleanup();
    }
}
