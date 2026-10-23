using System.Security.Cryptography;
using System.Text;

namespace FendaoZhengfeng.Core;

/// <summary>
/// Durable, per-reward accounting for the Encore Rewards effect.
/// The mod adapter calls these methods from the host-ordered Engine reducer;
/// native reward contents must only be changed after the corresponding
/// command has committed.
/// </summary>
public static class RewardRerollRules
{
    public const string EffectId = "encore_rewards";
    public const string NativeCommand = "reward_reroll";
    public const string OfferCommand = "offer_reroll";
    public const int BaseRefreshesPerReward = 1;
    public const int BonusPerRank = 1;
    public const int MaxRefreshesPerReward = 3;

    public readonly record struct Usage(int Used, int Limit)
    {
        public int Remaining => Math.Max(0, Limit - Used);
        public bool CanRefresh => Remaining > 0;
    }

    /// <summary>Base copy allows one refresh; each reinforcement adds one, capped at three per reward.</summary>
    public static int Limit(Racer player)
    {
        if (!player.Effects.TryGetValue(EffectId, out var holding)) return 0;
        int maxBonusRanks = (MaxRefreshesPerReward - BaseRefreshesPerReward) / BonusPerRank;
        return BaseRefreshesPerReward + Math.Clamp(holding.Rank, 0, maxBonusRanks) * BonusPerRank;
    }

    public static Usage NativeUsage(Racer player, string rewardKey)
        => new(player.History.GetValueOrDefault(NativeCounterKey(rewardKey)), Limit(player));

    public static Usage OfferUsage(Racer player, string offerId)
        => new(player.History.GetValueOrDefault(OfferCounterKey(offerId)), Limit(player));

    public static bool CanRefreshNative(Match match, Racer player, string rewardKey)
    {
        if (Limit(player) <= 0 || !ValidRewardKey(rewardKey)) return false;
        if (match.ConsumedRewards.Contains(ConsumedToken(player, rewardKey))) return false;
        return NativeUsage(player, rewardKey).CanRefresh;
    }

    /// <summary>
    /// Commit one native reward refresh. Call from the Engine reducer before
    /// the caller mutates the in-memory native Reward object.
    /// </summary>
    public static void CommitNative(Match match, Racer player, string rewardKey)
    {
        if (!ValidRewardKey(rewardKey)) throw new RuleException("奖励标识无效，无法刷新。");
        if (match.ConsumedRewards.Contains(ConsumedToken(player, rewardKey))) throw new RuleException("该奖励已领取，不能刷新。");
        int limit = Limit(player);
        if (limit <= 0) throw new RuleException("没有可用的奖励刷新次数。");
        var usage = NativeUsage(player, rewardKey);
        if (!usage.CanRefresh) throw new RuleException("这份奖励的刷新次数已用完。");
        player.History[NativeCounterKey(rewardKey)] = usage.Used + 1;
    }

    /// <summary>
    /// Replace the candidate list of an existing, unresolved mode Offer and
    /// consume its per-offer refresh atomically. The generated list is stored
    /// in Match.Offers, so closing and reopening the chooser cannot fish again.
    /// </summary>
    public static void RefreshOffer(Match match, Racer player, string rewardKey, Route route)
    {
        if (!ValidRewardKey(rewardKey)) throw new RuleException("候选奖励标识无效，无法刷新。");
        int limit = Limit(player);
        if (limit <= 0) throw new RuleException("没有可用的候选刷新次数。");

        string offerId = OfferId(player.Id, rewardKey, route);
        if (!match.Offers.TryGetValue(offerId, out var offer) || offer.Player != player.Id
            || offer.RewardKey != rewardKey || offer.Route != route || offer.Resolved)
            throw new RuleException("这份候选奖励已处理或不存在。");
        if (match.ConsumedRewards.Contains(ConsumedToken(player, rewardKey)))
            throw new RuleException("该候选奖励已领取，不能刷新。");
        if (route == Route.FirstKill)
        {
            if (!rewardKey.EndsWith(":first", StringComparison.Ordinal) || !player.BossPrizes.Contains(rewardKey))
                throw new RuleException("首杀候选奖励已失效。");
        }
        else if (!player.WonBattles.Contains(rewardKey.Split('|', 2)[0]))
        {
            throw new RuleException("候选奖励缺少对应的胜利记录。");
        }

        var usage = OfferUsage(player, offerId);
        if (!usage.CanRefresh) throw new RuleException("这份候选奖励的刷新次数已用完。");

        int nextGeneration = usage.Used + 1;
        // Keep Offer.Id and RewardKey stable. Only candidate generation is salted.
        // Candidates.Generate is deterministic for this salt, and the resulting
        // candidates are persisted in Offer before the command can be accepted.
        var candidates = Candidates.Generate(match, player,
            GenerationKey(rewardKey, route, nextGeneration), route, offer.Boss);
        offer.Candidates = candidates;
        player.History[OfferCounterKey(offerId)] = nextGeneration;
    }

    public static string OfferId(string playerId, string rewardKey, Route route)
        => playerId + ":" + rewardKey + ":" + route;

    private static bool ValidRewardKey(string key) => key.Length is > 0 and <= 256;
    private static string ConsumedToken(Racer player, string rewardKey) => player.Id + ":" + rewardKey;
    private static string NativeCounterKey(string key) => "encore_rewards:native:" + HashKey(key);
    private static string OfferCounterKey(string key) => "encore_rewards:offer:" + HashKey(key);
    private static string GenerationKey(string key, Route route, int generation)
        => key + "|encore:" + route + ":" + generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static string HashKey(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
}
