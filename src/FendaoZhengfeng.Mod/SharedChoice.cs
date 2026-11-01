using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace FendaoZhengfeng.Mod;

internal static class SharedChoice
{
    internal static async Task Commit(Player owner, string kind, string key, string effect = "", bool boss = false,
        FendaoZhengfeng.Core.Route route = FendaoZhengfeng.Core.Route.Disrupt, int generation = 0, int amount = 0)
    {
        var session = RaceSession.Current!;
        var actor = RaceRuntime.RacerFor(owner) ?? throw new InvalidOperationException("Missing reward owner.");
        var command = session.New(kind, key, effect, amount: amount, boss: boss, route: route) with { Actor = actor.Id, MaxHealth = owner.Creature.MaxHp, Id = "" };
        string identity = session.Engine.State.Id + ":" + actor.ResetCount + ":" + generation + ":" + FendaoZhengfeng.Core.StateJson.Serialize(command);
        command = command with { Id = "choice:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity))) };
        await session.SubmitSharedLifecycle(command);
    }

    internal static Task Profile(Player owner, string operation)
    {
        var session = RaceSession.Current!;
        var command = session.New("profile", operation) with { Actor = RaceRuntime.RacerFor(owner)!.Id, MaxHealth = owner.Creature.MaxHp,
            Profile = FendaoZhengfeng.Core.StateJson.Serialize(PublicProfiles.Capture(RaceRuntime.State!, owner)), Id = "" };
        string identity = session.Engine.State.Id + ":" + RaceRuntime.RacerFor(owner)!.ResetCount + ":" + command.Actor + ":" + operation;
        command = command with { Id = "profile:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity))) };
        return session.SubmitSharedLifecycle(command);
    }

    // Every peer must call with the same owner and options in the native reward flow.
    // Only that owner invokes the UI; cancellation is represented by an empty index list.
    internal static async Task<int> Choose(Player owner, int optionCount, Func<Task<int>> localChoice, PlayerChoiceContext? context = null)
    {
        var session = RaceSession.Current ?? throw new InvalidOperationException("Match ended before choice.");
        if (!session.Engine.State.Synchronous) return await localChoice();
        if (optionCount < 1) throw new ArgumentOutOfRangeException(nameof(optionCount));
        var synchronizer = RunManager.Instance.PlayerChoiceSynchronizer;
        uint choiceId = synchronizer.ReserveChoiceId(owner);
        if (context != null) await context.SignalPlayerChoiceBegun(owner, PlayerChoiceOptions.None);
        try
        {
            if (owner == RaceRuntime.LocalPlayer())
            {
                int selected = await localChoice();
                if (selected < -1 || selected >= optionCount) throw new InvalidOperationException("Invalid local reward choice.");
                synchronizer.SyncLocalChoice(owner, choiceId, PlayerChoiceResult.FromIndexes(selected < 0 ? [] : [selected]));
                return selected;
            }
            var pending = synchronizer.WaitForRemoteChoice(owner, choiceId);
            while (!pending.IsCompleted)
            {
                if (!ReferenceEquals(RaceSession.Current, session) || !session.Transport.IsConnected)
                    throw new IOException("Match disconnected while waiting for reward choice.");
                await Task.WhenAny(pending, Task.Delay(100));
            }
            var indexes = (await pending).AsIndexes().ToArray();
            if (indexes.Length == 0) return -1;
            if (indexes.Length != 1 || indexes[0] < 0 || indexes[0] >= optionCount)
                throw new InvalidOperationException("Invalid remote reward choice.");
            return indexes[0];
        }
        finally
        {
            if (context != null) await context.SignalPlayerChoiceEnded();
        }
    }
}
