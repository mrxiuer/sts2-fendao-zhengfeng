using FendaoZhengfeng.Core;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace FendaoZhengfeng.Mod;

internal static class NativeEventRecoveryVerification
{
    private sealed record Expected(string BattleKey, long Battle, int Wins, int Hp, int MaxHp, int Gold, string[] Deck, string RoomKey, string? FinishedDescription);
    private static void Check(bool ok, string reason)
    { if (!ok) throw new InvalidOperationException(reason); GD.Print("[FDZF EVENT RECOVERY PASS] " + reason); }
    private static async Task Wait(Func<bool> predicate, string reason)
    { long until = System.Environment.TickCount64 + 45000; while (!predicate()) { if (System.Environment.TickCount64 > until) throw new TimeoutException(reason); await Task.Delay(30); } }
    internal static async Task Run(NGame game, RunState run, RaceSession session, bool host, bool resume)
    {
        bool inProgress=System.Environment.GetEnvironmentVariable("FDZF_VERIFY_EVENT_IN_PROGRESS")=="1";
        bool midStep=false,saveStarted=false;int midHp=0,midGold=0;
        var phases = new HashSet<string>();
        session.Transport.RegisterMessageHandler<RaceMessage>((message, sender) =>
        { if(sender==session.Transport.NetId)return;
          if(message.Kind=="event_barrier")phases.Add(message.Content);
          if(message.Kind=="event_midstep") {var values=StateJson.Deserialize<int[]>(message.Content);midHp=values[0];midGold=values[1];midStep=true;}
          if(message.Kind=="event_save_started")saveStarted=true;
        });
        async Task Barrier(string phase)
        {
            long until = System.Environment.TickCount64 + 45000;
            do
            {
                session.Transport.SendMessage(new RaceMessage { Kind = "event_barrier", Content = phase }); await Task.Delay(100);
                if (System.Environment.TickCount64 > until) throw new TimeoutException("event peers: " + phase);
            } while (!phases.Contains(phase));
            session.Transport.SendMessage(new RaceMessage { Kind = "event_barrier", Content = phase });
        }
        string? openPage = System.Environment.GetEnvironmentVariable("FDZF_VERIFY_EVENT_PAGE");
        bool goldEvent = System.Environment.GetEnvironmentVariable("FDZF_VERIFY_EVENT_GOLD") == "1";
        var player = run.Players.Single();
        string expectedPath = Path.Combine(System.Environment.GetEnvironmentVariable("FDZF_REPORT_DIR")!, "event-" + session.LocalId + ".json");
        if (!resume)
        {
            await CreatureCmd.SetMaxAndCurrentHp(player.Creature, 500);
            var point = run.Map.StartingMapPoint.Children.OrderBy(point => point.coord.ToString(), StringComparer.Ordinal).First();
            await RunManager.Instance.EnterMapCoord(point.coord);
            await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "initial battle");
            await DebugTesting.WinBattle(); await Wait(() => !session.Me.InBattle, "initial victory");
            await RelicCmd.Obtain<MawBank>(player);
            if (!host && openPage != null)
            {
                await CreatureCmd.SetCurrentHp(player.Creature, 277);
                await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: ModelDb.Event<DenseVegetation>());
                await Wait(() => run.CurrentRoom is EventRoom page && page.LocalMutableEvent.CurrentOptions.Count == 2, "native initial vegetation page");
                if (openPage == "Rest")
                {
                    RunManager.Instance.EventSynchronizer.ChooseLocalOption(1);
                    await Wait(() => run.CurrentRoom is EventRoom page && page.LocalMutableEvent.CurrentOptions.Count == 1, "rest heals and opens fight page");
                    Check(player.Creature.CurrentHp > 277, "rest actually heals before saving the unfinished page");
                }
                Check(run.CurrentRoom is EventRoom page && !page.LocalMutableEvent.IsFinished, "saving a real unfinished page");
            }
            else if (!host && goldEvent)
            {
                await CreatureCmd.SetCurrentHp(player.Creature, 377);
                int beforeGold = player.Gold;
                await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: ModelDb.Event<DenseVegetation>());
                await Wait(() => run.CurrentRoom is EventRoom page && page.LocalMutableEvent.CurrentOptions.Count == 2, "native dense vegetation event");
                if(inProgress) {
                    bool held=false;
                    NativeEventStep.AfterGoldForVerification=async owner=> {
                        if(!ReferenceEquals(owner,session) || held)return;held=true;
                        Check(player.Creature.CurrentHp==369 && player.Gold>beforeGold && !((EventRoom)run.CurrentRoom!).LocalMutableEvent.IsFinished,
                            "real native event has paid HP and gold but has not finished its option");
                        Check(session.Recovery.SendSnapshot()==null,"partial event cannot become a periodic recovery backup");
                        session.Transport.SendMessage(new RaceMessage {Kind="event_midstep",Content=StateJson.Serialize(new[]{player.Creature.CurrentHp,player.Gold+5})});
                        await Wait(()=>saveStarted,"host starts saving while native event callback is unfinished");
                        Check(session.Recovery.NativeGrantBlocked && !session.Recovery.Frozen,"client drains its already-started event before freezing");
                        var nested=new GoldReward(5,player);
                        Check(await nested.SelectUnsynchronized() && nested.SuccessfullySelected,
                            "native reward inside the event returns without waiting on its enclosing step");
                        Check(NativeClaimJournal.Capture(session).Any(c=>c.Key==RaceRuntime.RewardKey(nested))
                            && NativeEventStep.IsApplying(session) && session.Recovery.SendSnapshot()==null,
                            "unfinished event keeps nested claim pending and cannot publish a partial page");

                    };
                }
                try {
                    RunManager.Instance.EventSynchronizer.ChooseLocalOption(0);
                    await Wait(() => run.CurrentRoom is EventRoom page && page.LocalMutableEvent.IsFinished && !NativeEventStep.IsApplying(session), "actual gold and life-loss event completes");
                    if(inProgress) {
                        await Wait(()=>session.Recovery.Frozen,"completed event reaches the held save checkpoint");
                        int stableGold=player.Gold,stableHp=player.Creature.CurrentHp;
                        RunManager.Instance.EventSynchronizer.ChooseLocalOption(0);
                        Check(player.Gold==stableGold && player.Creature.CurrentHp==stableHp && !NativeEventStep.IsApplying(session),
                            "new event input is blocked during the held save without invoking a finished option");
                        await Barrier("frozen-event");
                        await Wait(()=>!session.Recovery.NativeGrantBlocked && NativeClaimJournal.Capture(session).Length==0,
                            "event and nested native claim finish after the save releases play");
                    }
                } finally {NativeEventStep.AfterGoldForVerification=null;}
                Check(player.Creature.CurrentHp == 369 && player.Gold > beforeGold,
                    "finished ordinary event really pays gold and deducts native HP before save");
            }
            else
            {
                await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: ModelDb.Event<BattlewornDummy>());
                await Wait(() => run.CurrentRoom is EventRoom room && room.LocalMutableEvent.CurrentOptions.Count == 3, "real battleworn dummy event");
                RunManager.Instance.EventSynchronizer.ChooseLocalOption(1);
                await Wait(() => run.CurrentRoom is CombatRoom { ParentEventId: not null }
                    && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "actual event enters setting two combat");
                Check(run.CurrentRoomCount == 2 && run.CurrentRoom is CombatRoom { ShouldResumeParentEventAfterCombat: true },
                    "native event creates a real parent-plus-combat stack");
                await CreatureCmd.SetCurrentHp(player.Creature, host ? 333 : 377);
                if (!host)
                {
                    await DebugTesting.WinBattle();
                    await Wait(() => !session.Me.InBattle && run.CurrentRoom is EventRoom room && room.LocalMutableEvent.IsFinished
                        && player.Deck.Cards.Count(card => card.CurrentUpgradeLevel > 0) == 2,
                        "client automatically returns to the finished event with its real upgrade reward");
                }
            }
            if(inProgress && host) {
                await Wait(()=>midStep,"client native event reaches the HP/gold stop point");
                var saving=session.Recovery.SaveHost(true);
                Check(session.Recovery.IsDraining && !session.Recovery.Frozen && !saving.IsCompleted,
                    "host waits for ongoing event callbacks before freezing its archive");
                session.Transport.SendMessage(new RaceMessage {Kind="event_save_started"});await saving;
                var archive=RaceRecovery.Read(session.LocalId);var remote=archive.Players["2"];
                var rebuilt=NativeCheckpoint.Restore(new NativeCheckpoint.Bundle("0.5.0",archive.RulesVersion,0,archive.MatchJson,remote.NativePacket,remote.AppliedReceipts,remote.BattleKey,remote.Rewards));
                Check(rebuilt.Run.Players.Single().Gold==midGold && rebuilt.Run.Players.Single().Creature.CurrentHp==midHp
                    && rebuilt.Save.PreFinishedRoom!.EncounterState!.ContainsKey(EventRecovery.FinishedKey),
                    "actual mid-event save contains final native resources and the finished page together");
                await Barrier("frozen-event");session.Recovery.Release();
                session.Transport.SendMessage(new RaceMessage {Kind="recovery_release"});
            }
            var expected = new Expected(session.Me.BattleKey, session.Me.Battle, session.Me.VictoryCount,
                player.Creature.CurrentHp, player.Creature.MaxHp, player.Gold, player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).ToArray(), RaceRuntime.RoomKey,
                run.CurrentRoom is EventRoom done ? done.LocalMutableEvent.Description?.GetFormattedText() : null);
            RaceSession.AtomicWrite(expectedPath, StateJson.Serialize(expected));
            session.Recovery.SendSnapshot(); session.Recovery.SuspendPeriodicCaptureForVerification = true;
            await Barrier("snapshots");
            if (host)
            {
                await Wait(() => session.Recovery.BackupCount == 2 && session.Transport is NetHostGameService service && service.ConnectedPeers.Count == 0,
                    "event client disconnects after snapshot");
                await session.Recovery.SaveHost();
                Check(RaceRecovery.Read("1").Players.Count == 2, "host owns unfinished and completed event battle backups");
            }
            else { await Task.Delay(300); session.Transport.Disconnect(MegaCrit.Sts2.Core.Entities.Multiplayer.NetError.Quit); }
        }
        else
        {
            var expected = StateJson.Deserialize<Expected>(File.ReadAllText(expectedPath));
            if (host) await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "restored unfinished nested fight");
            else await Wait(() => run.CurrentRoom is EventRoom room && (openPage != null ? !room.LocalMutableEvent.IsFinished : room.LocalMutableEvent.IsFinished), "restored saved event page");
            Check(host ? run.CurrentRoomCount == 2 && run.CurrentRoom is CombatRoom { ParentEventId: not null, ShouldResumeParentEventAfterCombat: true }
                : run.CurrentRoomCount == 1 && ((EventRoom)run.CurrentRoom!).LocalMutableEvent.CurrentOptions.Count == (openPage == "Rest" ? 1 : openPage == "Initial" ? 2 : 0),
                "restored room stack and saved option count are exact");
            Check(RaceRuntime.RoomKey == expected.RoomKey && session.Me.Battle == expected.Battle && session.Me.VictoryCount == expected.Wins,
                "restored event or nested fight keeps its identity and does not add a victory");
            Check(player.Creature.CurrentHp == expected.Hp && player.Creature.MaxHp == expected.MaxHp && player.Gold == expected.Gold
                && player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).SequenceEqual(expected.Deck),
                "restoring the saved event does not grant upgrades or alter native resources");
            if (!host && openPage != null)
            {
                var room = (EventRoom)run.CurrentRoom!;
                Check(StateJson.Serialize(EventRecovery.CaptureOpen(room)) == session.Restored!.Value.Save.PreFinishedRoom!.EncounterState![EventRecovery.OpenKey],
                    "unfinished page text identity dynamic values and full RNG exactly match archive");
                int hpBefore = player.Creature.CurrentHp, goldBefore = player.Gold;
                decimal reward = room.LocalMutableEvent.DynamicVars.Gold.BaseValue;
                RunManager.Instance.EventSynchronizer.ChooseLocalOption(0);
                if (openPage == "Rest")
                {
                    await Wait(() => run.CurrentRoom is CombatRoom && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "restored fight option really starts native combat");
                    Check(player.Creature.CurrentHp == hpBefore, "restored rest page does not heal again when entering combat");
                    await DebugTesting.WinBattle(); await Wait(() => !session.Me.InBattle, "restored page combat settles");
                    Check(session.Me.VictoryCount == expected.Wins + 1, "restored page fight grants exactly one victory");
                }
                else
                {
                    await Wait(() => room.LocalMutableEvent.IsFinished, "restored initial option completes native event");
                    Check(player.Creature.CurrentHp == hpBefore - 8 && player.Gold == goldBefore + (int)reward,
                        "restored initial option pays its saved exact gold and life cost once");
                }
                await RunManager.Instance.EnterRoomDebug(RoomType.Monster);
                await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "next battle after saved open page");
                await Barrier("restored");
                GD.Print("[FDZF EVENT RECOVERY COMPLETE] " + session.LocalId + " resume=" + resume); game.GetTree().Quit(0); return;
            }
            if (host)
            {
                Check(RaceModifier.Active!.Turn == 1, "unfinished nested battle restarts on turn one");
                await DebugTesting.WinBattle();
                await Wait(() => !session.Me.InBattle && run.CurrentRoom is EventRoom done && done.LocalMutableEvent.IsFinished,
                    "resumed nested fight wins and automatically returns to its actual parent");
            }
            if (!host) Check(((EventRoom)run.CurrentRoom!).LocalMutableEvent.Description?.GetFormattedText() == expected.FinishedDescription,
                "already-finished event restores its saved final page instead of regenerating initial choices");
            if (!host)
                Check(StateJson.Serialize(EventRecovery.CaptureFinished((EventRoom)run.CurrentRoom!))
                    == session.Restored!.Value.Save.PreFinishedRoom!.EncounterState![EventRecovery.FinishedKey],
                    "finished page identity text and full event RNG state exactly match the host archive");
            int expectedUpgrades = !host && goldEvent ? 0 : 2;
            await Wait(() => run.CurrentRoom is EventRoom room && room.LocalMutableEvent.IsFinished
                && player.Deck.Cards.Count(card => card.CurrentUpgradeLevel > 0) == expectedUpgrades, "actual finished page and nonduplicated rewards");
            Check(run.CurrentRoomCount == 1 && session.Me.VictoryCount == expected.Wins + (host ? 1 : 0),
                "returning to the event upgrades two cards once without duplicate battle settlement");
            var after = player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).ToArray();
            int hp = player.Creature.CurrentHp, gold = player.Gold;
            await RunManager.Instance.ProceedFromTerminalRewardsScreen();
            Check(player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).SequenceEqual(after) && player.Gold == gold && player.Creature.CurrentHp == hp,
                "proceeding again from the finished event cannot repeat its upgrade gold or life loss");
            int nextRoomGold = player.Gold;
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster);
            await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "next battle after restored event");
            Check(player.Gold == nextRoomGold + 12, "a later fresh room still grants native MawBank income exactly once");
            Check(session.Me.BattleKey != expected.BattleKey, "next combat after returning to parent gets a new identity");
            await Barrier("restored");
        }
        GD.Print("[FDZF EVENT RECOVERY COMPLETE] " + session.LocalId + " resume=" + resume); game.GetTree().Quit(0);
    }
}
