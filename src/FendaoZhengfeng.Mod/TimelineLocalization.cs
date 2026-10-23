using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace FendaoZhengfeng.Mod;

[HarmonyPatch]
internal static class TimelineLocalizationPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(LocString), nameof(LocString.GetRawText));
        yield return AccessTools.Method(typeof(LocString), nameof(LocString.GetFormattedText));
    }

    private static bool Prefix(LocString __instance, ref string __result)
    {
        if (__instance.LocTable != "cards") return true;
        string prefix = ModelDb.Card<ExtraTimelineCard>().Id.Entry + ".";
        if (__instance.LocEntryKey == prefix + "title")
        {
            __result = "额外时间线";
            return false;
        }
        if (__instance.LocEntryKey == prefix + "description")
        {
            __result = "选择对手牌组中1张牌，免费打出其副本。不能选择「额外时间线」。";
            return false;
        }
        return true;
    }
}
