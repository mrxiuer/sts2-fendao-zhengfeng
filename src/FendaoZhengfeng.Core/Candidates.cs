using System.Security.Cryptography;
using System.Text;

namespace FendaoZhengfeng.Core;

public sealed class StableRandom(string seed)
{
    private ulong counter;
    public double Next()
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed + ":" + (counter++).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return (System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes) >> 11) * (1d / (1UL << 53));
    }
    public T Pick<T>(IReadOnlyList<T> values, Func<T, double> weight)
    {
        if (values.Count == 0) throw new RuleException("没有合法候选，奖励保持可领取。");
        double total = values.Sum(weight), x = Next() * total;
        if (total <= 0) throw new RuleException("候选权重无效。");
        foreach (var value in values) { x -= weight(value); if (x < 0) return value; }
        return values[^1];
    }
}

public static class Candidates
{
    public static List<string> Generate(Match m, Racer p, string key, Route route, bool boss)
    {
        var rng = new StableRandom(m.Seed + ":" + p.Id + ":" + key + ":" + route);
        var candidates = new List<Effect>();
        var available = Catalog.All.Where(e => route switch
        {
            Route.Mixed => e.Kind switch
            {
                EffectKind.Curse => boss ? e.Tier != Tier.Ordinary : e.Tier != Tier.Boss,
                EffectKind.Contract => !p.Effects.ContainsKey(e.Id),
                _ => boss ? e.BossPool : e.ElitePool
            },
            Route.Disrupt => e.Kind == EffectKind.Curse && (boss ? e.Tier != Tier.Ordinary : e.Tier != Tier.Boss),
            Route.Contract => e.Kind == EffectKind.Contract && !p.Effects.ContainsKey(e.Id),
            Route.Grow => e.Kind == EffectKind.Boon && (boss ? e.BossPool : e.ElitePool),
            _ => e.Kind == EffectKind.Boon && e.FirstKillPool
        }).Where(e => e.Id != "scapegoat" || !p.Has(e.Id) || p.Ghosts < 2).ToList();
        double Weight(Effect e) => e.Weight * (e.Kind == EffectKind.Curse ? 1 : Balance.RepeatWeight(p.History.GetValueOrDefault(e.Id)));
        void Take(Func<Effect, bool> filter)
        {
            var pool = available.Where(filter).ToList();
            if (pool.Count == 0) return;
            var chosen = rng.Pick(pool, Weight); candidates.Add(chosen); available.Remove(chosen);
        }
        if (route == Route.Mixed)
        {
            if (boss) Take(e => e.Kind == EffectKind.Curse && e.Tier == Tier.Boss);
            while (candidates.Count < 3 && available.Count > 0) Take(_ => true);
            // The guarantee has no fixed screen position and there are no category slots.
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = (int)(rng.Next() * (i + 1));
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }
        }
        else if (route == Route.Disrupt && boss) { Take(e => e.Tier == Tier.Boss); Take(e => e.Tier == Tier.Elite); Take(e => e.Tier == Tier.Elite); }
        else if (route == Route.Contract && boss) { Take(e => e.Tier == Tier.Elite); Take(e => e.Tier == Tier.Elite); Take(_ => true); }
        else if (route == Route.FirstKill) { Take(e => e.Rarity != Rarity.Common); while (candidates.Count < 3 && available.Count > 0) Take(_ => true); }
        else if (route is Route.Disrupt or Route.Contract)
        {
            for (int i = 0; i < 3; i++)
            {
                var tier = rng.Next() < (route == Route.Disrupt ? 0.75 : 0.85) ? Tier.Ordinary : Tier.Elite;
                if (available.Any(e => e.Tier == tier)) Take(e => e.Tier == tier); else Take(_ => true);
            }
        }
        else while (candidates.Count < 3 && available.Count > 0) Take(_ => true);
        if (candidates.Count == 0 || route == Route.Disrupt && boss && candidates.Count(e => e.Tier == Tier.Boss) != 1
            || route == Route.Contract && boss && candidates.Count(e => e.Tier == Tier.Elite) < 2)
            throw new RuleException("当前候选不足，不能献祭；保留原奖励。");
        return candidates.Select(e => e.Id).ToList();
    }
}
