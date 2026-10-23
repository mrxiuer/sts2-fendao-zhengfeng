using FendaoZhengfeng.Core;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Rewards;

namespace FendaoZhengfeng.Mod;

/// <summary>
/// Closes the gap between the synchronized refresh command and mutation of
/// the native reward. A pending receipt is the durable intent; AppliedReceipts
/// is the durable proof that its native side effect is already in the save.
/// </summary>
internal static class RewardRerollTransaction
{
    private static readonly object gate = new();
    private static readonly HashSet<string> inFlight = [];
    private static readonly HashSet<string> loggedConsumedInconsistencies = [];

    private static string Token(RaceSession session, string rewardKey)
        => session.Engine.State.Id + ":" + session.LocalId + ":" + rewardKey;

    private sealed class Lease(string token) : IDisposable
    {
        private string? leaseToken = token;
        public void Dispose()
        {
            string? current = Interlocked.Exchange(ref leaseToken, null);
            if (current == null) return;
            lock (gate) inFlight.Remove(current);
        }
    }

    internal static IDisposable Begin(RaceSession session, string rewardKey)
    {
        string token = Token(session, rewardKey);
        lock (gate)
        {
            if (!inFlight.Add(token)) throw new RuleException(L10n.T("这份奖励正在刷新，请稍候。"));
        }
        return new Lease(token);
    }

    internal static bool HasInFlight(RaceSession session)
    {
        string prefix = session.Engine.State.Id + ":" + session.LocalId + ":";
        lock (gate) return inFlight.Any(token => token.StartsWith(prefix, StringComparison.Ordinal));
    }

    internal static bool IsInFlight(RaceSession session, string rewardKey)
    {
        lock (gate) return inFlight.Contains(Token(session, rewardKey));
    }

    internal static Receipt? FindCommittedReceipt(RaceSession session, string id, string rewardKey)
        => session.Me.Receipts.FirstOrDefault(receipt => receipt.Id == id
            && receipt.Kind == "reroll_native" && receipt.Value == rewardKey);

    /// <summary>Apply and mark in one synchronous call; never insert an await here.</summary>
    internal static void ApplyAndMark(RaceSession session, Reward reward, Receipt receipt)
    {
        string key = RaceRuntime.RewardKey(reward);
        if (receipt.Kind != "reroll_native" || receipt.Value != key)
            throw new InvalidDataException(L10n.T("原生奖励刷新收据与奖励身份不匹配。"));
        if (session.ReceiptApplied(receipt.Id)) return;
        if (reward.SuccessfullySelected || session.Engine.State.ConsumedRewards.Contains(session.LocalId + ":" + key))
            throw new RuleException(L10n.T("奖励已领取，不能应用尚未完成的刷新。"));

        RewardRerollEffect.ApplyNativeReroll(reward);
        session.MarkReceiptApplied(receipt.Id);
    }

    /// <summary>
    /// Replays committed but unmarked native refreshes after the set is fully
    /// populated and before NRewardButton rows exist. Receipts for other sets
    /// remain pending and are deliberately neither acknowledged nor consumed.
    /// </summary>
    internal static IReadOnlyList<string> ReplayPending(RewardsSet set, bool scheduleAcknowledgements = true)
    {
        if (!RaceRuntime.Live || RaceSession.Current is not { } session) return [];

        var byKey = Flatten(set.Rewards).ToDictionary(RaceRuntime.RewardKey, StringComparer.Ordinal);
        var acknowledgements = new List<(Receipt Receipt, RewardsSet? Set)>();
        bool changed = false;
        foreach (var receipt in session.Me.Receipts
                     .Where(receipt => receipt.Kind == "reroll_native")
                     .OrderBy(receipt => receipt.Amount).ThenBy(receipt => receipt.Id, StringComparer.Ordinal))
        {
            string consumed = session.LocalId + ":" + receipt.Value;
            bool isApplied = session.ReceiptApplied(receipt.Id);
            bool isConsumed = session.Engine.State.ConsumedRewards.Contains(consumed);

            // The applied marker proves the native side effect was checkpointed.
            // A claimed reward may no longer exist in this screen's set, but its
            // receipt still needs its idempotent ack to close the transaction.
            if (isApplied && isConsumed)
            {
                acknowledgements.Add((receipt, null));
                continue;
            }

            // An unmatched receipt belongs to another reward set. Keep it until
            // the exact reward is reconstructed; only the consumed+applied case
            // above is safe to acknowledge without a visible reward.
            if (!byKey.TryGetValue(receipt.Value, out var reward)) continue;

            if (isApplied)
            {
                acknowledgements.Add((receipt, set));
                continue;
            }

            if (isConsumed)
            {
                ReportConsumedWithoutApplied(session, receipt);
                continue;
            }

            if (reward.SuccessfullySelected) continue;
            if (receipt.Amount <= 0 || RewardRerollRules.NativeUsage(session.Me, receipt.Value).Used < receipt.Amount) continue;

            RewardRerollEffect.ApplyNativeReroll(reward);
            session.MarkReceiptApplied(receipt.Id);
            changed = true;
            acknowledgements.Add((receipt, set));
        }

        if (changed) RewardRerollPersistence.CheckpointSet(set);
        if (!scheduleAcknowledgements) return acknowledgements.Select(item => item.Receipt.Id).ToArray();
        foreach (var (receipt, receiptSet) in acknowledgements)
            RaceUi.Run(async () =>
            {
                try { await AcknowledgeAsync(session, receipt, receiptSet); }
                catch (Exception error) { RaceUi.Error(L10n.T("奖励刷新已保存，收据确认将在恢复后重试：") + L10n.T(error.Message)); }
            });
        return acknowledgements.Select(item => item.Receipt.Id).ToArray();
    }

    private static void ReportConsumedWithoutApplied(RaceSession session, Receipt receipt)
    {
        string id = session.Engine.State.Id + ":" + session.LocalId + ":" + receipt.Id;
        lock (gate)
            if (!loggedConsumedInconsistencies.Add(id)) return;
        Godot.GD.PushWarning(L10n.F($"[分道争锋] 原生奖励已领取，但刷新收据 {receipt.Id} 没有已应用标记；保留收据且不会重复刷新。"));
    }

    internal static async Task AcknowledgeAsync(RaceSession session, Receipt receipt, RewardsSet? set)
    {
        if (!session.ReceiptApplied(receipt.Id)
            || session.Me.Receipts.All(existing => existing.Id != receipt.Id)) return;
        await session.Submit(session.New("ack", receipt.Id));
        if (set != null) RewardRerollPersistence.CheckpointSet(set);
        else RaceRuntime.Checkpoint();
    }

    private static IEnumerable<Reward> Flatten(IEnumerable<Reward> rewards)
    {
        foreach (var reward in rewards)
        {
            if (reward is LinkedRewardSet linked)
            {
                foreach (var child in Flatten(linked.Rewards)) yield return child;
            }
            else yield return reward;
        }
    }
}
