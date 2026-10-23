using System.Runtime.CompilerServices;
using FendaoZhengfeng.Core;

namespace FendaoZhengfeng.Mod;

internal static class NativeClaimJournal
{
    private static readonly ConditionalWeakTable<RaceSession, Dictionary<string, Command>> pending = new();
    internal static Func<RaceSession, Command, Task>? BeforeSubmitForVerification { get; set; }
    internal static Func<RaceSession, Command, Task>? AfterSubmitForVerification { get; set; }
    internal static Action<RaceSession, Command>? AfterRecordForVerification { get; set; }
    internal static void Record(RaceSession session, Command claim)
    {
        pending.GetOrCreateValue(session)[claim.Id] = claim;
    }
    internal static async Task SubmitWhenReady(RaceSession session, Command claim)
    {
        while(true) {
            await NativeRewardGrant.WaitForEntry(session);
            if(!session.IsMock) {
                if(!ReferenceEquals(RaceSession.Current,session) || !RaceRuntime.Live) throw new OperationCanceledException("比赛已结束。");
                if(!session.IsHost && !session.Transport.IsConnected) throw new IOException("比赛通信已断开，领取记录保留待房主恢复。");
                if(session.WaitingAtCheckpoint) throw new RuleException("个人进度已进入共同检查点。");
                // Both messages use the same reliable ordered transport. Never
                // let a confirmed claim outrun its already-granted inventory.
                if(session.Recovery.SendSnapshot()==null) {await Task.Delay(20);continue;}
            }
            try { await session.Submit(claim); return; }
            catch(RuleException) when(session.Recovery.NativeGrantBlocked) { }
        }
    }
    internal static async Task Flush(RaceSession session)
    {
        foreach (var claim in Capture(session))
        {
            if (session.Recovery.Frozen) return;
            await SubmitWhenReady(session,claim);
            Confirm(session, claim.Id);
        }
    }
    internal static bool Contains(RaceSession session,string id) => pending.GetOrCreateValue(session).ContainsKey(id);
    internal static void Confirm(RaceSession session, string id) => pending.GetOrCreateValue(session).Remove(id);
    internal static Command[] Capture(RaceSession session) => pending.GetOrCreateValue(session).Values
        .Where(c => !session.Engine.State.ProcessedCommands.Contains(c.Id)
            && !session.Engine.State.ConsumedRewards.Contains(session.LocalId + ":" + c.Key)).ToArray();
}
