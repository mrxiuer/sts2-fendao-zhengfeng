using FendaoZhengfeng.Core;
using Environment = System.Environment;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace FendaoZhengfeng.Mod;

internal static class NativeMenuResumeVerification
{
    private static void Check(bool ok, string name)
    { if (!ok) throw new InvalidOperationException(name); GD.Print("[FDZF MENU RESUME PASS] " + name); }
    private static async Task Wait(Func<bool> condition, string reason)
    { long deadline = Environment.TickCount64 + 30000; while (!condition()) { if (Environment.TickCount64 > deadline) throw new TimeoutException(reason); await Task.Delay(25); } }
    internal static async Task Run(NGame game, RunState run, RaceSession session, bool host, bool resume)
    {
        bool peerReady = false;
        session.Transport.RegisterMessageHandler<RaceMessage>((message, sender) =>
        { if (sender == session.Transport.NetId) return; if (message.Kind == "menu_resume_ready") peerReady = true; });
        var player = run.Players.Single();
        string expected = Path.Combine(Environment.GetEnvironmentVariable("FDZF_REPORT_DIR")!, "menu-expected-" + session.LocalId + ".json");
        if (!resume)
        {
            await PlayerCmd.GainGold(host ? 21 : 43, player);
            await RunManager.Instance.EnterRoomDebug(host ? RoomType.Shop : RoomType.RestSite);
            File.WriteAllText(expected, StateJson.Serialize(new[] { player.Gold, (int)player.Creature.CurrentHp, player.Deck.Cards.Count }));
            session.Recovery.SendSnapshot();
            session.Transport.SendMessage(new RaceMessage { Kind = "menu_resume_ready" });
            await Wait(() => peerReady, "both room snapshots ready");
            if (host)
            {
                await Wait(() => session.Recovery.BackupCount == 2, "host has both snapshots");
                await RecoveryUi.SaveAndQuit();
                Check(RaceRecovery.Read("1").Players.Count == 2, "menu save retains both isolated rooms");
            }
            await Wait(() => game.MainMenu != null && RaceSession.Current == null, "both peers return to main menu");
            await Wait(() => !game.Transition.InTransition, "save and quit reveals main menu");
        }
        else
        {
            Check(session.Restored != null && (host ? run.CurrentRoom is MerchantRoom : run.CurrentRoom is RestSiteRoom),
                "resume restores host shop and client rest site without replaying entry");
            Check(!game.Transition.InTransition && game.Transition.MouseFilter == Control.MouseFilterEnum.Ignore,
                "native character-select blackout is cleared on host and client");
            Check(StateJson.Deserialize<int[]>(File.ReadAllText(expected)).SequenceEqual(new[] { player.Gold, (int)player.Creature.CurrentHp, player.Deck.Cards.Count }),
                "room resources survive main-menu save and resume");
            // Out-of-band completion keeps a faster peer alive until both sets
            // of assertions ran; a message sent before this handler exists is lost.
            string completed = Path.Combine(Environment.GetEnvironmentVariable("FDZF_REPORT_DIR")!, "menu-resumed-" + session.LocalId);
            File.WriteAllText(completed, "passed");
            await Wait(() => File.Exists(Path.Combine(Environment.GetEnvironmentVariable("FDZF_REPORT_DIR")!, "menu-resumed-" + (host ? "2" : "1"))),
                "both resume assertions complete");
        }
        GD.Print("[FDZF MENU RESUME COMPLETE] " + session.LocalId + (resume ? " restored" : " saved"));
        game.GetTree().Quit(0);
    }
}
