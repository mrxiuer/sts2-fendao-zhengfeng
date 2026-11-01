using FendaoZhengfeng.Core;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using Godot;

namespace FendaoZhengfeng.Mod;

internal static class TimelineEffects
{
    internal static async Task PlayFromOpponent(PlayerChoiceContext ctx, Player player, string? selectedOpponentId = null)
    {
        if (!CombatManager.Instance.IsInProgress || RaceSession.Current is not { } session || RaceRuntime.State is not { } run)
        {
            RaceUi.Notify(L10n.T("额外时间线只能在战斗中使用。"));
            return;
        }

        // Freeze before any picker. Shared combat has authoritative native decks on every peer;
        // independent races retain the opponent's last public packet after a disconnect.
        IReadOnlyList<TimelineOpponentSnapshot> snapshots = session.Engine.State.Synchronous
            ? run.Players.Where(other => other != player).OrderBy(other => other.NetId)
                .Select(other => new TimelineOpponentSnapshot(RaceRuntime.RacerFor(other)!.Id, RaceRuntime.RacerFor(other)!.Name,
                    other.Deck.Cards.Select((card, index) => new MarketCard(index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        NativeCheckpoint.EncodeCard(card), card.Title)).ToArray()))
                .Where(snapshot => snapshot.Cards.Count > 0).ToArray()
            : TimelineRules.OpponentSnapshots(session.Engine.State, RaceRuntime.RacerFor(player)?.Id ?? session.LocalId);
        if (snapshots.Count == 0)
        {
            RaceUi.Notify(L10n.T("对手的牌组尚未同步，暂时无法复制。"));
            return;
        }

        TimelineOpponentSnapshot? source;
        if (selectedOpponentId != null)
        {
            source = snapshots.FirstOrDefault(snapshot => snapshot.Id == selectedOpponentId);
            if (source == null) return;
        }
        else
        {
            int selectedIndex = await SharedChoice.Choose(player, snapshots.Count, () => RaceUi.Choose(
                L10n.T("额外时间线 · 选择一名对手"),
                snapshots.Select(snapshot =>
                    L10n.F($"{DisplayName(snapshot)}\n牌组：{snapshot.Cards.Count}张"))
                    .ToArray()), ctx);
            if (selectedIndex < 0 || selectedIndex >= snapshots.Count) return;
            source = snapshots[selectedIndex];
        }

        var owner = source ?? throw new InvalidOperationException(L10n.T("所选公开牌组快照已失效。"));
        var previews = new List<CardModel>(owner.Cards.Count);
        try
        {
            foreach (var stored in owner.Cards)
            {
                try
                {
                    // Decode only real NativeCheckpoint packets supplied by the public profile.
                    var preview = NativeCheckpoint.DecodeCard(stored.Packet, run, player);
                    if (preview is ExtraTimelineCard) { preview.RemoveFromState(); continue; }
                    previews.Add(preview);
                }
                catch (Exception error)
                {
                    GD.PushWarning(L10n.F($"[分道争锋] 额外时间线跳过无法解码的公开卡牌 {stored.Id}：{L10n.T(error.Message)}"));
                }
            }

            if (previews.Count == 0)
            {
                RaceUi.Notify(L10n.T("这名对手没有可复制的卡牌。"));
                return;
            }

            var selected = (await CardSelectCmd.FromSimpleGrid(
                ctx,
                previews,
                player,
                new CardSelectorPrefs(new LocString("cards", ModelDb.Card<ExtraTimelineCard>().Id.Entry + ".title"), 1)
                { Cancelable = true })).FirstOrDefault();
            if (selected == null) return;
            if (!previews.Contains(selected)) throw new InvalidOperationException(L10n.T("选择的卡牌不属于这份公开快照。"));

            var combat = player.Creature.CombatState;
            if (combat == null) return;
            var copy = combat.CloneCard(selected);
            copy.DeckVersion = null;
            if (copy is ExtraTimelineCard timeline) timeline.IsTimelineCopy = true;
            bool shouldCleanCopy = true;
            try
            {
                var added = await CardPileCmd.AddGeneratedCardToCombat(copy, PileType.Play, player);
                if (!added.success) return;

                // CardCmd.AutoPlay is the native free-play path: it spends no energy or stars and
                // preserves the game's X-cost, target, pile and other special-card behavior.
                // Let the native auto-play path choose a legal target from combat RNG.
                // Never expose a pending copy as a manually playable hand card.
                await CardCmd.AutoPlay(ctx, copy, null, AutoPlayType.Default);
                shouldCleanCopy = false;
            }
            finally
            {
                if (shouldCleanCopy) await RemoveCombatCopy(copy);
            }
        }
        finally
        {
            // Preview cards belong to the local run scope; the combat clone is independent.
            foreach (var preview in previews) preview.RemoveFromState();
        }
    }

    private static async Task RemoveCombatCopy(CardModel card)
    {
        if (card.Pile?.Type.IsCombatPile() == true)
            await CardPileCmd.RemoveFromCombat(card, skipVisuals: false);
        card.RemoveFromState();
    }

    private static string DisplayName(TimelineOpponentSnapshot snapshot) =>
        RaceSession.Current?.Engine.State.Racers.TryGetValue(snapshot.Id, out var racer) == true
            ? L10n.RacerName(racer) : string.IsNullOrWhiteSpace(snapshot.Name) ? snapshot.Id : L10n.T(snapshot.Name);
}

public sealed partial class RaceModifier
{
    private HashSet<string> appliedTimelineReceipts = [];

    [SavedProperty]
    public string AppliedTimelineReceiptIds
    {
        get => StateJson.Serialize(appliedTimelineReceipts);
        set => appliedTimelineReceipts = StateJson.Deserialize<HashSet<string>>(value);
    }

    internal async Task ApplyTimelineReceipt(PlayerChoiceContext ctx, Receipt receipt)
    {
        if (receipt.Kind != "timeline_card") return;
        if (string.IsNullOrWhiteSpace(receipt.Id) || string.IsNullOrWhiteSpace(receipt.Value))
            throw new InvalidOperationException(L10n.T("额外时间线收据缺少稳定的授予事件标识。"));
        if (appliedTimelineReceipts.Contains(receipt.Id)) return;

        // The card's saved marker closes the crash window between the native deck add and the
        // parent receipt handler's global MarkReceiptApplied call.
        if (Player.Deck.Cards.OfType<ExtraTimelineCard>().Any(card => card.GrantReceiptId == receipt.Id))
        {
            appliedTimelineReceipts.Add(receipt.Id);
            return;
        }

        var granted = RunState.CreateCard<ExtraTimelineCard>(Player);
        granted.GrantReceiptId = receipt.Id;
        bool previousSuppression = suppressPropagation;
        suppressPropagation = false; // A new permanent card follows the ordinary native deck hooks.
        try
        {
            await CardPileCmd.Add(granted, PileType.Deck, skipVisuals: true);
        }
        catch
        {
            if (!Player.Deck.Cards.Contains(granted)) granted.RemoveFromState();
            throw;
        }
        finally
        {
            suppressPropagation = previousSuppression;
        }

        if (!Player.Deck.Cards.Contains(granted))
        {
            granted.RemoveFromState();
            throw new InvalidOperationException(L10n.T("额外时间线卡牌没有加入永久牌组，收据将保留待重试。"));
        }
        appliedTimelineReceipts.Add(receipt.Id);
    }
}
