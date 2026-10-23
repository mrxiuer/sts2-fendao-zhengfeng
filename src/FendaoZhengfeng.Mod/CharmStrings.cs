using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace FendaoZhengfeng.Mod;

internal static class CharmStrings
{
    internal const string TableName = "charm";

    internal static readonly Dictionary<string, string> Values = new()
    {
        ["card.title"] = "魅惑",
        ["card.description"] = "魅惑1名敌人2回合：攻击与负面状态转向同伴，无同伴则转向自身；增益转给你。",
        ["power.title"] = "牵心丝",
        ["power.description"] = "接下来{Amount}个敌方回合，攻击与负面状态转向其他敌人；若没有其他敌人，则转向自身。它给自己施加的增益改由玩家获得。"
    };

    internal static LocString Text(string key) => new(TableName, key);
}

[HarmonyPatch(typeof(LocManager), nameof(LocManager.GetTable))]
internal static class CharmLocTablePatch
{
    private static readonly LocTable chinese = new(CharmStrings.TableName, CharmStrings.Values);
    private static readonly LocTable english = new(CharmStrings.TableName, L10n.Table(CharmStrings.Values));

    private static bool Prefix(string __0, ref LocTable __result)
    {
        if (__0 != CharmStrings.TableName) return true;
        __result = L10n.English ? english : chinese;
        return false;
    }
}

[HarmonyPatch(typeof(CardModel), "get_Description")]
internal static class CharmCardDescriptionPatch
{
    private static bool Prefix(CardModel __instance, ref LocString __result)
    {
        if (__instance is not CharmCard) return true;
        __result = CharmStrings.Text("card.description");
        return false;
    }
}
