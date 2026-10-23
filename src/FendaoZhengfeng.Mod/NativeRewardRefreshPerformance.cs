using System.Diagnostics;
using FendaoZhengfeng.Core;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace FendaoZhengfeng.Mod;

// Uses the same four live ENet peers and late-run fixture as card/gold sampling.
internal static class NativeRewardRefreshPerformance
{
    private sealed record Sample(string Kind, int Round, double Milliseconds);
    private static readonly string[] kinds=["potion","card_refresh","potion_refresh","relic_refresh","offer_refresh"];
    private static async Task Wait(Func<bool> condition,string reason)
    { long until=System.Environment.TickCount64+45000; while(!condition()) { if(System.Environment.TickCount64>until) throw new TimeoutException(reason); await Task.Delay(10); } }
    private static void Check(bool value,string reason)
    { if(!value) throw new InvalidOperationException(reason); GD.Print("[FDZF REFRESH PERF PASS] "+reason); }
    private static void Click(NRewardButton row)=>AccessTools.Method(typeof(NRewardButton),"OnRelease").Invoke(row,null);
    private static NRewardButton[] Choices()=>NativeChoiceUi.ActiveVeil!.FindChildren("*","",true,false).OfType<NRewardButton>().ToArray();
    private static NCardRewardAlternativeButton[] Alternatives(NCardRewardSelectionScreen screen)
        =>((Control)AccessTools.Field(typeof(NCardRewardSelectionScreen),"_rewardAlternativesContainer").GetValue(screen)!)
            .GetChildren().OfType<NCardRewardAlternativeButton>().Where(button=>!button.IsQueuedForDeletion()).ToArray();
    internal static async Task Run(NGame game,RunState run,RaceSession session,Func<string,Task> barrier)
    {
        var player=run.Players.Single();var samples=new List<Sample>();int potionsAtStart=player.Potions.Count();
        Check(RewardRerollRules.Limit(session.Me)==1,"real encore holding grants one refresh per independent reward");
        for(int round=0;round<10;round++)
        {
            string kind=kinds[round%kinds.Length];
            await RunManager.Instance.EnterRoomDebug(kind is "relic_refresh" or "offer_refresh" ? RoomType.Elite : RoomType.Monster);
            await Wait(()=>player.PlayerCombatState?.Phase==PlayerTurnPhase.Play,"native refresh combat ready");
            PotionReward? injected=null;
            if(kind is "potion" or "potion_refresh")
            { injected=new PotionReward(player);((CombatRoom)run.CurrentRoom!).AddExtraReward(player,injected); }
            await DebugTesting.WinBattle();
            await Wait(()=>!session.Me.InBattle && RaceRuntime.RewardRoom==run.CurrentRoom && RaceRuntime.CombatRewards is { } set
                && (kind.StartsWith("potion",StringComparison.Ordinal) ? set.Rewards.Contains(injected!)
                    :kind=="card_refresh" ? set.Rewards.OfType<CardReward>().Any() :set.Rewards.OfType<RelicReward>().Any()),"current generated rewards ready");
            var rewards=RaceRuntime.CombatRewards!;
            Reward reward=kind.StartsWith("potion",StringComparison.Ordinal) ? injected!
                :kind=="card_refresh" ? rewards.Rewards.OfType<CardReward>().First() :rewards.Rewards.OfType<RelicReward>().First();
            NRewardButton? row=null;
            await Wait(()=>(row=game.GetTree().Root.FindChildren("*","",true,false).OfType<NRewardButton>().FirstOrDefault(b=>ReferenceEquals(b.Reward,reward)))!=null,"actual refresh reward row");
            string key=RaceRuntime.RewardKey(reward);
            NCardRewardSelectionScreen? screen=null;
            string offerId=RewardRerollRules.OfferId(session.LocalId,key,Route.Mixed);
            if(kind=="card_refresh")
            {
                Click(row!);
                await Wait(()=>(screen=game.GetTree().Root.FindChildren("*","",true,false).OfType<NCardRewardSelectionScreen>().FirstOrDefault(s=>s.IsVisibleInTree()))!=null
                    && Alternatives(screen).Length==2,"native alternatives include mode refresh below cards");
            }
            if(kind=="offer_refresh")
            {
                Click(row!);await Wait(()=>NativeChoiceUi.ActiveVeil!=null,"native relic choice menu");
                var first=NativeChoiceUi.ActiveVeil;Click(Choices()[1]);
                await Wait(()=>NativeChoiceUi.ActiveVeil is { } veil && !ReferenceEquals(first,veil) && session.Engine.State.Offers.ContainsKey(offerId),"native mixed offer with refresh option");
                Check(Choices().Length==5,"three mixed effects, refresh and return are actual native rows");
            }
            Button? refresh=null;
            if(kind is "potion_refresh" or "relic_refresh")
            {
                await Wait(()=>(refresh=row!.GetNodeOrNull<Button>("EncoreRewardReroll")) is { Disabled:false },"native row refresh button enabled");
            }
            await barrier("refresh-ready-"+round);
            int potionCount=player.Potions.Count();
            long started=Stopwatch.GetTimestamp();
            if(kind=="potion")
            {
                Click(row!);
                await Wait(()=>reward.SuccessfullySelected && session.Engine.State.ConsumedRewards.Contains(session.LocalId+":"+key)
                    && RewardPerformance.IsRewardClickComplete(reward),"native potion choice authoritative completion");
            }
            else if(kind=="offer_refresh")
            {
                var first=NativeChoiceUi.ActiveVeil;Click(Choices()[3]);
                await Wait(()=>RewardRerollRules.OfferUsage(session.Me,offerId).Used==1 && NativeChoiceUi.ActiveVeil is { } veil && !ReferenceEquals(first,veil),"mode refresh replaces actual candidates");
            }
            else
            {
                if(kind=="card_refresh") { var button=Alternatives(screen!)[1];button.EmitSignal(NClickableControl.SignalName.Released,button); }
                else refresh!.EmitSignal(Button.SignalName.Pressed);
                await Wait(()=>RewardRerollRules.NativeUsage(session.Me,key).Used==1 && !RewardRerollEffect.IsInFlight(session,key)
                    && session.Me.Receipts.All(r=>r.Kind!="reroll_native" || r.Value!=key),"native refresh and acknowledgement complete");
                if(kind=="card_refresh") await Wait(()=>Alternatives(screen!).Length==1,"exhausted card alternative removed");
                else await Wait(()=>refresh!.Disabled,"exhausted row button disabled");
            }
            samples.Add(new(kind,round,Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            if(kind=="potion")
            {
                Check(player.Potions.Count()==potionCount+1 && !await reward.SelectUnsynchronized(),"potion acquired once; stale native choice cannot add another");
            }
            else
            {
                Check(!reward.SuccessfullySelected && !session.Engine.State.ConsumedRewards.Contains(session.LocalId+":"+key),"refresh preserves the unclaimed reward");
                if(kind=="offer_refresh")
                {
                    var candidates=session.Engine.State.Offers[offerId].Candidates.ToArray();
                    Click(Choices()[^1]);await Wait(()=>!NativeChoiceUi.IsOpen && RewardPerformance.IsRewardClickComplete(reward),"cancel refreshed offer");
                    Click(row!);await Wait(()=>NativeChoiceUi.ActiveVeil!=null,"reopen relic choices");
                    var first=NativeChoiceUi.ActiveVeil;Click(Choices()[1]);
                    await Wait(()=>NativeChoiceUi.ActiveVeil is { } veil && !ReferenceEquals(first,veil),"reopen saved mixed offer");
                    Check(session.Engine.State.Offers[offerId].Candidates.SequenceEqual(candidates)
                        && RewardRerollRules.OfferUsage(session.Me,offerId).Used==1 && Choices().Length==4,"reopening preserves refreshed candidates and spent counter");
                    Click(Choices()[^1]);await Wait(()=>!NativeChoiceUi.IsOpen && RewardPerformance.IsRewardClickComplete(reward),"close unchanged reopened offer");
                }
                else
                {
                    bool rejected=false;
                    try { await RewardRerollEffect.RefreshAsync(null,reward,rewards); } catch(RuleException) { rejected=true; }
                    Check(rejected && RewardRerollRules.NativeUsage(session.Me,key).Used==1,"duplicate refresh cannot exceed one independent charge");
                    if(kind=="card_refresh")
                    { var skip=Alternatives(screen!)[0];skip.EmitSignal(NClickableControl.SignalName.Released,skip);
                        await Wait(()=>!GodotObject.IsInstanceValid(screen) || !screen!.IsInsideTree(),"close refreshed native card chooser"); }
                }
            }
            await barrier("refresh-done-"+round);
        }
        // Outside latency sampling: an open human chooser must not starve
        // roster updates, while an occupied transaction gate must defer them.
        if (!RewardPerformance.LegacyPathForVerification)
        {
            var cards=RaceRuntime.CombatRewards!.Rewards.OfType<CardReward>().First();
            var row=game.GetTree().Root.FindChildren("*","",true,false).OfType<NRewardButton>().First(b=>ReferenceEquals(b.Reward,cards));
            Click(row);NCardRewardSelectionScreen? chooser=null;
            await Wait(()=>(chooser=game.GetTree().Root.FindChildren("*","",true,false).OfType<NCardRewardSelectionScreen>().FirstOrDefault(s=>s.IsVisibleInTree()))!=null,"open roster verification chooser");
            void ForceDue()
            {
                AccessTools.Field(typeof(RaceSession),"lastTelemetry").SetValue(session,0L);
                AccessTools.Field(typeof(RaceSession),"lastPublicProfile").SetValue(session,"");
                AccessTools.Field(typeof(RaceSession),"pendingPublicProfile").SetValue(session,null);
            }
            ForceDue();long submitted=session.TelemetrySubmissionCount;session.Tick();
            Check(session.TelemetrySubmissionCount==submitted,"first changed profile waits for stability");
            await Wait(()=>session.TelemetrySubmissionCount>submitted,"roster continues refreshing while a human reads card choices");
            await Wait(()=>!(bool)AccessTools.Field(typeof(RaceSession),"sendingTelemetry").GetValue(session)!,"roster update has authoritative acknowledgement");
            var gate=(SemaphoreSlim)AccessTools.Field(typeof(RaceSession),"commandGate").GetValue(session)!;
            await gate.WaitAsync();
            try { ForceDue();submitted=session.TelemetrySubmissionCount;session.Tick();
                Check(session.TelemetrySubmissionCount==submitted,"background profile never queues behind an active transaction"); }
            finally { gate.Release(); }
            await Wait(()=>session.TelemetrySubmissionCount>submitted,"deferred roster update resumes after transaction gate release");
            var skip=Alternatives(chooser!)[0];skip.EmitSignal(NClickableControl.SignalName.Released,skip);
            await Wait(()=>!RewardChoicePatch.IsSelecting,"close roster verification chooser");
            await barrier("refresh-telemetry-verified");
        }
        session.FlushPersistence();
        Check(player.Deck.Cards.Count==160 && player.Potions.Count()==potionsAtStart+2,"only the two potion claims grant inventory; rerolls never claim cards or relics");
        var persisted=StateJson.Deserialize<Match>(File.ReadAllText(Path.Combine(session.SaveDirectory,"match.json")));
        Check(persisted.ProcessedCommands.Contains("perf-history-command-2999") && persisted.ConsumedRewards.SetEquals(session.Engine.State.ConsumedRewards),"full native refresh history and claims persist on disk");
        string report=StateJson.Serialize(new {Player=session.LocalId,Deck=player.Deck.Cards.Count,PotionGains=player.Potions.Count()-potionsAtStart,Samples=samples});
        RaceSession.AtomicWrite(Path.Combine(System.Environment.GetEnvironmentVariable("FDZF_REPORT_DIR")!,"late-refresh-"+session.LocalId+".json"),report);
        GD.Print("[FDZF REFRESH PERF REPORT] "+report);
        await NativeRewardNetworkPerformance.Finish(game,session,barrier,"refresh-complete");
    }
}
