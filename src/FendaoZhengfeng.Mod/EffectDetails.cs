using FendaoZhengfeng.Core;

namespace FendaoZhengfeng.Mod;

internal static class EffectDetails
{
    internal static string Describe(string id)
    {
        var effect = Catalog.Get(id);
        var session = RaceSession.Current;
        var racer = session?.Me;
        string kind = effect.Kind switch { EffectKind.Curse => L10n.T("诅咒"), EffectKind.Contract => L10n.T("双刃契约"), _ => L10n.T("增益") };
        string result = kind + " · " + RaceUi.TierName(effect.Tier) + " · " + (effect.Rarity == Rarity.Rare ? L10n.T("稀有") : effect.Rarity == Rarity.Uncommon ? L10n.T("罕见") : L10n.T("普通")) + "\n" + L10n.T(effect.Description);
        if (racer == null || !racer.Effects.TryGetValue(id, out var holding)) return result;
        int active = racer.Count(id);
        int pending = effect.Kind == EffectKind.Boon ? 0 : holding.Grants.Count - active;
        if (effect.Kind == EffectKind.Boon) { if (holding.Rank > 0) result += L10n.F($"\n强化 +{holding.Rank}"); }
        else result += (active > 0 ? L10n.F($"\n生效 {active} 层") : "") + (pending > 0 ? L10n.F($"\n下场生效 {pending} 层") : "");
        var modifier = RaceModifier.Active;
        result += id switch
        {
            "shared_fortune" => L10n.F($"\n下个节点领取 {Math.Min(30 + 5 * holding.Rank, racer.FortuneIncome.Values.DefaultIfEmpty().Max() / 2)} 金币。"),
            "fortune_dice" => L10n.F($"\n本回合攻击伤害倍率：{modifier?.Dice ?? 100}%"),
            "bitter_sweet" => L10n.F($"\n本回合手动出牌 {modifier?.ManualCards ?? 0} 张。"),
            "hard_shell" => racer.InBattle ? L10n.F($"\n当前第 {modifier?.Turn ?? 0} 回合：{(modifier?.Turn > 3 ? L10n.T("本场限制已结束。") : L10n.T("敌人状态栏显示剩余失血额度。"))}") : "",
            "bloom_decay" => L10n.T("\n卡牌右上角显示耐久；营地修复至8。"),
            "battle_hardened" => L10n.F($"\n累计胜利 {racer.VictoryCount}，本项伤害/格挡加成 {racer.VictoryCount * (1 + 0.1 * holding.Rank):0.#}%。"),
            "death_defiance" => L10n.F($"\n累计真实复活 {racer.RevivalCount}，本项成长 {racer.RevivalCount * (10 + holding.Rank):0.#}%。"),
            "scapegoat" => L10n.F($"\n储存 {racer.Ghosts}/2 次；距下次充能还需 {5 - holding.Used % 5} 胜。"),
            "catch_up" => L10n.F($"\n剩余 {holding.Used} 场战斗。"),
            "war_feeds_war" => L10n.F($"\n当前 {Balance.WarFeedsWarStacks(holding)}/3 层。"),
            "adversity_profit" => L10n.F($"\n整局已领取 {holding.Used}/{Balance.AdversityGoldCap(holding.Rank)} 金币。"),
            "no_plan" => L10n.F($"\n暂存 {modifier?.VaultCount ?? 0} 张技能牌。"),
            "combat_cycle" => L10n.F($"\n本回合{(modifier?.CycleUsed == true ? L10n.T("已触发") : L10n.T("尚未触发"))}；每回合一次。"),
            "heavy_followup" => L10n.F($"\n本回合{(modifier?.HeavyUsed == true ? L10n.T("已触发") : L10n.T("尚未触发"))}；本场强化额外抽牌已用 {modifier?.HeavyBonusUsed ?? 0}/{holding.Rank}。"),
            "cornered" => L10n.F($"\n本场{(modifier?.CorneredUsed == true ? L10n.T("已触发") : L10n.T("尚未触发"))}。"),
            "combo_tax" => L10n.F($"\n本回合手动出牌 {modifier?.ManualCards ?? 0} 张；每4张缴税一次。"),
            "disorder" => L10n.F($"\n本场{(modifier?.DisorderUsed == true ? L10n.T("已触发") : L10n.T("尚未触发"))}；首次重洗后加入临时晕眩。"),
            "desperate_echo" => L10n.F($"\n本回合{(modifier?.EchoUsed == true ? L10n.T("已触发") : L10n.T("尚未触发"))}；支付2生命，最低保留1。"),
            "expedition" => L10n.F($"\n当前悬赏进度 {racer.ExpeditionProgress}/{Engine.ExpeditionGoal(racer.Act)}；待结算额外遗物 {racer.ExpeditionRewards.Count} 份。"),
            "small_light" => L10n.F($"\n累计支付 {modifier?.SmallLightLoss ?? 0} 最大生命。"),
            "good_connections" => L10n.F($"\n点击图标购买；下次价格 {100 + 50 * racer.Purchases} 金币，已购买 {racer.Purchases} 次。待接收 {session!.Engine.State.Trades.Count(trade => trade.Buyer == racer.Id && !trade.Delivered)} 张，待交出 {session.Engine.State.Trades.Count(trade => trade.Seller == racer.Id && !trade.Removed)} 张。"),
            "r_key" => L10n.F($"\n点击图标使用；可用 {racer.ResetCharges} 次，已重置 {racer.ResetCount} 次，怪物生命/伤害/格挡额外 +{20 * racer.ResetCount}%。"),
            "chaos_core" => L10n.F($"\n当前间隔 {Balance.ChaosInterval(holding.Rank)} 回合。"),
            "piercing_edge" => L10n.F($"\n当前穿透 {Balance.PierceRatio(holding.Rank):P0}。"),
            "resonant_circuit" => L10n.F($"\n当前传导 {Balance.ConductionRatio(holding.Rank):P0}。"),
            "second_forge" => L10n.F($"\n再次升级额外增加 {3 + holding.Rank} 数值。"),
            "evil_echo" => L10n.F($"\n当前负面效果倍率 {Balance.EvilEchoMultiplier(holding.Rank)}。"),
            "flight_wings" => L10n.F($"\n当前攻击减伤 {1 - Balance.FlightReceivedRatio(holding.Rank):P0}；最高生命相同时按敌人站位顺序锁定。"),
            "gamblers_heart" => L10n.F($"\n当前暴击率 {Balance.CriticalChance(holding.Rank):P0}。暴击只在实际攻击时判定。"),
            "substitute_puppet" => modifier?.PuppetStatusText ?? "",
            "twin_slime" => L10n.F($"\n分裂后每个身体的最大生命为自身的{TwinSlimeRules.SplitPercent(holding.Rank + 1)}%。"),
            "giant_form" => L10n.F($"\n当前斩杀阈值：自身生命的{100 + 10 * holding.Rank}%；敌方最大生命须严格低于阈值。"),
            "encore_rewards" => L10n.F($"\n每份奖励最多重掷{RewardRerollRules.Limit(racer)}次；金币奖励不能刷新。"),
            "burrow_sanctuary" => L10n.F($"\n储存 {BurrowRules.Read(racer).Charges}/2 次；充能 {BurrowRules.Read(racer).Progress}/{BurrowRules.Read(racer).WinsPerCharge} 胜。") + (BurrowRules.Read(racer).Phase switch { BurrowRules.Phase.Burrowed => BurrowRules.Read(racer).BlockBroken ? L10n.T("\n遁地中；格挡已破，下回合眩晕。") : L10n.T("\n遁地中；下回合出土。"), BurrowRules.Phase.Stunned => L10n.T("\n眩晕：跳过本回合。"), _ => L10n.T("\n战斗中点击图标遁地。") }),
            "tail_refill" => racer.TailSpent ? L10n.T("\n蜥蜴尾巴已消耗；点击补充。") : L10n.T("\n当前无需补充。"),
            _ => ""
        };
        if (effect.Kind != EffectKind.Boon) result += L10n.T("\n持续至净化；净化移除全部同名层数。");
        else if (effect.Permanent) result += L10n.T("\n整局有效。");
        var sources = holding.Grants.Select(grant => grant.Source).Where(source => source.Length > 0 && source != racer.Id).Distinct()
            .Select(source => session!.Engine.State.Racers.TryGetValue(source, out var other) ? L10n.RacerName(other) : source).ToList();
        return sources.Count > 0 ? result + L10n.T("\n来源：") + string.Join("、", sources) : result;
    }
}
