using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;

namespace FendaoZhengfeng.Mod;

internal static class DebugTesting
{
    private static bool settling;

    public static async Task WinBattle()
    {
        var session = RaceSession.Current ?? throw new InvalidOperationException("当前没有分道争锋比赛。");
        if (!session.IsMock && !RaceConfig.DebugVisible) throw new InvalidOperationException("请先由房主开启调试模式。");
        if (settling) return;
        if (!RaceRuntime.Live || session.Paused || !session.Me.InBattle ||
            !CombatManager.Instance.IsInProgress || CombatManager.Instance.IsOverOrEnding ||
            RaceRuntime.State?.Players.Single().Creature.CombatState is not { } combat)
            throw new InvalidOperationException("当前没有可以结算的战斗，请进入战斗后使用。");
        settling = true;
        RaceUi.ConcealPanel();
        try
        {
            // Native kills preserve death hooks, additional waves and ordinary victory rewards.
            // Only this process's independent combat is touched; race results use normal synchronization.
            for (int wave = 0; wave < 24; wave++)
            {
                if (!ReferenceEquals(session, RaceSession.Current)) return;
                foreach (var enemy in combat.Enemies.Where(enemy => enemy.IsAlive).ToList())
                    await CreatureCmd.Kill(enemy, true);
                await CombatManager.Instance.CheckWinCondition();
                if (!session.Me.InBattle || CombatManager.Instance.IsOverOrEnding) return;
                await Task.Delay(50);
            }
            throw new InvalidOperationException("特殊敌人的阶段尚未结束，请手动完成战斗后继续测试。");
        }
        finally { settling = false; RaceUi.Refresh(); }
    }
    public static async Task EnterHeart()
    {
        var session = RaceSession.Current ?? throw new InvalidOperationException("当前没有比赛。");
        if (!RaceConfig.DebugVisible || !RaceRuntime.Live) throw new InvalidOperationException("请开启调试模式。");
        if (session.Me.InBattle || CombatManager.Instance.IsInProgress || session.Paused || session.WaitingAtCheckpoint)
            throw new InvalidOperationException("请先结算战斗并继续比赛。");
        if (session.Me.Finished || session.Me.Eliminated) throw new InvalidOperationException("请重新开启测试局。");
        RaceUi.ConcealPanel();
        var run = RaceRuntime.State!;
        while (run.CurrentActIndex < 4)
        {
            await MegaCrit.Sts2.Core.Runs.RunManager.Instance.EnterAct(run.CurrentActIndex + 1, false);
            await session.Submit(session.New("progress", amount: run.Players.Single().Creature.CurrentHp, value: "Map"));
        }
        run.AddVisitedMapCoord(run.Map.SecondBossMapPoint!.coord);
        await MegaCrit.Sts2.Core.Runs.RunManager.Instance.EnterRoomDebug(MegaCrit.Sts2.Core.Rooms.RoomType.Boss,
            model: MegaCrit.Sts2.Core.Models.ModelDb.Encounter<TowerHeartEncounter>().ToMutable());
    }

}
