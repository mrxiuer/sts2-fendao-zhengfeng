using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Runs;

namespace FendaoZhengfeng.Mod;

// Native AfterPreventingDeath calls only the actual preventer, rather than all
// modifiers. Await the whole restoration before recording a surviving player.
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPreventingDeath))]
internal static class RevivalHistoryPatch
{
    static void Postfix(IRunState runState, Creature creature, ref Task __result)
    {
        if (!RaceRuntime.Live || !ReferenceEquals(runState, RaceRuntime.State) || !creature.IsPlayer) return;
        var modifier = RaceModifier.Active;
        if (modifier != null) __result = Record(__result, modifier, creature);
    }
    private static async Task Record(Task restoration, RaceModifier modifier, Creature creature)
    {
        await restoration;
        if (RaceRuntime.Live && ReferenceEquals(modifier, RaceModifier.Active)) await modifier.RecordRevival(creature);
    }
}
