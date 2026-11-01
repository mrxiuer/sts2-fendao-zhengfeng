using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace FendaoZhengfeng.Mod;

public sealed partial class RaceModifier
{
    [SavedProperty] public int FdzfCardWear { get; set; }
}

internal static class SharedCardWear
{
    internal const string SaveKey = nameof(RaceModifier.FdzfCardWear);
    private sealed record Entry(int Remaining);
    private static readonly ConditionalWeakTable<CardModel, Entry> values = new();
    internal static int? Read(CardModel card) => values.TryGetValue(card, out var value) ? value.Remaining : null;
    internal static void Write(CardModel card, int remaining)
    { values.Remove(card); values.Add(card, new(Math.Clamp(remaining, 0, 8))); }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.ToSerializable))]
internal static class SharedCardWearSavePatch
{
    static void Postfix(CardModel __instance, SerializableCard __result)
    {
        if (RaceSession.Current?.Engine.State.Synchronous != true || __instance.IsCanonical) return;
        int? remaining = SharedCardWear.Read(__instance);
        if (__instance.Pile?.Type == PileType.Deck && RaceRuntime.RacerFor(__instance.Owner) != null)
            remaining = RaceModifier.ForPlayer(__instance.Owner)?.CardWear(__instance);
        if (remaining == null) return;
        __result.Props ??= new(); __result.Props.ints ??= [];
        __result.Props.ints.RemoveAll(value => value.name == SharedCardWear.SaveKey);
        __result.Props.ints.Add(new(SharedCardWear.SaveKey, remaining.Value));
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.FromSerializable))]
internal static class SharedCardWearLoadPatch
{
    static void Postfix(SerializableCard save, CardModel __result)
    {
        var entries = save.Props?.ints?.Where(value => value.name == SharedCardWear.SaveKey).ToArray();
        if (entries is { Length: > 0 }) SharedCardWear.Write(__result, entries[0].value);
    }
}
