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
            RaceUi.Notify("额外时间线只能在战斗中使用。");
            return;
        }

        // Freeze the reducer's real public packet list before opening any asynchronous picker.
        // In particular, do not query live peer connectivity: a disconnected racer's last snapshot is valid.
        var snapshots = TimelineRules.OpponentSnapshots(session.Engine.State, session.LocalId);
        if (snapshots.Count == 0)
        {
            RaceUi.Notify("对手的牌组尚未同步，暂时无法复制。");
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
            int selectedIndex = await RaceUi.Choose(
                "额外时间线 · 选择一名对手",
                snapshots.Select(snapshot =>
                    $"{DisplayName(snapshot)}\n牌组：{snapshot.Cards.Count}张")
                    .ToArray());
            if (selectedIndex < 0 || selectedIndex >= snapshots.Count) return;
            source = snapshots[selectedIndex];
        }

        var owner = source ?? throw new InvalidOperationException("所选公开牌组快照已失效。");
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
                    GD.PushWarning($"[分道争锋] 额外时间线跳过无法解码的公开卡牌 {stored.Id}：{error.Message}");
                }
            }

            if (previews.Count == 0)
            {
                RaceUi.Notify("这名对手没有可复制的卡牌。");
                return;
            }

            var selected = (await CardSelectCmd.FromSimpleGrid(
                ctx,
                previews,
                player,
                new CardSelectorPrefs(new LocString("cards", ModelDb.Card<ExtraTimelineCard>().Id.Entry + ".title"), 1)
                { Cancelable = true })).FirstOrDefault();
            if (selected == null) return;
            if (!previews.Contains(selected)) throw new InvalidOperationException("选择的卡牌不属于这份公开快照。");

            var combat = player.Creature.CombatState;
            if (combat == null) return;
            var copy = combat.CloneCard(selected);
            copy.DeckVersion = null;
            if (copy is ExtraTimelineCard timeline) timeline.IsTimelineCopy = true;
            bool shouldCleanCopy = true;
            try
            {
                var added = await CardPileCmd.AddGeneratedCardToCombat(copy, PileType.Hand, player);
                if (!added.success) return;

                var targetSelection = await SelectTarget(copy, player);
                if (!targetSelection.Continue) return;

                // CardCmd.AutoPlay is the native free-play path: it spends no energy or stars and
                // preserves the game's X-cost, target, pile and other special-card behavior.
                await CardCmd.AutoPlay(ctx, copy, targetSelection.Target, AutoPlayType.Default);
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

    private static async Task<(bool Continue, Creature? Target)> SelectTarget(CardModel card, Player player)
    {
        if (card.TargetType is not (TargetType.AnyEnemy or TargetType.AnyAlly)) return (true, null);

        var combat = card.CombatState ?? player.Creature.CombatState;
        if (combat == null) return (false, null);
        bool hasTarget = card.TargetType == TargetType.AnyEnemy
            ? combat.HittableEnemies.Any(card.IsValidTarget)
            : combat.PlayerCreatures.Any(card.IsValidTarget);

        // Match AutoPlay's native no-target result: no choice is needed, and it will move the card
        // to its result pile without playing it. This avoids presenting a dead target prompt.
        if (!hasTarget) return (true, null);

        var targetManager = NTargetManager.Instance;
        if (targetManager.IsInSelection) return (false, null);
        var hand = NCombatRoom.Instance?.Ui.Hand;
        if (hand == null) return (false, null);

        var sourceCard = hand.GetCard(card);
        if (sourceCard != null)
            targetManager.StartTargeting(card.TargetType, sourceCard, TargetMode.ClickMouseToTarget, () => CombatManager.Instance.IsOverOrEnding, null);
        else
            targetManager.StartTargeting(card.TargetType, hand.GlobalPosition, TargetMode.ClickMouseToTarget, () => CombatManager.Instance.IsOverOrEnding, null);

        Node? selectedNode = await targetManager.SelectionFinished();
        if (selectedNode == null) return (false, null);

        Creature? target = selectedNode switch
        {
            NCreature nCreature => nCreature.Entity,
            NMultiplayerPlayerState multiplayerPlayer => multiplayerPlayer.Player.Creature,
            _ => null
        };
        if (target == null || !card.IsValidTarget(target)) return (false, null);
        return (true, target);
    }

    private static async Task RemoveCombatCopy(CardModel card)
    {
        if (card.Pile?.Type.IsCombatPile() == true)
            await CardPileCmd.RemoveFromCombat(card, skipVisuals: true);
        card.RemoveFromState();
    }

    private static string DisplayName(TimelineOpponentSnapshot snapshot) =>
        string.IsNullOrWhiteSpace(snapshot.Name) ? snapshot.Id : snapshot.Name;
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
            throw new InvalidOperationException("额外时间线收据缺少稳定的授予事件标识。");
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
            throw new InvalidOperationException("额外时间线卡牌没有加入永久牌组，收据将保留待重试。");
        }
        appliedTimelineReceipts.Add(receipt.Id);
    }
}
