using System.Diagnostics;
using FendaoZhengfeng.Core;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace FendaoZhengfeng.Mod;

internal static class NativeRewardChoicePerformance
{
    private sealed record Sample(string Kind, int Round, double OpenMs, double ReadyMs, double IntermediateMs, double CommitMs, Dictionary<string,double> Stages, Dictionary<string,double> NetworkBefore, Dictionary<string,double> NetworkAfter, Dictionary<string,double> CommitStages);
    private static async Task Wait(Func<bool> condition, string reason)
    { long until = System.Environment.TickCount64 + 45000; while (!condition()) { if (System.Environment.TickCount64 > until) throw new TimeoutException(reason); await Task.Delay(10); } }
    private static void Check(bool value, string reason)
    { if (!value) throw new InvalidOperationException(reason); GD.Print("[FDZF REWARD CHOICES PASS] " + reason); }
    private static Dictionary<string,double> NetworkStats(RaceSession session)
    {
        if(session.Transport is not INetClientGameService { NetClient: ENetClient client }
            || AccessTools.Field(typeof(ENetClient), "_peer").GetValue(client) is not ENetPacketPeer peer) return [];
        return new() {
            ["LossPercent"]=100d*peer.GetStatistic(ENetPacketPeer.PeerStatistic.PacketLoss)/ENetPacketPeer.PacketLossScale,
            ["RttMs"]=peer.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime),
            ["LastRttMs"]=peer.GetStatistic(ENetPacketPeer.PeerStatistic.LastRoundTripTime),
            ["RttVarianceMs"]=peer.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTimeVariance)
        };
    }
    private static void Click(NRewardButton button) => AccessTools.Method(typeof(NRewardButton), "OnRelease").Invoke(button, null);
    private static NRewardButton Choice(int index) => NativeChoiceUi.ActiveVeil!.FindChildren("*", "", true, false).OfType<NRewardButton>().ElementAt(index);
    internal static async Task Run(NGame game, RunState run, RaceSession session, Func<string, Task> barrier)
    {
        var player = run.Players.Single(); var samples = new List<Sample>();
        if (session.IsHost)
        {
            foreach(var racer in session.Engine.State.Racers.Values)
                for(int i=0;i<2;i++) racer.BossPrizes.Add("perf-choice-"+i+":first");
            await session.Submit(session.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(run)) });
        }
        await Wait(() => session.Me.BossPrizes.Contains("perf-choice-1:first"), "controlled mode prizes delivered");
        for(int round=0;round<8;round++)
        {
            string kind = new[]{"card","relic","sacrifice","boss_choice"}[round%4];
            var roomType = kind == "card" ? RoomType.Monster : kind == "boss_choice" ? RoomType.Boss : RoomType.Elite;
            await RunManager.Instance.EnterRoomDebug(roomType);
            await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "choice battle play phase");
            await DebugTesting.WinBattle();
            await Wait(() => !session.Me.InBattle && RaceRuntime.RewardRoom == run.CurrentRoom && RaceRuntime.CombatRewards is { } ready
                && (kind == "card" ? ready.Rewards.OfType<CardReward>().Any() : kind == "boss_choice" ? ready.Rewards.OfType<RaceActionReward>().Any()
                    : ready.Rewards.OfType<RelicReward>().Any()), "current room native choice rewards");
            Reward reward = kind == "card" ? RaceRuntime.CombatRewards!.Rewards.OfType<CardReward>().First()
                : kind == "boss_choice" ? RaceRuntime.CombatRewards!.Rewards.OfType<RaceActionReward>().First()
                : RaceRuntime.CombatRewards!.Rewards.OfType<RelicReward>().First();
            NRewardButton? button = null;
            await Wait(() => (button=game.GetTree().Root.FindChildren("*","",true,false).OfType<NRewardButton>().FirstOrDefault(b=>ReferenceEquals(b.Reward,reward))) != null, "real choice reward button");
            if(kind == "relic")
            {
                RelicModel fixedRelic = round < 4 ? ModelDb.Relic<Vajra>().ToMutable() : ModelDb.Relic<Anchor>().ToMutable();
                AccessTools.Field(typeof(RelicReward), "_relic").SetValue(reward, fixedRelic);
                AccessTools.Field(typeof(RelicReward), "_predeterminedRelic").SetValue(reward, fixedRelic);
                AccessTools.Method(typeof(NRewardButton), "Reload").Invoke(button, null);
            }
            string key=RaceRuntime.RewardKey(reward);int deckBefore=player.Deck.Cards.Count, relicsBefore=player.Relics.Count, shieldsBefore=session.Me.Shields;
            await barrier("choice-open-"+round);
            long opened=Stopwatch.GetTimestamp();Click(button!);
            NCardRewardSelectionScreen? cardScreen=null;
            if(kind=="card") await Wait(() => (cardScreen=game.GetTree().Root.FindChildren("*","",true,false).OfType<NCardRewardSelectionScreen>().FirstOrDefault(s=>s.IsVisibleInTree())) != null
                && AccessTools.Field(typeof(NCardRewardSelectionScreen),"_completionSource").GetValue(cardScreen)!=null, "native card chooser ready");
            else await Wait(() => NativeChoiceUi.ActiveVeil!=null, "native mode or relic choices ready");
            double openMs=Stopwatch.GetElapsedTime(opened).TotalMilliseconds, intermediate=0;
            if(kind=="card")
                await Wait(() => (bool)AccessTools.Field(typeof(NCardHolder), "_isClickable").GetValue(cardScreen!.GetCardHolder(((CardReward)reward).Cards.First()))!, "native card misclick protection expires");
            double readyMs=Stopwatch.GetElapsedTime(opened).TotalMilliseconds;
            if(kind=="sacrifice")
            {
                var first=NativeChoiceUi.ActiveVeil;long next=Stopwatch.GetTimestamp();Click(Choice(1));
                await Wait(() => NativeChoiceUi.ActiveVeil is { } veil && !ReferenceEquals(veil,first), "mixed effect candidates ready");
                intermediate=Stopwatch.GetElapsedTime(next).TotalMilliseconds;
            }
            await barrier("choice-commit-"+round);
            var networkBefore=NetworkStats(session);
            var stagesBefore=RewardPerformance.StagesForReward(reward);
            long commit=Stopwatch.GetTimestamp();
            if(kind=="card") cardScreen!.GetCardHolder(((CardReward)reward).Cards.First())._GuiInput(new InputEventAction { Action=MegaInput.select, Pressed=true });
            else if(kind=="sacrifice")
            {
                var offer=session.Engine.State.Offers[session.LocalId+":"+key+":"+Route.Mixed];
                int index=offer.Candidates.FindIndex(id=>Catalog.Get(id).Kind==EffectKind.Boon && id!="siphon");
                if(index<0) index=offer.Candidates.FindIndex(id=>Catalog.Get(id).Kind!=EffectKind.Boon);
                Click(Choice(Math.Max(0,index)));
            }
            else Click(Choice(kind=="boss_choice" ? 1 : 0));
            await Wait(() => reward.SuccessfullySelected && session.Engine.State.ConsumedRewards.Contains(session.LocalId+":"+key) && RewardPerformance.IsRewardClickComplete(reward), "selected reward, UI completion and authoritative receipt");
            double commitMs=Stopwatch.GetElapsedTime(commit).TotalMilliseconds;
            var stages=RewardPerformance.StagesForReward(reward);
            var commitStages=stages.ToDictionary(pair=>pair.Key,pair=>Math.Max(0,pair.Value-stagesBefore.GetValueOrDefault(pair.Key)));
            samples.Add(new(kind,round,openMs,readyMs,intermediate,commitMs,stages,networkBefore,NetworkStats(session),commitStages));
            Check(kind=="card" ? player.Deck.Cards.Count==deckBefore+1 : kind=="relic" ? player.Relics.Count==relicsBefore+1
                : kind=="boss_choice" ? session.Me.Shields==shieldsBefore+1 : session.Engine.State.Offers[session.LocalId+":"+key+":"+Route.Mixed].Resolved,
                kind+" actual native reward commits once, round "+round);
            await Wait(() => !NativeChoiceUi.IsOpen, "completed native choice veil cleanup");
            Check(!await reward.SelectUnsynchronized() && !NativeChoiceUi.IsOpen, kind+" stale selection cannot reopen or duplicate the reward");
            await barrier("choice-done-"+round);
        }
        await barrier("wire-repair-ready");
        if (!RewardPerformance.LegacyPathForVerification && !session.IsHost)
        {
            int repairs=session.StateRepairs;
            AccessTools.Field(typeof(RaceSession), "receivedWireJson").SetValue(session,null);
            await session.Submit(session.New("claim","wire-repair:"+session.LocalId));
            Check(session.StateRepairs>repairs && session.StateRepairRequests>0,
                "missing delta baseline requests a full snapshot and completes the original command");
        }
        await barrier("wire-repair-complete");
        session.FlushPersistence();
        var persisted=StateJson.Deserialize<Match>(File.ReadAllText(Path.Combine(session.SaveDirectory,"match.json")));
        Check(persisted.ProcessedCommands.Contains("perf-history-command-2999")
            && persisted.ConsumedRewards.SetEquals(session.Engine.State.ConsumedRewards),
            "disk archive retains complete historical command data after wire deltas");
        var report=StateJson.Serialize(new { Player=session.LocalId, StartDeck=160, EndDeck=player.Deck.Cards.Count, StateBroadcasts=session.StateBroadcastCount, DeltaBroadcasts=session.DeltaBroadcastCount, WireBytes=session.StateWireBytes, RepairRequests=session.StateRepairRequests, Repairs=session.StateRepairs, Samples=samples });
        RaceSession.AtomicWrite(Path.Combine(System.Environment.GetEnvironmentVariable("FDZF_REPORT_DIR")!,"late-choices-"+session.LocalId+".json"),report);
        GD.Print("[FDZF REWARD CHOICES REPORT] "+report);
        await NativeRewardNetworkPerformance.Finish(game, session, barrier, "choice-complete");
    }
}
