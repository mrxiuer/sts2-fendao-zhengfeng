using System.Runtime.CompilerServices;
using FendaoZhengfeng.Core;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rewards;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace FendaoZhengfeng.Mod;

public sealed record SavedRewardContent(string Key, string Packet, string[]? Cards = null,
    string? Relic = null, string? Potion = null, bool CanSkip = true, bool CanReroll = false,
    bool HasRerolled = false, int CardFlags = 0, SavedRewardContent[]? Children = null, bool ManualCards = false, int RelicTier = 0);
public sealed record SavedCombatRewards(string RoomKey, SavedRewardContent[] Rewards, bool DisallowSkipping);

internal static class RewardRecovery
{
    internal static SavedCombatRewards? Pending { get; set; }
    private static readonly ConditionalWeakTable<RewardsSet, object> restored = new();
    internal static bool IsRestored(RewardsSet set) => restored.TryGetValue(set, out _);
    private static string Pack(IPacketSerializable value)
    { var writer = new PacketWriter(); value.Serialize(writer); return Convert.ToBase64String(writer.Buffer, 0, writer.BytePosition); }
    private static T Unpack<T>(string packet) where T : IPacketSerializable, new()
    { var reader = new PacketReader(); reader.Reset(Convert.FromBase64String(packet)); var value = new T(); value.Deserialize(reader); return value; }
    internal static SavedCombatRewards? Capture(CombatRoom room)
    {
        if (RaceRuntime.CombatRewards is not { } set || RaceRuntime.RewardRoom != room) return null;
        return new(RaceRuntime.RoomKey, set.Rewards.Select(CaptureReward).OfType<SavedRewardContent>().ToArray(), set.DisallowSkipping);
    }
    private static SavedRewardContent? CaptureReward(Reward reward)
    {
        var session = RaceSession.Current!; string key = RaceRuntime.RewardKey(reward);
        if (reward.SuccessfullySelected || session.Engine.State.ConsumedRewards.Contains(session.LocalId + ":" + key)
            || reward is RaceActionReward action && !action.Available) return null;
        if (reward is LinkedRewardSet linked)
        {
            var children = linked.Rewards.Select(CaptureReward).OfType<SavedRewardContent>().ToArray();
            return children.Length == 0 ? null : new(key, "", Children: children);
        }
        if (reward is CardReward cards)
        {
            var options = (CardCreationOptions)AccessTools.PropertyGetter(typeof(CardReward), "RerollOptions").Invoke(cards, null)!;
            if (options.CardPoolFilter != null) throw new InvalidOperationException("此卡牌奖励使用不可保存的自定义筛选器。");
            var save = new SerializableReward { RewardType = RewardType.Card, Source = options.Source, RarityOdds = options.RarityOdds,
                CardPoolIds = options.CardPools.Select(pool => pool.Id).ToList(), OptionCount = cards.Cards.Count() };
            return new(key, Pack(save), cards.Cards.Select(NativeCheckpoint.EncodeCard).ToArray(), CanSkip: cards.CanSkip,
                CanReroll: cards.CanReroll, HasRerolled: (bool)AccessTools.Field(typeof(CardReward), "_hasBeenRerolled").GetValue(cards)!, CardFlags: (int)options.Flags,
                ManualCards: (bool)AccessTools.Field(typeof(CardReward), "_cardsWereManuallySet").GetValue(cards)!);
        }
        return new(key, Pack(reward.ToSerializable()),
            Relic: reward is RelicReward relic ? Pack(relic.Relic!.ToSerializable()) : null,
            Potion: reward is PotionReward potion ? Pack(potion.Potion!.ToSerializable(0)) : null,
            RelicTier: reward is RelicReward tiered ? (int)tiered.Rarity : 0);
    }
    internal static RewardsSet Restore(SavedCombatRewards save, Player player, CombatRoom room)
    {
        if (save.RoomKey != RaceRuntime.RoomKey) throw new InvalidDataException("剩余奖励与恢复房间不匹配。");
        var set = new RewardsSet(player).EmptyForRoom(room);
        if (save.DisallowSkipping) set.WithSkippingDisallowed();
        set.Rewards.AddRange(save.Rewards.Select(entry => RestoreReward(entry, player)));
        // These are the actual populated choices, not recipes to roll again.
        AccessTools.Field(typeof(RewardsSet), "_isGenerated").SetValue(set, true);
        restored.Add(set, new()); return set;
    }
    private static Reward RestoreReward(SavedRewardContent entry, Player player)
    {
        Reward reward;
        if (entry.Children != null) reward = new LinkedRewardSet(entry.Children.Select(child => RestoreReward(child, player)).ToList(), player);
        else if (entry.Cards != null)
        {
            var save = Unpack<SerializableReward>(entry.Packet);
            var options = new CardCreationOptions(save.CardPoolIds.Select(ModelDb.GetById<CardPoolModel>), save.Source, save.RarityOdds)
                .WithFlags((CardCreationFlags)entry.CardFlags);
            var choices = entry.Cards.Select(packet => NativeCheckpoint.DecodeCard(packet, (RunState)player.RunState, player)).ToArray();
            var cards = entry.ManualCards
                ? new CardReward(choices, save.Source, player, options) { CanSkip = entry.CanSkip, CanReroll = entry.CanReroll }
                : new CardReward(options, choices.Length, player) { CanSkip = entry.CanSkip, CanReroll = entry.CanReroll };
            if (!entry.ManualCards)
                ((List<CardCreationResult>)AccessTools.Field(typeof(CardReward), "_cards").GetValue(cards)!).AddRange(choices.Select(card => new CardCreationResult(card)));
            AccessTools.Field(typeof(CardReward), "_hasBeenRerolled").SetValue(cards, entry.HasRerolled); reward = cards;
        }
        else if (entry.Relic != null)
        {
            var model = RelicModel.FromSerializable(Unpack<SerializableRelic>(entry.Relic));
            var relic = new RelicReward((RelicRarity)entry.RelicTier, player);
            AccessTools.Field(typeof(RelicReward), "_relic").SetValue(relic, model);
            if (Unpack<SerializableReward>(entry.Packet).PredeterminedModelId != ModelId.none)
                AccessTools.Field(typeof(RelicReward), "_predeterminedRelic").SetValue(relic, model);
            reward = relic;
        }
        else if (entry.Potion != null) reward = new PotionReward(PotionModel.FromSerializable(Unpack<SerializablePotion>(entry.Potion)), player);
        else reward = Reward.FromSerializable(Unpack<SerializableReward>(entry.Packet), player);
        RaceRuntime.BindRewardKey(reward, entry.Key); return reward;
    }
}

[HarmonyPatch(typeof(RewardsCmd), nameof(RewardsCmd.GenerateForRoomEnd))]
internal static class RestoreRoomRewardsPatch
{
    static bool Prefix(Player player, AbstractRoom room, ref Task<RewardsSet> __result)
    {
        if (!RaceRuntime.Live || RewardRecovery.Pending is not { } pending || room is not CombatRoom { IsPreFinished: true } combat) return true;
        var set = RewardRecovery.Restore(pending, player, combat); RewardRecovery.Pending = null;
        __result = Task.FromResult(set); return false;
    }
}
