using FendaoZhengfeng.Core;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rooms;

namespace FendaoZhengfeng.Mod;

internal static class FiveActCampaign
{
    internal const int ActCount = 5;
    // Extra acts use native act-two/act-three encounter and art pools, with their own
    // room rolls, map seeds, double bosses and escalating combat multipliers.
    internal static IReadOnlyList<ActModel> Expand(IReadOnlyList<ActModel> acts)
    {
        if (acts.Count == ActCount) return acts;
        if (acts.Count != 3) throw new RuleException("分道争锋需要三个原生基础幕来生成五幕比赛。");
        return [acts[0], acts[1], acts[2], acts[1], acts[2]];
    }
    internal static decimal ExtraMonsterMultiplier(int act, int resets) => 1 + Math.Max(0, act - 2) * 0.2m + resets * 0.2m;
    internal static bool IsFinalBoss(RunState run) => run.CurrentMapCoord == (run.Map.SecondBossMapPoint ?? run.Map.BossMapPoint).coord;
    internal static void PrepareRooms(RunState run)
    {
        for (int index = 3; index < run.Acts.Count; index++)
        {
            var act = run.Acts[index];
            var bosses = act.AllBossEncounters.OrderBy(boss => boss.Id.ToString(), StringComparer.Ordinal).ToList();
            if (bosses.Count < 2) throw new RuleException("新增幕需要至少两种原生Boss遭遇。");
            var random = new StableRandom(run.Rng.Seed + ":race-bosses:" + index);
            int first = (int)(random.Next() * bosses.Count);
            int second = (first + 1 + (int)(random.Next() * (bosses.Count - 1))) % bosses.Count;
            act.SetBossEncounter(bosses[first]); act.SetSecondBossEncounter(bosses[second]);
        }
    }
    internal static void ResetEncounters(RunState run)
    {
        foreach (var act in run.Acts)
        {
            var rooms = (RoomSet)AccessTools.Field(typeof(ActModel), "_rooms").GetValue(act)!;
            rooms.eventsVisited = 0; rooms.normalEncountersVisited = 0;
            rooms.eliteEncountersVisited = 0; rooms.bossEncountersVisited = 0;
        }
    }
    internal static StandardActMap CreateLateMap(RunState run)
    {
        var rng = new Rng(run.Rng.Seed, $"act_{run.CurrentActIndex + 1}_map");
        var native = run.Act.GetMapPointTypes(rng);
        var counts = new MapPointTypeCounts(native.NumOfUnknowns, native.NumOfRests)
        {
            NumOfElites = native.NumOfElites + run.CurrentActIndex - 2,
            PointTypesThatIgnoreRules = [.. native.PointTypesThatIgnoreRules]
        };
        return new StandardActMap(rng, run.Act, false, false, true, counts);
    }
}

[HarmonyPatch(typeof(NGame), nameof(NGame.StartNewSingleplayerRun))]
internal static class FiveActStartPatch
{
    static void Prefix(ref IReadOnlyList<ActModel> acts, IReadOnlyList<ModifierModel> modifiers)
    { if (RaceSession.Current != null && modifiers.Any(modifier => modifier is RaceModifier)) acts = FiveActCampaign.Expand(acts); }
}
[HarmonyPatch(typeof(RunManager), nameof(RunManager.GenerateRooms))]
internal static class FiveActRoomsPatch
{ static void Postfix() { if (RaceRuntime.Live) FiveActCampaign.PrepareRooms(RaceRuntime.State!); } }
[HarmonyPatch(typeof(StandardActMap), nameof(StandardActMap.CreateFor))]
internal static class FiveActMapPatch
{
    static bool Prefix(RunState runState, bool replaceTreasureWithElites, ref StandardActMap __result)
    {
        if (!RaceRuntime.Live || runState != RaceRuntime.State || runState.CurrentActIndex < 3) return true;
        __result = FiveActCampaign.CreateLateMap(runState); return false;
    }
}
