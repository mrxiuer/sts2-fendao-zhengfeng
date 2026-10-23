using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace FendaoZhengfeng.Mod;

// Tracks the whole async option, including native effects and nested rewards.
// Never try to serialize its unfinished task as a stable event page.
internal sealed class NativeEventStep : IDisposable
{
    private static readonly AsyncLocal<NativeEventStep?> current=new();
    private static readonly ConditionalWeakTable<RaceSession,HashSet<NativeEventStep>> active=new();
    private readonly RaceSession session;
    private readonly NativeEventStep? previous;
    private bool disposed;
    internal static Func<RaceSession,Task>? AfterGoldForVerification {get;set;}
    internal NativeEventStep(RaceSession session)
    {this.session=session;previous=current.Value;current.Value=this;active.GetOrCreateValue(session).Add(this);}
    internal static bool IsApplying(RaceSession session)=>active.GetOrCreateValue(session).Count>0;
    internal static bool HasEnclosingStep(RaceSession session)=>current.Value is {disposed:false} step && ReferenceEquals(step.session,session);
    internal static async Task WhenIdle(RaceSession session)
    {
        long until=System.Environment.TickCount64+5000;
        while(IsApplying(session) || NativeRewardGrant.IsApplying(session)) {
            if(System.Environment.TickCount64>=until) throw new IOException("事件或奖励仍在执行，未覆盖旧存档；请完成当前选择后重试。");
            await Task.Delay(10);
        }
        if(RaceRuntime.State?.CurrentRoom is EventRoom)
            await RunManager.Instance.EventSynchronizer.AwaitPendingOptionTasks();
    }
    public void Dispose()
    {if(disposed)return;disposed=true;active.GetOrCreateValue(session).Remove(this);current.Value=previous;}
}

[HarmonyPatch(typeof(EventOption),nameof(EventOption.Chosen))]
internal static class NativeEventStepPatch
{
    private static readonly HashSet<EventOption> originalCalls=[];
    static bool Prefix(EventOption __instance,ref Task __result)
    {
        if(!RaceRuntime.Live || originalCalls.Contains(__instance)) return true;
        var session=RaceSession.Current!;
        if(session.Recovery.NativeGrantBlocked && !NativeEventStep.HasEnclosingStep(session)) {__result=Task.CompletedTask;return false;}
        __result=Choose(session,__instance);return false;
    }
    private static async Task Choose(RaceSession session,EventOption option)
    {
        using var step=new NativeEventStep(session);
        Task original;
        originalCalls.Add(option);
        try {original=option.Chosen();}
        finally {originalCalls.Remove(option);}
        await original;
    }
}

[HarmonyPatch(typeof(EventSynchronizer),nameof(EventSynchronizer.ChooseLocalOption))]
internal static class BlockNewEventChoiceDuringSavePatch
{
    static bool Prefix()
    {
        if(!RaceRuntime.Live || RaceSession.Current is not {} session || !session.Recovery.NativeGrantBlocked
            || NativeEventStep.HasEnclosingStep(session)) return true;
        RaceUi.Notify("正在保存比赛，请稍候。");return false;
    }
}
