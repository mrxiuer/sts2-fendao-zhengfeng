using System.Runtime.CompilerServices;
using FendaoZhengfeng.Core;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Rewards;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Rewards;

namespace FendaoZhengfeng.Mod;

// Driftwood enables CardReward.CanReroll. Native Generate then creates a REROLL
// alternative underneath the cards; Populate rebuilds both cards and alternatives.
// Use that same native path, replacing only its callback and title for this boon.
internal static class NativeCardRewardRefresh
{
    private sealed record Owner(CardReward Reward);
    private static readonly ConditionalWeakTable<CardRewardAlternative, Owner> owners = new();
    internal readonly record struct GenerationState(bool Modified, bool NativeCanReroll);
    internal static int Remaining(CardReward reward)
    {
        if (!RaceRuntime.Live || RaceSession.Current is not { } session || !session.Me.Has(RewardRerollRules.EffectId)) return 0;
        string key = RaceRuntime.RewardKey(reward);
        return RewardRerollRules.CanRefreshNative(session.Engine.State,session.Me,key)
            ? RewardRerollRules.NativeUsage(session.Me,key).Remaining : 0;
    }
    internal static GenerationState BeforeGenerate(CardReward reward)
    {
        bool native = reward.CanReroll;
        bool enabled = Remaining(reward) > 0;
        if (enabled) reward.CanReroll = true;
        return new(enabled, native);
    }
    internal static void AfterGenerate(CardReward reward, GenerationState state, ref IReadOnlyList<CardRewardAlternative> result)
    {
        if (!state.Modified) return;
        reward.CanReroll = state.NativeCanReroll;
        var choices = result.ToList();
        int index = choices.FindIndex(choice => choice.OptionId.Equals("REROLL",StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new InvalidOperationException(L10n.T("原生卡牌奖励没有创建刷新选项。"));
        var alternative = new CardRewardAlternative("REROLL", async () =>
        {
            // Preserve Driftwood's independent native refresh before spending a mode charge.
            if (reward.CanReroll) { reward.Reroll(); RaceRuntime.Checkpoint(); return; }
            if (RaceSession.Current is not { } session || Remaining(reward) <= 0)
                throw new RuleException(L10n.T("这份卡牌奖励没有可用的刷新次数。"));
            var set = RaceRuntime.CombatRewards;
            if (set == null || !RewardRerollEffect.IsTrackedReward(set,reward))
                set = RewardRerollPersistence.CurrentSetFor(reward);
            if (set == null) throw new RuleException(L10n.T("当前卡牌奖励集已关闭，不能刷新。"));
            await RewardRerollEffect.RefreshAsync(null,reward,set);
        }, PostAlternateCardRewardAction.DoNothing);
        owners.Add(alternative,new(reward)); choices[index] = alternative; result = choices;
    }
    internal static void Restore(CardReward reward,GenerationState state)
    { if (state.Modified) reward.CanReroll = state.NativeCanReroll; }
    internal static bool TryTitle(CardRewardAlternative alternative,out LocString text)
    {
        if (!owners.TryGetValue(alternative,out var owner)) { text=null!; return false; }
        int count = Remaining(owner.Reward) + (owner.Reward.CanReroll ? 1 : 0);
        text = new LocString("fdzf_encore", "refresh." + count); return true;
    }
}

[HarmonyPatch(typeof(CardRewardAlternative),nameof(CardRewardAlternative.Generate))]
internal static class NativeModeCardRefreshPatch
{
    static void Prefix(CardReward cardReward,out NativeCardRewardRefresh.GenerationState __state)
        => __state = NativeCardRewardRefresh.BeforeGenerate(cardReward);
    static void Postfix(CardReward cardReward,NativeCardRewardRefresh.GenerationState __state,ref IReadOnlyList<CardRewardAlternative> __result)
        => NativeCardRewardRefresh.AfterGenerate(cardReward,__state,ref __result);
    static Exception? Finalizer(CardReward cardReward,NativeCardRewardRefresh.GenerationState __state,Exception? __exception)
    { if (__exception != null) NativeCardRewardRefresh.Restore(cardReward,__state); return __exception; }
}
[HarmonyPatch(typeof(CardRewardAlternative),nameof(CardRewardAlternative.Title),MethodType.Getter)]
internal static class NativeModeCardRefreshTitlePatch
{
    static bool Prefix(CardRewardAlternative __instance,ref LocString __result)
    { if (!NativeCardRewardRefresh.TryTitle(__instance,out var title)) return true; __result=title; return false; }
}
[HarmonyPatch]
internal static class NativeModeCardRefreshTextPatch
{
    static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    { yield return AccessTools.Method(typeof(LocString),nameof(LocString.GetRawText)); yield return AccessTools.Method(typeof(LocString),nameof(LocString.GetFormattedText)); }
    static bool Prefix(LocString __instance,ref string __result)
    {
        if (__instance.LocTable != "fdzf_encore" || !__instance.LocEntryKey.StartsWith("refresh.",StringComparison.Ordinal)) return true;
        __result = L10n.T("刷新卡牌（剩余") + __instance.LocEntryKey["refresh.".Length..] + L10n.T("次）"); return false;
    }
}
