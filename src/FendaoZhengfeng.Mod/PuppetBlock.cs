using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.ValueProps;

namespace FendaoZhengfeng.Mod;

public sealed partial class RaceModifier
{
    public override bool ShouldClearBlock(Creature creature) => PuppetFollower.Guard(creature) == null;
    public override decimal ModifyBlockAdditive(Creature target, decimal block, ValueProp props, CardModel? cardSource, CardPlay? cardPlay)
        => PuppetFollower.Guard(target)?.GetPower<DexterityPower>() is { } dex
            ? dex.ModifyBlockAdditive(dex.Owner, block, props, null, cardPlay) : 0;
    private decimal PuppetBlockRatio(Creature target, decimal block, ValueProp props, CardModel? cardSource, CardPlay? cardPlay)
        => PuppetFollower.Guard(target)?.GetPower<FrailPower>() is { } frail
            ? frail.ModifyBlockMultiplicative(frail.Owner, block, props, null, cardPlay) : 1;
}

// Cards such as Body Slam and Entrench read the protected body's block,
// but the actual block storage and its change events remain on the follower.
[HarmonyPatch(typeof(Creature), nameof(Creature.Block), MethodType.Getter)]
internal static class PuppetBlockViewPatch
{
    static bool Prefix(Creature __instance, ref int __result)
    { if (PuppetFollower.Guard(__instance) is not { } puppet) return true; __result = puppet.Block; return false; }
}
[HarmonyPatch(typeof(Creature), nameof(Creature.GainBlockInternal))]
internal static class PuppetGainBlockPatch
{
    static bool Prefix(Creature __instance, decimal amount)
    { if (PuppetFollower.Guard(__instance) is not { } puppet) return true; puppet.GainBlockInternal(amount); return false; }
}
[HarmonyPatch(typeof(Creature), nameof(Creature.LoseBlockInternal))]
internal static class PuppetLoseBlockPatch
{
    static bool Prefix(Creature __instance, decimal amount)
    { if (PuppetFollower.Guard(__instance) is not { } puppet) return true; puppet.LoseBlockInternal(amount); return false; }
}
// Show block once, on the real follower. Keep the logical getter for cards.
[HarmonyPatch(typeof(NHealthBar), "RefreshBlockUi")]
internal static class PuppetOwnerBlockUiPatch
{
    static bool Prefix(Creature ____creature, Control ____blockContainer, Control ____blockOutline, Control ____hpForeground)
    {
        if (PuppetFollower.Guard(____creature) == null) return true;
        ____blockContainer.Visible = false; ____blockOutline.Visible = false;
        ____hpForeground.SelfModulate = new Color("F1373E"); return false;
    }
}
