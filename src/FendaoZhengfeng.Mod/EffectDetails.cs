using FendaoZhengfeng.Core;

namespace FendaoZhengfeng.Mod;

internal static class EffectDetails
{
    internal static string Describe(string id)
    {
        var effect = Catalog.Get(id);
        var session = RaceSession.Current;
        var racer = session?.Me;
        string kind = effect.Kind switch { EffectKind.Curse => "诅咒", EffectKind.Contract => "双刃契约", _ => "增益" };
        string result = kind + " · " + RaceUi.TierName(effect.Tier) + " · " + (effect.Rarity == Rarity.Rare ? "稀有" : effect.Rarity == Rarity.Uncommon ? "罕见" : "普通") + "\n" + effect.Description;
        if (racer == null || !racer.Effects.TryGetValue(id, out var holding)) return result;
        int active = racer.Count(id);
        int pending = effect.Kind == EffectKind.Boon ? 0 : holding.Grants.Count - active;
        result += effect.Kind == EffectKind.Boon ? $"\n强化 {holding.Rank}" : $"\n已激活 {active} 次，待生效 {pending} 次";
        var modifier = RaceModifier.Active;
        result += id switch
        {
            "shared_fortune" => $"\n当前记录的最高对手收入 {racer.FortuneIncome.Values.DefaultIfEmpty().Max()} 金币；待结算 {Math.Min(30 + 5 * holding.Rank, racer.FortuneIncome.Values.DefaultIfEmpty().Max() / 2)} 金币，进入下一个节点到账。转账与模式奖励不参与复制。",
            "fortune_dice" => $"\n本回合攻击伤害倍率：{modifier?.Dice ?? 100}%",
            "bitter_sweet" => $"\n本回合已手动出牌：{modifier?.ManualCards ?? 0}；首张翻倍，第三张重放。自动出牌不计。",
            "hard_shell" => $"\n当前第 {modifier?.Turn ?? 0} 回合；仅前3回合限制敌人每回合失血。敌人状态图标显示剩余额度。",
            "bloom_decay" => "\n永久牌与对应战斗副本的右上角显示剩余耐久数字；0耐久将枯萎，营地维护恢复为8。",
            "battle_hardened" => $"\n累计胜利 {racer.VictoryCount}，本项伤害/格挡加成 {racer.VictoryCount * (1 + 0.1 * holding.Rank):0.#}%。",
            "death_defiance" => $"\n累计真实复活 {racer.RevivalCount}，本项成长 {racer.RevivalCount * (10 + holding.Rank):0.#}%。",
            "scapegoat" => $"\n储存 {racer.Ghosts}/2 次；距下次充能还需 {5 - holding.Used % 5} 胜。",
            "catch_up" => $"\n剩余 {holding.Used} 场战斗。",
            "war_feeds_war" => $"\n当前 {Balance.WarFeedsWarStacks(holding)}/3 层；每场开战获得 {Balance.WarFeedsWarStacks(holding)} 力量、{Balance.WarFeedsWarStacks(holding)} 敏捷、{Balance.WarFeedsWarStacks(holding)} 集中。",
            "adversity_profit" => $"\n整局已领取 {holding.Used}/{Balance.AdversityGoldCap(holding.Rank)} 金币；剩余额度 {Math.Max(0, Balance.AdversityGoldCap(holding.Rank) - holding.Used)}。跨幕不失效、不重置额度。",
            "no_plan" => $"\n暂存 {modifier?.VaultCount ?? 0} 张技能牌。每次战胜后的原生奖励栏提供一次取回选择。",
            "combat_cycle" => $"\n本回合{(modifier?.CycleUsed == true ? "已触发" : "尚未触发")}；每回合一次。",
            "heavy_followup" => $"\n本回合{(modifier?.HeavyUsed == true ? "已触发" : "尚未触发")}；本场强化额外抽牌已用 {modifier?.HeavyBonusUsed ?? 0}/{holding.Rank}。",
            "cornered" => $"\n本场{(modifier?.CorneredUsed == true ? "已触发" : "尚未触发")}。",
            "combo_tax" => $"\n本回合手动出牌 {modifier?.ManualCards ?? 0} 张；每4张缴税一次。",
            "disorder" => $"\n本场{(modifier?.DisorderUsed == true ? "已触发" : "尚未触发")}；首次重洗后加入临时晕眩。",
            "desperate_echo" => $"\n本回合{(modifier?.EchoUsed == true ? "已触发" : "尚未触发")}；支付2生命，最低保留1。",
            "expedition" => $"\n当前悬赏进度 {racer.ExpeditionProgress}/{Engine.ExpeditionGoal(racer.Act)}；待结算额外遗物 {racer.ExpeditionRewards.Count} 份。",
            "small_light" => $"\n点击图标使用；累计支付 {modifier?.SmallLightLoss ?? 0} 点最大生命。战斗中不可用。",
            "good_connections" => $"\n点击图标购买；下次价格 {100 + 50 * racer.Purchases} 金币，已购买 {racer.Purchases} 次。待接收 {session!.Engine.State.Trades.Count(trade => trade.Buyer == racer.Id && !trade.Delivered)} 张，待交出 {session.Engine.State.Trades.Count(trade => trade.Seller == racer.Id && !trade.Removed)} 张。",
            "r_key" => $"\n点击图标使用；可用 {racer.ResetCharges} 次，已重置 {racer.ResetCount} 次，怪物生命/伤害/格挡额外 +{20 * racer.ResetCount}%。",
            _ => ""
        };
        if (effect.Kind != EffectKind.Boon) result += "\n持续至净化；净化移除同名全部强化。";
        else if (!effect.Permanent && id != "catch_up") result += "\n持续至自己的下一次幕Boss击杀。";
        var sources = holding.Grants.Select(grant => grant.Source).Distinct().Select(source => source == racer.Id ? "自己" : session!.Engine.State.Racers.TryGetValue(source, out var other) ? other.Name : source);
        return result + "\n来源：" + string.Join("、", sources);
    }
}
