using FendaoZhengfeng.Core;
using Environment = System.Environment;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace FendaoZhengfeng.Mod;

internal static class NativeVictoryNetworkVerification
{
    private static async Task Wait(Func<bool> condition, string reason)
    { long deadline = Environment.TickCount64 + 30000; while (!condition()) { if (Environment.TickCount64 > deadline) throw new TimeoutException(reason); await Task.Delay(25); } }
    internal static async Task Run(NGame game, RunState run, RaceSession session, bool host)
    {
        string directory = Environment.GetEnvironmentVariable("FDZF_REPORT_DIR")!;
        string resultPath = Path.Combine(directory, "victory-" + session.LocalId + ".json");
        bool disconnect = Environment.GetEnvironmentVariable("FDZF_VERIFY_VICTORY_DISCONNECT") == "1";
        await RunManager.Instance.EnterRoomDebug(RoomType.Monster, model: ModelDb.Encounter<SlimesNormal>().ToMutable());
        var player = run.Players.Single();
        await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, "ready for final strike");
        var combat = player.Creature.CombatState!;
        await PlayerCmd.GainEnergy(100, player);
        var enemies = combat.Enemies.Where(enemy => enemy.IsAlive).ToList();
        foreach (var enemy in enemies) await CreatureCmd.SetCurrentHp(enemy, 1);
        // Keep the first attack ordinary, then cut the client's real ENet link
        // immediately before its lethal final attack. The control keeps it intact.
        Exception? failure = null;
        try
        {
            foreach (var target in enemies)
            {
                if (!host && disconnect && target == enemies.Last()) session.Transport.Disconnect(NetError.Quit);
                var card = combat.CreateCard<StrikeIronclad>(player);
                await CardPileCmd.Add(card, PileType.Hand, skipVisuals: false);
                await card.OnPlayWrapper(new ThrowingPlayerChoiceContext(), target, false,
                    new ResourceInfo { EnergySpent = 1, EnergyValue = 1, StarsSpent = 0, StarValue = 0 }, true);
            }
            await CombatManager.Instance.CheckWinCondition();
        }
        catch (Exception error) { failure = error; }
        var room = (CombatRoom)run.CurrentRoom!;
        var result = new { host, disconnect, allEnemiesDead = enemies.All(enemy => !enemy.IsAlive), nativePreFinished = room.IsPreFinished,
            modeStillInBattle = session.Me.InBattle, exception = failure?.ToString() };
        File.WriteAllText(resultPath, StateJson.Serialize(result));
        GD.Print("[FDZF VICTORY PROBE] " + StateJson.Serialize(result));
        if (!host && disconnect)
        {
            if (failure == null || !result.allEnemiesDead || result.nativePreFinished || !result.modeStillInBattle)
                throw new InvalidOperationException("disconnect did not reproduce the unfinished native victory boundary");
            await Wait(() => game.MainMenu != null && RaceSession.Current == null && !game.Transition.InTransition,
                "failed settlement returns client to usable main menu");
            await Wait(() => game.GetTree().Root.FindChildren("*", "", true, false).Any(node => node.GetType().Name == "NErrorPopup"),
                "settlement failure exposes the native error popup");
            GD.Print("[FDZF VICTORY PASS] failed settlement grants no unconfirmed win and returns client to main menu with an error popup");
        }
        else
        {
            if (failure != null) throw failure;
            await Wait(() => room.IsPreFinished && !session.Me.InBattle, "connected victory commits and native rewards finish");
            GD.Print("[FDZF VICTORY PASS] connected plain Strikes complete native victory");
        }
        await Wait(() => File.Exists(Path.Combine(directory, "victory-" + (host ? "2" : "1") + ".json")), "peer evidence recorded");
        await Task.Delay(500);
        game.GetTree().Quit(0);
    }
}
