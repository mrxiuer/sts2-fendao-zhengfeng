using FendaoZhengfeng.Core;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace FendaoZhengfeng.Mod;

public sealed partial class RaceModifier
{
    private Dictionary<CardModel, string> deckIds = [];
    private List<string> deferredIds = [];
    private List<MarketCard> skillVault = [];
    private HashSet<string> appliedTrades = [];
    private HashSet<string> recoveredBattles = [];
    private string planGrant = "";
    [SavedProperty] public string DeckIdentities { get => IsCanonical ? "[]" : StateJson.Serialize(Player.Deck.Cards.Select(CardIdentity).ToList()); set => deferredIds = StateJson.Deserialize<List<string>>(value); }
    [SavedProperty] public string SkillVault { get => StateJson.Serialize(skillVault); set => skillVault = StateJson.Deserialize<List<MarketCard>>(value); }
    [SavedProperty] public string AppliedTrades { get => StateJson.Serialize(appliedTrades); set => appliedTrades = StateJson.Deserialize<HashSet<string>>(value); }
    [SavedProperty] public string RecoveredBattles { get => StateJson.Serialize(recoveredBattles); set => recoveredBattles = StateJson.Deserialize<HashSet<string>>(value); }
    [SavedProperty] public string PlanGrant { get => planGrant; set => planGrant = value; }
    [SavedProperty] public int SmallLightLoss { get; set; }
    internal int VaultCount => skillVault.Count;
    internal bool CanRecoverSkill => Me.Has("no_plan") && Me.WonBattles.Contains(Me.BattleKey) && skillVault.Count > 0 && !recoveredBattles.Contains(Me.BattleKey);
    internal void LoadExtraState()
    {
        for (int i = 0; i < Math.Min(deferredIds.Count, Player.Deck.Cards.Count); i++) deckIds[Player.Deck.Cards[i]] = deferredIds[i];
        if (Player.Deck.Cards.Count >= deferredIds.Count) deferredIds.Clear();
    }
    [SavedProperty] public int NextDeckIdentity { get; set; }
    internal string CardIdentity(CardModel card)
    {
        if (S.Engine.State.Synchronous)
        {
            // New multiplayer clients deserialize modifiers too, without calling AfterRunLoaded.
            if (deferredIds.Count > 0) LoadExtraState();
            // Restored remote cards can contain newer IDs than this modifier's local counter.
            // Read all existing identities before allocating any new one, preserving uniqueness.
            string prefix = "shared:" + Me.Id + ":";
            foreach (var entry in Player.Deck.Cards)
            {
                if (!deckIds.TryGetValue(entry, out var existing) && SharedCardIdentity.Read(entry) is { } restored)
                    deckIds[entry] = existing = restored;
                if (existing != null && existing.StartsWith(prefix, StringComparison.Ordinal)
                    && int.TryParse(existing.AsSpan(prefix.Length), System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out int index))
                    NextDeckIdentity = Math.Max(NextDeckIdentity, checked(index + 1));
            }
            foreach (var entry in Player.Deck.Cards)
                if (!deckIds.ContainsKey(entry)) deckIds[entry] = prefix + NextDeckIdentity++;
        }
        if (!deckIds.TryGetValue(card, out var id)) deckIds[card] = id = Guid.NewGuid().ToString("N");
        if (S.Engine.State.Synchronous) SharedCardIdentity.Write(card, id);
        return id;
    }
    internal int CardWear(CardModel card) => wear.GetValueOrDefault(card, SharedCardWear.Read(card) ?? 8);
    internal int? RemainingWear(CardModel card)
    {
        if (!Me.Has("bloom_decay") || card.Owner != Player) return null;
        if (card is WitheredCard) return 0;
        var original = card.DeckVersion ?? (card.Pile?.Type == PileType.Deck ? card : null);
        if (original == null || original.Type is CardType.Status or CardType.Curse) return null;
        return CardWear(original);
    }
    internal List<MarketCard> CaptureMarket() => Player.Deck.Cards.Select(card => new MarketCard(CardIdentity(card), NativeCheckpoint.EncodeCard(card), card.Title, RemainingWear(card) ?? -1)).ToList();
    internal bool IsReserved(CardModel card)
    {
        var original = card.DeckVersion ?? card;
        return deckIds.TryGetValue(original, out var id) && S.Engine.State.Trades.Any(trade => trade.Seller == Me.Id && trade.Card.Id == id && !trade.Removed);
    }
    private async Task RemovePermanent(CardModel card)
    {
        // Combat copies are already present when BeforeCombatStart runs.
        if (CombatManager.Instance.IsInProgress && Player.Creature.CombatState != null)
            foreach (var pile in new[] { PileType.Hand, PileType.Draw, PileType.Discard, PileType.Exhaust, PileType.Play })
                foreach (var copy in pile.GetPile(Player).Cards.Where(copy => copy.DeckVersion == card).ToList())
                    await CardPileCmd.RemoveFromCombat(copy, true);
        await CardPileCmd.RemoveFromDeck(card, false);
        wear.Remove(card); deckIds.Remove(card);
    }
    private async Task AddPermanent(MarketCard stored, bool addCombatCopy)
    {
        var restored = NativeCheckpoint.DecodeCard(stored.Packet, RunState, Player);
        deckIds[restored] = stored.Id;
        if (stored.Wear >= 0) wear[restored] = stored.Wear;
        await CardPileCmd.Add(restored, PileType.Deck, skipVisuals: true);
        if (addCombatCopy && Player.Creature.CombatState is { } combat)
        {
            var copy = combat.CloneCard(restored); copy.DeckVersion = restored;
            await CardPileCmd.AddGeneratedCardToCombat(copy, PileType.Draw, Player);
        }
    }
    internal async Task BeforeAdvancedCombat()
    {
        suppressPropagation = true;
        try
        {
            foreach (var trade in S.Engine.State.Trades.Where(trade => trade.Seller == Me.Id && (!trade.Removed || S.Engine.State.Synchronous && !appliedTrades.Contains("remove:" + trade.Id)) && trade.SellerBattle <= Me.Battle).ToList())
            {
                string marker = "remove:" + trade.Id;
                if (!appliedTrades.Contains(marker))
                {
                    var card = Player.Deck.Cards.SingleOrDefault(card => CardIdentity(card) == trade.Card.Id)
                        ?? throw new InvalidOperationException(L10n.T("已成交卡牌丢失，无法安全交接：") + trade.Card.Name);
                    await RemovePermanent(card); appliedTrades.Add(marker);
                }
                await S.SubmitSharedLifecycle(LifecycleCommand("trade_removed", trade.Id));
            }
            foreach (var trade in S.Engine.State.Trades.Where(trade => trade.Buyer == Me.Id && (!trade.Delivered || S.Engine.State.Synchronous && !appliedTrades.Contains("deliver:" + trade.Id)) && trade.BuyerBattle <= Me.Battle).ToList())
            {
                string marker = "deliver:" + trade.Id;
                if (!appliedTrades.Contains(marker))
                { await AddPermanent(trade.Card with { Id = "bought-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(trade.Id)))[..32] }, true); appliedTrades.Add(marker); }
                await S.SubmitSharedLifecycle(LifecycleCommand("trade_delivered", trade.Id));
            }
            if (Me.Has("no_plan") && Me.Effects["no_plan"].Grants.FirstOrDefault()?.EventId is { } grant && grant != planGrant)
            {
                foreach (var card in Player.Deck.Cards.Where(card => card.Type == CardType.Skill).ToList())
                { skillVault.Add(new(CardIdentity(card), NativeCheckpoint.EncodeCard(card), card.Title, RemainingWear(card) ?? -1)); await RemovePermanent(card); }
                planGrant = grant;
            }
        }
        finally { suppressPropagation = false; }
    }
    internal async Task RestoreSkills()
    {
        bool previous = suppressPropagation; suppressPropagation = true;
        try { foreach (var stored in skillVault.ToList()) { await AddPermanent(stored, Me.InBattle); skillVault.Remove(stored); } planGrant = ""; }
        finally { suppressPropagation = previous; }
    }
    internal async Task<bool> RecoverSkill(int selectedIndex = -1)
    {
        if (!CanRecoverSkill) return true;
        var choices = skillVault.Select(stored => NativeCheckpoint.DecodeCard(stored.Packet, RunState, Player)).ToList();
        CardModel? selected = selectedIndex >= 0 ? choices[selectedIndex] : null;
        try
        {
            selected ??= (await CardSelectCmd.FromSimpleGrid(new BlockingPlayerChoiceContext(), choices, Player,
                new CardSelectorPrefs(new LocString(ModEntry.Id, "action.recover.title"), 1) { Cancelable = true })).FirstOrDefault();
            if (selected == null) return false;
            int index = choices.IndexOf(selected); if (index < 0) throw new InvalidOperationException(L10n.T("技能选择失效。"));
            var stored = skillVault[index];
            suppressPropagation = true;
            try { await CardPileCmd.Add(selected, PileType.Deck, skipVisuals: true); deckIds[selected] = stored.Id; if (stored.Wear >= 0) wear[selected] = stored.Wear; }
            finally { suppressPropagation = false; }
            skillVault.RemoveAt(index); recoveredBattles.Add(Me.BattleKey); RaceRuntime.Checkpoint(); RaceUi.Refresh(); return true;
        }
        finally { foreach (var card in choices.Where(card => card.Pile?.Type != PileType.Deck)) card.RemoveFromState(); }
    }
    internal async Task RemoveWithSmallLight(CardModel? chosen = null, PlayerChoiceContext? context = null, string operation = "")
    {
        if (!Me.Has("small_light") || Me.InBattle || CombatManager.Instance.IsInProgress || Me.Finished || Me.ResetPending) throw new RuleException(L10n.T("小而轻只能在非战斗时使用。"));
        if (Player.Creature.MaxHp <= 3) throw new RuleException(L10n.T("需要支付3点最大生命，支付后至少保留1点。"));
        var prefs = new CardSelectorPrefs(new LocString(ModEntry.Id, "action.remove.title"), 1) { Cancelable = true };
        var card = chosen ?? (context == null ? await CardSelectCmd.FromDeckGeneric(Player, prefs, candidate => !IsReserved(candidate))
            : await CardSelectCmd.FromSimpleGrid(context, Player.Deck.Cards.Where(candidate => !IsReserved(candidate)).ToList(), Player, prefs)).FirstOrDefault();
        if (card == null) return;
        if (!Player.Deck.Cards.Contains(card) || IsReserved(card) || Player.Creature.MaxHp <= 3) throw new RuleException(L10n.T("该卡牌已不可移除。"));
        await RemovePermanent(card); SmallLightLoss += 3; await CreatureCmd.SetMaxHp(Player.Creature, Player.Creature.MaxHp - 3);
        if (S.Engine.State.Synchronous) await SharedChoice.Profile(Player, operation);
        else await S.Submit(S.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(RunState)) });
        RaceRuntime.Checkpoint(); RaceUi.Refresh();
    }
}

internal static class AdvancedEffects
{
    private static bool pumping;
    internal static void Tick()
    {
        SharedReset.Tick();
        if (RaceSession.Current?.Engine.State.Synchronous == true) return;
        if (pumping || !RaceRuntime.Live || RaceSession.Current is not { WaitingAtCheckpoint: false } session || session.Me.ResetPending
            || RaceModifier.Active is not { } modifier || !session.Me.Receipts.Any(receipt => receipt.Kind is "gold" or "spend_gold")) return;
        pumping = true;
        RaceUi.Run(async () => { try { await modifier.ApplyReceipts(new ThrowingPlayerChoiceContext(), session.Me.InBattle, true); } finally { pumping = false; } });
    }
    internal static bool UpgradeBlocked(CardModel card) => !card.IsCanonical && RaceRuntime.Live && card.Owner != null && card.Owner.RunState == RaceRuntime.State
        && (RaceRuntime.RacerFor(card.Owner)?.Has("farewell_upgrades") == true || RaceModifier.ForPlayer(card.Owner)?.IsReserved(card) == true);
    internal static async Task Click(string id)
    {
        if (RaceSession.Current is not { } session || RaceModifier.Active is not { } modifier) return;
        if (session.Engine.State.Synchronous && id is "small_light" or "good_connections" or "tail_refill" or BurrowRules.EffectId)
        { SharedItemAction.Enqueue(id); return; }
        if (id == BurrowRules.EffectId) await BurrowEffect.Click();
        else if (id == "small_light") await modifier.RemoveWithSmallLight();
        else if (id == "good_connections") await BuyCard();
        else if (id == "tail_refill")
        {
            var player = RaceRuntime.LocalPlayer()!;
            if (session.Me.InBattle || CombatManager.Instance.IsInProgress || !player.Relics.OfType<MegaCrit.Sts2.Core.Models.Relics.LizardTail>().Any(tail => tail.WasUsed)) throw new RuleException(L10n.T("请在非战斗时补充已消耗的蜥蜴尾巴。"));
            await session.Submit(session.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(RaceRuntime.State!)) });
            await session.Submit(session.New("tail_refill"));
            await modifier.ApplyReceipts(new ThrowingPlayerChoiceContext(), false);
            RaceRuntime.Checkpoint(); RaceUi.Refresh();
        }
        else if (id == "r_key")
        {
            int choice = await RaceUi.Choose(L10n.T("R键 · 确认重置"), [L10n.T("使用R键\n保留当前角色资源与全部模式状态，回到第一幕起点；此后怪物生命、伤害、格挡累计增加20%。"), L10n.T("取消")]);
            if (choice == 0) await ResetRun();
        }
    }
    internal static async Task BuyCard(string? sellerId = null, string? cardId = null)
    {
        var session = RaceSession.Current!; var run = RaceRuntime.State!; var modifier = RaceModifier.Active!;
        if (!session.Me.Has("good_connections")) throw new RuleException(L10n.T("尚未取得强买强卖。"));
        var sellers = session.Engine.State.Racers.Values.Where(racer => racer.Id != session.LocalId && !racer.Eliminated && !racer.Finished && !racer.ResetPending).ToList();
        int who = sellerId == null ? await RaceUi.Choose(L10n.T("强买强卖 · 选择玩家"), sellers.Select(racer => L10n.RacerName(racer) + L10n.T("\n查看其当前公开牌组，已被购买的牌不能再次购买。")).ToList()) : sellers.FindIndex(racer => racer.Id == sellerId);
        if (who < 0) return;
        var seller = sellers[who]; var cards = seller.MarketCards.Where(card => !session.Engine.State.Trades.Any(trade => trade.Seller == seller.Id && trade.Card.Id == card.Id)).ToList();
        if (cards.Count == 0) throw new RuleException(L10n.T("对方尚无可购买的公开卡牌，请稍后刷新。"));
        int price = checked(100 + 50 * session.Me.Purchases);
        string? selectedId = cardId;
        if (selectedId == null)
        {
            var previews = cards.Select(card => NativeCheckpoint.DecodeCard(card.Packet, run, RaceRuntime.LocalPlayer(run)!)).ToList();
            try
            {
                var selected = (await CardSelectCmd.FromSimpleGrid(new ThrowingPlayerChoiceContext(), previews, RaceRuntime.LocalPlayer(run)!,
                    new CardSelectorPrefs(new LocString(ModEntry.Id, "action.buy.title"), 1) { Cancelable = true })).FirstOrDefault();
                if (selected == null) return; selectedId = cards[previews.IndexOf(selected)].Id;
            }
            finally { foreach (var card in previews) card.RemoveFromState(); }
            int confirmation = await RaceUi.Choose(L10n.F($"支付 {price} 金币购买卡牌"), [L10n.T("确认购买\n金币支付给原主人；双方各自在下场开战时交接，不改变正在进行的战斗。"), L10n.T("取消")]);
            if (confirmation != 0) return;
        }
        await session.Submit(session.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(run)) });
        await session.Submit(session.New("market_buy", seller.Id, selectedId, RaceRuntime.LocalPlayer(run)!.Gold));
        await modifier.ApplyReceipts(new ThrowingPlayerChoiceContext(), session.Me.InBattle); RaceRuntime.Checkpoint(); RaceUi.Refresh();
    }
    internal static async Task BuyCardShared(MegaCrit.Sts2.Core.Entities.Players.Player owner, PlayerChoiceContext context, string operation)
    {
        var session = RaceSession.Current!;
        string ownerId = RaceRuntime.RacerFor(owner)!.Id;
        var sellers = session.Engine.State.Racers.Values.Where(racer => racer.Id != ownerId && !racer.Eliminated && !racer.Finished && !racer.ResetPending)
            .OrderBy(racer => racer.Id, StringComparer.Ordinal).ToArray();
        if (sellers.Length == 0) throw new RuleException("当前没有可交易的队友。");
        int who = await SharedChoice.Choose(owner, sellers.Length, () => RaceUi.Choose(L10n.T("强买强卖 · 选择玩家"), sellers.Select(racer => L10n.RacerName(racer)).ToArray()), context);
        if (who < 0) return;
        var seller = sellers[who];
        var cards = seller.MarketCards.Where(card => !session.Engine.State.Trades.Any(trade => trade.Seller == seller.Id && trade.Card.Id == card.Id)).ToArray();
        if (cards.Length == 0) throw new RuleException("该玩家当前没有可购买的卡牌。");
        var previews = cards.Select(card => NativeCheckpoint.DecodeCard(card.Packet, RaceRuntime.State!, owner)).ToList();
        try
        {
            var selected = (await CardSelectCmd.FromSimpleGrid(context, previews, owner,
                new CardSelectorPrefs(new LocString(ModEntry.Id, "action.buy.title"), 1) { Cancelable = true })).FirstOrDefault();
            if (selected == null) return;
            string cardId = cards[previews.IndexOf(selected)].Id;
            int price = checked(100 + 50 * RaceRuntime.RacerFor(owner)!.Purchases);
            int confirm = await SharedChoice.Choose(owner, 2, () => RaceUi.Choose(L10n.F($"支付 {price} 金币购买卡牌"),
                [L10n.T("确认购买 · 金币交给原主人，双方下场战斗交接卡牌"), L10n.T("取消")]), context);
            if (confirm != 0) return;
            await SharedChoice.Profile(owner, operation);
            await SharedChoice.Commit(owner, "market_buy", seller.Id, cardId, amount: owner.Gold, generation: int.Parse(operation.AsSpan("active:".Length), System.Globalization.CultureInfo.InvariantCulture));
            foreach (var modifier in RaceRuntime.State!.Modifiers.OfType<RaceModifier>()) await modifier.ApplyReceipts(context, CombatManager.Instance.IsInProgress, true);
        }
        finally { foreach (var preview in previews) preview.RemoveFromState(); }
    }
    internal static async Task ResetRun()
    {
        var session = RaceSession.Current!; var run = RaceRuntime.State!;
        if (session.Me.InBattle || CombatManager.Instance.IsInProgress || session.WaitingAtCheckpoint || session.PauseRequested) throw new RuleException(L10n.T("请先完成战斗，再使用R键。"));
        await session.Submit(session.New("reset_use"));
        if (session.Engine.State.Synchronous) { await SharedReset.Ensure(session); return; }
        RaceRuntime.ResetLocalTracking();
        // Keep the current run and its actual player objects; only regenerate act/map state.
        // No second Neow/opening relic or fresh Lizard Tail is granted.
        run.ExtraFields.StartedWithNeow = false;
        FiveActCampaign.ResetEncounters(run);
        await RunManager.Instance.EnterAct(0, false);
        await session.Submit(session.New("reset_ready"));
        await session.Submit(session.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(run)) });
        RaceRuntime.Checkpoint(); RaceUi.Refresh();
    }
}

[HarmonyPatch(typeof(CardModel), "get_IsUpgradable")]
internal static class UpgradeAvailabilityPatch
{ static void Postfix(CardModel __instance, ref bool __result) { if (__result && AdvancedEffects.UpgradeBlocked(__instance)) __result = false; } }
[HarmonyPatch(typeof(CardModel), nameof(CardModel.UpgradeInternal))]
internal static class UpgradeExecutionPatch
{ static bool Prefix(CardModel __instance) => !AdvancedEffects.UpgradeBlocked(__instance); }
[HarmonyPatch(typeof(CardModel), "get_IsTransformable")]
internal static class ReservedCardTransformPatch
{ static void Postfix(CardModel __instance, ref bool __result) { if (RaceRuntime.Live && RaceModifier.Active?.IsReserved(__instance) == true) __result = false; } }
