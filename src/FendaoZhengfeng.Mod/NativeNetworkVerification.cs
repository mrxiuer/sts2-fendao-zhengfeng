using FendaoZhengfeng.Core;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Test;
using MegaCrit.Sts2.Core.Unlocks;

namespace FendaoZhengfeng.Mod;

internal static class NativeNetworkVerification
{
    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); GD.Print("[FDZF NETWORK PASS] " + name); }
    private static async Task Wait(Func<bool> predicate, string name, int milliseconds = 45000)
    { long until = System.Environment.TickCount64 + milliseconds; while (!predicate()) { if (System.Environment.TickCount64 > until) throw new TimeoutException(name); await Task.Delay(30); } }
    public static async Task Run(NGame game)
    {
        bool host = System.Environment.GetEnvironmentVariable("FDZF_VERIFY_ROLE") == "host";
        INetGameService? transport = null;
        try
        {
            await game.GameStartupComplete;
            var mock = new SaveManager(new MockGodotFileIo("user://fdzf-network-memory")); mock.InitProfileId(1); mock.InitSettingsDataForTest(); mock.InitPrefsDataForTest(); mock.InitProgressData(); mock.SetFtuesEnabled(false); SaveManager.MockInstanceForTesting(mock);
            var version = PeerVersionInfo.LocalDefault();
            Godot.Timer? pump = null;
            void Pump(INetGameService service)
            { pump = new Godot.Timer { WaitTime = 0.02, Autostart = true }; pump.Timeout += service.Update; game.AddChild(pump); }
            if (host)
            {
                var service = new NetHostGameService(version); transport = service;
                if (service.StartENetHost(39181, 3) is { } error) throw new IOException(error.ToString());
                Pump(service); GD.Print("[FDZF NETWORK] ENet host listening");
                await Wait(() => service.ConnectedPeers.Count == 1, "client game handshake");
            }
            else
            {
                await Task.Delay(2000);
                var service = new NetClientGameService(version); transport = service; var client = new ENetClient(service); service.Initialize(client, PlatformType.None); Pump(service);
                if (await client.ConnectToHost(2, "127.0.0.1", 39181) is { } error) throw new IOException(error.ToString());
                await Wait(() => service.IsConnected, "game connection handshake");
            }
            var listener = new Listener(); var lobby = new StartRunLobby(GameMode.Standard, transport, listener, 2);
            lobby.Players.Add(new() { id = 1, character = ModelDb.Character<Ironclad>(), unlockState = UnlockState.all.ToSerializable() });
            lobby.Players.Add(new() { id = 2, character = ModelDb.Character<Ironclad>(), unlockState = UnlockState.all.ToSerializable() });
            bool remoteDone = false, remoteSaved = false; transport.RegisterMessageHandler<RaceMessage>((msg, sender) => { if (sender == transport.NetId) return; if (msg.Kind == "verification_done") remoteDone = true; if (msg.Kind == "verification_saved") remoteSaved = true; });
            bool resume = System.Environment.GetEnvironmentVariable("FDZF_VERIFY_RESUME") == "1"; RaceRuntime.ResumeRequested = resume;
            if (System.Environment.GetEnvironmentVariable("FDZF_VERIFY_VANILLA") == "1")
            {
                RaceConfig.ModeEnabled = !host;
                var normal = await RaceRuntime.StartConfigured(game, lobby, ActModel.GetDefaultList(), [], "FDZFVANILLA", 0, false);
                Check(RaceSession.Current == null && normal.Players.Count == 2 && normal.Modifiers.All(modifier => modifier is not RaceModifier), "host mode-off uses native two-player run despite client mode-on");
                transport.SendMessage(new RaceMessage { Kind = "verification_done" });
                await Wait(() => remoteDone, "vanilla peer completion");
                GD.Print("[FDZF VANILLA COMPLETE] host-off native cooperative flow passed.");
                game.GetTree().Quit(0); return;
            }
            RaceConfig.ModeEnabled = host; RaceConfig.DebugMode = host; RaceConfig.DisabledEffects = host ? "fortune_dice" : "ritual,exhaustion";
            var agreed = await LobbyRules.Agree(lobby);
            Check(agreed.Enabled && agreed.Debug && !agreed.Effects.Contains("fortune_dice") && agreed.Effects.Contains("exhaustion") && agreed.Effects.Contains("ritual"), "host config overrides different client mode and effect selections");
            var runTask = RaceRuntime.Start(game, lobby, ActModel.GetDefaultList(), [], "FDZFNETVERIFY", 0, agreed);
            await Wait(() => RaceSession.Current != null, "session capture"); pump?.QueueFree();
            var run = await runTask; var session = RaceSession.Current!;
            Check(session.Engine.State.Racers.Count == 2, "two real ENet peers share one match");
            Check(run.Players.Count == 1 && run.Players[0].NetId == 1, "gameplay remains isolated singleplayer on each process");
            await session.Submit(session.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(run)) });
            string otherId = host ? "2" : "1";
            await Wait(() => session.Engine.State.Racers[otherId].PublicPacket.Length > 0 && PublicProfiles.Read(session.Engine.State.Racers[otherId])?.Relics.Any(relic => relic is MegaCrit.Sts2.Core.Models.Relics.LizardTail) == true, "peer public profile including late starting relic");
            var peerView = PublicProfiles.Read(session.Engine.State.Racers[otherId]);
            Check(peerView != null && peerView.Deck.Cards.Count > 0 && peerView.Relics.Any(relic => relic is MegaCrit.Sts2.Core.Models.Relics.LizardTail), "peer native deck and relic snapshot crosses real ENet transport");

            if (resume)
            {
                await Wait(() => run.Players[0].PlayerCombatState?.Phase == PlayerTurnPhase.Play, "resumed combat ready");
                Check(session.Restored != null, "loaded matching shared checkpoint");
                Check(run.CurrentMapCoord == session.Restored!.Value.Save.VisitedMapCoords.Last(), "resumed at committed next map node");
                Check(session.Me.Battle == session.Restored!.Value.Match.Racers[session.LocalId].Battle + 1, "resumption advances one new battle only");
                if (!host) Check(session.Me.BurdenSnapshot > 0, "remote curse survives process restart");
                if (host) await Wait(() => remoteDone, "resumed client completion");
                else { transport.SendMessage(new RaceMessage { Kind = "verification_done" }); await Task.Delay(500); }
                GD.Print("[FDZF NETWORK RESUME COMPLETE] " + (host ? "host" : "client") + " restored and entered live combat."); game.GetTree().Quit(0); return;
            }
            await RunManager.Instance.EnterRoomDebug(host ? RoomType.Elite : RoomType.Monster);
            await Wait(() => RaceModifier.Active?.Turn == 1 && run.Players[0].PlayerCombatState?.Phase == PlayerTurnPhase.Play, "first combat ready");
            if (host)
            {
                await Wait(() => session.Engine.State.Racers["2"].InBattle, "opponent in independent combat");
                session.Me.WonBattles.Add("fixture");
                await session.Submit(session.New("offer", "fixture|0", route: Route.Disrupt));
                var offer = session.Engine.State.Offers["1:fixture|0:Disrupt"];
                await session.Submit(session.New("choose", offer.Id, offer.Candidates[0]));
                Check(session.Engine.State.Racers["2"].Effects.Count == 1, "group sacrifice dispatched to remote player");
                await Wait(() => session.Engine.State.Racers["2"].Battle == 2, "opponent entered next combat");
                Check(session.Me.Battle == 1 && CombatManager.Instance.IsInProgress, "opponent progresses without advancing local combat");
                Check(session.Engine.State.Racers["2"].BurdenSnapshot > 0, "remote curse active only in next combat");
                session.RequestPause();
            }
            else
            {
                await Wait(() => session.Me.Effects.Count == 1, "incoming group attack");
                Check(session.Me.Burden == 0 && session.Me.BurdenSnapshot == 0, "incoming curse does not change ongoing fight");
                await WinCurrent();
                await RunManager.Instance.EnterRoomDebug(RoomType.Monster);
                await Wait(() => session.Me.Battle == 2 && run.Players[0].PlayerCombatState?.Phase == PlayerTurnPhase.Play, "second personal combat");
                Check(session.Me.BurdenSnapshot > 0, "queued curse activates on next battle boundary");
                await Wait(() => session.PauseRequested, "host shared-save request");
            }
            await WinCurrent();
            var next = run.Map.StartingMapPoint.Children.OrderBy(p => p.coord.ToString(), StringComparer.Ordinal).First();
            var barrier = RunManager.Instance.EnterMapCoord(next.coord);
            await Wait(() => session.Paused, "shared checkpoint commit");
            Check(session.WaitingAtCheckpoint, "personal progress frozen at common revision");
            var report = System.Environment.GetEnvironmentVariable("FDZF_REPORT_DIR")!;
            var saved = NativeCheckpoint.Read(Path.Combine(report, "resume", session.LocalId + ".json"));
            Check(saved.Match.Revision == session.Engine.State.Revision, "checkpoint contains committed shared revision");
            Check(saved.Run.Players.Count == 1 && saved.Run.Players[0].Deck.Cards.Count == run.Players[0].Deck.Cards.Count, "native personal deck restored from packet");
            if (host) { await Wait(() => remoteSaved, "client persisted checkpoint receipt"); session.ContinueRace(); await barrier; await Wait(() => remoteDone, "client completion receipt"); }
            else { transport.SendMessage(new RaceMessage { Kind = "verification_saved" }); await barrier; transport.SendMessage(new RaceMessage { Kind = "verification_done" }); await Task.Delay(500); }
            GD.Print("[FDZF NETWORK COMPLETE] " + (host ? "host" : "client") + " cross-process checks passed."); game.GetTree().Quit(0);
        }
        catch (Exception e) { GD.PushError("[FDZF NETWORK FAILED] " + (host ? "host" : "client") + " " + e); game.GetTree().Quit(1); }
    }
    private static async Task WinCurrent()
    {
        var player = RaceRuntime.State!.Players.Single();
        if (player.Creature.CombatState is { } combat)
            foreach (var enemy in combat.Enemies.ToList()) await CreatureCmd.Kill(enemy, true);
        await CombatManager.Instance.CheckWinCondition();
        await Wait(() => !RaceSession.Current!.Me.InBattle, "combat victory settlement");
    }
    private sealed class Listener : IStartRunLobbyListener
    {
        public void PlayerConnected(MegaCrit.Sts2.Core.Entities.Multiplayer.StartRunLobbyPlayer player) { }
        public void PlayerChanged(MegaCrit.Sts2.Core.Entities.Multiplayer.StartRunLobbyPlayer player, bool isRandomCharacterResolution) { }
        public void AscensionChanged() { }
        public void SeedChanged() { }
        public void ModifiersChanged() { }
        public void MaxAscensionChanged() { }
        public void RemotePlayerDisconnected(MegaCrit.Sts2.Core.Entities.Multiplayer.StartRunLobbyPlayer player) { }
        public void BeginRun(string seed, List<ActModel> acts, IReadOnlyList<ModifierModel> modifiers) { }
        public void LocalPlayerDisconnected(NetErrorInfo info) { }
    }
}
