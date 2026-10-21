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
    public const string RulesVersion = "1.0.0-design.9";
    public static readonly IReadOnlyList<Effect> All = [
        C("exhaustion", "力竭", Tier.Ordinary, Rarity.Common, "开战失去1力量；每次强化再失去1。"),
        C("imbalance", "失衡", Tier.Ordinary, Rarity.Common, "开战失去1敏捷；每次强化再失去1。"),
        C("iron_curtain", "铁幕", Tier.Ordinary, Rarity.Common, "敌人每回合获得2格挡；每次强化+2。"),
        C("timidity", "怯战", Tier.Ordinary, Rarity.Common, "开战获得1回合虚弱；每次强化+1回合。"),
        C("fortune_dice", "命运骰子", Tier.Elite, Rarity.Common, "每回合攻击伤害随机为80%～110%。每次强化降低下限5%，最低50%。"),
        C("clutter", "滞念", Tier.Elite, Rarity.Uncommon, "起手加入2张杂念，回合结束消耗；每次强化多1张。"),
        C("abet", "助凶", Tier.Ordinary, Rarity.Uncommon, "敌人开战获得1力量；每次强化+1。"),
        C("carapace", "甲壳", Tier.Elite, Rarity.Uncommon, "敌人每回合获得3覆甲；每次强化+1。"),
        C("overgrowth", "血肉滋长", Tier.Ordinary, Rarity.Rare, "敌人初始生命+10%；每次强化再+10%。"),
        C("barbs", "倒刺", Tier.Elite, Rarity.Rare, "每张攻击牌对每名敌人首次造成未格挡伤害时，受到1反伤；每次强化+1。"),
        C("hard_shell", "硬壳", Tier.Boss, Rarity.Rare, "前3回合，敌人每回合最多损失40%最大生命；每次强化降低5%，最低20%。"),
        C("ritual", "邪仪", Tier.Boss, Rarity.Rare, "敌人每次行动结束获得1力量；每次强化+1。"),
        D("all_in", "孤注一掷", Tier.Elite, Rarity.Uncommon, "首回合获得1能量，但不能获得格挡。"),
        D("tomorrow", "借来的明天", Tier.Ordinary, Rarity.Uncommon, "首回合多1能量、抽1张牌；第二回合各减少1。"),
        D("greedy_pocket", "贪婪口袋", Tier.Elite, Rarity.Uncommon, "战斗金币+25%；起手少抽1张牌，至少抽1张。"),
        D("bitter_sweet", "先苦后甜", Tier.Boss, Rarity.Rare, "每回合首张手动牌费用翻倍，第三张手动牌额外打出一次。"),
        C("burn_bridges", "异种侵染", Tier.Elite, Rarity.Rare, "每获得1张永久牌，随机将1张原有牌变为其他角色的同稀有度牌，保留升级；每次强化多变化1张。"),
        D("borrowed_time", "预借时间", Tier.Boss, Rarity.Rare, "每回合获得1能量，同时获得3灾厄。"),
        D("bloom_decay", "盛放与凋零", Tier.Boss, Rarity.Rare, "每次卡牌奖励可多选1张。永久牌有8耐久，打出消耗1；耗尽后枯萎，可在营地修复。"),
        B("shared_fortune", "有福同享", Rarity.Common, "进入下一节点时，获得期间单个对手最高金币收入的50%，上限30；每次强化上限+5。转账与模式金币不计。", false, true, true, true, Tier.Elite),
        B("adversity_profit", "逆境生财", Rarity.Common, "每次战斗胜利，按开战时携带的不同诅咒获得金币：普通1、精英3、Boss5。同名只计一次，契约不计。整局上限800；每次强化上限+400。", true, true, true, true, Tier.Elite),
        B("war_feeds_war", "以战养战", Rarity.Common, "每次取得叠1层，最多3层。开战时每层获得1力量、1敏捷、1集中。", false, true, false, true, Tier.Ordinary),
        B("catch_up", "错峰出击", Rarity.Common, "接下来1场战斗起手多抽1张牌；落后至少一幕时延长至2场。", false, true, false, false, Tier.Ordinary),
        B("shared_misery", "灾祸共鸣", Rarity.Uncommon, "敌人塞入的状态牌会转发给对手，在其下场战斗加入；每名对手最多待收5张。", false, true, true, true, Tier.Elite),
        B("retaliation", "以牙还牙", Rarity.Uncommon, "收到献祭诅咒时，施加者获得1张诅咒牌；每名施加者最多2张，每次强化上限+1。", false, true, true, true, Tier.Elite),
        B("siphon", "汲取", Rarity.Uncommon, "每场可放弃1次卡牌奖励，获得10金币和下场首回合2能量；每次强化金币+2。", true, true, true, true, Tier.Elite),
        B("battle_hardened", "愈战愈勇", Rarity.Rare, "每胜利1场，攻击伤害与格挡+1%；每次强化使每胜加成再+0.1%。计入本局此前胜利。", true, false, true, true, Tier.Boss),
        B("death_defiance", "死不足惜", Rarity.Rare, "每复活1次，最大生命、攻击伤害与格挡+10%；每次强化使每次加成再+1%。计入本局此前复活。", true, false, true, true, Tier.Boss),
        D("no_plan", "技艺封存", Tier.Boss, Rarity.Rare, "下场开战移出全部技能牌；此后每胜任选1张取回。净化时返还余牌。"),
        B("small_light", "小而轻", Rarity.Rare, "非战斗时点击：失去3最大生命，移除1张牌。可重复使用，至少保留1最大生命。", true, false, true, true, Tier.Boss),
        C("farewell_upgrades", "和你的升级说再见吧", Tier.Boss, Rarity.Rare, "所有玩家从下场战斗起无法升级卡牌，包括施加者。保留已有升级，可被诅咒护盾抵挡。"),
        B("good_connections", "强买强卖", Rarity.Rare, "点击购买对手的1张牌，金币交给原主人。100金币起，每次购买涨50；双方在各自下场开战时交接。", true, false, true, true, Tier.Boss),
        B("r_key", "R键", Rarity.Rare, "非战斗时点击：保留物品和状态，回到第一幕。怪物生命、伤害、格挡+20%，可累加；此后Boss均算首杀。每次取得可用1次。", true, false, true, false, Tier.Boss),
        B("sharpen", "磨刃", Rarity.Common, "开战获得1力量；每次强化+1。", false, true, false, true, Tier.Ordinary),
        B("light_step", "轻步", Rarity.Common, "开战获得1敏捷；每次强化+1。", false, true, false, true, Tier.Ordinary),
        B("prepared", "整装", Rarity.Common, "开战获得6格挡；每次强化+3。", false, true, false, true, Tier.Ordinary),
        B("poise", "蓄势", Rarity.Common, "首回合多抽1张牌；每次强化+1。", false, true, false, true, Tier.Ordinary),
        B("restoration", "静养", Rarity.Common, "每场胜利恢复2生命；每次强化+1。", false, true, false, true, Tier.Ordinary),
        C("thin_armor", "薄甲", Tier.Ordinary, Rarity.Common, "开战获得1回合脆弱；每次强化+1回合。"),
        C("exposed", "露隙", Tier.Ordinary, Rarity.Common, "开战获得1回合易伤；每次强化+1回合。"),
        C("abrasion", "磨损", Tier.Ordinary, Rarity.Common, "开战失去2生命，至少保留1；每次强化多失去1。不触发失血收益。"),
        B("combat_cycle", "攻守轮转", Rarity.Uncommon, "每回合首次手动先打攻击再打技能，获得4格挡；每次强化+2。", false, true, true, true, Tier.Elite),
        B("heavy_followup", "重剑余势", Rarity.Uncommon, "每回合首次手动打出实付至少2能量的攻击，抽1张牌；每次强化增加1次本场额外抽1张的机会。", false, true, true, true, Tier.Elite),
        B("cornered", "困中求生", Rarity.Uncommon, "回合开始时，若生命不高于一半，获得8格挡并抽1张牌。每场1次；每次强化格挡+4。", false, true, true, true, Tier.Elite),
        C("combo_tax", "连打缴税", Tier.Elite, Rarity.Uncommon, "每手动打出4张牌，所有存活敌人获得3格挡；每次强化+2。"),
        C("disorder", "失序", Tier.Elite, Rarity.Uncommon, "每场首次重洗弃牌堆时，在抽牌堆加入2张临时晕眩；每次强化多1张。"),
        D("lone_crown", "孤勇王冠", Tier.Boss, Rarity.Rare, "开战获得2力量、2敏捷。普通治疗减半，营地治疗与复活不受影响。"),
        D("desperate_echo", "破釜回响", Tier.Boss, Rarity.Rare, "每回合首次手动打出实付至少2能量的非X费攻击，额外打出1次，并失去2生命，至少保留1。"),
        B("expedition", "远征悬赏", Rarity.Rare, "击杀精英累计悬赏，完成后获得1份可献祭遗物并重新累计。前两幕需3次，第三四幕2次，第五幕1次；重复取得进度+1。", true, false, true, true, Tier.Boss),
        B("scapegoat", "替死鬼", Rarity.Rare, "每5胜储存1次免死，最多2次；重复取得补1次。触发后剩1生命，不能抵挡直接处决或主动支付生命。", true, false, true, true, Tier.Boss),
        B("chaos_core", "混沌核心", Rarity.Uncommon, "开战生成1个随机充能球，此后每4回合再生成1个；每次强化间隔−1，最短1回合。", true, true, true, true, Tier.Elite),
        B("piercing_edge", "破甲尖锋", Rarity.Uncommon, "攻击伤害的15%无视格挡；每次强化+5%，最多100%。", true, true, true, true, Tier.Elite),
        B("resonant_circuit", "共振回路", Rarity.Rare, "对敌人造成实际伤害时，将其中20%传导给其余敌人；每次强化+5%。传导可被格挡，不会再次传导。", true, false, true, true, Tier.Boss),
        B("second_forge", "淬炼余火", Rarity.Rare, "卡牌可再次升级，额外获得3点伤害、格挡或治疗；每次强化额外+1。仍受升级禁令限制。", true, false, true, true, Tier.Boss),
        B("tail_refill", "续尾针线", Rarity.Rare, "非战斗时点击：支付350金币，恢复已消耗的蜥蜴尾巴。可重复使用。", true, false, true, true, Tier.Boss)
    ];
    private static readonly IReadOnlyDictionary<string, Effect> byId = All.ToDictionary(effect => effect.Id, StringComparer.Ordinal);
    public static Effect Get(string id) => byId.TryGetValue(id, out var effect) ? effect : throw new RuleException("未知效果：" + id);
    private static Effect C(string id, string n, Tier t, Rarity r, string d) => new(id, n, EffectKind.Curse, t, r, d);
    private static Effect D(string id, string n, Tier t, Rarity r, string d) => new(id, n, EffectKind.Contract, t, r, d);
    private static Effect B(string id, string n, Rarity r, string d, bool p, bool e, bool b, bool f, Tier tier) => new(id, n, EffectKind.Boon, tier, r, d, p, e, b, f);
}

public sealed class RuleException(string message) : Exception(message);

public static class Balance
{
    public const int TailRefillCost = 350;
    public static int ChaosInterval(int rank) => Math.Max(1, 4 - Math.Max(0, rank));
    public static decimal PierceRatio(int rank) => Math.Clamp(0.15m + 0.05m * Math.Max(0, rank), 0, 1);
    public static decimal ConductionRatio(int rank) => 0.20m + 0.05m * Math.Max(0, rank);
    public static int AdversityGoldCap(int rank) => checked(800 + 400 * Math.Max(0, rank));
    public static int AdversityGoldPerKind(Tier tier) => tier switch { Tier.Ordinary => 1, Tier.Elite => 3, _ => 5 };
    public static int WarFeedsWarStacks(Holding? holding) => holding == null ? 0 : Math.Clamp(holding.Rank + 1, 1, 3);
    public static double RepeatWeight(int count) => Math.Max(Math.Pow(0.4, Math.Max(0, count)), 0.1);
    public static double RareMultiplier(int burden, bool relic) => 1 + (relic ? 1.5 : 2) * Math.Max(0, burden) / (Math.Max(0, burden) + 8d);
    public static int? CleansePrice(Effect e, int count)
    {
        if (e.Kind == EffectKind.Boon || e.Tier == Tier.Boss) return null;
        decimal r = e.Rarity switch { Rarity.Common => 1m, Rarity.Uncommon => 1.25m, _ => 1.5m };
        return (int)(5 * Math.Ceiling((e.Tier == Tier.Ordinary ? 60 : 120) * r * (1 + 0.15m * Math.Max(0, count - 1)) / 5));
    }
}
