using FendaoZhengfeng.Core;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;

namespace FendaoZhengfeng.Mod;

internal static class NativeRecoveryVerification
{
    private static void Check(bool ok, string name)
    { if (!ok) throw new InvalidOperationException(name); GD.Print("[FDZF RECOVERY PASS] " + name); }
    private static async Task Wait(Func<bool> condition, string reason)
    { long deadline = System.Environment.TickCount64 + 45000; while (!condition()) { if (System.Environment.TickCount64 > deadline) throw new TimeoutException(reason); await Task.Delay(25); } }
    internal static async Task Run(NGame game, RunState run, RaceSession session, bool host, bool resume)
    {
        var player = run.Players.Single();
        async Task<bool[]> ProbeCriticalSequence()
        {
            var results = new List<bool>(); var modifier = RaceModifier.Active!;
            var card = player.Deck.Cards.First(card => card.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack);
            for (int i = 0; i < 3; i++)
            {
                await new MegaCrit.Sts2.Core.Commands.Builders.AttackCommand(0).FromCard(card, null)
                    .Targeting(player.Creature.CombatState!.Enemies.First(enemy => enemy.IsAlive)).WithNoAttackerAnim()
                    .Execute(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext());
                results.Add(modifier.LastAttackCritical);
            }
            return results.ToArray();
        }
        string criticalPath = Path.Combine(System.Environment.GetEnvironmentVariable("FDZF_REPORT_DIR")!, "critical-start-" + session.LocalId + ".json");
        if (resume)
        {
            await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "restored first playable turn");
            Check(session.Restored != null && session.Me.Battle == 1, "restart keeps original battle number and does not activate future curses");
            Check(RaceModifier.Active!.Turn == 1 && player.Creature.CombatState!.RoundNumber == 1, "unfinished combat restarts from turn one");
            Check(player.Creature.CurrentHp == 333 && player.Creature.MaxHp == 500, "saved HP and maximum HP survive restart without repeat abrasion loss");
            Check(player.Creature.GetPowerAmount<StrengthPower>() == -1, "opening power is reconstructed exactly once");
            await Wait(() => player.Gold == (host ? 99 : 432), "journal replays gold missing from last disconnected peer native backup");
            Check(session.Me.Count("iron_curtain") == 0 && session.Me.Count("iron_curtain", false) == 1, "curse received during combat remains pending after resume");
            Check(session.Me.Receipts.All(receipt => receipt.Id != "recovery-test-gold"), "recovered native gold is acknowledged without double payout");
            var replayedCriticals = await ProbeCriticalSequence();
            Check(replayedCriticals.SequenceEqual(StateJson.Deserialize<bool[]>(File.ReadAllText(criticalPath))) && RaceModifier.Active!.CriticalRolls == 3,
                "restarting unfinished combat replays the original critical sequence from its beginning");
            session.Transport.SendMessage(new RaceMessage { Kind = "recovery_test_done" });
            await Task.Delay(600);
            GD.Print("[FDZF RECOVERY COMPLETE] " + (host ? "host" : "client") + " resumed host-owned save."); game.GetTree().Quit(0); return;
        }
        bool peerReady = false, peerPaid = false;
        session.Transport.RegisterMessageHandler<RaceMessage>((message, sender) =>
        { if (sender == session.Transport.NetId) return; if (message.Kind == "recovery_test_ready") peerReady = true; if (message.Kind == "recovery_test_paid") peerPaid = true; });
        if (host)
        {
            foreach (var racer in session.Engine.State.Racers.Values)
                foreach (var id in new[] { "abrasion", "exhaustion" }) racer.Effects[id] = new() { Id = id, Grants = [new() { EventId = "fixture", ActiveBattle = 1, Source = "1" }] };
            foreach (var racer in session.Engine.State.Racers.Values)
                racer.Effects["gamblers_heart"] = new() { Id = "gamblers_heart" };
            await session.Submit(session.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(run)) });
        }
        await Wait(() => session.Me.Effects.ContainsKey("abrasion"), "test opening curse fixture");
        await CreatureCmd.SetMaxAndCurrentHp(player.Creature, 500);
        var point = run.Map.StartingMapPoint.Children.OrderBy(point => point.coord.ToString(), StringComparer.Ordinal).First();
        await RunManager.Instance.EnterMapCoord(point.coord);
        await Wait(() => session.Me.InBattle && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "initial combat ready");
        File.WriteAllText(criticalPath, StateJson.Serialize(await ProbeCriticalSequence()));
        CombatManager.Instance.SetReadyToEndTurn(player, false);
        await Wait(() => RaceModifier.Active!.Turn >= 2 && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "second playable turn before save");
        await CreatureCmd.SetCurrentHp(player.Creature, 333);
        session.Recovery.SendSnapshot(); session.Recovery.SuspendPeriodicCaptureForVerification = true;
        session.Transport.SendMessage(new RaceMessage { Kind = "recovery_test_ready" });
        await Wait(() => peerReady, "both peers have sent native snapshots");
        if (host)
        {
            await Wait(() => session.Recovery.BackupCount == 2, "host received peer backup");
            foreach (var racer in session.Engine.State.Racers.Values)
                racer.Effects["iron_curtain"] = new() { Id = "iron_curtain", Grants = [new() { EventId = "pending", ActiveBattle = 2, Source = "1" }] };
            session.Engine.State.Racers["2"].Receipts.Add(new("recovery-test-gold", "gold", "", 333));
            await session.Submit(session.New("profile") with { Profile = StateJson.Serialize(PublicProfiles.Capture(run)) });
            await Wait(() => peerPaid, "client applied and acknowledged gold after last native backup");
            await Wait(() => session.Transport is NetHostGameService service && service.ConnectedPeers.Count == 0, "client disconnected before host save");
            var pause = new MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu.NPauseMenu();
            try { await (Task)HarmonyLib.AccessTools.Method(pause.GetType(), "CloseToMenu").Invoke(pause, null)!; }
            finally { pause.Free(); }
            var archive = RaceRecovery.Read("1"); var match = StateJson.Deserialize<Match>(archive.MatchJson);
            Check(archive.Players.Count == 2 && match.Racers["2"].Receipts.Any(receipt => receipt.Id == "recovery-test-gold"), "disconnected native backup retains receipt journal for acknowledged but uncaptured gold");
            Check(!File.Exists(Path.Combine(System.Environment.GetEnvironmentVariable("FDZF_REPORT_DIR")!, "resume", "2.json")), "host save does not require client-owned resume file");
            var menu = game.GetTree().Root.FindChildren("*", "", true, false).OfType<NMainMenu>().First();
            Check(menu.GetNodeOrNull<Control>("MainMenuTextButtons/" + RecoveryUi.ButtonName)?.Visible == true, "main menu exposes independent race continue button");
        }
        else
        {
            await Wait(() => player.Gold == 432 && session.Me.Receipts.All(receipt => receipt.Id != "recovery-test-gold"), "native gold receipt and acknowledgement");
            session.Transport.SendMessage(new RaceMessage { Kind = "recovery_test_paid" });
            await Task.Delay(300); session.Transport.Disconnect(MegaCrit.Sts2.Core.Entities.Multiplayer.NetError.Quit);
        }
        GD.Print("[FDZF RECOVERY COMPLETE] " + (host ? "host" : "client") + " disconnected save stage."); game.GetTree().Quit(0);
    }
}
