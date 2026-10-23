using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Rewards;

namespace FendaoZhengfeng.Mod;

// A native reward mutates inventory before SuccessfullySelected is set. Never
// serialize that interval. The first card chooser is safe until a choice returns.
internal sealed class NativeRewardGrant : IDisposable
{
    private static readonly AsyncLocal<NativeRewardGrant?> current = new();
    private static readonly ConditionalWeakTable<RaceSession, HashSet<NativeRewardGrant>> active = new();
    private readonly RaceSession session;
    private readonly NativeRewardGrant? previous;
    private bool applying = true, selected, disposed;
    private readonly Reward reward;
    private readonly bool modeChoiceAllowed;
    internal static Func<RaceSession, Reward, Task>? AfterGrantForVerification { get; set; }
    internal NativeRewardGrant(RaceSession session, Reward reward, bool modeOnly=false)
    {
        this.session=session;this.reward=reward;modeChoiceAllowed=modeOnly || reward is RaceActionReward;
        previous=current.Value;current.Value=this;active.GetOrCreateValue(session).Add(this);
    }
    internal static bool HasEnclosingGrant(RaceSession session) => current.Value is {disposed:false} parent && ReferenceEquals(parent.session,session);
    internal static bool IsApplying(RaceSession session) => active.GetOrCreateValue(session).Any(grant=>grant.applying);
    internal static async Task WaitForEntry(RaceSession session)
    {
        while(session.Recovery.NativeGrantBlocked && !( !session.Recovery.Frozen
            && (current.Value is {disposed:false,applying:true} parent && ReferenceEquals(parent.session,session)
                || NativeEventStep.HasEnclosingStep(session)))) {
            if(!ReferenceEquals(RaceSession.Current,session)) throw new OperationCanceledException(L10n.T("比赛已结束。"));
            await Task.Delay(20);
        }
    }
    internal static async Task WhenIdle(RaceSession session)
    {
        long until=System.Environment.TickCount64+5000;
        while(IsApplying(session)) {
            if(System.Environment.TickCount64>=until) throw new IOException(L10n.T("奖励仍在发放，未覆盖旧存档；请完成当前选择后重试。"));
            await Task.Delay(10);
        }
    }
    internal static NativeRewardGrant? BeginChoice(bool modeChoice=false)
    {
        var grant=current.Value;
        if(grant==null || (modeChoice ? !grant.modeChoiceAllowed : grant.reward is not CardReward)) return null;
        if(grant is {disposed:false,selected:false}) grant.applying=false;
        return grant;
    }
    internal static async Task<T> FinishChoice<T>(Task<T> choice, NativeRewardGrant? grant)
    {
        T result=await choice;
        if(grant is {disposed:false}) {
            await WaitForEntry(grant.session);
            grant.selected=true; grant.applying=true;
        }
        return result;
    }
    public void Dispose()
    {
        if(disposed) return;
        disposed=true;active.GetOrCreateValue(session).Remove(this);current.Value=previous;
    }
}

[HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen.OptionSelected))]
internal static class NativeRewardChoiceBoundaryPatch
{
    static void Prefix(out NativeRewardGrant? __state) => __state=NativeRewardGrant.BeginChoice();
    static void Postfix(ref Task<int?> __result, NativeRewardGrant? __state)
    { if(__state!=null) __result=NativeRewardGrant.FinishChoice(__result,__state); }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterRewardTaken))]
internal static class NativeRewardGrantVerificationPatch
{
    static void Postfix(Reward __2, ref Task __result)
    {
        if(System.Environment.GetEnvironmentVariable("FDZF_NATIVE_VERIFY")=="1"
            && RaceSession.Current is { } session && NativeRewardGrant.AfterGrantForVerification is { } hook)
            __result=StopAfterGrant(__result,session,__2,hook);
    }
    private static async Task StopAfterGrant(Task original,RaceSession session,Reward reward,Func<RaceSession,Reward,Task> hook)
    { await original;await hook(session,reward); }
}
