using MegaCrit.Sts2.Core.Models;
using FendaoZhengfeng.Core;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace FendaoZhengfeng.Mod;

/// <summary>Focused checks for persistent offer generation and native reward adapters.</summary>
internal static class NativeRerollVerification
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("验证失败：" + name);
        Godot.GD.Print("[FDZF REROLL PASS] " + name);
    }

    internal static void VerifyRules()
    {
        var player = new Racer { Id = "1" };
        Check(RewardRerollRules.Limit(player) == 0, "effect absent means no refreshes");
        player.Effects[RewardRerollRules.EffectId] = new() { Id = RewardRerollRules.EffectId };
        Check(RewardRerollRules.Limit(player) == 1, "base effect grants one refresh per reward");
        player.Effects[RewardRerollRules.EffectId].Rank = 2;
        Check(RewardRerollRules.Limit(player) == 3, "each reinforcement adds one, capped at three per reward");
        player.Effects[RewardRerollRules.EffectId].Rank = 20;
        Check(RewardRerollRules.Limit(player) == RewardRerollRules.MaxRefreshesPerReward, "reinforcement cap prevents unlimited rerolls");

        player.Effects[RewardRerollRules.EffectId].Rank = 0;
        player.WonBattles.Add("battle-1");
        var match = new Match
        {
            Id = "reroll-verification",
            Seed = "stable-reroll-seed",
            EnabledEffects = Catalog.All.Select(effect => effect.Id).ToHashSet(),
            Racers = new() { [player.Id] = player }
        };
        string rewardKey = "battle-1|mode-offer";
        string offerId = RewardRerollRules.OfferId(player.Id, rewardKey, Route.Mixed);
        var offer = new Offer
        {
            Id = offerId,
            Player = player.Id,
            RewardKey = rewardKey,
            Route = Route.Mixed,
            Boss = false,
            Candidates = Candidates.Generate(match, player, rewardKey, Route.Mixed, false)
        };
        match.Offers.Add(offerId, offer);
        var firstGeneration = Candidates.Generate(match, player, rewardKey + "|encore:Mixed:1", Route.Mixed, false);
        RewardRerollRules.RefreshOffer(match, player, rewardKey, Route.Mixed);
        Check(offer.Candidates.SequenceEqual(firstGeneration) && RewardRerollRules.OfferUsage(player, offerId).Used == 1,
            "mode reroll commits a deterministic candidate generation and its per-offer counter together");

        var beforeRestore = offer.Candidates.ToArray();
        var reopened = StateJson.Deserialize<Match>(StateJson.Serialize(match));
        var reopenedOffer = reopened.Offers[offerId];
        Check(reopenedOffer.Candidates.SequenceEqual(beforeRestore)
            && RewardRerollRules.OfferUsage(reopened.Racers[player.Id], offerId).Used == 1,
            "closing or restoring a choice preserves both candidates and consumed refresh count");

        player.Effects[RewardRerollRules.EffectId].Rank = 2;
        var secondGeneration = Candidates.Generate(match, player, rewardKey + "|encore:Mixed:2", Route.Mixed, false);
        RewardRerollRules.RefreshOffer(match, player, rewardKey, Route.Mixed);
        Check(offer.Candidates.SequenceEqual(secondGeneration), "next refresh advances to its own generation salt");
        RewardRerollRules.RefreshOffer(match, player, rewardKey, Route.Mixed);
        Check(RewardRerollRules.OfferUsage(player, offerId).Used == 3, "reinforced effect permits up to three refreshes of this offer");
        bool fourthRejected = false;
        try { RewardRerollRules.RefreshOffer(match, player, rewardKey, Route.Mixed); }
        catch (RuleException) { fourthRejected = true; }
        Check(fourthRejected, "offer cannot exceed its per-reward limit");

        string nativeKey = "battle-1|native-reward";
        RewardRerollRules.CommitNative(match, player, nativeKey);
        Check(RewardRerollRules.NativeUsage(player, nativeKey).Used == 1,
            "native refresh count is persisted under its reward identity");
        const string otherNativeKey = "battle-1|other-native-reward";
        RewardRerollRules.CommitNative(match, player, otherNativeKey);
        Check(RewardRerollRules.NativeUsage(player, otherNativeKey).Used == 1
            && RewardRerollRules.NativeUsage(player, nativeKey).Used == 1,
            "separate native rewards have independent refresh counters");
        match.ConsumedRewards.Add(player.Id + ":" + nativeKey);
        Check(!RewardRerollRules.CanRefreshNative(match, player, nativeKey),
            "claimed reward cannot be refreshed");
        Check(RewardRerollRules.CanRefreshNative(match, player, otherNativeKey),
            "claiming one reward does not consume another reward's refresh");
    }

    /// <summary>Runs against a live mock player's native reward RNG and model pools.</summary>
    internal static void VerifyNativeGenerators(Player player)
    {
        foreach (var bounds in new[] { (20, 45), (25, 25), (0, 0) })
        {
            var gold = new GoldReward(bounds.Item1, bounds.Item2, player);
            gold.Populate();
            int amount = gold.Amount;
            string key = RaceRuntime.RewardKey(gold);
            bool rejected = false;
            try { RewardRerollEffect.ApplyNativeReroll(gold); }
            catch (RuleException) { rejected = true; }
            Check(!RewardRerollEffect.IsSupported(gold) && rejected && gold.Amount == amount
                && RaceRuntime.RewardKey(gold) == key,
                "gold refresh is rejected without changing amount or identity: " + bounds);
        }

        var potion = new PotionReward(player);
        potion.Populate();
        string potionKey = RaceRuntime.RewardKey(potion);
        RewardRerollEffect.ApplyNativeReroll(potion);
        Check(potion.Potion != null && RaceRuntime.RewardKey(potion) == potionKey,
            "potion reroll uses the native out-of-combat pool on the same reward");

        var options = CardCreationOptions.ForRoom(player, RoomType.Monster).WithFlags(CardCreationFlags.IsFromCombat);
        var cards = new CardReward(options, 3, player);
        cards.Populate();
        string cardKey = RaceRuntime.RewardKey(cards);
        RewardRerollEffect.ApplyNativeReroll(cards);
        Check(cards.Cards.Count() == 3 && RaceRuntime.RewardKey(cards) == cardKey,
            "card reroll invokes the native generator and keeps its reward identity");

        var filteredOptions = CardCreationOptions.ForRoom(player, RoomType.Monster)
            .WithFilter(card => card.Rarity != CardRarity.Rare)
            .WithFlags(CardCreationFlags.IsFromCombat);
        var filtered = new CardReward(filteredOptions, 3, player);
        filtered.Populate();
        var allowedIds = RewardRerollPersistence.FreezeCardFilter(filtered).ToHashSet(StringComparer.Ordinal);
        RewardRerollEffect.ApplyNativeReroll(filtered);
        Check(filtered.Cards.All(card => allowedIds.Contains(card.Id.ToString())),
            "filtered card reroll freezes and honors the original allowed card IDs");
        var filteredSave = RewardRerollPersistence.CaptureNativeReward(filtered)!;
        var restoreFiltered = AccessTools.Method(typeof(RewardRecovery), "RestoreReward")!;
        var filteredAfterLoad = (CardReward)restoreFiltered.Invoke(null, [filteredSave, player])!;
        RewardRerollEffect.ApplyNativeReroll(filteredAfterLoad);
        Check(filteredAfterLoad.Cards.All(card => allowedIds.Contains(card.Id.ToString())),
            "filtered card reroll source survives recovery and remains restricted to the captured IDs");

        var relic = new RelicReward(player);
        relic.Populate();
        string relicKey = RaceRuntime.RewardKey(relic);
        RewardRerollEffect.ApplyNativeReroll(relic);
        Check(relic.Relic != null && relic.Rarity == RelicRarity.None && RaceRuntime.RewardKey(relic) == relicKey,
            "relic reroll keeps native rarity generation and run grab-bag semantics");

        // The native constructor asserts mutability. Clone the canonical model
        // once; passing relic.Relic would try to mutate an already-mutable model.
        var predeterminedModel = ModelDb.GetById<RelicModel>(relic.Relic!.Id).ToMutable();
        var predetermined = new RelicReward(predeterminedModel, player);
        var predeterminedRarity = predetermined.Relic!.Rarity;
        RewardRerollEffect.ApplyNativeReroll(predetermined);
        Check(predetermined.Relic != null && predetermined.Relic.Rarity == predeterminedRarity
            && AccessTools.Field(typeof(RelicReward), "_predeterminedRelic")!.GetValue(predetermined) != null,
            "predetermined relic rerolls from the native run pool at the same rarity and remains serializable as fixed content");

        // CardReward has already populated mutable models; SpecialCardReward
        // takes ownership of this generated card directly.
        var special = new SpecialCardReward(cards.Cards.First(), player);
        var specialRarity = cards.Cards.First().Rarity;
        RewardRerollEffect.ApplyNativeReroll(special);
        Check(AccessTools.Field(typeof(SpecialCardReward), "_card")!.GetValue(special) is CardModel replacementCard
            && replacementCard.Rarity == specialRarity,
            "special fixed-card reward rerolls within its native card pool and original rarity");

        VerifyReceiptRecovery(player);
    }

    private static void VerifyReceiptRecovery(Player player)
    {
        var session = RaceSession.Current ?? throw new InvalidOperationException("原生奖励验证缺少比赛会话。");
        if (RaceRuntime.State?.CurrentRoom is not CombatRoom room)
            throw new InvalidOperationException("原生奖励恢复验证需要当前战斗房间。");

        string originalMatch = StateJson.Serialize(session.Engine.State);
        string[] originalApplied = session.AppliedReceiptIds();
        var appliedField = AccessTools.Field(typeof(RaceSession), "appliedReceipts")!;
        var engineSetter = AccessTools.PropertySetter(typeof(RaceSession), nameof(RaceSession.Engine))!;
        string key = RaceRuntime.RoomKey + "|reroll-recovery:" + Guid.NewGuid().ToString("N");
        string roomKey = RaceRuntime.RoomKey;
        var nativeWriter = new MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketWriter();
        RunManager.Instance.ToSave(null).Serialize(nativeWriter);
        string nativePacket = Convert.ToBase64String(nativeWriter.Buffer, 0, nativeWriter.BytePosition);
        try
        {
            var effectMatch = StateJson.Deserialize<Match>(originalMatch);
            effectMatch.Racers[session.LocalId].Effects[RewardRerollRules.EffectId] = new() { Id = RewardRerollRules.EffectId };
            engineSetter.Invoke(session, [new FendaoZhengfeng.Core.Engine(effectMatch)]);
            ((HashSet<string>)appliedField.GetValue(session)!).Clear();

            var originalPotion = new PotionReward(player);
            originalPotion.Populate();
            RaceRuntime.BindRewardKey(originalPotion, key);
            var preApplyReward = (SavedRewardContent?)AccessTools.Method(typeof(RewardRecovery), "CaptureReward")!.Invoke(null, [originalPotion])
                ?? throw new InvalidOperationException("无法捕获刷新前的原生药水奖励。");

            var command = session.New(RewardRerollRules.NativeCommand, key);
            session.Submit(command).GetAwaiter().GetResult();
            var receipt = RewardRerollTransaction.FindCommittedReceipt(session, command.Id + ":reroll", key)
                ?? throw new InvalidOperationException("原生刷新命令没有提交对应收据。");
            Check(receipt.Amount == 1 && !session.ReceiptApplied(receipt.Id),
                "native command commits usage and a pending receipt before changing the reward");

            var beforeApplyBundle = new NativeCheckpoint.Bundle("0.5.1", Catalog.RulesVersion,
                session.Engine.State.Revision, StateJson.Serialize(session.Engine.State), nativePacket,
                session.AppliedReceiptIds(), roomKey, new SavedCombatRewards(roomKey, [preApplyReward], false));
            beforeApplyBundle = StateJson.Deserialize<NativeCheckpoint.Bundle>(StateJson.Serialize(beforeApplyBundle));
            Check(NativeCheckpoint.Restore(beforeApplyBundle).Run.Players.Single().Character.Id == player.Character.Id,
                "pre-apply native reward snapshot survives full checkpoint serialization and restore");
            engineSetter.Invoke(session, [new FendaoZhengfeng.Core.Engine(StateJson.Deserialize<Match>(beforeApplyBundle.MatchJson))]);
            ((HashSet<string>)appliedField.GetValue(session)!).Clear();
            ((HashSet<string>)appliedField.GetValue(session)!).UnionWith(beforeApplyBundle.AppliedReceipts ?? []);
            var restoredBeforeApply = RewardRecovery.Restore(beforeApplyBundle.Rewards!, player, room);
            var replayedPotion = (PotionReward)restoredBeforeApply.Rewards.Single();
            RewardRerollTransaction.ReplayPending(restoredBeforeApply, scheduleAcknowledgements: false);
            Check(replayedPotion.Potion != null && session.ReceiptApplied(receipt.Id)
                && session.Me.Receipts.Any(existing => existing.Id == receipt.Id),
                "pre-apply native snapshot restores the old reward and replays its committed receipt once");

            var postApplyReward = (SavedRewardContent?)AccessTools.Method(typeof(RewardRecovery), "CaptureReward")!.Invoke(null, [replayedPotion])
                ?? throw new InvalidOperationException("无法捕获刷新后的原生药水奖励。");
            var afterApplyBundle = new NativeCheckpoint.Bundle("0.5.1", Catalog.RulesVersion,
                session.Engine.State.Revision, StateJson.Serialize(session.Engine.State), nativePacket,
                session.AppliedReceiptIds(), roomKey, new SavedCombatRewards(roomKey, [postApplyReward], false));
            afterApplyBundle = StateJson.Deserialize<NativeCheckpoint.Bundle>(StateJson.Serialize(afterApplyBundle));
            engineSetter.Invoke(session, [new FendaoZhengfeng.Core.Engine(StateJson.Deserialize<Match>(afterApplyBundle.MatchJson))]);
            ((HashSet<string>)appliedField.GetValue(session)!).Clear();
            ((HashSet<string>)appliedField.GetValue(session)!).UnionWith(afterApplyBundle.AppliedReceipts ?? []);
            var restoredAfterApply = RewardRecovery.Restore(afterApplyBundle.Rewards!, player, room);
            var postReplayPotion = (PotionReward)restoredAfterApply.Rewards.Single();
            var postApplyPotion = postReplayPotion.Potion;
            var usage = RewardRerollRules.NativeUsage(session.Me, key).Used;
            RewardRerollTransaction.ReplayPending(restoredAfterApply, scheduleAcknowledgements: false);
            RewardRerollTransaction.ReplayPending(restoredAfterApply, scheduleAcknowledgements: false);
            Check(ReferenceEquals(postReplayPotion.Potion, postApplyPotion)
                && RewardRerollRules.NativeUsage(session.Me, key).Used == usage
                && session.ReceiptApplied(receipt.Id)
                && session.Me.Receipts.Any(existing => existing.Id == receipt.Id),
                "post-apply pre-ack snapshot and duplicate replay preserve the generated reward without rerolling");

            string consumedToken = session.LocalId + ":" + key;
            var emptyRestoredSet = RewardRecovery.Restore(new SavedCombatRewards(roomKey, [], false), player, room);
            session.Engine.State.ConsumedRewards.Add(consumedToken);
            ((HashSet<string>)appliedField.GetValue(session)!).Remove(receipt.Id);
            var unmarkedConsumedAcks = RewardRerollTransaction.ReplayPending(emptyRestoredSet, scheduleAcknowledgements: false);
            Check(!unmarkedConsumedAcks.Contains(receipt.Id)
                && !session.ReceiptApplied(receipt.Id)
                && session.Me.Receipts.Any(existing => existing.Id == receipt.Id),
                "consumed reward without an applied marker remains pending and is never rerolled or acknowledged");

            session.MarkReceiptApplied(receipt.Id);
            var appliedConsumedAcks = RewardRerollTransaction.ReplayPending(emptyRestoredSet, scheduleAcknowledgements: false);
            Check(appliedConsumedAcks.Contains(receipt.Id),
                "applied receipt for a consumed reward is queued for acknowledgement without a visible reward");
            RewardRerollTransaction.AcknowledgeAsync(session, receipt, null).GetAwaiter().GetResult();
            Check(session.Me.Receipts.All(existing => existing.Id != receipt.Id),
                "consumed applied reroll receipt can be acknowledged after the durable marker without its old reward row");
        }
        finally
        {
            engineSetter.Invoke(session, [new FendaoZhengfeng.Core.Engine(StateJson.Deserialize<Match>(originalMatch))]);
            ((HashSet<string>)appliedField.GetValue(session)!).Clear();
            ((HashSet<string>)appliedField.GetValue(session)!).UnionWith(originalApplied);
        }
    }
}
