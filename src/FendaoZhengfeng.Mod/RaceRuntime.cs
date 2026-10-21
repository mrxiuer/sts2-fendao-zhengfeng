using System.Reflection;
using Godot;
using HarmonyLib;
using FendaoZhengfeng.Core;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace FendaoZhengfeng.Mod;

public static class RaceRuntime
{
    public static bool Enabled { get => RaceConfig.ModeEnabled; set => RaceConfig.ModeEnabled = value; }
    public static bool ResumeRequested { get; set; }
    internal static bool ResumeFromHostArchive { get; set; }
    internal static string ReplayingBattleKey { get; set; } = "";
    public static CombatRoom? RewardRoom { get; set; }
    internal static RewardsSet? CombatRewards { get; set; }
    public static CardPlay? CurrentCardPlay { get; set; }
    public static RunState? State => RunManager.Instance.DebugOnlyGetState();
    public static string RoomKey => State is { } s ? $"lap{RaceSession.Current?.Me.ResetCount ?? 0}:{s.CurrentActIndex}:{s.ActFloor}:{s.CurrentRoom?.Id}:{s.CurrentMapCoord}" : "";
    public static bool Live => RaceSession.Current != null && State?.Modifiers.Any(m => m is RaceModifier) == true;
    private static readonly Dictionary<Reward, string> rewardIds = [];
    private static string lastNode = "";
    internal static async Task<RunState> Start(NGame game, StartRunLobby lobby, IReadOnlyList<ActModel> acts, IReadOnlyList<ModifierModel> modifiers, string seed, int ascension, LobbyRules? rules = null)
    {
        rewardIds.Clear(); RewardRoom = null; CombatRewards = null; CurrentCardPlay = null; lastNode = "";
        var session = new RaceSession(lobby, seed, rules ?? LobbyRules.Local());
        try
        {
            await session.WaitForHandshake();
            if (session.Restored is { } restored)
            {
                await RunManager.Instance.SetUpSavedSingleplayer(restored.Run, restored.Save);
                AccessTools.PropertySetter(typeof(RunManager), nameof(RunManager.ShouldSave)).Invoke(RunManager.Instance, [false]);
                await game.LoadRun(restored.Run, restored.Save.PreFinishedRoom);
                return restored.Run;
            }
            var list = modifiers.Where(m => m is not RaceModifier).ToList(); list.Add(ModelDb.Modifier<RaceModifier>().ToMutable());
            // The built-in singleplayer service intentionally uses player id 1. Real lobby identities remain in RaceSession.
            var result = await game.StartNewSingleplayerRun(lobby.LocalPlayer.character, false, acts, list, seed, lobby.GameMode, ascension);
            return result;
        }
        catch { session.Dispose(); throw; }
    }
    internal static async Task<RunState> StartConfigured(NGame game, StartRunLobby lobby, IReadOnlyList<ActModel> acts, IReadOnlyList<ModifierModel> modifiers, string seed, int ascension, bool shouldSave = true, DateTimeOffset? dailyTime = null)
    {
        var rules = await LobbyRules.Agree(lobby);
        ResumeRequested = rules.Resume;
        if (rules.Enabled) return await Start(game, lobby, acts, modifiers, seed, ascension, rules);
        StartRacePatch.NativeCall = true;
        Task<RunState> original;
        try { original = game.StartNewMultiplayerRun(lobby, shouldSave, acts, modifiers, seed, ascension, dailyTime); }
        finally { StartRacePatch.NativeCall = false; }
        return await original;
    }
    internal static void BindRewardKey(Reward reward, string key) => rewardIds[reward] = key;
    public static string RewardKey(Reward reward)
    { if (!rewardIds.TryGetValue(reward, out var key)) { key = (RaceSession.Current?.Me.BattleKey ?? RoomKey) + "|" + rewardIds.Count; rewardIds[reward] = key; } return key; }
    public static async Task NodeChanged()
    {
        if (!Live || RaceSession.Current is not { } s) return;
        var key = RoomKey;
        // Initial room hooks can run before local tracking is established; do not discard its income window.
        if (lastNode != key && (lastNode.Length > 0 || s.Me.FortuneIncome.Count > 0))
            await s.Submit(s.New("node_end", lastNode.Length > 0 ? lastNode : "initial:" + key));
        lastNode = key;
    }
    internal static void ResetLocalTracking() { rewardIds.Clear(); RewardRoom = null; CombatRewards = null; lastNode = ""; }
    public static void Checkpoint()
    {
        if (!Live || RaceSession.Current?.IsMock == true) return;
        try
        {
            var s = RaceSession.Current!; var snapshot = s.Recovery.SendSnapshot();
            if (snapshot == null) return;
            var bundle = new NativeCheckpoint.Bundle("0.5.0", s.Engine.State.Version, s.Engine.State.Revision,
                StateJson.Serialize(s.Engine.State), snapshot.NativePacket, snapshot.AppliedReceipts, snapshot.BattleKey);
            s.QueueSave("checkpoint.json", StateJson.Serialize(bundle));
        }
        catch (Exception e) { RaceUi.Error("个人检查点保存失败：" + e.Message); }
    }
}

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.IncrementNumReloads))]
internal static class IsolatedReloadPatch
{
    static bool Prefix(SerializableRun save, ref Task __result)
    { if (!RaceRuntime.Live) return true; save.NumReloads++; __result = Task.CompletedTask; return false; }
}

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveRun))]
internal static class SharedCheckpointBarrierPatch
{
    static bool Prefix(AbstractRoom? preFinishedRoom, ref Task __result)
    {
        if (!RaceRuntime.Live || preFinishedRoom != null || RaceSession.Current is not { PauseRequested: true } s) return true;
        __result = s.ArriveAtCheckpoint(); return false;
    }
}

public static class NativeCheckpoint
{
    public sealed record Bundle(string ModVersion, string RulesVersion, long Revision, string MatchJson, string NativePacket,
        string[]? AppliedReceipts = null, string ReplayBattleKey = "");
    public static string EncodeCard(CardModel card)
    { var writer = new PacketWriter(); card.ToSerializable().Serialize(writer); return Convert.ToBase64String(writer.Buffer, 0, writer.BytePosition); }
    public static CardModel DecodeCard(string value, RunState run, Player owner)
    { var reader = new PacketReader(); reader.Reset(Convert.FromBase64String(value)); var save = new SerializableCard(); save.Deserialize(reader); var card = CardModel.FromSerializable(save); run.AddCard(card, owner); return card; }
    public static (Match Match, RunState Run, SerializableRun Save) Read(string path)
    {
        var b = StateJson.Deserialize<Bundle>(File.ReadAllText(path));
        return Restore(b);
    }
    public static (Match Match, RunState Run, SerializableRun Save) Restore(Bundle b)
    {
        if (b.RulesVersion != Catalog.RulesVersion) throw new InvalidDataException("检查点规则版本不兼容。");
        var match = StateJson.Deserialize<Match>(b.MatchJson); var reader = new PacketReader(); reader.Reset(Convert.FromBase64String(b.NativePacket));
        var save = new SerializableRun(); save.Deserialize(reader); return (match, RunState.FromSerializable(save), save);
    }
}

[HarmonyPatch(typeof(NGame), nameof(NGame._Ready))]
internal static class InstallUiPatch { static void Postfix(NGame __instance) => RaceUi.Install(__instance); }

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
internal static class DisposeRacePatch
{
    static void Prefix(out bool __state) { __state = RaceRuntime.Live; }
    static void Postfix(bool __state) { if (__state) { RaceSession.Current?.Dispose(); RaceUi.Refresh(); } }
}

[HarmonyPatch(typeof(NGame), nameof(NGame.StartNewMultiplayerRun))]
internal static class StartRacePatch
{
    internal static bool NativeCall;
    static bool Prefix(NGame __instance, StartRunLobby lobby, IReadOnlyList<ActModel> acts, IReadOnlyList<ModifierModel> modifiers, string seed, int ascensionLevel, bool shouldSave, DateTimeOffset? dailyTime, ref Task<RunState> __result)
    { if (NativeCall) return true; __result = RaceRuntime.StartConfigured(__instance, lobby, acts, modifiers, seed, ascensionLevel, shouldSave, dailyTime); return false; }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.FinalizeStartingRelics))]
internal static class StartingTailPatch
{
    static void Postfix(ref Task __result) { if (RaceRuntime.Live) __result = After(__result); }
    static async Task After(Task original) { await original; var p = RaceRuntime.State!.Players.Single(); if (!p.Relics.Any(r => r is MegaCrit.Sts2.Core.Models.Relics.LizardTail)) await MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain<MegaCrit.Sts2.Core.Models.Relics.LizardTail>(p); }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.WinRun))]
internal static class FinishPatch
{
    static void Prefix() { if (RaceRuntime.Live && RaceSession.Current is { } s) RaceUi.Run(async () => await s.Submit(s.New("finish"))); }
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Combat.CombatManager), nameof(MegaCrit.Sts2.Core.Combat.CombatManager.LoseCombat))]
internal static class DefeatPatch
{ static void Prefix() { if (RaceRuntime.Live && RaceSession.Current is { } s) RaceUi.Run(async () => await s.Submit(s.New("defeat"))); } }
