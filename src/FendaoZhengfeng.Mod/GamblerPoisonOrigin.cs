using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;

namespace FendaoZhengfeng.Mod;

// Native Poison deliberately omits the dealer from both previews and damage.
// Preserve its actual applier locally, without changing native damage ownership
// for other relics, history, or hooks and without treating arbitrary null damage
// as player damage.
internal static class GamblerPoisonOrigin
{
    internal static readonly AsyncLocal<PoisonPower?> Current = new();
    internal static Creature? ApplierFor(Creature? target) => Current.Value is { } power && power.Owner == target ? power.Applier : null;
    [ThreadStatic] internal static bool EnterOriginal;
    internal static async Task Trigger(PoisonPower power)
    {
        var previous = Current.Value; Current.Value = power;
        try { EnterOriginal = true; await power.Trigger(); }
        finally { EnterOriginal = false; Current.Value = previous; }
    }
}
[HarmonyPatch(typeof(PoisonPower), nameof(PoisonPower.Trigger))]
internal static class GamblerPoisonTriggerPatch
{
    static bool Prefix(PoisonPower __instance, ref Task __result)
    {
        if (GamblerPoisonOrigin.EnterOriginal) { GamblerPoisonOrigin.EnterOriginal = false; return true; }
        if (!RaceRuntime.Live || RaceSession.Current?.Me.Has("gamblers_heart") != true) return true;
        __result = GamblerPoisonOrigin.Trigger(__instance); return false;
    }
}
[HarmonyPatch(typeof(PoisonPower), nameof(PoisonPower.CalculateTotalDamageNextTurn))]
internal static class GamblerPoisonPreviewPatch
{
    static void Prefix(PoisonPower __instance, out PoisonPower? __state)
    { __state = GamblerPoisonOrigin.Current.Value; GamblerPoisonOrigin.Current.Value = __instance; }
    static void Finalizer(PoisonPower? __state) => GamblerPoisonOrigin.Current.Value = __state;
}
