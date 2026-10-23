using System.Globalization;

namespace FendaoZhengfeng.Core;

public static class PuppetRules
{
    public const string Id = "substitute_puppet";
    public static int Generation(Holding holding) => holding.Counters.GetValueOrDefault("generation");
    public static void Granted(Racer racer, Holding holding, string eventId, int maxHp)
    {
        bool fresh = !holding.Counters.ContainsKey("generation");
        if (fresh)
        {
            if (maxHp is < 1 or > 10000000) throw new RuleException("傀儡初始生命无效。");
            holding.Counters["generation"] = racer.History[Id];
            holding.Counters["initial_hp"] = maxHp;
        }
        int amount = fresh ? holding.Counters["initial_hp"] : (int)Math.Ceiling(holding.Counters["initial_hp"] * 0.25m);
        racer.Receipts.Add(new(eventId + ":puppet", fresh ? "puppet_create" : "puppet_reinforce",
            Generation(holding).ToString(CultureInfo.InvariantCulture), amount));
    }
    public static void Destroyed(Racer racer, string generation)
    {
        if (racer.Effects.TryGetValue(Id, out var holding)
            && Generation(holding).ToString(CultureInfo.InvariantCulture) == generation)
            racer.Effects.Remove(Id);
    }
}
