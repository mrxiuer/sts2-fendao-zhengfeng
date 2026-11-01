using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace FendaoZhengfeng.Mod;

public sealed partial class RaceModifier
{
    [SavedProperty] public string FdzfDeckIdentity { get; set; } = "";
}

internal static class SharedCardIdentity
{
    internal const string SaveKey = nameof(RaceModifier.FdzfDeckIdentity);
    private sealed record Entry(string Id);
    private static readonly ConditionalWeakTable<CardModel, Entry> identities = new();
    internal static string? Read(CardModel card) => identities.TryGetValue(card, out var value) ? value.Id : null;
    internal static void Write(CardModel card, string id) { identities.Remove(card); identities.Add(card, new(id)); }
}

// Native co-op replaces remote permanent card objects when synchronizing player state.
// Carry identity in the card's own packet rather than relying on object references.
[HarmonyPatch(typeof(CardModel), nameof(CardModel.ToSerializable))]
internal static class SharedCardIdentitySavePatch
{
    static void Postfix(CardModel __instance, SerializableCard __result)
    {
        if (RaceSession.Current?.Engine.State.Synchronous != true || __instance.IsCanonical) return;
        string? id = SharedCardIdentity.Read(__instance);
        if (__instance.Pile?.Type == PileType.Deck && RaceRuntime.RacerFor(__instance.Owner) != null)
            id = RaceModifier.ForPlayer(__instance.Owner)?.CardIdentity(__instance);
        if (id == null) return;
        __result.Props ??= new(); __result.Props.strings ??= [];
        __result.Props.strings.RemoveAll(value => value.name == SharedCardIdentity.SaveKey);
        __result.Props.strings.Add(new(SharedCardIdentity.SaveKey, id));
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.FromSerializable))]
internal static class SharedCardIdentityLoadPatch
{
    static void Postfix(SerializableCard save, CardModel __result)
    {
        string? id = save.Props?.strings?.FirstOrDefault(value => value.name == SharedCardIdentity.SaveKey).value;
        if (!string.IsNullOrEmpty(id)) SharedCardIdentity.Write(__result, id);
    }
}
