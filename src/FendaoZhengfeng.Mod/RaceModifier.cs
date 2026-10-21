using FendaoZhengfeng.Core;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

public sealed partial class RaceModifier : ModifierModel
{
    private RaceSession S => RaceSession.Current ?? throw new InvalidOperationException("分道争锋会话未初始化。");
    private Racer Me => S.Me;
    private Player Player => RunState.Players.Single();
    public static RaceModifier? Active => RaceRuntime.State?.Modifiers.OfType<RaceModifier>().FirstOrDefault();
    public override MegaCrit.Sts2.Core.Localization.LocString Title => new(ModEntry.Id, "mode.title");
    public override MegaCrit.Sts2.Core.Localization.LocString Description => new(ModEntry.Id, "mode.description");
    public int Turn { get; private set; }
    public int ManualCards { get; private set; }
    public int Dice { get; private set; } = 100;
    internal bool IsOpeningEnemy(Creature creature) => initialEnemies.Contains(creature);
    internal int RemainingEnemyBudget(Creature creature) => Math.Max(0, (int)Math.Ceiling(openingMaxHp.GetValueOrDefault(creature, creature.MaxHp) * Math.Max(0.2, 0.4 - 0.05 * (N("hard_shell") - 1))) - hpLost.GetValueOrDefault(creature));
    private readonly HashSet<Creature> resetScaled = [];
    private readonly Dictionary<Creature, int> resetBaseHp = [];
    internal int ResetBaseHp(Creature creature) => resetBaseHp.GetValueOrDefault(creature, creature.MaxHp);
    public override async Task AfterCreatureAddedToCombat(Creature creature)
    {
        if (creature.IsEnemy && FiveActCampaign.ExtraMonsterMultiplier(RunState.CurrentActIndex, Me.ResetCount) > 1 && resetScaled.Add(creature))
        { resetBaseHp[creature] = creature.MaxHp; await CreatureCmd.SetMaxAndCurrentHp(creature, Math.Ceiling(creature.MaxHp * (FiveActCampaign.ExtraMonsterMultiplier(RunState.CurrentActIndex, Me.ResetCount)))); }
    }
    private bool firstDraw;
    private bool suppressPropagation;
    private bool reflecting;
    private readonly HashSet<(CardModel, Creature)> barbsHits = [];
    private readonly Dictionary<Creature, int> hpLost = [];
    private readonly HashSet<Creature> initialEnemies = [];
    private readonly Dictionary<Creature, int> openingMaxHp = [];
    private readonly Dictionary<CardModel, int> wear = [];
    private readonly HashSet<Reward> expandedRewards = [];
    private string deferredWear = "[]";
    [SavedProperty] public string OpeningBattleKey { get; set; } = "";
    [SavedProperty] public string OpeningReceiptId { get; set; } = "";
    [SavedProperty] public int OpeningEnergy { get; set; }
    [SavedProperty] public string OpeningStatuses { get; set; } = "[]";
    private bool replayOpening;
    [SavedProperty] public int AppliedMaxHpBonus { get; set; }
    [SavedProperty]
    public string WearJson
    {
        get => IsCanonical ? "[]" : StateJson.Serialize(Player.Deck.Cards.Select(c => wear.GetValueOrDefault(c, 8)).ToList());
        set => deferredWear = value;
    }
    protected override void AfterRunLoaded(RunState runState) { wear.Clear(); var values = StateJson.Deserialize<List<int>>(deferredWear); for (int i = 0; i < Math.Min(values.Count, Player.Deck.Cards.Count); i++) wear[Player.Deck.Cards[i]] = values[i]; LoadExtraState(); }
    private int N(string id) => Me.Count(id);
    private PlayerChoiceContext Context() => new ThrowingPlayerChoiceContext();
    public override async Task BeforeCombatStart()
    {
        ResetTierCombat();
        Turn = 0; ManualCards = 0; firstDraw = false; barbsHits.Clear(); hpLost.Clear(); initialEnemies.Clear(); openingMaxHp.Clear();
        string key = RaceRuntime.RoomKey;
        replayOpening = RaceRuntime.ReplayingBattleKey == key && Me.InBattle;
        await S.Submit(S.New("battle_start", key));
        await BeforeAdvancedCombat();
        var state = Player.Creature.CombatState ?? throw new InvalidOperationException("战斗未创建。");
        foreach (var enemy in state.Enemies) initialEnemies.Add(enemy);
        await Power<StrengthPower>(Player.Creature, -N("exhaustion"));
        await Power<DexterityPower>(Player.Creature, -N("imbalance"));
        await Power<WeakPower>(Player.Creature, N("timidity"));
        foreach (var enemy in initialEnemies)
        {
            await AfterCreatureAddedToCombat(enemy);
            await Power<StrengthPower>(enemy, N("abet"));
            if (N("overgrowth") > 0) await CreatureCmd.SetMaxAndCurrentHp(enemy, Math.Ceiling(enemy.MaxHp * (1 + 0.1m * N("overgrowth"))));
            openingMaxHp[enemy] = enemy.MaxHp;
        }
        await TierOpening();
        await UpdateMaxHp();
        if (!Me.UsedServices.Contains("opening:" + key))
        {
            var opening = S.New("consume_opening", key);
            OpeningBattleKey = key; OpeningReceiptId = opening.Id; OpeningEnergy = Me.PendingEnergy;
            OpeningStatuses = StateJson.Serialize(Me.PendingStatuses);
            await S.Submit(opening);
        }
        else if (!replayOpening) throw new RuleException("该战斗开场已经结算，不能重复进入。");
    }
    private async Task Power<T>(Creature creature, int amount) where T : PowerModel
    { if (amount != 0) await PowerCmd.Apply<T>(Context(), creature, amount, null, null); }
    public override async Task AfterEnergyResetLate(Player player)
    {
        if (player != Player) return; ResetTierTurn(); Turn++; ManualCards = 0; firstDraw = false; hpLost.Clear(); RefreshHandCosts();
        if (N("fortune_dice") > 0) { int min = Math.Max(50, 80 - 5 * (N("fortune_dice") - 1)); var r = new StableRandom(S.Engine.State.Seed + ":" + S.LocalId + ":" + Me.BattleKey + ":" + Turn); Dice = min + (int)(r.Next() * (111 - min)); } else Dice = 100;
        if (Me.Has("borrowed_time")) { await PlayerCmd.GainEnergy(1, player); await Power<DoomPower>(player.Creature, 3); }
        if (Turn == 1)
        {
            if (Me.Has("all_in")) await PlayerCmd.GainEnergy(1, player);
            if (Me.Has("tomorrow")) await PlayerCmd.GainEnergy(1, player);
            if (replayOpening && OpeningBattleKey == Me.BattleKey)
            {
                foreach (var receipt in Me.Receipts.Where(receipt => receipt.Id.StartsWith(OpeningReceiptId + ":", StringComparison.Ordinal)).ToList())
                    await S.Submit(S.New("ack", receipt.Id));
                await PlayerCmd.GainEnergy(OpeningEnergy, Player);
                foreach (string id in StateJson.Deserialize<string[]>(OpeningStatuses))
                {
                    var model = ModelDb.AllCards.FirstOrDefault(card => card.Id.ToString() == id && card.Type == CardType.Status);
                    if (model != null) await CardPileCmd.AddGeneratedCardToCombat(Player.Creature.CombatState!.CreateCard(model, Player), PileType.Draw, null);
                }
            }
            replayOpening = false; RaceRuntime.ReplayingBattleKey = "";
            await ApplyReceipts(Context(), true);
        }
        if (Turn == 2 && Me.Has("tomorrow")) await PlayerCmd.LoseEnergy(1, player);
        foreach (var enemy in initialEnemies.Where(e => e.IsAlive)) if (N("iron_curtain") > 0) await CreatureCmd.GainBlock(enemy, 2 * N("iron_curtain"), ValueProp.Unpowered, null);
        RaceUi.Refresh();
    }
    public override async Task AfterSideTurnStartLate(CombatSide side, IReadOnlyList<Creature> participants, ICombatState combatState)
    {
        if (side != CombatSide.Enemy || N("carapace") == 0) return;
        foreach (var enemy in initialEnemies.Where(e => e.IsAlive)) await Power<PlatingPower>(enemy, 2 + N("carapace"));
    }
    public override async Task AfterSideTurnEnd(PlayerChoiceContext choiceContext, CombatSide side, IEnumerable<Creature> participants)
    {
        if (side == CombatSide.Enemy && N("ritual") > 0) foreach (var e in participants.Where(initialEnemies.Contains).Where(e => e.IsAlive)) await Power<StrengthPower>(e, N("ritual"));
    }
    public override decimal ModifyHandDrawLate(Player player, decimal count)
    {
        if (player != Player) return count;
        if (Turn == 1) { if (Me.Has("tomorrow")) count++; count += RankValue("poise", 1, 1); if (Me.Has("catch_up")) count++; if (Me.Has("greedy_pocket")) count = Math.Max(1, count - 1); }
        if (Turn == 2 && Me.Has("tomorrow")) count = Math.Max(0, count - 1); return count;
    }
    public override async Task AfterCardDrawn(PlayerChoiceContext ctx, CardModel card, bool fromHandDraw)
    {
        if (firstDraw || Turn != 1 || !fromHandDraw || N("clutter") == 0) return;
        // Insert after the normal hand-draw batch, not after its first card.
        firstDraw = true; await Task.CompletedTask;
    }
    public override async Task AfterPlayerTurnStartLate(PlayerChoiceContext ctx, Player player)
    {
        if (player != Player) return;
        await TierTurnStart(ctx);
        if (Turn != 1) return;
        if (N("clutter") > 0) for (int i = 0; i < 1 + N("clutter"); i++) await CardPileCmd.AddGeneratedCardToCombat(Player.Creature.CombatState!.CreateCard<ClutterCard>(Player), PileType.Hand, null);
        await ApplyReceipts(ctx, true);
    }
    public override decimal ModifyDamageMultiplicative(Creature? target, decimal amount, ValueProp props, Creature? dealer, CardModel? cardSource, CardPlay? cardPlay)
        => dealer == Player.Creature && cardSource?.Type == CardType.Attack ? (decimal)(1 + Me.Growth) * Dice / 100m : dealer?.IsEnemy == true ? FiveActCampaign.ExtraMonsterMultiplier(RunState.CurrentActIndex, Me.ResetCount) : 1m;
    public override decimal ModifyBlockMultiplicative(Creature target, decimal block, ValueProp props, CardModel? cardSource, CardPlay? cardPlay)
        => target == Player.Creature ? (Me.Has("all_in") && Turn == 1 ? 0 : (decimal)(1 + Me.Growth)) : target.IsEnemy ? FiveActCampaign.ExtraMonsterMultiplier(RunState.CurrentActIndex, Me.ResetCount) : 1;
    public override decimal ModifyHpLostAfterOstyLate(Creature target, decimal amount, ValueProp props, Creature? dealer, CardModel? cardSource)
    {
        if (initialEnemies.Contains(target) && N("hard_shell") > 0 && Turn <= 3)
        { int cap = (int)Math.Ceiling(openingMaxHp[target] * Math.Max(0.2, 0.4 - 0.05 * (N("hard_shell") - 1))); amount = Math.Min(amount, Math.Max(0, cap - hpLost.GetValueOrDefault(target))); }
        // Only intercept combat damage. Direct Kill/force death never passes this hook.
        if (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsEnding
            && target == Player.Creature && target.CurrentHp > 0 && amount >= target.CurrentHp
            && cardSource?.Owner != Player && S.TryReserveGhost())
            return Math.Max(0, target.CurrentHp - 1);
        return amount;
    }
    public override Task AfterCurrentHpChanged(Creature creature, decimal delta)
    { if (initialEnemies.Contains(creature) && delta < 0) hpLost[creature] = hpLost.GetValueOrDefault(creature) + (int)-delta; return Task.CompletedTask; }
    public override async Task AfterDamageGiven(PlayerChoiceContext ctx, Creature? dealer, DamageResult result, ValueProp props, Creature target, CardModel? cardSource)
    {
        if (reflecting || dealer != Player.Creature || cardSource?.Type != CardType.Attack || result.UnblockedDamage <= 0 || !initialEnemies.Contains(target) || N("barbs") == 0) return;
        if (!barbsHits.Add((cardSource, target))) return;
        reflecting = true; try { await CreatureCmd.Damage(ctx, Player.Creature, N("barbs"), ValueProp.Unpowered, null, null, null); } finally { reflecting = false; }
    }
    public override Task BeforeCardPlayed(CardPlay play)
    {
        TierBeforePlay(play);
        if (play.IsFirstInSeries) barbsHits.RemoveWhere(hit => hit.Item1 == play.Card);
        if (!play.IsAutoPlay && play.IsFirstInSeries)
        {
            ManualCards++;
            RaceRuntime.CurrentCardPlay = play;
            RefreshHandCosts();
        }
        return Task.CompletedTask;
    }
    public override bool TryModifyEnergyCostInCombatLate(CardModel card, decimal originalCost, out decimal modifiedCost)
    {
        bool applies = Me.Has("bitter_sweet") && ManualCards == 0 && card.Owner == Player;
        // The native hook consumes the out value even when the method returns false.
        modifiedCost = applies ? originalCost * 2 : originalCost;
        return applies;
    }
    private void RefreshHandCosts()
    {
        foreach (var card in PileType.Hand.GetPile(Player).Cards) card.InvokeEnergyCostChanged();
    }
    public override int ModifyCardPlayCount(CardModel card, Creature? target, int playCount)
        => Me.Has("bitter_sweet") && ManualCards == 2 && card.Owner == Player && !CardPlayScope.IsAutomatic(card) ? playCount + 1 + (ShouldEcho(card) ? 1 : 0) : playCount + (ShouldEcho(card) ? 1 : 0);
    public override async Task AfterCardPlayedLate(PlayerChoiceContext ctx, CardPlay play)
    {
        if (!play.IsLastInSeries || play.IsAutoPlay) return;
        await TierAfterPlay(ctx, play);
        var original = play.Card.DeckVersion;
        if (Me.Has("bloom_decay") && original != null && original.Type is not (CardType.Status or CardType.Curse))
        { wear[original] = Math.Max(0, wear.GetValueOrDefault(original, 8) - 1); }
        await Task.CompletedTask;
    }
    public override async Task AfterCombatVictory(CombatRoom room)
    {
        await S.Submit(S.New("victory", Me.BattleKey, value: room.RoomType.ToString(), boss: room.RoomType == RoomType.Boss && FiveActCampaign.IsFinalBoss(RunState)));
        resetScaled.Clear();
        if (room.RoomType.ToString() == "Boss" && RunState.CurrentActIndex == RunState.Acts.Count - 1)
        {
            var finalPoint = RunState.Map.SecondBossMapPoint ?? RunState.Map.BossMapPoint;
            if (RunState.CurrentMapCoord == finalPoint.coord) await S.Submit(S.New("finish"));
        }
        await ApplyReceipts(Context(), false); RaceRuntime.Checkpoint();
    }
    public override async Task BeforeCombatRewardOffered(RewardsSet rewards, CombatRoom room)
    {
        RaceRuntime.RewardRoom = room; RaceRuntime.CombatRewards = rewards;
        await AddExpeditionRewards(rewards);
        if (CanRecoverSkill) rewards.Rewards.Add(new RaceActionReward(Player, 1));
        if (room.RoomType == RoomType.Boss && Me.BossPrizes.Count > 0) rewards.Rewards.Add(new RaceActionReward(Player, 2));
        await Task.CompletedTask;
    }
    public override async Task AfterRoomEntered(AbstractRoom room)
    {
        await RaceRuntime.NodeChanged();
        await S.Submit(S.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(RunState)) });
        await S.Submit(S.New("progress", amount: Player.Creature.CurrentHp, value: room.RoomType.ToString()));
        await ApplyReceipts(Context(), false); RaceRuntime.Checkpoint(); RaceUi.Refresh();
    }
    public override bool TryModifyRewardsLate(Player player, List<Reward> rewards, AbstractRoom? room)
    {
        if (player != Player || !Me.Has("bloom_decay") || room is not CombatRoom) return false;
        var original = rewards.OfType<CardReward>().FirstOrDefault(); if (original == null || !expandedRewards.Add(original)) return false;
        var options = (CardCreationOptions)HarmonyLib.AccessTools.PropertyGetter(typeof(CardReward), "RerollOptions").Invoke(original, null)!;
        rewards.Add(new CardReward(options, 3, Player)); return true;
    }
    internal async Task RecordRevival(Creature creature)
    {
        if (creature != Player.Creature || creature.CurrentHp <= 0 || !creature.IsAlive) return;
        // Record every successful native revival, even before this boon is owned.
        await S.Submit(S.New("revive"));
        await UpdateMaxHp(); RaceRuntime.Checkpoint(); RaceUi.Refresh();
    }
    public async Task UpdateMaxHp()
    {
        decimal basis = Player.Creature.MaxHp - AppliedMaxHpBonus + SmallLightLoss;
        int target = (int)Math.Floor(basis * (1 + (Me.Has("death_defiance") ? Me.RevivalCount * (0.1m + 0.01m * Me.Effects["death_defiance"].Rank) : 0))) - SmallLightLoss;
        target = Math.Max(1, target);
        AppliedMaxHpBonus = Math.Max(0, target + SmallLightLoss - (int)basis);
        if (target != Player.Creature.MaxHp) await CreatureCmd.SetMaxHp(Player.Creature, target);
    }
    private readonly SemaphoreSlim receiptGate = new(1);
    public async Task ApplyReceipts(PlayerChoiceContext ctx, bool inCombat, bool monetaryOnly = false)
    {
        await receiptGate.WaitAsync();
        try { await ApplyReceiptCore(ctx, inCombat, monetaryOnly); } finally { receiptGate.Release(); }
    }
    private async Task ApplyReceiptCore(PlayerChoiceContext ctx, bool inCombat, bool monetaryOnly)
    {
        foreach (var r in Me.Receipts.ToList())
        {
            if (S.ReceiptApplied(r.Id)) { await S.Submit(S.New("ack", r.Id)); continue; }
            if (monetaryOnly && r.Kind is not ("gold" or "spend_gold")) continue;
            if ((r.Kind is "energy" or "status") && !inCombat) continue;
            suppressPropagation = true;
            try
            {
                switch (r.Kind)
                {
                    case "gold": await PlayerCmd.GainGold(r.Amount, Player); break;
                    case "spend_gold": await PlayerCmd.LoseGold(r.Amount, Player); break;
                    case "heal": await CreatureCmd.Heal(Player.Creature, r.Amount); break;
                    case "heal_percent": await CreatureCmd.Heal(Player.Creature, Math.Max(1, Math.Floor(Player.Creature.MaxHp * r.Amount / 100m))); break;
                    case "energy": await PlayerCmd.GainEnergy(r.Amount, Player); break;
                    case "status":
                        var model = ModelDb.AllCards.FirstOrDefault(c => c.Id.ToString() == r.Value && c.Type == CardType.Status);
                        if (model != null) await CardPileCmd.AddGeneratedCardToCombat(Player.Creature.CombatState!.CreateCard(model, Player), PileType.Draw, null); break;
                    case "curse_card":
                        var pool = ModelDb.AllCards.Where(c => c.Type == CardType.Curse && c.IsRemovable).OrderBy(c => c.Id.ToString(), StringComparer.Ordinal).ToList();
                        if (pool.Count > 0) { var rng = new StableRandom(r.Id); await CardPileCmd.AddCursesToDeck([pool[(int)(rng.Next() * pool.Count)]], Player); }
                        break;
                    case "restore_skills": await RestoreSkills(); break;
                    case "repair": case "restore_deck": await Repair(); break;
                }
                S.MarkReceiptApplied(r.Id); await S.Submit(S.New("ack", r.Id));
            }
            finally { suppressPropagation = false; }
        }
    }
    public bool IsSuppressed => suppressPropagation;
    public override async Task AfterCardGeneratedForCombat(CardModel card, Player? creator)
    {
        if (suppressPropagation || creator != null || card.Type != CardType.Status || card is ClutterCard or WitheredCard || !Me.Has("shared_misery")) return;
        await S.Submit(S.New("status", amount: 1, value: card.Id.ToString()));
    }
    public override async Task AfterCardChangedPiles(CardModel card, PileType oldPileType, AbstractModel? clonedBy)
    {
        if (!suppressPropagation && Me.Has("bloom_decay") && card is not WitheredCard && card.DeckVersion is { } original
            && wear.GetValueOrDefault(original, 8) == 0 && card.Pile?.Type is PileType.Discard or PileType.Draw or PileType.Hand)
        {
            suppressPropagation = true;
            try { var wilted = Player.Creature.CombatState!.CreateCard<WitheredCard>(Player); wilted.DeckVersion = original; wilted.OriginalPacket = NativeCheckpoint.EncodeCard(original); await CardCmd.Transform(card, wilted); }
            finally { suppressPropagation = false; }
            return;
        }
        if (suppressPropagation || oldPileType == PileType.Deck || card.Pile?.Type != PileType.Deck || card.Type is CardType.Status or CardType.Curse || N("burn_bridges") == 0) return;
        var options = Player.Deck.Cards.Where(c => c != card && c.IsTransformable && !IsReserved(c)).ToList();
        var rng = new StableRandom(S.Engine.State.Seed + ":" + Me.Battle + ":" + Player.Deck.Cards.Count + ":" + card.Id);
        suppressPropagation = true;
        try
        {
            for (int i = 0; i < N("burn_bridges") && options.Count > 0; i++)
            {
                var old = options[(int)(rng.Next() * options.Count)]; options.Remove(old);
                var pool = ModelDb.AllCharacters.Where(c => c.Id != Player.Character.Id).SelectMany(c => c.CardPool.AllCards)
                    .Where(c => c.Rarity == old.Rarity && c.CanBeGeneratedByModifiers && c.Type is CardType.Attack or CardType.Skill or CardType.Power)
                    .Where(c => !c.HasStarCostX && c.CanonicalStarCost < 0).OrderBy(c => c.Id.ToString(), StringComparer.Ordinal).ToList();
                if (pool.Count == 0) continue;
                var replacement = RunState.CreateCard(pool[(int)(rng.Next() * pool.Count)], Player);
                for (int upgrade = 0; upgrade < Math.Min(old.CurrentUpgradeLevel, replacement.MaxUpgradeLevel); upgrade++) CardCmd.Upgrade(replacement);
                var result = await CardCmd.Transform(old, replacement);
                if (result != null) { wear.Remove(old); wear[replacement] = 8; }
            }
        }
        finally { suppressPropagation = false; }
    }
    private async Task WitherDeck()
    {
        suppressPropagation = true;
        try
        {
            foreach (var card in Player.Deck.Cards.Where(c => wear.GetValueOrDefault(c, 8) <= 0 && !IsReserved(c)).ToList())
            { var replacement = RunState.CreateCard<WitheredCard>(Player); replacement.OriginalPacket = NativeCheckpoint.EncodeCard(card); var result = await CardCmd.Transform(card, replacement); if (result?.success == true) wear.Remove(card); }
        }
        finally { suppressPropagation = false; }
    }
    public async Task FinishWithering() => await WitherDeck();
    public async Task Repair()
    {
        suppressPropagation = true;
        try { foreach (var card in Player.Deck.Cards.OfType<WitheredCard>().ToList()) await CardCmd.Transform(card, NativeCheckpoint.DecodeCard(card.OriginalPacket, RunState, Player)); wear.Clear(); }
        finally { suppressPropagation = false; }
    }
}
