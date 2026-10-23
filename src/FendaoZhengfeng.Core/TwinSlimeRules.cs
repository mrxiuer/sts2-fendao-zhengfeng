namespace FendaoZhengfeng.Core;

/// <summary>Pure rules for the Twin Slime combat transformation.</summary>
public static class TwinSlimeRules
{
    public const string EffectId = "twin_slime";
    public const int BaseSplitPercent = 50;
    public const int SplitPercentPerExtraStack = 5;
    public const int MaximumSplitPercent = 75;

    public static int SplitPercent(int stacks)
    {
        if (stacks <= 0) return 0;
        return (int)Math.Min(MaximumSplitPercent, (long)BaseSplitPercent + ((long)stacks - 1L) * SplitPercentPerExtraStack);
    }

    public static decimal SplitRatio(int stacks) => SplitPercent(stacks) / 100m;

    /// <summary>Each twin gets the same rounded share of the source maximum HP, at least one.</summary>
    public static int BodyMaxHp(int sourceMaxHp, int stacks)
    {
        if (stacks <= 0) return 0;
        decimal hp = Math.Max(1, sourceMaxHp) * SplitRatio(stacks);
        return Math.Max(1, (int)Math.Round(hp, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Recombine the two temporary combat max-HP pools without making the split bonus permanent.
    /// Max-HP gained or lost on either body during combat is retained as a delta from its split value.
    /// </summary>
    public static int RecombinedMaxHp(int sourceMaxHp, int splitBodyMaxHp, int currentFirstMaxHp, int currentSecondMaxHp)
    {
        long delta = (long)currentFirstMaxHp + currentSecondMaxHp - 2L * splitBodyMaxHp;
        return (int)Math.Clamp((long)Math.Max(1, sourceMaxHp) + delta, 1L, int.MaxValue);
    }

    public static int RecombinedCurrentHp(int currentFirstHp, int currentSecondHp, int recombinedMaxHp)
        => (int)Math.Clamp((long)currentFirstHp + currentSecondHp, 0L, Math.Max(1, recombinedMaxHp));

    /// <summary>Preserves any native replay count while giving both bodies the same number of card resolutions.</summary>
    public static int PairedPlayCount(int nativePlayCount)
    {
        if (nativePlayCount <= 0) return 0;
        return nativePlayCount > int.MaxValue / 2 ? int.MaxValue : nativePlayCount * 2;
    }
}
