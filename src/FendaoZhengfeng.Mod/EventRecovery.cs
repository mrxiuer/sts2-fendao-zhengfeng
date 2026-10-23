using System.Reflection;
using System.Runtime.CompilerServices;
using FendaoZhengfeng.Core;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace FendaoZhengfeng.Mod;

internal sealed record SavedFinishedEvent(string RoomKey, string Description, string RngPacket);
internal sealed record SavedOpenEvent(string RoomKey, string Page, string RngPacket, decimal Gold, decimal Heal, decimal HpLoss);
internal static class EventRecovery
{
    internal const string OpenKey = "fdzf_open_event_v1";
    private static readonly ConditionalWeakTable<EventRoom, SavedOpenEvent> openPending = new();
    internal const string FinishedKey = "fdzf_finished_event_v1";
    private static readonly ConditionalWeakTable<EventRoom, SavedFinishedEvent> pending = new();
    private static readonly ConditionalWeakTable<EventModel, object> restoredModels = new();
    private static readonly object marker = new();
    internal static SavedFinishedEvent CaptureFinished(EventRoom room)
    {
        var model = room.LocalMutableEvent;
        if (!model.IsFinished || model.LayoutType != EventLayoutType.Default) throw new InvalidDataException("不是可保存的已结束普通事件。");
        var writer = new PacketWriter(); model.Rng.ToSerializable().Serialize(writer);
        return new(RaceRuntime.RoomKey, model.Description?.GetFormattedText() ?? "", Convert.ToBase64String(writer.Buffer, 0, writer.BytePosition));
    }
    internal static EventRoom FromFinishedSave(SerializableRoom save, SavedFinishedEvent finished)
    {
        var room = new EventRoom(save); pending.Add(room, finished);
        RaceRuntime.BindRestoredRoom(room, finished.RoomKey); return room;
    }
    internal static SavedOpenEvent? CaptureOpen(EventRoom room)
    {
        var model = room.LocalMutableEvent;
        if (model is not DenseVegetation || model.IsFinished) return null;
        var page = model.Description?.LocEntryKey;
        if (page is not ("DENSE_VEGETATION.pages.INITIAL.description" or "DENSE_VEGETATION.pages.REST.description")) return null;
        var writer = new PacketWriter(); model.Rng.ToSerializable().Serialize(writer);
        return new(RaceRuntime.RoomKey, page, Convert.ToBase64String(writer.Buffer, 0, writer.BytePosition),
            model.DynamicVars.Gold.BaseValue, model.DynamicVars.Heal.BaseValue, model.DynamicVars.HpLoss.BaseValue);
    }
    internal static EventRoom FromOpenSave(SerializableRoom save, SavedOpenEvent page)
    {
        var room = new EventRoom(save); openPending.Add(room, page);
        RaceRuntime.BindRestoredRoom(room, page.RoomKey); return room;
    }
    private static bool TryRestoreOpen(EventModel model, Player player, EventCombatSynchronizer? synchronizer)
    {
        if (player.RunState.CurrentRoom is not EventRoom room || !openPending.TryGetValue(room, out var save)) return false;
        if (model is not DenseVegetation || room.CanonicalEvent.Id != model.Id
            || save.Page is not ("DENSE_VEGETATION.pages.INITIAL.description" or "DENSE_VEGETATION.pages.REST.description"))
            throw new InvalidDataException("无法恢复该事件页面。");
        Initialize(model, player, synchronizer, save.RngPacket);
        model.DynamicVars.Gold.BaseValue = save.Gold; model.DynamicVars.Heal.BaseValue = save.Heal; model.DynamicVars.HpLoss.BaseValue = save.HpLoss;
        IEnumerable<EventOption> options;
        if (save.Page.EndsWith(".INITIAL.description", StringComparison.Ordinal))
            options = (IReadOnlyList<EventOption>)AccessTools.Method(typeof(DenseVegetation), "GenerateInitialOptions").Invoke(model, null)!;
        else
            options = [new EventOption(model, () => (Task)AccessTools.Method(typeof(DenseVegetation), "Fight").Invoke(model, null)!, "DENSE_VEGETATION.pages.REST.options.FIGHT")];
        AccessTools.Method(typeof(EventModel), "SetEventState").Invoke(model, [new LocString(model.LocTable, save.Page), options]);
        restoredModels.Add(model, marker); openPending.Remove(room); return true;
    }
    private static void Initialize(EventModel model, Player player, EventCombatSynchronizer? synchronizer, string packet)
    {
        var reader = new PacketReader(); reader.Reset(Convert.FromBase64String(packet));
        var rng = new SerializableRng(); rng.Deserialize(reader);
        AccessTools.PropertySetter(typeof(EventModel), nameof(EventModel.Owner)).Invoke(model, [player]);
        AccessTools.PropertySetter(typeof(EventModel), nameof(EventModel.Rng)).Invoke(model, [new Rng(rng)]);
        AccessTools.Field(typeof(EventModel), "_combatSynchronizer").SetValue(model, synchronizer);
    }
    internal static bool TryRestorePage(EventModel model, Player player, EventCombatSynchronizer? synchronizer)
    {
        if (TryRestoreOpen(model, player, synchronizer)) return true;
        if (player.RunState.CurrentRoom is not EventRoom room || !pending.TryGetValue(room, out var save)
            || room.CanonicalEvent.Id != model.Id) return false;
        Initialize(model, player, synchronizer, save.RngPacket);
        AccessTools.Field(typeof(EventModel), "_currentOptions").SetValue(model, new List<EventOption>());
        AccessTools.Field(typeof(EventModel), "_cleanupCalled").SetValue(model, true);
        AccessTools.Field(typeof(EventModel), "_isFinished").SetValue(model, true);
        AccessTools.PropertySetter(typeof(EventModel), nameof(EventModel.Description)).Invoke(model, [NativeChoiceUi.Text(save.Description)]);
        restoredModels.Add(model, marker); pending.Remove(room); return true;
    }
    internal static bool WasRestoredAtLoad(EventModel model) => restoredModels.TryGetValue(model, out _);
}

[HarmonyPatch(typeof(AbstractRoom), nameof(AbstractRoom.FromSerializable))]
internal static class RestoreSavedEventRoomPatch
{
    static bool Prefix(SerializableRoom? serializableRoom, ref AbstractRoom? __result)
    {
        if (!RaceRuntime.Live || serializableRoom is not { RoomType: RoomType.Event }) return true;
        if (serializableRoom.EncounterState?.TryGetValue(EventRecovery.OpenKey, out var open) == true && open != null)
        { __result = EventRecovery.FromOpenSave(serializableRoom, StateJson.Deserialize<SavedOpenEvent>(open)); return false; }
        if (serializableRoom.EncounterState?.TryGetValue(EventRecovery.FinishedKey, out var json) != true || json == null) return true;
        __result = EventRecovery.FromFinishedSave(serializableRoom, StateJson.Deserialize<SavedFinishedEvent>(json)); return false;
    }
}

[HarmonyPatch(typeof(EventModel), nameof(EventModel.BeginEvent))]
internal static class RestoreSavedEventModelPatch
{
    static bool Prefix(EventModel __instance, Player player, EventCombatSynchronizer? combatSynchronizer, ref Task __result)
    {
        if (!RaceRuntime.Live || !EventRecovery.TryRestorePage(__instance, player, combatSynchronizer)) return true;
        __result = Task.CompletedTask; return false;
    }
}

[HarmonyPatch]
internal static class SuppressRestoredEventStartPatch
{
    static IEnumerable<MethodBase> TargetMethods() => typeof(EventModel).Assembly.GetTypes()
        .Where(type => typeof(EventModel).IsAssignableFrom(type))
        .Select(type => type.GetMethod(nameof(EventModel.AfterEventStarted), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        .OfType<MethodInfo>();
    static bool Prefix(EventModel __instance, ref Task __result)
    {
        if (!RaceRuntime.Live || !EventRecovery.WasRestoredAtLoad(__instance)) return true;
        __result = Task.CompletedTask; return false;
    }
}

// Native loading rebuilds parents only for pre-finished combat. An unfinished
// event fight must also keep its real parent so its victory can return there.
[HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterRoom))]
internal static class RestoreUnfinishedNestedCombatPatch
{
    static bool Prefix(RunManager __instance, AbstractRoom room, ref Task __result)
    {
        if (!RaceRuntime.Live || !RaceRuntime.LoadingSavedRoom || RaceRuntime.State?.CurrentRoomCount != 0
            || room is not CombatRoom { IsPreFinished: false, ParentEventId: not null } combat) return true;
        __result = EnterStack(__instance, combat); return false;
    }
    private static async Task EnterStack(RunManager manager, CombatRoom combat)
    {
        var enter = AccessTools.Method(typeof(RunManager), "EnterRoomInternal");
        RaceRuntime.RestoringParentEvent = true;
        try { await (Task)enter.Invoke(manager, [new EventRoom(ModelDb.GetById<EventModel>(combat.ParentEventId!)), true])!; }
        finally { RaceRuntime.RestoringParentEvent = false; }
        await (Task)enter.Invoke(manager, [combat, false])!;
    }
}

