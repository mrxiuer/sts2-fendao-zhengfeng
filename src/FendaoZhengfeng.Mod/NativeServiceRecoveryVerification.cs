using FendaoZhengfeng.Core;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace FendaoZhengfeng.Mod;

internal static class NativeServiceRecoveryVerification
{
    private sealed record Expected(SavedCamp Camp, int Hp, int MaxHp, int Gold, string[] Deck, string[] Services,
        int Victories, int RewardsRng, int ShopsRng, int TransformRng);
    private static void Check(bool ok, string reason)
    { if (!ok) throw new InvalidOperationException(reason); GD.Print("[FDZF SERVICE RECOVERY PASS] " + reason); }
    private static async Task Wait(Func<bool> predicate, string reason)
    { long until = System.Environment.TickCount64 + 60000; while (!predicate()) { if (System.Environment.TickCount64 > until) throw new TimeoutException(reason); await Task.Delay(30); } }
    private static async Task<bool> Select(RestSiteOption option)
    {
        var room = NRestSiteRoom.Instance!;
        var button = room.GetButtonForOption(option) ?? throw new InvalidOperationException("Missing native camp button.");
        await (Task)AccessTools.Method(typeof(NRestSiteButton), "SelectOption").Invoke(button, [option])!;
        return !room.Options.Contains(option);
    }
    internal static async Task Run(NGame game, RunState run, RaceSession session, bool host, bool resume)
    {
        var player = run.Players.Single(); var released = new HashSet<string>();
        var arrivals = new Dictionary<string, HashSet<ulong>>();
        session.Transport.RegisterMessageHandler<RaceMessage>((message, sender) =>
        {
            if (message.Kind == "service_arrive" && host && session.Engine.State.Racers.ContainsKey(sender.ToString()))
            { if (!arrivals.TryGetValue(message.Content, out var peers)) arrivals[message.Content] = peers = []; peers.Add(sender); }
            if (message.Kind == "service_release" && sender == session.HostId) released.Add(message.Content);
        });
        async Task Barrier(string phase)
        {
            if (host)
            {
                if (!arrivals.TryGetValue(phase, out var peers)) arrivals[phase] = peers = [];
                peers.Add(session.Transport.NetId); await Wait(() => peers.Count == 4, "four service peers: " + phase);
                session.Transport.SendMessage(new RaceMessage { Kind = "service_release", Content = phase });
                await Task.Delay(200); return;
            }
            long until = System.Environment.TickCount64 + 60000;
            while (!released.Contains(phase))
            {
                session.Transport.SendMessage(new RaceMessage { Kind = "service_arrive", Content = phase }); await Task.Delay(100);
                if (System.Environment.TickCount64 > until) throw new TimeoutException("service barrier: " + phase);
            }
        }
        string expectedPath = Path.Combine(System.Environment.GetEnvironmentVariable("FDZF_REPORT_DIR")!, "camp-" + session.LocalId + ".json");
        if (!resume)
        {
            await CreatureCmd.SetMaxAndCurrentHp(player.Creature, 500);
            var point = run.Map.StartingMapPoint.Children.OrderBy(point => point.coord.ToString(), StringComparer.Ordinal).First();
            await RunManager.Instance.EnterMapCoord(point.coord);
            await Wait(() => player.PlayerCombatState?.Phase == MegaCrit.Sts2.Core.Combat.PlayerTurnPhase.Play, "first service fixture battle");
            await DebugTesting.WinBattle(); await Wait(() => !session.Me.InBattle, "service fixture victory");
            await RelicCmd.Obtain<Shovel>(player);
            await RelicCmd.Obtain<EternalFeather>(player);
            await CreatureCmd.SetCurrentHp(player.Creature, 250);
            await Barrier("before-camp");
            if (host)
            {
                foreach (var racer in session.Engine.State.Racers.Values)
                    racer.Effects["exhaustion"] = new() { Id = "exhaustion", Grants = [new() { EventId = "camp-fixture", ActiveBattle = racer.Battle, Source = "1" }] };
                await session.Submit(session.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(run)) });
            }
            await Wait(() => session.Me.Effects.ContainsKey("exhaustion"), "native mode cleanse premise");
            await RunManager.Instance.EnterRoomDebug(RoomType.RestSite);
            var camp = (RestSiteRoom)run.CurrentRoom!;
            var smith = camp.Options.OfType<SmithRestSiteOption>().Single(); smith.SmithCount = 2;
            Check(camp.Options.Any(option => option is DigRestSiteOption) && camp.Options.Any(option => option is RaceRestOption),
                "native shovel and mode cleanse options exist alongside rest and smith");
            if (host)
            {
                int index = camp.Options.ToList().FindIndex(option => option is HealRestSiteOption);
                int beforeRest = player.Creature.CurrentHp;
                Check(await Select(camp.Options[index]) && player.Creature.CurrentHp == Math.Min(500, beforeRest + 150),
                    "host chooses actual native rest once before saving");
            }
            else if (session.LocalId == "3")
            {
                int index = camp.Options.ToList().FindIndex(option => option is SmithRestSiteOption);
                var selector = new TestCardSelector(); selector.PrepareToSelect([0, 1]);
                using (CardSelectCmd.UseSelector(selector))
                    Check(await Select(camp.Options[index])
                        && player.Deck.Cards.Count(card => card.CurrentUpgradeLevel > 0) == 2,
                        "third peer uses native two-card smith before saving");
            }
            else if (session.LocalId == "4")
            {
                int index = camp.Options.ToList().FindIndex(option => option.OptionId == "FDZF_CLEANSE");
                var choice = Select(camp.Options[index]);
                await Wait(() => NativeChoiceUi.ActiveVeil != null, "native cleanse picker");
                var row = NativeChoiceUi.ActiveVeil!.FindChildren("*", "", true, false).OfType<NRewardButton>().First();
                AccessTools.Method(typeof(NRewardButton), "OnRelease").Invoke(row, null);
                Check(await choice && !session.Me.Effects.ContainsKey("exhaustion")
                    && session.Me.UsedServices.Contains("camp:" + RaceRuntime.RoomKey),
                    "fourth peer cleanses through actual native option and commits its once-only service");
            }
            Check(session.LocalId == "2" ? camp.Options.Count == 4 : camp.Options.Count == 0,
                "unselected peer retains four options and selected peers have no remaining choices");
            if (session.LocalId != "2")
            {
                session.FlushPersistence();
                var checkpoint = StateJson.Deserialize<NativeCheckpoint.Bundle>(File.ReadAllText(Path.Combine(session.SaveDirectory, "checkpoint.json")));
                var reader = new PacketReader(); reader.Reset(Convert.FromBase64String(checkpoint.NativePacket));
                var savedRun = new SerializableRun(); savedRun.Deserialize(reader);
                string actual = savedRun.PreFinishedRoom!.EncounterState![CampRecovery.SaveKey];
                Check(actual == StateJson.Serialize(CampRecovery.Capture(camp)),
                    "automatic camp checkpoint contains committed consumed options rather than the pre-commit list");
            }
            var expected = new Expected(CampRecovery.Capture(camp), player.Creature.CurrentHp, player.Creature.MaxHp, player.Gold,
                player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).ToArray(), session.Me.UsedServices.Order().ToArray(),
                session.Me.VictoryCount, player.PlayerRng.Rewards.ToSerializable().counter, player.PlayerRng.Shops.ToSerializable().counter,
                player.PlayerRng.Transformations.ToSerializable().counter);
            RaceSession.AtomicWrite(expectedPath, StateJson.Serialize(expected));
            session.Recovery.SendSnapshot(); session.Recovery.SuspendPeriodicCaptureForVerification = true;
            await Barrier("snapshot");
            if (host)
            {
                await Wait(() => session.Recovery.BackupCount == 4 && session.Transport is NetHostGameService service && service.ConnectedPeers.Count == 0,
                    "three service clients disconnected after backing up");
                await session.Recovery.SaveHost();
                Check(RaceRecovery.Read("1").Players.Count == 4, "host archive contains all four independent camp states");
            }
            else { await Task.Delay(300); session.Transport.Disconnect(MegaCrit.Sts2.Core.Entities.Multiplayer.NetError.Quit); }
        }
        else
        {
            var expected = StateJson.Deserialize<Expected>(File.ReadAllText(expectedPath));
            await Wait(() => run.CurrentRoom is RestSiteRoom && NRestSiteRoom.Instance != null, "restored native camp");
            var camp = (RestSiteRoom)run.CurrentRoom!;
            Check(StateJson.Serialize(CampRecovery.Capture(camp)) == StateJson.Serialize(expected.Camp),
                "remaining native and mode options, smith amount, identity and completion state restore exactly");
            Check(player.Creature.CurrentHp == expected.Hp && player.Creature.MaxHp == expected.MaxHp && player.Gold == expected.Gold
                && player.Deck.Cards.Select(NativeCheckpoint.EncodeCard).SequenceEqual(expected.Deck),
                "camp reload does not repeat healing upgrade or any native resource gain");
            Check(session.Me.UsedServices.Order().SequenceEqual(expected.Services) && session.Me.VictoryCount == expected.Victories,
                "mode service identity and victory count remain exact");
            Check(player.PlayerRng.Rewards.ToSerializable().counter == expected.RewardsRng
                && player.PlayerRng.Shops.ToSerializable().counter == expected.ShopsRng
                && player.PlayerRng.Transformations.ToSerializable().counter == expected.TransformRng,
                "camp restoration does not regenerate options or advance gameplay RNG counters");
            if (session.LocalId == "2")
            {
                int index = camp.Options.ToList().FindIndex(option => option is SmithRestSiteOption);
                var selector = new TestCardSelector(); selector.PrepareToSelect([0, 1]);
                using (CardSelectCmd.UseSelector(selector))
                    Check(await Select(camp.Options[index])
                        && player.Deck.Cards.Count(card => card.CurrentUpgradeLevel > 0) == 2,
                        "restored modified smith option actually upgrades two selected native cards");
            }
            else
                Check(camp.Options.Count == 0, "already completed camp cannot offer rest smith or mode cleanse again");
            await Wait(() => NRestSiteRoom.Instance!.ProceedButton.IsEnabled, "restored completed camp proceed button");
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster);
            await Wait(() => player.PlayerCombatState?.Phase == MegaCrit.Sts2.Core.Combat.PlayerTurnPhase.Play, "next room after restored camp");
            await DebugTesting.WinBattle(); await Wait(() => !session.Me.InBattle, "next room victory after restored camp");
            Check(session.Me.VictoryCount == expected.Victories + 1, "restored camp exits without hanging and next battle settles once");
            await Barrier("completed");
        }
        GD.Print("[FDZF SERVICE RECOVERY COMPLETE] " + session.LocalId + " resume=" + resume); game.GetTree().Quit(0);
    }
}
