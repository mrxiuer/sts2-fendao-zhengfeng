using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace FendaoZhengfeng.Mod;

public sealed class CharmRedirectPower : PowerModel
{
    public override PowerType Type => PowerType.Debuff;
    public override PowerStackType StackType => PowerStackType.Counter;
    public override LocString Title => CharmStrings.Text("power.title");
    public override LocString Description => CharmStrings.Text("power.description");

    public override async Task AfterSideTurnEnd(PlayerChoiceContext context, CombatSide side, IEnumerable<Creature> participants)
    {
        if (side == CombatSide.Enemy && Owner.IsEnemy && Owner.IsAlive && participants.Contains(Owner))
            await PowerCmd.TickDownDuration(this);
    }
}

[HarmonyPatch]
internal static class CharmPowerIconPatch
{
    static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    { yield return AccessTools.PropertyGetter(typeof(PowerModel), nameof(PowerModel.Icon)); yield return AccessTools.PropertyGetter(typeof(PowerModel), nameof(PowerModel.BigIcon)); }
    static bool Prefix(PowerModel __instance, ref Texture2D __result)
    { if (__instance is not CharmRedirectPower) return true; __result = EffectIcons.Get("heartstrings_charm"); return false; }
}
[HarmonyPatch(typeof(PowerModel), "get_HoverTips")]
internal static class CharmPowerHoverPatch
{
    static bool Prefix(PowerModel __instance, ref IEnumerable<IHoverTip> __result)
    {
        if (__instance is not CharmRedirectPower power) return true;
        __result = [new HoverTip(CharmStrings.Text("power.title"), L10n.F($"剩余{power.Amount}个敌方回合：攻击与负面状态转向其他敌人；无同伴则转向自身。增益转给玩家。"), EffectIcons.Get("heartstrings_charm"))];
        return false;
    }
}
