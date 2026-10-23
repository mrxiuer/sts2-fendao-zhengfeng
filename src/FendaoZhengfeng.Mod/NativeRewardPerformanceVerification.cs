using FendaoZhengfeng.Core;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Test;

namespace FendaoZhengfeng.Mod;

/// <summary>Runs one actual reward-button selection inside the game's Godot process.</summary>
internal static class NativeRewardPerformanceVerification
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        GD.Print("[FDZF REWARD PERF PASS] " + name);
    }

    private static async Task Wait(Func<bool> condition, string reason)
    {
        long deadline = System.Environment.TickCount64 + 45000;
        while (!condition())
        {
            if (System.Environment.TickCount64 > deadline) throw new TimeoutException(reason);
            await Task.Delay(25);
        }
    }

    internal static async Task Run(NGame game)
    {
        RaceSession? session = null;
        try
        {
            await game.GameStartupComplete;
            if (!RewardPerformance.Enabled)
                throw new InvalidOperationException("Run this fixture with FDZF_PROFILE_REWARDS=1.");

            var memory = new SaveManager(new MockGodotFileIo("user://fdzf-reward-performance-memory"));
            memory.InitProfileId(1);
            memory.InitSettingsDataForTest();
            memory.InitPrefsDataForTest();
            memory.InitProgressData();
            memory.SetFtuesEnabled(false);
            SaveManager.MockInstanceForTesting(memory);

            RaceConfig.ModeEnabled = true;
            RaceConfig.DebugMode = true;
            RaceConfig.DisabledEffects = "";
            RaceRuntime.ResumeRequested = false;
            const string seed = "FDZFREWARDPERF";
            session = RaceSession.CreateVerification(seed);
            var run = await game.StartNewSingleplayerRun(
                ModelDb.Character<Ironclad>(),
                false,
                ActModel.GetDefaultList(),
                [ModelDb.Modifier<RaceModifier>().ToMutable()],
                seed,
                GameMode.Standard);

            var player = run.Players.Single();
            await CreatureCmd.SetMaxAndCurrentHp(player.Creature, 500);
            var first = run.Map.StartingMapPoint.Children
                .OrderBy(point => point.coord.ToString(), StringComparer.Ordinal)
                .First();
            await RunManager.Instance.EnterMapCoord(first.coord);
            await Wait(() => session.Me.InBattle && player.PlayerCombatState?.Phase.ToString() == "Play",
                "initial native combat reaches the player's turn");

            await DebugTesting.WinBattle();
            await Wait(() => run.CurrentRoom is CombatRoom { IsPreFinished: true }
                && RaceRuntime.CombatRewards != null
                && RaceRuntime.CombatRewards.Rewards.OfType<GoldReward>().Any(reward => !reward.SuccessfullySelected),
                "native combat opens a selectable gold reward");

            var gold = RaceRuntime.CombatRewards!.Rewards
                .OfType<GoldReward>()
                .First(reward => !reward.SuccessfullySelected);
            var rewardKey = RaceRuntime.RewardKey(gold);
            NRewardButton? button = null;
            await Wait(() =>
            {
                button = game.GetTree().Root.FindChildren("*", "", true, false)
                    .OfType<NRewardButton>()
                    .FirstOrDefault(candidate => ReferenceEquals(candidate.Reward, gold));
                return button != null;
            }, "native reward button appears in the live screen");

            int clickBefore = RewardPerformance.CompletedClicks;
            int goldBefore = player.Gold;
            HarmonyLib.AccessTools.Method(typeof(NRewardButton), "OnRelease")!
                .Invoke(button, null);
            await Wait(() => RewardPerformance.CompletedClicks > clickBefore,
                "native reward button selection finishes");
            session.FlushPersistence();
            RewardPerformance.ReportLast("native-fixture-after-flush");

            Check(gold.SuccessfullySelected && player.Gold > goldBefore,
                "the live native gold button grants gold exactly once");
            Check(session.Engine.State.ConsumedRewards.Contains(session.LocalId + ":" + rewardKey),
                "the same click commits the reward receipt");
            Check(RewardPerformance.LastCount(RewardPerfStage.ButtonTask) > 0
                && RewardPerformance.LastCount(RewardPerfStage.NativeSelect) > 0
                && RewardPerformance.LastCount(RewardPerfStage.ClickToUi) > 0,
                "trace covers native button release through the reward UI update");
            Check(RewardPerformance.LastCount(RewardPerfStage.EngineExecute) > 0
                && RewardPerformance.LastCount(RewardPerfStage.CoreCloneSerialize) > 0
                && RewardPerformance.LastCount(RewardPerfStage.CoreCloneDeserialize) > 0,
                "trace separates Engine.Execute from both Match clone JSON passes");
            Check(RewardPerformance.LastCount(RewardPerfStage.PublishState) > 0
                && RewardPerformance.LastCount(RewardPerfStage.PublishSerialize) > 0
                && RewardPerformance.LastCount(RewardPerfStage.Checkpoint) > 0,
                "trace covers state publication and its reward checkpoint");
            Check(RewardPerformance.LastCount(RewardPerfStage.RecoveryCapture) > 0
                && RewardPerformance.LastCount(RewardPerfStage.NativeToSave) > 0
                && RewardPerformance.LastCount(RewardPerfStage.NativePacketSerialize) > 0
                && RewardPerformance.LastCount(RewardPerfStage.RewardSnapshot) > 0,
                "trace separates synchronous native-save and pending-reward capture");
            Check(RewardPerformance.LastCount(RewardPerfStage.CheckpointMatchCacheHit) > 0
                && RewardPerformance.LastCount(RewardPerfStage.CheckpointMatchSerialize) == 0,
                "checkpoint reuses the just-published Match JSON at the same revision");
            Check(session.Transport.Type == NetGameType.Singleplayer
                && RewardPerformance.LastCount(RewardPerfStage.NoOpBroadcastSkipped) > 0
                && RewardPerformance.LastCount(RewardPerfStage.Compression) == 0,
                "singleplayer skips the empty broadcast before compression");
            Check(RewardPerformance.LastCount(RewardPerfStage.SaveQueue) > 0
                && RewardPerformance.LastCount(RewardPerfStage.DiskWrite) > 0
                && File.Exists(Path.Combine(session.SaveDirectory, "match.json"))
                && File.Exists(Path.Combine(session.SaveDirectory, "checkpoint.json")),
                "trace correlates queued save contents with flushed match and checkpoint writes");

            GD.Print("[FDZF REWARD PERF COMPLETE] Native reward click path passed.");
            game.GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PushError("[FDZF REWARD PERF FAILED] " + error);
            game.GetTree().Quit(1);
        }
        finally
        {
            session?.Dispose();
        }
    }
}
