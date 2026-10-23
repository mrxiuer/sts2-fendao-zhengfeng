using System.Threading;
using MegaCrit.Sts2.Core.Rewards;

namespace FendaoZhengfeng.Mod;

// Only native gold-reward income joins the same authoritative claim. Ordinary
// shop/event/relic income keeps its existing independent accounting.
internal sealed class RewardIncomeBatch : IDisposable
{
    private static readonly AsyncLocal<RewardIncomeBatch?> current = new();
    private readonly RewardIncomeBatch? previous;
    private readonly RaceSession session;
    private readonly bool active;
    internal int Income { get; private set; }
    internal RewardIncomeBatch(RaceSession session, Reward reward)
    {
        this.session = session; previous = current.Value;
        active = reward is GoldReward && !RewardPerformance.LegacyPathForVerification;
        if (active) current.Value = this;
    }
    internal static bool Capture(RaceSession session, int income)
    {
        if (current.Value is not { active: true } batch || !ReferenceEquals(batch.session, session)) return false;
        batch.Income = checked(batch.Income + income); return true;
    }
    public void Dispose() { if (active) current.Value = previous; }
}
