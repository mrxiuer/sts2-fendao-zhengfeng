namespace FendaoZhengfeng.Core;

// Native inventory is already present in the associated snapshot. Only replay
// the missing authoritative accounting, never the native item acquisition.
public static class RewardClaimRecovery
{
    public static Match Merge(Match source, string player, IEnumerable<Command> claims)
    {
        var engine = new Engine(StateJson.Deserialize<Match>(StateJson.Serialize(source)));
        foreach (var claim in claims)
        {
            if (claim.Actor != player || claim.Kind != "claim" || claim.Amount < 0)
                throw new RuleException("原生奖励完成凭证无效。");
            if (engine.State.ProcessedCommands.Contains(claim.Id)
                || engine.State.ConsumedRewards.Contains(player + ":" + claim.Key)) continue;
            engine.Execute(claim);
        }
        return engine.State;
    }
}
