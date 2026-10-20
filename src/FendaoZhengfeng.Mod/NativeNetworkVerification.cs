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
        bool interactive = System.Environment.GetEnvironmentVariable("FDZF_INTERACTIVE_NETWORK") == "1";
        ushort port = interactive && ushort.TryParse(System.Environment.GetEnvironmentVariable("FDZF_LOCAL_PORT"), out var selectedPort) ? selectedPort : (ushort)39181;
        INetGameService? transport = null;
        try
        {
            await game.GameStartupComplete;
            var mock = new SaveManager(new MockGodotFileIo("user://fdzf-network-memory")); mock.InitProfileId(1); mock.InitSettingsDataForTest(); mock.InitPrefsDataForTest(); mock.InitProgressData(); mock.SetFtuesEnabled(false); SaveManager.MockInstanceForTesting(mock);
            InteractiveUnlockPatch.Enabled = interactive;
            var version = PeerVersionInfo.LocalDefault();
            Godot.Timer? pump = null;
            void Pump(INetGameService service)
            { pump = new Godot.Timer { WaitTime = 0.02, Autostart = true }; pump.Timeout += service.Update; game.AddChild(pump); }
            if (host)
            {
                var service = new NetHostGameService(version); transport = service;
                if (service.StartENetHost(port, 3) is { } error) throw new IOException(error.ToString());
                Pump(service); GD.Print("[FDZF NETWORK] ENet host listening");
                await Wait(() => service.ConnectedPeers.Count == 1, "client game handshake");
            }
            else
            {
                await Task.Delay(2000);
                var service = new NetClientGameService(version); transport = service; var client = new ENetClient(service); service.Initialize(client, PlatformType.None); Pump(service);
                if (await client.ConnectToHost(2, "127.0.0.1", port) is { } error) throw new IOException(error.ToString());
                await Wait(() => service.IsConnected, "game connection handshake");
            }
            var listener = new Listener(); var lobby = new StartRunLobby(GameMode.Standard, transport, listener, 2);
            lobby.Players.Add(new() { id = 1, character = ModelDb.Character<Ironclad>(), unlockState = UnlockState.all.ToSerializable() });
            lobby.Players.Add(new() { id = 2, character = ModelDb.Character<Ironclad>(), unlockState = UnlockState.all.ToSerializable() });
            bool remoteDone = false, remoteSaved = false, remoteInteractiveReady = false; string? remoteCampaign = null; transport.RegisterMessageHandler<RaceMessage>((msg, sender) => { if (sender == transport.NetId) return; if (msg.Kind == "interactive_ready") remoteInteractiveReady = true; if (msg.Kind == "verification_done") remoteDone = true; if (msg.Kind == "verification_saved") remoteSaved = true; if (msg.Kind == "verification_campaign") remoteCampaign = msg.Content; });
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
            if (interactive) { RaceConfig.ModeEnabled = true; RaceConfig.DebugMode = true; }
            else { RaceConfig.ModeEnabled = host; RaceConfig.DebugMode = host; RaceConfig.DisabledEffects = host ? "fortune_dice" : "ritual,exhaustion"; }
            var agreed = await LobbyRules.Agree(lobby);
            if (!interactive) Check(agreed.Enabled && agreed.Debug && !agreed.Effects.Contains("fortune_dice") && agreed.Effects.Contains("exhaustion") && agreed.Effects.Contains("ritual"), "host config overrides different client mode and effect selections");
            var runTask = RaceRuntime.Start(game, lobby, ActModel.GetDefaultList(), [], interactive ? System.Environment.GetEnvironmentVariable("FDZF_LOCAL_SEED") ?? "FDZFLOCAL" : "FDZFNETVERIFY", 0, agreed);
            await Wait(() => RaceSession.Current != null, "session capture"); pump?.QueueFree();
            var run = await runTask; var session = RaceSession.Current!;
            if (interactive) Check(run.ExtraFields.StartedWithNeow && run.CurrentRoom is EventRoom opening
                && opening.LocalMutableEvent is MegaCrit.Sts2.Core.Models.Events.Neow && opening.LocalMutableEvent.CurrentOptions.Count == 3,
                "interactive peers retain the native three-choice opening alongside Lizard Tail");
            int startingNativeGold = run.Players.Single().Gold;
            Check(session.Engine.State.Racers.Count == 2, "two real ENet peers share one match");
            Check(run.Players.Count == 1 && run.Players[0].NetId == 1, "gameplay remains isolated singleplayer on each process");
            await session.Submit(session.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(run)) });
            string otherId = host ? "2" : "1";
            await Wait(() => session.Engine.State.Racers[otherId].PublicPacket.Length > 0 && PublicProfiles.Read(session.Engine.State.Racers[otherId])?.Relics.Any(relic => relic is MegaCrit.Sts2.Core.Models.Relics.LizardTail) == true, "peer public profile including late starting relic");
            Check(run.Acts.Count == 5, "race run contains five acts on both real ENet peers");
            if (interactive)
            {
                DisplayServer.WindowSetTitle("分道争锋 · 本地双进程 · " + (host ? "房主 / 1号" : "客户端 / 2号"));
                transport.SendMessage(new RaceMessage { Kind = "interactive_ready" });
                await Wait(() => remoteInteractiveReady, "interactive peer ready");
                GD.Print("[FDZF INTERACTIVE READY] " + (host ? "host" : "client") + " seed=" + run.Rng.Seed);
                if (System.Environment.GetEnvironmentVariable("FDZF_INTERACTIVE_SMOKE") == "1")
                {
                    await Task.Delay(1500);
                    string? screenshots = System.Environment.GetEnvironmentVariable("FDZF_SCREENSHOT_DIR");
                    if (!string.IsNullOrWhiteSpace(screenshots))
                    {
                        Directory.CreateDirectory(screenshots);
                        game.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(screenshots, host ? "interactive-host.png" : "interactive-client.png"));
                    }
                    GD.Print("[FDZF INTERACTIVE SMOKE COMPLETE] " + (host ? "host" : "client"));
                    game.GetTree().Quit(0);
                }
                return;
            }
            var campaignPreview = RunState.FromSerializable(RunManager.Instance.ToSave(null));
            var signatures = new List<string>();
            for (int index = 3; index < 5; index++)
            {
                campaignPreview.CurrentActIndex = index;
                var map = FiveActCampaign.CreateLateMap(campaignPreview);
                signatures.Add(campaignPreview.Act.BossEncounter.Id + "/" + campaignPreview.Act.SecondBossEncounter!.Id + ":" + NativeCampaignVerification.MapSignature(map));
            }
            string campaignDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|", signatures))));
            transport.SendMessage(new RaceMessage { Kind = "verification_campaign", Content = campaignDigest });
            await Wait(() => remoteCampaign != null, "remote fourth/fifth act map digest");
            Check(remoteCampaign == campaignDigest, "fourth and fifth maps including links node types and both bosses match across actual ENet processes");
            var peerView = PublicProfiles.Read(session.Engine.State.Racers[otherId]);
            Check(peerView != null && peerView.Deck.Cards.Count > 0 && peerView.Relics.Any(relic => relic is MegaCrit.Sts2.Core.Models.Relics.LizardTail), "peer native deck and relic snapshot crosses real ENet transport");

            if (resume)
            {
                Check(session.Engine.State.Trades.Count == 1 && session.Engine.State.Trades.Single().Delivered && session.Engine.State.Trades.Single().Removed,
                    "completed card trade survives both processes restarting without redelivery");
                await Wait(() => run.Players[0].PlayerCombatState?.Phase == PlayerTurnPhase.Play, "resumed combat ready");
                Check(session.Restored != null, "loaded matching shared checkpoint");
                Check(run.CurrentMapCoord == session.Restored!.Value.Save.VisitedMapCoords.Last(), "resumed at committed next map node");
                Check(session.Me.Battle == session.Restored!.Value.Match.Racers[session.LocalId].Battle + 1, "resumption advances one new battle only");
                if (!host) Check(session.Me.BurdenSnapshot > 0, "remote curse survives process restart");
                if (host) await Wait(() => remoteDone, "resumed client completion");
                else { transport.SendMessage(new RaceMessage { Kind = "verification_done" }); await Task.Delay(500); }
                GD.Print("[FDZF NETWORK RESUME COMPLETE] " + (host ? "host" : "client") + " restored and entered live combat."); game.GetTree().Quit(0); return;
            }
            int beforeMarketDeck = run.Players.Single().Deck.Cards.Count;
            int beforeMarketGold = startingNativeGold;
            string tradedCardId;
            if (host)
            {
                session.Me.Effects["good_connections"] = new() { Id = "good_connections" };
                await PlayerCmd.GainGold(500, run.Players.Single());
                tradedCardId = session.Engine.State.Racers["2"].MarketCards.First().Id;
                await AdvancedEffects.BuyCard("2", tradedCardId);
                Check(run.Players.Single().Deck.Cards.Count == beforeMarketDeck, "real ENet purchase leaves current buyer deck unchanged until next combat");
            }
            else
            {
                await Wait(() => session.Engine.State.Trades.Count == 1, "host market purchase received");
                tradedCardId = session.Engine.State.Trades.Single().Card.Id;
                Check(run.Players.Single().Deck.Cards.Count == beforeMarketDeck, "real ENet sale keeps original card until seller next combat");
            }
            await RunManager.Instance.EnterRoomDebug(host ? RoomType.Elite : RoomType.Monster);
            await Wait(() => RaceModifier.Active?.Turn == 1 && run.Players[0].PlayerCombatState?.Phase == PlayerTurnPhase.Play, "first combat ready");
            await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), run.Players.Single().Creature, 9999,
                MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered, run.Players.Single().Creature.CombatState!.Enemies.First(), null, null);
            Check(session.Me.RevivalCount == 1 && run.Players.Single().Creature.CurrentHp > 0,
                "real native revival records the correct local racer over ENet before owning its boon");
            await Wait(() => session.Engine.State.Racers[otherId].RevivalCount == 1, "remote revival history synchronization");
            Check(session.Engine.State.Racers[otherId].RevivalCount == 1, "peer revival history arrives exactly once over actual ENet");
            await Wait(() => session.Engine.State.Trades.Single().Delivered && session.Engine.State.Trades.Single().Removed, "independent ENet card handoffs");
            Check(run.Players.Single().Deck.Cards.Count == beforeMarketDeck + (host ? 1 : -1), "real ENet handoff changes each native deck exactly once");
            GD.Print($"[FDZF NETWORK GOLD] initial={beforeMarketGold} actual={run.Players.Single().Gold} host={host}");
            Check(run.Players.Single().Gold == beforeMarketGold + (host ? 400 : 100), "real ENet buyer pays and seller receives native gold exactly once");
            if (!host) Check(!RaceModifier.Active!.CaptureMarket().Any(card => card.Id == tradedCardId), "real ENet seller stable card identity is removed");
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
            var liveModifier = RaceModifier.Active;
            var saved = NativeCheckpoint.Read(Path.Combine(report, "resume", session.LocalId + ".json"));
            Check(ReferenceEquals(RaceModifier.Active, liveModifier), "reading an isolated checkpoint preview never replaces the active gameplay modifier");
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
        var session = RaceSession.Current!;
        RaceUi.Refresh();
        Check(RaceUi.DebugWinButtonVisible, "network peer exposes quick victory in debug panel");
        int victories = session.Me.VictoryCount;
        await DebugTesting.WinBattle();
        await Wait(() => !session.Me.InBattle, "combat victory settlement");
        Check(session.Me.VictoryCount == victories + 1, "debug quick victory settles exactly once through native hooks");
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

// The opt-in manual test starts from an in-memory profile with full unlocks.
// Never persist changes to the user's progress or alter ordinary game sessions.
[HarmonyLib.HarmonyPatch(typeof(SaveManager), nameof(SaveManager.GenerateUnlockStateFromProgress))]
internal static class InteractiveUnlockPatch
{
    internal static bool Enabled;
    static void Postfix(ref UnlockState __result)
    {
        if (Enabled && System.Environment.GetEnvironmentVariable("FDZF_NATIVE_VERIFY") == "1"
            && System.Environment.GetEnvironmentVariable("FDZF_INTERACTIVE_NETWORK") == "1") __result = UnlockState.all;
    }
}
