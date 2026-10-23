using FendaoZhengfeng.Core;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace FendaoZhengfeng.Mod;

internal sealed record TwinSlimeSavedState(
    int Version,
    string RoomKey,
    bool SplitOccurred,
    int Stacks,
    int SourceMaxHp,
    int SplitBodyMaxHp,
    int FirstCurrentHp,
    int FirstMaxHp,
    int SecondCurrentHp,
    int SecondMaxHp);

internal static class TwinSlimePersistence
{
    private const int CurrentVersion = 1;
    private const int MaximumStacks = 100000;

    internal static string GetForSave(RaceModifier modifier)
    {
        string stored = modifier.StoredTwinSlimeStateJson;
        if (!IsCurrent(modifier))
            return stored;

        string roomKey = RaceRuntime.RoomKey;
        Player? player = RaceRuntime.State?.Players.SingleOrDefault();
        if (player != null
            && player.RunState == modifier.SlimeRunState
            && TwinSlimeRuntime.Find(player) is { } pair
            && pair.State == player.Creature.CombatState
            && pair.State.ContainsCreature(pair.First)
            && pair.State.ContainsCreature(pair.Second)
            && !string.IsNullOrWhiteSpace(roomKey))
        {
            var state = new TwinSlimeSavedState(
                CurrentVersion,
                roomKey,
                SplitOccurred: true,
                pair.Stacks,
                pair.SourceMaxHp,
                pair.SplitBodyMaxHp,
                pair.First.CurrentHp,
                pair.First.MaxHp,
                pair.Second.CurrentHp,
                pair.Second.MaxHp);
            if (IsValid(state))
            {
                stored = StateJson.Serialize(state);
                modifier.StoredTwinSlimeStateJson = stored;
                return stored;
            }
            modifier.StoredTwinSlimeStateJson = "";
            return "";
        }

        // A SavedProperty getter can run while native loading still has no
        // current room. Preserve valid pending data until RestorePendingRoom
        // can validate the actual room and consume or reject it deliberately.
        if (TryRead(stored, out _)) return stored;

        modifier.StoredTwinSlimeStateJson = "";
        return "";
    }

    internal static bool TryRead(string? json, out TwinSlimeSavedState? state)
    {
        state = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4096)
            return false;
        try
        {
            var parsed = StateJson.Deserialize<TwinSlimeSavedState>(json);
            if (!IsValid(parsed))
                return false;
            state = parsed;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static void ClearStored(Player player)
    {
        if (RaceModifier.Active is { } modifier && modifier.SlimeRunState == player.RunState)
            modifier.StoredTwinSlimeStateJson = "";
    }

    internal static bool TryGetRecombinedSnapshot(Player player, out int currentHp, out int maxHp)
    {
        currentHp = 0;
        maxHp = 0;
        if (RaceModifier.Active is not { } modifier
            || !IsCurrent(modifier)
            || player.RunState != modifier.SlimeRunState
            || TwinSlimeRuntime.Find(player) is not { } pair
            || pair.State != player.Creature.CombatState
            || !pair.State.ContainsCreature(pair.First)
            || !pair.State.ContainsCreature(pair.Second))
            return false;

        maxHp = TwinSlimeRules.RecombinedMaxHp(
            pair.SourceMaxHp, pair.SplitBodyMaxHp, pair.First.MaxHp, pair.Second.MaxHp);
        currentHp = TwinSlimeRules.RecombinedCurrentHp(pair.First.CurrentHp, pair.Second.CurrentHp, maxHp);
        return true;
    }

    internal static async Task RestorePendingRoom(RaceModifier modifier)
    {
        if (!IsCurrent(modifier))
            return;

        string json = modifier.StoredTwinSlimeStateJson;
        if (string.IsNullOrWhiteSpace(json))
            return;
        if (!TryRead(json, out var saved))
        {
            modifier.StoredTwinSlimeStateJson = "";
            GD.PushWarning("Twin Slime ignored an invalid saved body state.");
            return;
        }

        if (saved!.RoomKey != RaceRuntime.RoomKey
            || RaceRuntime.State?.Players.SingleOrDefault() is not { } player
            || player.RunState != modifier.SlimeRunState
            || TwinSlimeRuntime.StackCount(player) < saved.Stacks)
        {
            modifier.StoredTwinSlimeStateJson = "";
            return;
        }

        if (TwinSlimeRuntime.Find(player) != null)
        {
            modifier.StoredTwinSlimeStateJson = "";
            return;
        }

        bool restored = await TwinSlimeRuntime.RestoreSavedSplit(player, saved);
        modifier.StoredTwinSlimeStateJson = "";
        if (!restored)
            GD.PushError("Twin Slime could not recreate its saved pair in the active combat room.");
    }

    private static bool IsCurrent(RaceModifier modifier)
        => RaceRuntime.Live
            && ReferenceEquals(RaceModifier.Active, modifier)
            && ReferenceEquals(RaceRuntime.State, modifier.SlimeRunState);

    private static bool IsValid(TwinSlimeSavedState state)
        => state.Version == CurrentVersion
            && state.SplitOccurred
            && !string.IsNullOrWhiteSpace(state.RoomKey)
            && state.RoomKey.Length <= 512
            && state.Stacks is > 0 and <= MaximumStacks
            && state.SourceMaxHp > 0
            && state.SplitBodyMaxHp == TwinSlimeRules.BodyMaxHp(state.SourceMaxHp, state.Stacks)
            && state.FirstMaxHp > 0
            && state.SecondMaxHp > 0
            && state.FirstCurrentHp >= 0
            && state.FirstCurrentHp <= state.FirstMaxHp
            && state.SecondCurrentHp >= 0
            && state.SecondCurrentHp <= state.SecondMaxHp;
}

public sealed partial class RaceModifier
{
    internal RunState SlimeRunState => RunState;
    private string twinSlimeStateJson = "";

    [SavedProperty]
    public string TwinSlimeStateJson
    {
        get => TwinSlimePersistence.GetForSave(this);
        set => twinSlimeStateJson = value ?? "";
    }

    internal string StoredTwinSlimeStateJson
    {
        get => twinSlimeStateJson;
        set => twinSlimeStateJson = value ?? "";
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.ToSerializable))]
internal static class TwinSlimePlayerSnapshotPatch
{
    static void Postfix(Player __instance, SerializablePlayer __result)
    {
        if (!TwinSlimePersistence.TryGetRecombinedSnapshot(__instance, out int currentHp, out int maxHp))
            return;
        __result.CurrentHp = currentHp;
        __result.MaxHp = maxHp;
    }
}

[HarmonyPatch(typeof(RaceModifier), nameof(RaceModifier.BeforeCombatStart))]
internal static class TwinSlimeRestoreSavedRoomPatch
{
    static void Postfix(ref Task __result, RaceModifier __instance)
        => __result = RestoreAfterCombatStart(__result, __instance);

    private static async Task RestoreAfterCombatStart(Task original, RaceModifier modifier)
    {
        await original;
        await TwinSlimePersistence.RestorePendingRoom(modifier);
    }
}