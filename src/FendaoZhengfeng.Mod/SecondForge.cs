using System.Runtime.CompilerServices;
using FendaoZhengfeng.Core;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace FendaoZhengfeng.Mod;

internal static class SecondForge
{
    internal const string SaveKey = nameof(RaceModifier.FdzfSecondForgeBonus);
    private sealed class Entry(int bonus) { internal int Bonus = bonus; }
    private static readonly ConditionalWeakTable<CardModel, Entry> bonuses = new();
    internal static int Bonus(CardModel card) => bonuses.TryGetValue(card, out var entry) ? entry.Bonus : 0;
    private static IEnumerable<DynamicVar> NumericVars(CardModel card) => card.DynamicVars.Values
        .Where(value => value is DamageVar or BlockVar or HealVar or CalculatedDamageVar or CalculatedBlockVar)
        .Select(value => value is CalculatedVar ? (DynamicVar)AccessTools.Method(typeof(CalculatedVar), "GetBaseVar").Invoke(value, null)! : value)
        .Distinct();
    internal static bool Available(CardModel card) => !card.IsCanonical && RaceRuntime.Live && ModelDb.GetById<CardModel>(card.Id).MaxUpgradeLevel == 1 && card.Owner?.RunState == RaceRuntime.State
        && RaceSession.Current!.Me.Has("second_forge") && NumericVars(card).Any();
    internal static void Apply(CardModel card, int bonus)
    {
        int previous = Bonus(card);
        bonuses.Remove(card); bonuses.Add(card, new(bonus));
        foreach (var value in NumericVars(card)) value.UpgradeValueBy(bonus - previous);
        AccessTools.Field(typeof(CardModel), "_currentUpgradeLevel").SetValue(card, 2);
        card.DynamicVars.RecalculateForUpgradeOrEnchant();
        (AccessTools.Field(typeof(CardModel), "Upgraded").GetValue(card) as Action)?.Invoke();
    }
    internal static void Copy(CardModel source, CardModel clone)
    { int bonus = Bonus(source); if (bonus > 0) { bonuses.Remove(clone); bonuses.Add(clone, new(bonus)); } }
    internal static void Forget(CardModel card) => bonuses.Remove(card);
    internal static void RefreshOwned()
    {
        if (!RaceRuntime.Live || RaceSession.Current is not { } session || !session.Me.Has("second_forge")) return;
        var player = RaceRuntime.State!.Players.Single(); var cards = player.Deck.Cards.ToHashSet();
        if (player.PlayerCombatState != null)
            foreach (var pile in new[] { MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand, MegaCrit.Sts2.Core.Entities.Cards.PileType.Draw, MegaCrit.Sts2.Core.Entities.Cards.PileType.Discard, MegaCrit.Sts2.Core.Entities.Cards.PileType.Exhaust, MegaCrit.Sts2.Core.Entities.Cards.PileType.Play })
                cards.UnionWith(pile.GetPile(player).Cards);
        int desired = 3 + session.Me.Effects["second_forge"].Rank;
        foreach (var card in cards.Where(card => Bonus(card) > 0 && Bonus(card) < desired)) Apply(card, desired);
    }
}
[HarmonyPatch(typeof(CardModel), "get_MaxUpgradeLevel")]
internal static class SecondForgeMaximumPatch
{
    static void Postfix(CardModel __instance, ref int __result)
    { if (__result == 1 && (SecondForge.Bonus(__instance) > 0 || SecondForge.Available(__instance))) __result = 2; }
}
[HarmonyPatch(typeof(CardModel), nameof(CardModel.UpgradeInternal))]
internal static class SecondForgeUpgradePatch
{
    static bool Prefix(CardModel __instance)
    {
        if (__instance.CurrentUpgradeLevel != 1 || !SecondForge.Available(__instance) || AdvancedEffects.UpgradeBlocked(__instance)) return true;
        SecondForge.Apply(__instance, 3 + RaceSession.Current!.Me.Effects["second_forge"].Rank); return false;
    }
}
[HarmonyPatch(typeof(AbstractModel), nameof(AbstractModel.MutableClone))]
internal static class SecondForgeClonePatch
{
    static void Postfix(AbstractModel __instance, AbstractModel __result)
    { if (__instance is CardModel source && __result is CardModel clone) SecondForge.Copy(source, clone); }
}
[HarmonyPatch(typeof(CardModel), nameof(CardModel.DowngradeInternal))]
internal static class SecondForgeDowngradePatch
{
    // Clear metadata before the native upgrade-change event can serialize it.
    static void Prefix(CardModel __instance) => SecondForge.Forget(__instance);
}
[HarmonyPatch(typeof(CardModel), nameof(CardModel.ToSerializable))]
internal static class SecondForgeSavePatch
{
    static void Postfix(CardModel __instance, SerializableCard __result)
    {
        int bonus = SecondForge.Bonus(__instance); if (bonus <= 0) return;
        __result.Props ??= new(); __result.Props.ints ??= [];
        __result.Props.ints.RemoveAll(value => value.name == SecondForge.SaveKey);
        __result.Props.ints.Add(new(SecondForge.SaveKey, bonus));
    }
}
[HarmonyPatch(typeof(CardModel), nameof(CardModel.FromSerializable))]
internal static class SecondForgeLoadPatch
{
    static void Prefix(ref SerializableCard save, out int __state)
    {
        __state = save.Props?.ints?.FirstOrDefault(value => value.name == SecondForge.SaveKey).value ?? 0;
        if (__state <= 0) return;
        if (save.CurrentUpgradeLevel != 2 || __state > 1000000) throw new InvalidDataException("二次强化存档数值无效。");
        var props = save.Props!;
        save = new SerializableCard
        {
            Id = save.Id, CurrentUpgradeLevel = 1, Enchantment = save.Enchantment, FloorAddedToDeck = save.FloorAddedToDeck,
            Props = new SavedProperties { ints = props.ints!.Where(value => value.name != SecondForge.SaveKey).ToList(),
                bools = props.bools, strings = props.strings, intArrays = props.intArrays, modelIds = props.modelIds, cards = props.cards, cardArrays = props.cardArrays }
        };
    }
    static void Postfix(CardModel __result, int __state)
    { if (__state > 0) { SecondForge.Apply(__result, __state); __result.FinalizeUpgradeInternal(); } }
}
