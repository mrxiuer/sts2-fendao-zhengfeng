using System.Diagnostics;
using FendaoZhengfeng.Core;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace FendaoZhengfeng.Mod;

internal static class NativeRewardNetworkPerformance
{
    private sealed record Sample(int Round, double Milliseconds, Dictionary<string, double> Stages);
    private static async Task Wait(Func<bool> condition, string reason)
    { long until = System.Environment.TickCount64 + 45000; while (!condition()) { if (System.Environment.TickCount64 > until) throw new TimeoutException(reason); await Task.Delay(10); } }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); GD.Print("[FDZF REWARD NETWORK PASS] " + message); }
    internal static async Task Finish(NGame game, RaceSession session, Func<string,Task> barrier, string phase)
    {
        await barrier(phase);
        if (session.IsHost)
        {
            var host=(NetHostGameService)session.Transport;
            var enet=(ENetHost)AccessTools.Field(typeof(NetHostGameService), "_netHost").GetValue(host)!;
            var connection=(ENetConnection)AccessTools.Field(typeof(ENetHost), "_connection").GetValue(enet)!;
            var closingPeers=connection.GetPeers().ToArray();
            long until=System.Environment.TickCount64+45000;
            while(host.ConnectedPeers.Count>0 || closingPeers.Any(peer=>peer.GetState()!=ENetPacketPeer.PeerState.Disconnected))
            {
                if(System.Environment.TickCount64>until) throw new TimeoutException("performance peers did not acknowledge completion by closing");
                if(host.ConnectedPeers.Count>0) session.Transport.SendMessage(new RaceMessage {Kind="perf_release",Content=phase});
                await Task.Delay(30);
            }
        }
        else if (session.Transport is INetClientGameService { NetClient: ENetClient client }
            && AccessTools.Field(typeof(ENetClient), "_peer").GetValue(client) is ENetPacketPeer peer)
        {
            // Native DisconnectFromHost flushes once then destroys its socket,
            // so its unreliable quit notice can be lost. Keep servicing the real
            // ENet connection until the reliable transport disconnect completes.
            // The native host removes application peers only on its own quit
            // packet, not merely an ENet disconnect event. Deliver that packet
            // reliably and let both ends finish servicing the transport handshake.
            var quit=ENetPacket.FromDisconnection(new ENetDisconnection {
                reason=MegaCrit.Sts2.Core.Entities.Multiplayer.NetError.Quit });
            if(peer.Send(0,quit.AllBytes,1)!=Error.Ok) throw new IOException("reliable test quit notice failed");
            peer.PeerDisconnectLater();
            await Wait(() => peer.GetState() == ENetPacketPeer.PeerState.Disconnected,
                "acknowledged ENet disconnect before closing the test process");
            session.Transport.Disconnect(MegaCrit.Sts2.Core.Entities.Multiplayer.NetError.Quit, now:true);
        }
        GD.Print("[FDZF REWARD NETWORK COMPLETE] "+session.LocalId);
        session.Dispose();
        await Task.Delay(100);
        game.GetTree().Quit(0);
    }
    internal static async Task Run(NGame game, RunState run, RaceSession session)
    {
        // Isolated A/B only: explicit reward/node checkpoints and host-save requests
        // remain active. This switch measures the 3-second redundant recovery traffic.
        session.Recovery.SuspendPeriodicCaptureForVerification =
            System.Environment.GetEnvironmentVariable("FDZF_VERIFY_NO_PERIODIC_BACKUP") == "1";
        var player = run.Players.Single(); var arrivals = new Dictionary<string, HashSet<ulong>>(); var released = new HashSet<string>();
        session.Transport.RegisterMessageHandler<RaceMessage>((message, sender) =>
        {
            if (message.Kind == "perf_arrive" && session.IsHost && session.Engine.State.Racers.ContainsKey(sender.ToString()))
            { if (!arrivals.TryGetValue(message.Content, out var peers)) arrivals[message.Content] = peers = []; peers.Add(sender); }
            if (message.Kind == "perf_release" && sender == session.HostId) released.Add(message.Content);
        });
        async Task Barrier(string phase)
        {
            if (session.IsHost)
            {
                if (!arrivals.TryGetValue(phase, out var peers)) arrivals[phase] = peers = [];
                peers.Add(session.Transport.NetId); await Wait(() => peers.Count == 4, "four ready " + phase);
                session.Transport.SendMessage(new RaceMessage { Kind = "perf_release", Content = phase }); return;
            }
            long until = System.Environment.TickCount64 + 45000;
            do { session.Transport.SendMessage(new RaceMessage { Kind = "perf_arrive", Content = phase }); await Task.Delay(30);
                if (System.Environment.TickCount64 > until) throw new TimeoutException("host release " + phase);
            } while (!released.Contains(phase));
        }
        await CreatureCmd.SetMaxAndCurrentHp(player.Creature, 5000);
        while (player.Deck.Cards.Count < 160)
        {
            var card = run.CreateCard(ModelDb.Card<StrikeIronclad>(), player);
            if (player.Deck.Cards.Count % 2 == 0) CardCmd.Upgrade(card);
            await CardPileCmd.Add(card, PileType.Deck, skipVisuals: true);
        }
        await session.Submit(session.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(run)) });
        await Barrier("profiles");
        if (session.IsHost)
        {
            foreach (var racer in session.Engine.State.Racers.Values)
            {
                foreach (var id in new[] { "sharpen", "light_step", "war_feeds_war", "adversity_profit", "battle_hardened", "encore_rewards", "second_forge", "resonant_circuit", "piercing_edge", "exhaustion", "imbalance", "abet" })
                    racer.Effects[id] = new() { Id = id, Grants = [new() { EventId = "perf:" + id, Source = racer.Id, ActiveBattle = 0 }] };
                for (int i = 0; i < 120; i++) { racer.WonBattles.Add("perf-history-" + i); racer.UsedServices.Add("perf-used-" + i); }
                racer.VictoryCount = 120;
            }
            for (int i = 0; i < 3000; i++) session.Engine.State.ProcessedCommands.Add("perf-history-command-" + i);
            await session.Submit(session.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(run)) });
        }
        await Wait(() => session.Engine.State.ProcessedCommands.Contains("perf-history-command-2999"), "large four-player state arrives");
        Check(session.Engine.State.Racers.Values.All(r => r.Effects.Count >= 12 && r.MarketCards.Count >= 160), "four peers have large native decks and twelve effects each");
        if (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_REWARD_REFRESH") == "1")
        { await NativeRewardRefreshPerformance.Run(game, run, session, Barrier); return; }
        if (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_REWARD_CHOICES") == "1")
        { await NativeRewardChoicePerformance.Run(game, run, session, Barrier); return; }
        var samples = new List<Sample>();
        for (int round = 0; round < 12; round++)
        {
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster);
            await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "native combat turn");
            await DebugTesting.WinBattle();
            await Wait(() => !session.Me.InBattle && RaceRuntime.CombatRewards?.Rewards.OfType<GoldReward>().Any(r => !r.SuccessfullySelected) == true, "native gold reward");
            var gold = RaceRuntime.CombatRewards!.Rewards.OfType<GoldReward>().First(r => !r.SuccessfullySelected);
            NRewardButton? button = null;
            await Wait(() => (button = game.GetTree().Root.FindChildren("*", "", true, false).OfType<NRewardButton>().FirstOrDefault(b => ReferenceEquals(b.Reward, gold))) != null, "native clickable gold button");
            string key = RaceRuntime.RewardKey(gold); int before = player.Gold, clicks = RewardPerformance.CompletedClicks;
            await Barrier("click-" + round);
            long started = Stopwatch.GetTimestamp();
            AccessTools.Method(typeof(NRewardButton), "OnRelease").Invoke(button, null);
            await Wait(() => gold.SuccessfullySelected && session.Engine.State.ConsumedRewards.Contains(session.LocalId + ":" + key)
                && RewardPerformance.CompletedClicks > clicks, "native selection and authoritative receipt");
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            samples.Add(new(round, elapsed, RewardPerformance.LastClickStages()));
            Check(player.Gold > before && !await gold.SelectUnsynchronized(), "native receipt prevents duplicate gold round " + round);
            await Barrier("done-" + round);
        }
        session.FlushPersistence();
        var sorted = samples.Select(s => s.Milliseconds).Order().ToArray();
        string report = StateJson.Serialize(new { Player = session.LocalId, Deck = player.Deck.Cards.Count, MatchBytes = System.Text.Encoding.UTF8.GetByteCount(StateJson.Serialize(session.Engine.State)),
            StateBroadcasts=session.StateBroadcastCount, DeltaBroadcasts=session.DeltaBroadcastCount, WireBytes=session.StateWireBytes, Median = sorted[sorted.Length / 2], P95 = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], Maximum = sorted[^1], Samples = samples });
        RaceSession.AtomicWrite(Path.Combine(System.Environment.GetEnvironmentVariable("FDZF_REPORT_DIR")!, "late-rewards-" + session.LocalId + ".json"), report);
        GD.Print("[FDZF REWARD NETWORK REPORT] " + report);
        await Finish(game, session, Barrier, "complete");
    }
}
