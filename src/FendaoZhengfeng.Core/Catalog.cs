namespace FendaoZhengfeng.Core;

public enum EffectKind { Curse, Contract, Boon }
public enum Tier { Ordinary, Elite, Boss }
public enum Rarity { Common, Uncommon, Rare }
public enum Route { Disrupt, Contract, Grow, FirstKill, Mixed }
public enum CleanseAt { Camp, Shop, Boss }

public sealed record Effect(string Id, string Name, EffectKind Kind, Tier Tier, Rarity Rarity,
    string Description, bool Permanent = false, bool ElitePool = false, bool BossPool = false, bool FirstKillPool = false)
{
    public int Burden => Rarity switch { Rarity.Common => 1, Rarity.Uncommon => 2, _ => 4 };
    public int Weight => Rarity switch { Rarity.Common => 100, Rarity.Uncommon => 50, _ => 20 };
}

public static class Catalog
{
    public const string RulesVersion = "1.0.0-design.5";
    public static readonly IReadOnlyList<Effect> All = [
        C("exhaustion", "力竭", Tier.Ordinary, Rarity.Common, "每场战斗开始失去1点力量。每次强化再失去1点。"),
        C("imbalance", "失衡", Tier.Ordinary, Rarity.Common, "每场战斗开始失去1点敏捷。每次强化再失去1点。"),
        C("iron_curtain", "铁幕", Tier.Ordinary, Rarity.Common, "敌人每回合获得2点格挡。每次强化增加2点。"),
        C("timidity", "怯战", Tier.Ordinary, Rarity.Common, "开战获得1回合虚弱。每次强化延长1回合。"),
        C("fortune_dice", "命运骰子", Tier.Elite, Rarity.Common, "每回合攻击伤害为80%至110%。强化每次降低下限5个百分点，最低50%。"),
        C("clutter", "滞念", Tier.Elite, Rarity.Uncommon, "起手抽牌后加入2张杂念，每次强化多1张；回合结束消耗。"),
        C("abet", "助凶", Tier.Ordinary, Rarity.Uncommon, "敌人开战获得1点力量。每次强化增加1点。"),
        C("carapace", "甲壳", Tier.Elite, Rarity.Uncommon, "敌人每回合获得3层覆甲。每次强化增加1层。"),
        C("overgrowth", "血肉滋长", Tier.Ordinary, Rarity.Rare, "敌人初始生命提高10%。每次强化再提高基础生命的10%。"),
        C("barbs", "倒刺", Tier.Elite, Rarity.Rare, "每张攻击牌对每名敌人首次造成伤害后反伤1。每次强化增加1。"),
        C("hard_shell", "硬壳", Tier.Boss, Rarity.Rare, "前三回合敌人每回合最多失去40%最大生命。强化每次降低5个百分点，最低20%。"),
        C("ritual", "邪仪", Tier.Boss, Rarity.Rare, "敌人每次行动结束获得1力量，每次强化增加1。"),
        D("all_in", "孤注一掷", Tier.Elite, Rarity.Uncommon, "首回合获得1能量，但不能新获得格挡。"),
        D("tomorrow", "借来的明天", Tier.Ordinary, Rarity.Uncommon, "首回合多1能量和1抽牌，第二回合各减少1。"),
        D("greedy_pocket", "贪婪口袋", Tier.Elite, Rarity.Uncommon, "战斗金币增加25%，起手少抽1张，至少抽1张。"),
        D("bitter_sweet", "先苦后甜", Tier.Boss, Rarity.Rare, "每回合第一张手动牌费用翻倍，第三张合法手动牌重放一次。"),
        D("burn_bridges", "过河拆桥", Tier.Elite, Rarity.Rare, "新增永久卡牌后，变化一张原有牌为其他角色同稀有度兼容牌，保留升级。"),
        D("borrowed_time", "预借时间", Tier.Boss, Rarity.Rare, "每回合获得1能量，同时施加自身3灾厄。"),
        D("bloom_decay", "盛放与凋零", Tier.Boss, Rarity.Rare, "卡牌奖励额外选择一次；永久牌有8耐久，用尽变为可修复的粘液。"),
        B("shared_fortune", "有福同享", Rarity.Common, "每节点复制对手最高原生金币收入的50%，最多30金。", false, true, true, true, Tier.Elite),
        B("adversity_profit", "逆境生财", Rarity.Common, "有代价净化每点已激活负担获得3金，累计最多45金。", false, true, true, true, Tier.Elite),
        B("war_feeds_war", "以战养战", Rarity.Common, "发动遗物献祭攻击恢复5%最大生命，最多3次。", false, true, false, true, Tier.Ordinary),
        B("catch_up", "错峰出击", Rarity.Common, "落后一幕时接下来2场起手多抽1张，否则1场。", false, true, false, false, Tier.Ordinary),
        B("shared_misery", "有难同当", Rarity.Uncommon, "敌人原始塞入的状态牌转发到对手下场战斗，全来源合计最多5张。", false, true, true, true, Tier.Elite),
        B("retaliation", "以牙还牙", Rarity.Uncommon, "受到献祭诅咒时，施加者获得一张可移除诅咒牌；每人最多2张。", false, true, true, true, Tier.Elite),
        B("siphon", "汲取", Rarity.Uncommon, "每场放弃一次卡牌奖励，获得10金与下场首回合2能量。", true, true, true, true, Tier.Elite),
        B("battle_hardened", "愈战愈勇", Rarity.Rare, "每次个人战斗胜利提高攻击伤害与格挡1%，追溯历史。", true, false, true, true, Tier.Boss),
        B("death_defiance", "死不足惜", Rarity.Rare, "每次真实复活提高最大生命、攻击伤害和格挡10%，追溯历史。", true, false, true, true, Tier.Boss),
        D("no_plan", "无计可施", Tier.Boss, Rarity.Rare, "下场战斗前移出牌组全部技能牌；此后每胜可从移出的牌中任选1张取回，直到取完。净化返还剩余牌。"),
        B("small_light", "小而轻", Rarity.Rare, "非战斗时点击图标，任选1张永久牌移除并失去3最大生命；可反复使用，最大生命不得低于1；持有后不再进入候选池。", true, false, true, true, Tier.Boss),
        C("farewell_upgrades", "和你的升级说再见吧", Tier.Boss, Rarity.Rare, "包括施加者在内的所有玩家，从各自下场战斗起不能再升级卡牌；保留已有升级，诅咒护盾正常抵挡。"),
        B("good_connections", "广结善缘", Rarity.Rare, "点击图标购买对手永久牌，100金币起，每成功购买一次加50；金币支付原主人，双方各自在下场开战时交接；持有后不再进入候选池。", true, false, true, true, Tier.Boss),
        B("r_key", "R键", Rarity.Rare, "非战斗时点击重回第一幕起点，保留生命、牌组、遗物、金币、药水与模式状态；每次取得可用1次，每次重置使怪物生命、伤害和格挡再加20%，此后每幕按首杀发奖。", true, false, true, false, Tier.Boss),
        B("sharpen", "磨刃", Rarity.Common, "每场开战获得1力量，每次强化再加1；持续至自己的下一次幕Boss击杀。", false, true, false, true, Tier.Ordinary),
        B("light_step", "轻步", Rarity.Common, "每场开战获得1敏捷，每次强化再加1；持续至自己的下一次幕Boss击杀。", false, true, false, true, Tier.Ordinary),
        B("prepared", "整装", Rarity.Common, "每场开战获得6格挡，每次强化再加3；持续至自己的下一次幕Boss击杀。", false, true, false, true, Tier.Ordinary),
        B("poise", "蓄势", Rarity.Common, "每场首回合额外抽1张，每次强化再加1；持续至自己的下一次幕Boss击杀。", false, true, false, true, Tier.Ordinary),
        B("restoration", "静养", Rarity.Common, "每场胜利恢复2生命，每次强化再加1；持续至自己的下一次幕Boss击杀。", false, true, false, true, Tier.Ordinary),
        C("thin_armor", "薄甲", Tier.Ordinary, Rarity.Common, "每场开战获得1回合脆弱，每次强化延长1回合。"),
        C("exposed", "露隙", Tier.Ordinary, Rarity.Common, "每场开战获得1回合易伤，每次强化延长1回合。"),
        C("abrasion", "磨损", Tier.Ordinary, Rarity.Common, "每场开战失去2当前生命，每次强化再失去1，最低保留1；不触发卖血收益。"),
        B("combat_cycle", "攻守轮转", Rarity.Uncommon, "每回合首次先手动打出攻击再打出技能，获得4格挡，每次强化再加2；每回合一次。", false, true, true, true, Tier.Elite),
        B("heavy_followup", "重剑余势", Rarity.Uncommon, "每回合首次手动打出实际支付至少2能量的攻击，抽1张；强化每级使每场一次触发额外再抽1张；仍每回合最多触发一次。", false, true, true, true, Tier.Elite),
        B("cornered", "困中求生", Rarity.Uncommon, "回合开始时生命不高于50%，获得8格挡并抽1张，每场一次；每次强化增加4格挡。", false, true, true, true, Tier.Elite),
        C("combo_tax", "连打缴税", Tier.Elite, Rarity.Uncommon, "每回合手动打出第4、8、12张等卡牌后，所有存活敌人获得3格挡；每次强化再加2。"),
        C("disorder", "失序", Tier.Elite, Rarity.Uncommon, "每场第一次重洗弃牌堆时，将2张临时晕眩加入抽牌堆；每次强化多1张。"),
        D("lone_crown", "孤勇王冠", Tier.Boss, Rarity.Rare, "每场开战获得2力量、2敏捷；普通治疗减半，营地恢复与复活不受影响。"),
        D("desperate_echo", "破釜回响", Tier.Boss, Rarity.Rare, "每回合首次手动打出实际支付至少2能量的非X费攻击重放1次，然后失去2生命，最低保留1。"),
        B("expedition", "远征悬赏", Rarity.Rare, "获得后击杀精英领取额外可献祭遗物；前两幕需3次、第三四幕需2次、第五幕需1次；完成后继续下一轮，重复获得增加1进度。", true, false, true, true, Tier.Boss),
        B("scapegoat", "替死鬼", Rarity.Rare, "每5胜储存1次抵挡致命伤害，最多2次，重复取得补1次。触发后剩1生命；不保护直接处决、强制死亡或主动支付生命。", true, false, true, true, Tier.Boss)
    ];
    public static Effect Get(string id) => All.FirstOrDefault(e => e.Id == id) ?? throw new RuleException("未知效果：" + id);
    private static Effect C(string id, string n, Tier t, Rarity r, string d) => new(id, n, EffectKind.Curse, t, r, d);
    private static Effect D(string id, string n, Tier t, Rarity r, string d) => new(id, n, EffectKind.Contract, t, r, d);
    private static Effect B(string id, string n, Rarity r, string d, bool p, bool e, bool b, bool f, Tier tier) => new(id, n, EffectKind.Boon, tier, r, d, p, e, b, f);
}

public sealed class RuleException(string message) : Exception(message);

public static class Balance
{
    public static double RepeatWeight(int count) => Math.Max(Math.Pow(0.4, Math.Max(0, count)), 0.1);
    public static double RareMultiplier(int burden, bool relic) => 1 + (relic ? 1.5 : 2) * Math.Max(0, burden) / (Math.Max(0, burden) + 8d);
    public static int? CleansePrice(Effect e, int count)
    {
        if (e.Kind == EffectKind.Boon || e.Tier == Tier.Boss) return null;
        decimal r = e.Rarity switch { Rarity.Common => 1m, Rarity.Uncommon => 1.25m, _ => 1.5m };
        return (int)(5 * Math.Ceiling((e.Tier == Tier.Ordinary ? 60 : 120) * r * (1 + 0.15m * Math.Max(0, count - 1)) / 5));
    }
}
