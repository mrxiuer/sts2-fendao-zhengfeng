using System.Reflection;
using FendaoZhengfeng.Core;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

// Keep the scope alive until the native async play finishes, including nested plays.
[HarmonyPatch(typeof(CardModel), nameof(CardModel.OnPlayWrapper))]
internal static class CardPlayScope
{
    private sealed record Scope(CardModel Card, bool Automatic);
    private static readonly Dictionary<CardModel, List<Scope>> scopes = [];
    public static bool IsAutomatic(CardModel card) => scopes.TryGetValue(card, out var stack) && stack.Count > 0 && stack[^1].Automatic;
    static void Prefix(CardModel __instance, bool isAutoPlay, out Scope __state)
    {
        __state = new(__instance, isAutoPlay);
        if (!scopes.TryGetValue(__instance, out var stack)) scopes[__instance] = stack = [];
        stack.Add(__state);
    }
    static void Postfix(ref Task __result, Scope __state) { __result = Complete(__result, __state); }
    private static async Task Complete(Task original, Scope scope)
    {
        try { await original; } finally { Remove(scope); }
    }
    private static void Remove(Scope scope)
    {
        if (!scopes.TryGetValue(scope.Card, out var stack)) return;
        stack.Remove(scope);
        if (stack.Count == 0) scopes.Remove(scope.Card);
    }
    static Exception? Finalizer(Exception? __exception, Scope __state) { if (__exception != null) Remove(__state); return __exception; }
}

[HarmonyPatch(typeof(Reward), nameof(Reward.SelectUnsynchronized))]
internal static class RewardChoicePatch
{
    private static readonly HashSet<Reward> originalCalls = [];
    private static readonly HashSet<Reward> selecting = [];
    static bool Prefix(Reward __instance, ref Task<bool> __result)
    {
        if (!RaceRuntime.Live || originalCalls.Contains(__instance)) return true;
        if (!selecting.Add(__instance)) { __result = Task.FromResult(false); return false; }
        __result = Choose(__instance); return false;
    }
    private static async Task<bool> Choose(Reward reward)
    {
        try
        {
            var s = RaceSession.Current!; var type = RaceRuntime.State!.CurrentRoom?.RoomType.ToString();
            if (reward is RelicReward && type is "Elite" or "Boss")
            {
                int action = await RaceUi.Choose("遗物奖励", ["领取遗物", "献祭遗物：干扰对手", "献祭遗物：自我契约", "献祭遗物：自身成长"]);
                if (action < 0) return false;
                if (action > 0)
                {
                    var route = action switch { 1 => Route.Disrupt, 2 => Route.Contract, _ => Route.Grow };
                    bool done = await RaceUi.ChooseEffect(RaceRuntime.RewardKey(reward), route, type == "Boss");
                    if (done) { reward.OnSkipped(); AccessTools.PropertySetter(typeof(Reward), nameof(Reward.SuccessfullySelected)).Invoke(reward, [true]); }
                    return done;
                }
            }
            if (reward is CardReward && s.Me.Has("siphon") && !s.Me.UsedServices.Contains("siphon:" + s.Me.BattleKey))
            {
                int action = await RaceUi.Choose("卡牌奖励", ["查看并选择卡牌", $"汲取：放弃本次选牌，获得{10 + 2 * s.Me.Effects["siphon"].Rank}金币及下场首回合2能量"]);
                if (action < 0) return false;
                if (action == 1)
                {
                    await s.Submit(s.New("siphon", s.Me.BattleKey)); await RaceModifier.Active!.ApplyReceipts(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(), false);
                    reward.OnSkipped(); AccessTools.PropertySetter(typeof(Reward), nameof(Reward.SuccessfullySelected)).Invoke(reward, [true]); RaceRuntime.Checkpoint(); return true;
                }
            }
            var key = RaceRuntime.RewardKey(reward);
            if (s.Engine.State.ConsumedRewards.Contains(s.LocalId + ":" + key)) return false;
            originalCalls.Add(reward); bool success;
            try { success = await reward.SelectUnsynchronized(); } finally { originalCalls.Remove(reward); }
            if (success) { await s.Submit(s.New("claim", key)); RaceRuntime.Checkpoint(); }
            return success;
        }
        finally { selecting.Remove(reward); }
    }
}

[HarmonyPatch(typeof(PlayerCmd), nameof(PlayerCmd.GainGold))]
internal static class GoldIncomePatch
{
    static void Prefix(Player player, ref decimal amount, bool wasStolenBack, out int __state)
    {
        __state = 0; if (!RaceRuntime.Live || RaceModifier.Active?.IsSuppressed == true || amount <= 0 || wasStolenBack) return;
        __state = (int)Math.Floor(amount);
        if (RaceSession.Current!.Me.Has("greedy_pocket") && !RaceSession.Current.Me.InBattle && RaceRuntime.State?.CurrentRoom is CombatRoom) amount = Math.Floor(amount * 1.25m);
    }
    static void Postfix(ref Task __result, int __state)
    { if (__state > 0) __result = After(__result, __state); }
    static async Task After(Task original, int income) { await original; if (RaceSession.Current is { } s) await s.Submit(s.New("gold", amount: income)); }
}

[HarmonyPatch(typeof(CardRarityOdds), nameof(CardRarityOdds.RollWithoutChangingFutureOdds), [typeof(CardRarityOddsType), typeof(float)])]
internal static class CardOddsPatch
{
    static bool Prefix(CardRarityOdds __instance, CardRarityOddsType type, float offset, ref CardRarity __result)
    {
        if (!RaceRuntime.Live || type == CardRarityOddsType.BossEncounter) return true;
        var s = RaceSession.Current!; int burden = s.Me.InBattle || RaceRuntime.State?.CurrentRoom is CombatRoom ? s.Me.BurdenSnapshot : s.Me.Burden;
        if (burden <= 0) return true;
        var getter = AccessTools.Method(typeof(CardRarityOdds), "GetBaseOdds");
        double rare = Math.Clamp((float)getter.Invoke(null, [type, CardRarity.Rare])! + offset, 0, 1);
        double uncommon = Math.Clamp((float)getter.Invoke(null, [type, CardRarity.Uncommon])!, 0, 1 - rare);
        double common = Math.Max(0, 1 - rare - uncommon), r = rare * Balance.RareMultiplier(burden, false), sum = r + uncommon + common;
        var rng = (MegaCrit.Sts2.Core.Random.Rng)AccessTools.Field(typeof(CardRarityOdds), "_rng").GetValue(__instance)!;
        var value = rng.NextFloat();
        __result = value < r / sum ? CardRarity.Rare : value < (r + uncommon) / sum ? CardRarity.Uncommon : CardRarity.Common; return false;
    }
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Factories.RelicFactory), nameof(MegaCrit.Sts2.Core.Factories.RelicFactory.RollRarity), [typeof(MegaCrit.Sts2.Core.Random.Rng)])]
internal static class RelicOddsPatch
{
    static bool Prefix(MegaCrit.Sts2.Core.Random.Rng rng, ref MegaCrit.Sts2.Core.Entities.Relics.RelicRarity __result)
    {
        if (!RaceRuntime.Live) return true; var s = RaceSession.Current!;
        int burden = RaceRuntime.State?.CurrentRoom is CombatRoom ? s.Me.BurdenSnapshot : s.Me.Burden; if (burden <= 0) return true;
        double rare = 0.17 * Balance.RareMultiplier(burden, true), sum = 0.83 + rare, x = rng.NextFloat();
        __result = x < rare / sum ? MegaCrit.Sts2.Core.Entities.Relics.RelicRarity.Rare : x < (rare + 0.33) / sum ? MegaCrit.Sts2.Core.Entities.Relics.RelicRarity.Uncommon : MegaCrit.Sts2.Core.Entities.Relics.RelicRarity.Common; return false;
    }
}

[HarmonyPatch(typeof(CombatRoom), nameof(CombatRoom.Exit))]
internal static class FinishWitheringPatch
{
    static void Postfix(ref Task __result) { if (RaceRuntime.Live) __result = After(__result); }
    static async Task After(Task original) { await original; if (RaceModifier.Active is { } modifier) await modifier.FinishWithering(); }
}

public sealed class RaceRestOption(Player owner, bool repair = false) : RestSiteOption(owner)
{
    public override string OptionId => repair ? "FDZF_REPAIR" : "FDZF_CLEANSE";
    public override IEnumerable<string> AssetPaths => [];
    public override LocString Description => new(ModEntry.Id, repair ? "repair.description" : "cleanse.description");
    public override bool IsEnabled => RaceRuntime.Live && !RaceSession.Current!.Me.UsedServices.Contains("camp:" + RaceRuntime.RoomKey);
    public override async Task<bool> OnSelect()
    {
        var s = RaceSession.Current!;
        if (repair) await s.Submit(s.New("repair", RaceRuntime.RoomKey));
        else { var id = await RaceUi.ChooseCleanse(CleanseAt.Camp); if (id == null) return false; await s.Submit(s.New("camp_cleanse", RaceRuntime.RoomKey, id)); }
        await RaceModifier.Active!.ApplyReceipts(new MegaCrit.Sts2.Core.GameActions.Multiplayer.ThrowingPlayerChoiceContext(), false); RaceRuntime.Checkpoint(); RaceUi.Refresh(); return true;
    }
}

[HarmonyPatch(typeof(RestSiteOption), nameof(RestSiteOption.Generate))]
internal static class CampOptionsPatch
{
    static void Postfix(Player player, List<RestSiteOption> __result)
    {
        if (!RaceRuntime.Live) return;
        if (RaceSession.Current!.Me.Effects.Values.Any(h => Catalog.Get(h.Id).Kind != EffectKind.Boon && Catalog.Get(h.Id).Tier == Tier.Ordinary)) __result.Add(new RaceRestOption(player));
        if (RaceSession.Current.Me.Has("bloom_decay")) __result.Add(new RaceRestOption(player, true));
    }
}

[HarmonyPatch(typeof(RestSiteOption), "get_Title")]
internal static class CampTitlePatch
{ static bool Prefix(RestSiteOption __instance, ref LocString __result) { if (__instance is not RaceRestOption) return true; __result = new(ModEntry.Id, __instance.OptionId == "FDZF_REPAIR" ? "repair.title" : "cleanse.title"); return false; } }

[HarmonyPatch(typeof(RestSiteOption), "get_Icon")]
internal static class CampIconPatch
{ static bool Prefix(RestSiteOption __instance, ref Texture2D __result) { if (__instance is not RaceRestOption) return true; __result = ModelDb.Modifier<RaceModifier>().Icon; return false; } }

[HarmonyPatch]
internal static class LocalizationPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(LocString), nameof(LocString.GetRawText));
        yield return AccessTools.Method(typeof(LocString), nameof(LocString.GetFormattedText));
    }
    static bool Prefix(LocString __instance, ref string __result)
    {
        if (__instance.LocTable == ModEntry.Id)
        {
            if (__instance.LocEntryKey.StartsWith("effect.", StringComparison.Ordinal) && __instance.LocEntryKey.EndsWith(".title", StringComparison.Ordinal))
            {
                __result = Catalog.Get(__instance.LocEntryKey[7..^6]).Name;
                return false;
            }
            __result = __instance.LocEntryKey switch
            {
                "repair.title" => "恢复耐久",
                "repair.description" => "恢复所有卡牌的耐久并解除枯萎。占用本次营地行动，不净化契约。",
                "cleanse.title" => "净化诅咒",
                "cleanse.description" => "移除一个普通档诅咒或双刃契约，包括全部强化。占用本次营地行动。",
                "mode.title" => "分道争锋",
                "mode.description" => "独立推进的多人竞速模式。",
                _ => __instance.LocEntryKey
            }; return false;
        }
        if (__instance.LocTable == "cards")
        {
            var key = __instance.LocEntryKey;
            if (key.StartsWith(ModelDb.Card<ClutterCard>().Id.Entry + ".", StringComparison.Ordinal)) { __result = key.EndsWith(".title", StringComparison.Ordinal) ? "杂念" : "不能打出。回合结束时消耗。"; return false; }
            if (key.StartsWith(ModelDb.Card<WitheredCard>().Id.Entry + ".", StringComparison.Ordinal)) { __result = key.EndsWith(".title", StringComparison.Ordinal) ? "枯萎的卡牌" : "抽1张牌。消耗。可在营地恢复为原卡。"; return false; }
        }
        return true;
    }
}
