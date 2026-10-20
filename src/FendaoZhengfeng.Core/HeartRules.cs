namespace FendaoZhengfeng.Core;

public enum HeartAction { Debilitate, BloodShots, Echo, Strengthen }

public static class HeartRules
{
    public const int BaseHp = 900;
    public const int BloodDamage = 2;
    public const int BloodHits = 12;
    public const int EchoDamage = 40;
    public const int CopyAtRound = 4;
    public const int RestoreAtRound = 7;
    public static int RoundBudget(int maxHp) => Math.Max(1, (int)Math.Ceiling(maxHp * 0.25m));
    public static HeartAction ActionAt(string seed, int round)
    {
        if (round <= 1) return HeartAction.Debilitate;
        if (round % 3 == 1) return HeartAction.Strengthen;
        int cycle = (round - 2) / 3;
        bool bloodFirst = new StableRandom(seed + ":heart-cycle:" + cycle).Next() < 0.5;
        bool first = (round - 2) % 3 == 0;
        return first == bloodFirst ? HeartAction.BloodShots : HeartAction.Echo;
    }
    public static int StrengthForBuff(int number) => number < 4 ? 2 : number == 4 ? 12 : 22;
}
