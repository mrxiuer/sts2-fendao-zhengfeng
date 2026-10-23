using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using FendaoZhengfeng.Core;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace FendaoZhengfeng.Mod;

internal static class NativeRewardClickVerification
{
    private static async Task Wait(Func<bool> ready, string reason)
    {
        long until = System.Environment.TickCount64 + 40000;
        while (!ready()) { if (System.Environment.TickCount64 > until) throw new TimeoutException(reason); await Task.Delay(25); }
    }
    internal static async Task Run(NGame game, RunState run, Action<bool,string> check)
    {
        var session = RaceSession.Current!;
        session.Engine.State.EnabledEffects.Add(RewardRerollRules.EffectId);
        await MockTesting.GrantEffect(RewardRerollRules.EffectId);
        var driftwood = await RelicCmd.Obtain<Driftwood>(run.Players.Single());
        await DebugTesting.WinBattle();
        await Wait(() => run.CurrentRoom is CombatRoom { IsPreFinished: true }
            && RaceRuntime.CombatRewards?.Rewards.OfType<GoldReward>().Any() == true, "实际胜利奖励未生成。");
        var reward = RaceRuntime.CombatRewards!.Rewards.OfType<GoldReward>().First();
        NRewardButton? row = null;
        await Wait(() => {
            row = game.GetTree().Root.FindChildren("*", "", true, false).OfType<NRewardButton>()
                .FirstOrDefault(item => ReferenceEquals(item.Reward, reward));
            return row != null && row.IsInsideTree();
        }, "原生金币奖励行未展示。");
        string key = RaceRuntime.RewardKey(reward);
        int amount = reward.Amount;
        int beforeGold = run.Players.Single().Gold;
        bool rejected = false;
        try { await RewardRerollEffect.RefreshAsync(row, reward, RaceRuntime.CombatRewards!); }
        catch (RuleException) { rejected = true; }
        check(row!.GetNodeOrNull<Button>("EncoreRewardReroll") == null && rejected
            && !reward.SuccessfullySelected && reward.Amount == amount
            && run.Players.Single().Gold == beforeGold
            && RewardRerollRules.NativeUsage(session.Me, key).Used == 0,
            "the actual gold row has no refresh button and rejects refresh without changing gold or consuming a charge");
        await NativeAcceptanceVisuals.Capture("reroll-01-native-reward-buttons");
        var cards = RaceRuntime.CombatRewards!.Rewards.OfType<CardReward>().First();
        string cardKey = RaceRuntime.RewardKey(cards);
        var cardRow = game.GetTree().Root.FindChildren("*", "", true, false).OfType<NRewardButton>()
            .First(item => ReferenceEquals(item.Reward,cards));
        check(cardRow.GetNodeOrNull<Button>("EncoreRewardReroll") == null,
            "card reward rows have no overlaid mode button; refresh belongs below the native card choices");
        check(cards.CanReroll && driftwood.Owner == run.Players.Single(),
            "the actual native Driftwood relic enables its independent card refresh");
        AccessTools.Method(typeof(NRewardButton),"OnRelease").Invoke(cardRow,null);
        NCardRewardSelectionScreen? screen = null;
        await Wait(() => {
            screen = game.GetTree().Root.FindChildren("*", "", true, false).OfType<NCardRewardSelectionScreen>()
                .FirstOrDefault(item => item.IsVisibleInTree());
            return screen != null;
        }, "原生卡牌奖励界面未打开。");
        Control Options() => (Control)AccessTools.Field(typeof(NCardRewardSelectionScreen),"_rewardAlternativesContainer").GetValue(screen)!;
        NCardRewardAlternativeButton[] Buttons() => Options().GetChildren().OfType<NCardRewardAlternativeButton>()
            .Where(item => !item.IsQueuedForDeletion()).ToArray();
        await Wait(() => Buttons().Length==2,"原生跳过与刷新选项未出现。");
        await NativeAcceptanceVisuals.Capture("reroll-02-native-card-refresh");
        Buttons()[1].EmitSignal(NClickableControl.SignalName.Released,Buttons()[1]);
        await Wait(() => !cards.CanReroll && Buttons().Length==2,"漂流木刷新未完成。");
        check(RewardRerollRules.NativeUsage(session.Me,cardKey).Used==0 && NativeCardRewardRefresh.Remaining(cards)==1,
            "native Driftwood refresh is spent first without consuming a mode refresh");
        Buttons()[1].EmitSignal(NClickableControl.SignalName.Released,Buttons()[1]);
        await Wait(() => RewardRerollRules.NativeUsage(session.Me,cardKey).Used==1 && Buttons().Length==1,
            "卡牌界面的模式刷新未完成或没有隐藏耗尽按钮。");
        check(!cards.SuccessfullySelected && GodotObject.IsInstanceValid(screen) && screen!.IsVisibleInTree(),
            "mode refresh replaces the native cards in place, keeps selection open and removes the exhausted option");
        await NativeAcceptanceVisuals.Capture("reroll-03-native-card-refreshed");
        Buttons()[0].EmitSignal(NClickableControl.SignalName.Released,Buttons()[0]);
        await Wait(() => !GodotObject.IsInstanceValid(screen) || !screen!.IsInsideTree(),"原生跳过选牌没有关闭界面。");
        check(!cards.SuccessfullySelected && RewardRerollRules.NativeUsage(session.Me,cardKey).Used==1,
            "canceling selection does not restore the card reward's spent mode refresh");

    }
}
