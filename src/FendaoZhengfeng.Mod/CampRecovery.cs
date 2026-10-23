using System.Collections;
using System.Runtime.CompilerServices;
using FendaoZhengfeng.Core;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace FendaoZhengfeng.Mod;

internal sealed record SavedCampOption(string Id, int SmithCount = 1);
internal sealed record SavedCamp(string RoomKey, SavedCampOption[] Options, uint? LastChosen, bool Completed);

internal static class CampRecovery
{
    internal const string SaveKey = "fdzf_camp_v1";
    private static readonly ConditionalWeakTable<RestSiteRoom, SavedCamp> pending = new();
    private static IList Records(RestSiteSynchronizer synchronizer)
        => (IList)AccessTools.Field(typeof(RestSiteSynchronizer), "_restSites").GetValue(synchronizer)!;
    internal static SavedCamp Capture(RestSiteRoom room)
    {
        var records = Records(RunManager.Instance.RestSiteSynchronizer);
        if (records.Count != 1) throw new InvalidDataException(L10n.T("营地尚未完成初始化。"));
        object record = records[0]!; var type = record.GetType();
        var completion = (TaskCompletionSource)AccessTools.Field(type, "completionTaskSource").GetValue(record)!;
        var options = room.Options.Select(option =>
        {
            if (option.GetType().Assembly != typeof(RestSiteOption).Assembly && option is not RaceRestOption)
                throw new InvalidDataException(L10n.T("尚不支持保存第三方营地选项：") + option.GetType().FullName);
            return new SavedCampOption(option.OptionId, option is SmithRestSiteOption smith ? smith.SmithCount : 1);
        }).ToArray();
        return new(RaceRuntime.RoomKey, options, (uint?)AccessTools.Field(type, "lastChosenOptionIndex").GetValue(record), completion.Task.IsCompleted);
    }
    internal static RestSiteRoom FromSave(SavedCamp save)
    {
        var room = new RestSiteRoom(); pending.Add(room, save);
        RaceRuntime.BindRestoredRoom(room, save.RoomKey); return room;
    }
    private static RestSiteOption RestoreOption(SavedCampOption save, Player player) => save.Id switch
    {
        "HEAL" => new HealRestSiteOption(player),
        "SMITH" => new SmithRestSiteOption(player) { SmithCount = save.SmithCount },
        "MEND" => new MendRestSiteOption(player),
        "CLONE" => new CloneRestSiteOption(player),
        "COOK" => new CookRestSiteOption(player),
        "DIG" => new DigRestSiteOption(player),
        "HATCH" => new HatchRestSiteOption(player),
        "KINDLE" => new KindleRestSiteOption(player),
        "LIFT" => new LiftRestSiteOption(player),
        "FDZF_CLEANSE" => new RaceRestOption(player),
        "FDZF_REPAIR" => new RaceRestOption(player, true),
        _ => throw new InvalidDataException(L10n.T("不认识保存的营地选项：") + save.Id)
    };
    internal static bool TryRestore(RestSiteSynchronizer synchronizer)
    {
        if (RaceRuntime.State is not { CurrentRoom: RestSiteRoom room } run || !pending.TryGetValue(room, out var save)) return false;
        if (run.Players.Count != 1) throw new InvalidDataException(L10n.T("营地恢复必须使用独立单人进度。"));
        var records = Records(synchronizer);
        var type = AccessTools.Inner(typeof(RestSiteSynchronizer), "PlayerRestSite");
        var record = Activator.CreateInstance(type, nonPublic: true)!;
        var options = save.Options.Select(option => RestoreOption(option, run.Players.Single())).ToList();
        AccessTools.Field(type, "options").SetValue(record, options);
        AccessTools.Field(type, "lastChosenOptionIndex").SetValue(record, save.LastChosen);
        if (save.Completed || options.Count == 0)
            ((TaskCompletionSource)AccessTools.Field(type, "completionTaskSource").GetValue(record)!).TrySetResult();
        records.Clear(); records.Add(record); pending.Remove(room); return true;
    }
}

[HarmonyPatch(typeof(AbstractRoom), nameof(AbstractRoom.FromSerializable))]
internal static class RestoreCampRoomPatch
{
    static bool Prefix(SerializableRoom? serializableRoom, ref AbstractRoom? __result)
    {
        if (!RaceRuntime.Live || serializableRoom is not { RoomType: RoomType.RestSite }
            || serializableRoom.EncounterState?.TryGetValue(CampRecovery.SaveKey, out var json) != true || json == null) return true;
        __result = CampRecovery.FromSave(StateJson.Deserialize<SavedCamp>(json)); return false;
    }
}

[HarmonyPatch(typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.BeginRestSite))]
internal static class RestoreCampOptionsPatch
{
    static bool Prefix(RestSiteSynchronizer __instance)
        => !RaceRuntime.Live || !CampRecovery.TryRestore(__instance);
}

// The native synchronizer removes consumed options only after OnSelect returns.
// Capture the committed camp rather than the still-open list inside its option.
[HarmonyPatch(typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.ChooseLocalOption))]
internal static class CommitCampCheckpointPatch
{
    static void Postfix(ref Task<bool> __result)
    { if (RaceRuntime.Live) __result = AfterCommit(__result); }
    private static async Task<bool> AfterCommit(Task<bool> original)
    {
        bool selected = await original;
        if (selected && RaceRuntime.Live && RaceRuntime.State?.CurrentRoom is RestSiteRoom) RaceRuntime.Checkpoint();
        return selected;
    }
}
