namespace FendaoZhengfeng.Core;

public static partial class Catalog
{
    private static IReadOnlyList<Effect> AdvancedDefinitions => [
        B("substitute_puppet", "替身傀儡", Rarity.Rare, "获得壁垒傀儡，代你承受攻击与负面状态，并接收你的格挡；你无法治疗。傀儡死亡后失去此道具，伤害溢出由你承受。强化增加傀儡初始生命的25%最大生命。", true, false, true, true, Tier.Boss),
        B("burrow_sanctuary", "地底庇护", Rarity.Rare, "每3场胜利储存1次遁地，最多2次；每次强化少需1胜，最少1胜。使用时获得等同最大生命的格挡并结束回合；下回合出土。遁地期间破盾则眩晕1回合。", true, false, true, true, Tier.Boss),
        B("giant_form", "巨人化", Rarity.Rare, "体型翻倍。攻击斩杀最大生命低于你当前生命的敌人；每次强化斩杀阈值+10%。", true, false, true, true, Tier.Boss),
        B("extra_timeline", "额外时间线", Rarity.Rare, "获得1张2费时间线卡，可打出对手牌组中任意1张牌的副本。每次取得再获得1张。", true, false, true, true, Tier.Boss),
        B("heartstrings_charm", "牵心丝", Rarity.Rare, "获得1张魅惑：2费，消耗。使敌人两回合内攻击与负面状态转向其他敌人，无其他敌人则转向自身；增益转给你。每次取得再获得1张。", true, false, true, true, Tier.Boss),
        B("twin_slime", "双生黏液", Rarity.Rare, "化身史莱姆。致命伤害使你分裂为两个史莱姆，各有你50%的最大生命，每张牌各打出一次；任一死亡则你死亡。强化每次+5个百分点，最多75%。", true, false, true, true, Tier.Boss),
        B("encore_rewards", "再来一次", Rarity.Rare, "非金币奖励可刷新1次；每次强化再+1次，最多3次。", true, false, true, true, Tier.Boss)
    ];
}
