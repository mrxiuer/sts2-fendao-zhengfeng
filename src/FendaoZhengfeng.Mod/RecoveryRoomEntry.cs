using HarmonyLib;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace FendaoZhengfeng.Mod;

internal static class RecoveryRoomEntry
{
    // Host backups contain post-entry resources. A legacy shared checkpoint
    // with no explicit room is still pre-entry and must run the normal hooks.
    internal static bool AlreadyEntered(IRunState run)
        => RaceRuntime.Live && RaceRuntime.LoadingSavedRoom && RaceRuntime.RestoringCapturedRoom
            && ReferenceEquals(run, RaceRuntime.State);
    internal static async Task RestoreModeTracking(AbstractRoom room)
    {
        if (RaceRuntime.RestoringParentEvent || RaceModifier.Active is not { } modifier) return;
        await modifier.AfterRoomEntered(room);
        modifier.InvokeExecutionFinished();
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeRoomEntered))]
internal static class SkipCapturedRoomBeforeEntryPatch
{
    static bool Prefix(IRunState __0, ref Task __result)
    {
        if (!RecoveryRoomEntry.AlreadyEntered(__0)) return true;
        __result = Task.CompletedTask; return false;
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterRoomEntered))]
internal static class SkipCapturedRoomAfterEntryPatch
{
    static bool Prefix(IRunState __0, AbstractRoom __1, ref Task __result)
    {
        if (!RecoveryRoomEntry.AlreadyEntered(__0)) return true;
        __result = RecoveryRoomEntry.RestoreModeTracking(__1); return false;
    }
}

// A debug/event room can exist before any point was visited in a new act.
// Native loading otherwise ignores its explicit room and enters a blank map.
[HarmonyPatch(typeof(RunManager), nameof(RunManager.LoadIntoLatestMapCoord))]
internal static class RestoreCapturedRoomWithoutMapHistoryPatch
{
    static bool Prefix(RunManager __instance, AbstractRoom? preFinishedRoom, ref Task __result)
    {
        if (preFinishedRoom == null || RaceRuntime.State is not { } run || run.VisitedMapCoords.Count != 0
            || !RecoveryRoomEntry.AlreadyEntered(run)) return true;
        __result = Enter(__instance, preFinishedRoom); return false;
    }
    private static Task Enter(RunManager manager, AbstractRoom room)
    {
        // The normal map-entry path initializes replay/checksum recording before
        // starting combat. Preserve that setup even when no map point exists.
        if (manager.CombatReplayWriter.IsEnabled) manager.CombatReplayWriter.RecordInitialState(manager.ToSave(null));
        return manager.EnterRoom(room);
    }
}
