using FendaoZhengfeng.Core;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace FendaoZhengfeng.Mod;

// A committed team reset is observed by every peer, including the non-initiator.
internal static class SharedReset
{
    private static RaceSession? owner;
    private static int generation = -1;
    private static Task? transition;

    internal static void Tick()
    {
        if (!RaceRuntime.Live || RaceSession.Current is not { } session || !session.Engine.State.Synchronous
            || !session.Me.ResetPending) return;
        if (ReferenceEquals(owner, session) && generation == session.Me.ResetCount) return;
        RaceUi.Run(() => Ensure(session));
    }

    internal static Task Ensure(RaceSession session)
    {
        if (ReferenceEquals(owner, session) && generation == session.Me.ResetCount && transition != null) return transition;
        owner = session;
        generation = session.Me.ResetCount;
        transition = Execute(session);
        return transition;
    }

    private static async Task Execute(RaceSession session)
    {
        var run = RaceRuntime.State ?? throw new InvalidOperationException("Missing shared run during reset.");
        if (CombatManager.Instance.IsInProgress) throw new RuleException("请先完成战斗，再使用R键。");
        long deadline = Environment.TickCount64 + 30000;
        while (RunManager.Instance.ActionExecutor.IsRunning)
        {
            CheckConnection(session, deadline);
            await Task.Delay(30);
        }
        RaceRuntime.ResetLocalTracking();
        run.ExtraFields.StartedWithNeow = false;
        FiveActCampaign.ResetEncounters(run);
        await RunManager.Instance.EnterAct(0, false);
        await session.Submit(session.New("reset_ready"));
        while (session.Engine.State.Racers.Values.Any(racer => racer.ResetPending))
        {
            CheckConnection(session, deadline);
            await Task.Delay(30);
        }
        await session.Submit(session.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(run)) });
        RaceUi.Refresh();
    }

    private static void CheckConnection(RaceSession session, long deadline)
    {
        if (!ReferenceEquals(RaceSession.Current, session) || !session.Transport.IsConnected)
            throw new IOException("团队重置时连接已断开。");
        if (Environment.TickCount64 >= deadline) throw new TimeoutException("等待团队完成重置超时。");
    }
}
