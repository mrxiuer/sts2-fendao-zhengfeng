using Environment = System.Environment;
using Engine = FendaoZhengfeng.Core.Engine;
using MegaCrit.Sts2.Core.Saves;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using FendaoZhengfeng.Core;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace FendaoZhengfeng.Mod;

internal enum RewardPerfStage
{
    ClickToUi,
    ButtonTask,
    NativeSelect,
    ModeChooseEffect,
    ModeDialogWait,
    Submit,
    HostCommand,
    CommandQueueWait,
    CommandRoundtrip,
    NetworkStateApply,
    NetworkStateExpand,
    SnapshotSend,
    BackupAccept,
    BackupDeserialize,
    EngineExecute,
    CoreCloneSerialize,
    CoreCloneDeserialize,
    PublishState,
    PublishSerialize,
    Compression,
    Checkpoint,
    RecoveryCapture,
    NativeToSave,
    NativePacketSerialize,
    RewardSnapshot,
    RerollSnapshot,
    RewardCardPacket,
    CheckpointMatchSerialize,
    CheckpointMatchCacheHit,
    CheckpointBundleSerialize,
    SaveQueue,
    DiskWrite,
    UiUpdate,
    NoOpBroadcastSkipped,
    Count
}

/// <summary>仅在 FDZF_PROFILE_REWARDS=1 时记录奖励点击各阶段耗时。</summary>
internal static class RewardPerformance
{
    internal sealed class Trace(string subject, string commandId = "", string commandKind = "", bool isClick = false)
    {
        internal readonly string Subject = subject;
        internal readonly long StartedAt = Stopwatch.GetTimestamp();
        internal readonly long[] Ticks = new long[(int)RewardPerfStage.Count];
        internal readonly int[] Counts = new int[(int)RewardPerfStage.Count];
        internal readonly bool IsClick = isClick;
        internal string CommandId = commandId;
        internal string CommandKind = commandKind;
        internal int Completed;
        internal int PendingSubmits;
    }

    internal readonly record struct Timing(Trace? Trace, RewardPerfStage Stage, long StartedAt);
    internal readonly record struct ReleaseScope(Trace? Previous, Trace? Started);
    internal readonly record struct SubmitScope(Timing Timing, string Id, string Kind);
    internal readonly record struct MessageScope(Trace? Previous, Trace? Trace, Timing Timing, bool Created);
    private static readonly bool enabled = Environment.GetEnvironmentVariable("FDZF_PROFILE_REWARDS") == "1";
    private static readonly AsyncLocal<Trace?>? ambient = enabled ? new AsyncLocal<Trace?>() : null;
    private static Trace? activeClick;
    private static Trace? lastCompleted;
    private static Trace? lastCompletedClick;
    private static readonly ConditionalWeakTable<Reward,Trace> rewardClicks = new();
    private static RaceSession? cachedSession;
    private static Match? cachedMatch;
    private static long cachedRevision = -1;
    private static string? cachedJson;
    private readonly record struct SaveWriteKey(string Path, string Contents);

    private sealed class SaveWriteKeyComparer : IEqualityComparer<SaveWriteKey>
    {
        public bool Equals(SaveWriteKey left, SaveWriteKey right)
            => StringComparer.OrdinalIgnoreCase.Equals(left.Path, right.Path)
                && ReferenceEquals(left.Contents, right.Contents);

        public int GetHashCode(SaveWriteKey key)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Path),
                RuntimeHelpers.GetHashCode(key.Contents));
    }

    private static readonly object saveTraceGate = new();
    private static readonly ConcurrentDictionary<SaveWriteKey, Trace> saveTraces = new(new SaveWriteKeyComparer());
    private static int completedClicks;
    private static int cacheHits;
    private static int singleplayerBroadcastSkips;

    internal static bool Enabled => enabled;
    internal static readonly bool LegacyPathForVerification = Environment.GetEnvironmentVariable("FDZF_NATIVE_VERIFY") == "1" && Environment.GetEnvironmentVariable("FDZF_REWARD_LEGACY_PATH") == "1";
    private static Trace? Current => !enabled ? null : ambient?.Value;
    private static Trace? ActivePendingClick
    {
        get
        {
            var trace = Volatile.Read(ref activeClick);
            return trace != null && Volatile.Read(ref trace.PendingSubmits) > 0 ? trace : null;
        }
    }
    internal static Trace? CurrentTraceForPatch => Current;

    internal static Timing Begin(RewardPerfStage stage)
    {
        if (!enabled || Current is not { } trace) return default;
        return new(trace, stage, Stopwatch.GetTimestamp());
    }

    internal static void End(Timing timing)
    {
        if (timing.StartedAt == 0 || timing.Trace is not { } trace) return;
        Add(trace, timing.Stage, Stopwatch.GetTimestamp() - timing.StartedAt);
    }

    private static void Add(Trace trace, RewardPerfStage stage, long ticks)
    {
        int index = (int)stage;
        Interlocked.Add(ref trace.Ticks[index], Math.Max(0, ticks));
        Interlocked.Increment(ref trace.Counts[index]);
    }

    private static void AddInstant(Trace trace, RewardPerfStage stage)
        => Interlocked.Increment(ref trace.Counts[(int)stage]);

    internal static int CompletedClicks => Volatile.Read(ref completedClicks);
    internal static int LastCount(RewardPerfStage stage)
    {
        var trace = Volatile.Read(ref lastCompleted);
        return trace == null ? 0 : Volatile.Read(ref trace.Counts[(int)stage]);
    }
    internal static long LastTicks(RewardPerfStage stage)
        => lastCompleted is { } trace ? Interlocked.Read(ref trace.Ticks[(int)stage]) : 0;
    internal static Dictionary<string, double> LastClickStages()
    {
        var trace = Volatile.Read(ref lastCompletedClick);
        return trace == null ? [] : Enum.GetValues<RewardPerfStage>().Where(stage => stage != RewardPerfStage.Count && trace.Counts[(int)stage] > 0)
            .ToDictionary(stage => stage.ToString(), stage => trace.Ticks[(int)stage] * 1000d / Stopwatch.Frequency);
    }
    internal static bool IsRewardClickComplete(Reward reward)
        => rewardClicks.TryGetValue(reward, out var trace) && Volatile.Read(ref trace.Completed) != 0;
    internal static Dictionary<string,double> StagesForReward(Reward reward)
    {
        if (!rewardClicks.TryGetValue(reward, out var trace)) throw new InvalidOperationException("Missing native reward click trace.");
        return Enum.GetValues<RewardPerfStage>().Where(stage=>stage!=RewardPerfStage.Count && trace.Counts[(int)stage]>0)
            .ToDictionary(stage=>stage.ToString(),stage=>trace.Ticks[(int)stage]*1000d/Stopwatch.Frequency);
    }
    internal static int CacheHitCount => Volatile.Read(ref cacheHits);
    internal static int SingleplayerBroadcastSkipCount => Volatile.Read(ref singleplayerBroadcastSkips);

    internal static void StartClick(Reward reward, out ReleaseScope scope)
    {
        if (!enabled)
        {
            scope = default;
            return;
        }
        var previous = ambient!.Value;
        // Nested mode rows complete the existing reward interaction; they are
        // not independent claims and must not overwrite its pending trace.
        if (reward.GetType().DeclaringType == typeof(NativeChoiceUi)) { scope = new(previous, null); return; }
        var trace = new Trace(reward.GetType().Name, isClick: true);
        rewardClicks.Remove(reward); rewardClicks.Add(reward, trace);
        Volatile.Write(ref activeClick, trace);
        ambient.Value = trace;
        scope = new(previous, trace);
    }

    internal static void RestoreRelease(ReleaseScope scope, Exception? exception)
    {
        if (!enabled) return;
        ambient!.Value = scope.Previous;
        if (exception != null && scope.Started is { } trace)
            Finish(trace, "release-error:" + exception.GetType().Name);
    }

    internal static Task CompleteButton(Task original, Trace trace, Timing buttonTiming, NRewardButton button)
        => CompleteButtonAsync(original, trace, buttonTiming, button);

    private static async Task CompleteButtonAsync(Task original, Trace trace, Timing timing, NRewardButton button)
    {
        string outcome = "selected";
        try { await original; }
        catch (Exception error) { outcome = "error:" + error.GetType().Name; throw; }
        finally
        {
            End(timing);
            if (button.Reward?.SuccessfullySelected != true) outcome = "not-selected";
            Finish(trace, outcome);
        }
    }

    internal static Task<bool> CompleteNativeSelect(Task<bool> original, Timing timing)
        => CompleteBoolAsync(original, timing);

    private static async Task<bool> CompleteBoolAsync(Task<bool> original, Timing timing)
    {
        try { return await original; }
        finally { End(timing); }
    }

    internal static Task<bool> CompleteModeChoice(Task<bool> original, Timing timing)
        => CompleteBoolAsync(original, timing);

    internal static Task<int> CompleteDialog(Task<int> original, Timing timing)
        => CompleteIntAsync(original, timing);

    private static async Task<int> CompleteIntAsync(Task<int> original, Timing timing)
    {
        try { return await original; }
        finally { End(timing); }
    }

    internal static void FinishSubmit(Task original, SubmitScope scope, ref Task result)
    {
        if (scope.Timing.StartedAt == 0) return;
        result = CompleteSubmitAsync(original, scope);
    }

    private static async Task CompleteSubmitAsync(Task original, SubmitScope scope)
    {
        try { await original; }
        finally
        {
            End(scope.Timing);
            if (scope.Timing.Trace is { } trace) Interlocked.Decrement(ref trace.PendingSubmits);
        }
    }

    internal static SubmitScope StartSubmit(Command command)
    {
        var timing = Begin(RewardPerfStage.Submit);
        if (timing.Trace is { } trace)
        {
            trace.CommandId = command.Id;
            trace.CommandKind = command.Kind;
            Interlocked.Increment(ref trace.PendingSubmits);
        }
        return new(timing, command.Id, command.Kind);
    }

    internal static MessageScope StartMessage(RaceSession session, RaceMessage message, out bool track)
    {
        track = false;
        if (!enabled) return default;
        var previous = ambient!.Value;
        Trace? trace = previous ?? ActivePendingClick;
        bool created = false;

        if(session.IsHost && message.Kind=="recovery_snapshot") { trace=new Trace("native-backup");created=true; }
        if (session.IsHost && message.Kind is "command" or "native_claim")
        {
            try
            {
                var command = StateJson.Deserialize<Command>(message.Content);
                if (IsRewardCommand(command.Kind))
                {
                    trace = new Trace("host:" + command.Kind, command.Id, command.Kind);
                    created = true;
                }
            }
            catch { }
        }

        if (trace == null) return default;
        ambient.Value = trace;
        var stage = message.Kind is "state" or "state_delta" ? RewardPerfStage.NetworkStateApply : RewardPerfStage.HostCommand;
        var timing = new Timing(trace, stage, Stopwatch.GetTimestamp());
        track = true;
        return new(previous, trace, timing, created);
    }

    internal static void FinishMessage(MessageScope scope, Exception? exception)
    {
        if (!enabled || scope.Trace is not { } trace) return;
        End(scope.Timing);
        ambient!.Value = scope.Previous;
        if (scope.Created)
            Finish(trace, exception == null ? "host-command" : "host-error:" + exception.GetType().Name);
    }

    internal static void Finish(Trace trace, string outcome)
    {
        if (!enabled || Interlocked.Exchange(ref trace.Completed, 1) != 0) return;
        if (trace.IsClick) Add(trace, RewardPerfStage.ClickToUi, Stopwatch.GetTimestamp() - trace.StartedAt);
        if (ReferenceEquals(Volatile.Read(ref activeClick), trace))
            Interlocked.CompareExchange(ref activeClick, null, trace);
        Volatile.Write(ref lastCompleted, trace);
        if (trace.IsClick) { Volatile.Write(ref lastCompletedClick, trace); Interlocked.Increment(ref completedClicks); }
        WriteReport(trace, outcome);
    }

    internal static void ReportLast(string suffix)
    {
        if (!enabled || Volatile.Read(ref lastCompleted) is not { } trace) return;
        WriteReport(trace, suffix);
    }

    private static void WriteReport(Trace trace, string outcome)
    {
        var line = new StringBuilder(520);
        line.Append("[FDZF REWARD PERF] subject=").Append(trace.Subject)
            .Append(" outcome=").Append(outcome);
        if (trace.CommandId.Length > 0)
            line.Append(" command=").Append(trace.CommandKind).Append(':').Append(trace.CommandId);
        Append(line, trace, RewardPerfStage.ButtonTask, "click_to_ui");
        Append(line, trace, RewardPerfStage.ClickToUi, "release_to_ui");
        Append(line, trace, RewardPerfStage.NativeSelect, "native_select");
        Append(line, trace, RewardPerfStage.ModeChooseEffect, "choose_effect");
        Append(line, trace, RewardPerfStage.ModeDialogWait, "dialog_wait");
        Append(line, trace, RewardPerfStage.Submit, "submit_roundtrip");
        Append(line, trace, RewardPerfStage.HostCommand, "host_command");
        Append(line, trace, RewardPerfStage.CommandQueueWait, "command_queue");
        Append(line, trace, RewardPerfStage.CommandRoundtrip, "command_network");
        Append(line, trace, RewardPerfStage.NetworkStateApply, "state_apply");
        Append(line, trace, RewardPerfStage.NetworkStateExpand, "state_expand");
        Append(line, trace, RewardPerfStage.SnapshotSend, "snapshot_send");
        Append(line, trace, RewardPerfStage.BackupAccept, "backup_accept");
        Append(line, trace, RewardPerfStage.BackupDeserialize, "backup_deserialize");
        Append(line, trace, RewardPerfStage.EngineExecute, "engine_execute");
        Append(line, trace, RewardPerfStage.CoreCloneSerialize, "clone_json_out");
        Append(line, trace, RewardPerfStage.CoreCloneDeserialize, "clone_json_in");
        Append(line, trace, RewardPerfStage.PublishState, "publish_total");
        Append(line, trace, RewardPerfStage.PublishSerialize, "publish_json");
        Append(line, trace, RewardPerfStage.Compression, "brotli");
        Append(line, trace, RewardPerfStage.Checkpoint, "checkpoint_total");
        Append(line, trace, RewardPerfStage.RecoveryCapture, "recovery_capture");
        Append(line, trace, RewardPerfStage.NativeToSave, "native_to_save");
        Append(line, trace, RewardPerfStage.NativePacketSerialize, "native_packet");
        Append(line, trace, RewardPerfStage.RewardSnapshot, "reward_snapshot");
        Append(line, trace, RewardPerfStage.RerollSnapshot, "reroll_snapshot");
        Append(line, trace, RewardPerfStage.RewardCardPacket, "card_packets");
        Append(line, trace, RewardPerfStage.CheckpointMatchSerialize, "checkpoint_match_json");
        Append(line, trace, RewardPerfStage.CheckpointMatchCacheHit, "checkpoint_match_cache_hit");
        Append(line, trace, RewardPerfStage.CheckpointBundleSerialize, "checkpoint_bundle_json");
        Append(line, trace, RewardPerfStage.SaveQueue, "save_enqueue");
        Append(line, trace, RewardPerfStage.DiskWrite, "file_write");
        Append(line, trace, RewardPerfStage.UiUpdate, "reward_ui_update");
        Append(line, trace, RewardPerfStage.NoOpBroadcastSkipped, "no_op_broadcast_skip");
        line.Append(" cache_hits=").Append(Volatile.Read(ref cacheHits))
            .Append(" skipped_singleplayer_broadcasts=").Append(Volatile.Read(ref singleplayerBroadcastSkips));
        GD.Print(line.ToString());
    }

    private static void Append(StringBuilder line, Trace trace, RewardPerfStage stage, string label)
    {
        int index = (int)stage;
        int count = Volatile.Read(ref trace.Counts[index]);
        if (count == 0) return;
        long ticks = Interlocked.Read(ref trace.Ticks[index]);
        line.Append(' ').Append(label).Append('=').Append(Milliseconds(ticks)).Append("ms/").Append(count);
    }

    private static string Milliseconds(long ticks)
        => (ticks * 1000.0 / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture);

    private static bool IsRewardCommand(string kind)
        => kind is "claim" or "gold" or "offer" or "choose" or "siphon" or "boss_shield"
            or "boss_cleanse" or "camp_cleanse" or "market_buy" or "tail_refill" or "repair";

    internal static string SerializeCoreClone<T>(T value)
    {
        var timer = Begin(RewardPerfStage.CoreCloneSerialize);
        string json = StateJson.Serialize(value);
        End(timer);
        return json;
    }

    internal static T DeserializeCoreClone<T>(string json)
    {
        var timer = Begin(RewardPerfStage.CoreCloneDeserialize);
        T value = StateJson.Deserialize<T>(json);
        End(timer);
        return value;
    }

    internal static string SerializePublishedMatch(Match state)
    {
        var timer = Begin(RewardPerfStage.PublishSerialize);
        string json = StateJson.Serialize(state);
        End(timer);
        if (RaceSession.Current is { } session && ReferenceEquals(session.Engine.State, state))
        {
            cachedSession = session;
            cachedMatch = state;
            cachedRevision = state.Revision;
            cachedJson = json;
        }
        return json;
    }

    internal static string SerializeCheckpointMatch(Match state)
    {
        var trace = Current;
        if (cachedJson != null && ReferenceEquals(cachedSession, RaceSession.Current)
            && ReferenceEquals(cachedMatch, state) && cachedRevision == state.Revision)
        {
            if (enabled) Interlocked.Increment(ref cacheHits);
            if (trace != null) AddInstant(trace, RewardPerfStage.CheckpointMatchCacheHit);
            return cachedJson;
        }

        var timer = Begin(RewardPerfStage.CheckpointMatchSerialize);
        string json = StateJson.Serialize(state);
        End(timer);
        return json;
    }

    internal static string SerializeCheckpointBundle(NativeCheckpoint.Bundle bundle)
    {
        var timer = Begin(RewardPerfStage.CheckpointBundleSerialize);
        string json = StateJson.Serialize(bundle);
        End(timer);
        return json;
    }

    internal static void InvalidatePublishedMatch(RaceSession session)
    {
        if (!ReferenceEquals(cachedSession, session)) return;
        cachedSession = null;
        cachedMatch = null;
        cachedRevision = -1;
        cachedJson = null;
    }

    internal static void RecordSingleplayerBroadcastSkip()
    {
        if (!enabled) return;
        Interlocked.Increment(ref singleplayerBroadcastSkips);
        if (Current is { } trace) AddInstant(trace, RewardPerfStage.NoOpBroadcastSkipped);
    }

    private static SaveWriteKey SaveWriteKeyFor(string path, string contents)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch { fullPath = path; }
        return new(fullPath, contents);
    }

    internal static Timing BeginSaveQueue(string path, string contents)
    {
        var timing = Begin(RewardPerfStage.SaveQueue);
        var key = SaveWriteKeyFor(path, contents);
        lock (saveTraceGate)
        {
            // LatestFileWriter coalesces pending writes by path. Drop any older
            // association for this path before recording the value that won.
            foreach (var existing in saveTraces.Keys)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(existing.Path, key.Path))
                    saveTraces.TryRemove(existing, out _);
            }
            if (timing.Trace is { } trace) saveTraces[key] = trace;
        }
        return timing;
    }

    internal static Timing BeginDiskWrite(string path, string contents)
    {
        if (!enabled) return default;
        var key = SaveWriteKeyFor(path, contents);
        lock (saveTraceGate)
        {
            if (!saveTraces.TryRemove(key, out var trace)) return default;
            return new(trace, RewardPerfStage.DiskWrite, Stopwatch.GetTimestamp());
        }
    }

    internal static void EndDiskWrite(Timing timing) => End(timing);

    private static MethodInfo GenericStateJsonMethod(string name)
        => typeof(StateJson).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == name && method.IsGenericMethodDefinition);

    private static MethodInfo ClosedStateJsonMethod(string name, Type type)
        => GenericStateJsonMethod(name).MakeGenericMethod(type);

    private static MethodInfo Wrapper(string name, bool generic = false)
        => typeof(RewardPerformance).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method => method.Name == name && method.IsGenericMethodDefinition == generic);

    private static IEnumerable<CodeInstruction> ReplaceCall(IEnumerable<CodeInstruction> instructions, MethodInfo from, MethodInfo to)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(from)) instruction.operand = to;
            yield return instruction;
        }
    }

    internal static IEnumerable<CodeInstruction> ProfileEngineCalls(IEnumerable<CodeInstruction> instructions)
    {
        var sourceSerialize = ClosedStateJsonMethod(nameof(StateJson.Serialize), typeof(Match));
        var sourceDeserialize = ClosedStateJsonMethod(nameof(StateJson.Deserialize), typeof(Match));
        var targetSerialize = Wrapper(nameof(SerializeCoreClone), true).MakeGenericMethod(typeof(Match));
        var targetDeserialize = Wrapper(nameof(DeserializeCoreClone), true).MakeGenericMethod(typeof(Match));
        return ReplaceCall(ReplaceCall(instructions, sourceSerialize, targetSerialize), sourceDeserialize, targetDeserialize);
    }

    internal static IEnumerable<CodeInstruction> CachePublishedMatchCall(IEnumerable<CodeInstruction> instructions)
        => ReplaceCall(instructions, ClosedStateJsonMethod(nameof(StateJson.Serialize), typeof(Match)),
            Wrapper(nameof(SerializePublishedMatch)));

    internal static IEnumerable<CodeInstruction> CacheCheckpointCalls(IEnumerable<CodeInstruction> instructions)
    {
        var replacedMatch = ReplaceCall(instructions, ClosedStateJsonMethod(nameof(StateJson.Serialize), typeof(Match)),
            Wrapper(nameof(SerializeCheckpointMatch)));
        return ReplaceCall(replacedMatch, ClosedStateJsonMethod(nameof(StateJson.Serialize), typeof(NativeCheckpoint.Bundle)),
            Wrapper(nameof(SerializeCheckpointBundle)));
    }
}

[HarmonyPatch(typeof(RaceSession), "Broadcast")]
internal static class SkipNoOpSingleplayerBroadcastPatch
{
    static bool Prefix(RaceSession __instance)
    {
        if (__instance.Transport.Type != NetGameType.Singleplayer) return true;
        RewardPerformance.RecordSingleplayerBroadcastSkip();
        return false;
    }
}

[HarmonyPatch(typeof(RaceSession), "PublishState")]
internal static class ReusePublishedMatchJsonPatch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        => RewardPerformance.CachePublishedMatchCall(instructions);
}

[HarmonyPatch(typeof(RaceRuntime), nameof(RaceRuntime.Checkpoint))]
internal static class ReuseCheckpointMatchJsonPatch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        => RewardPerformance.CacheCheckpointCalls(instructions);
}

[HarmonyPatch(typeof(RaceSession), nameof(RaceSession.Dispose))]
internal static class InvalidatePublishedMatchOnDisposePatch
{
    static void Prefix(RaceSession __instance) => RewardPerformance.InvalidatePublishedMatch(__instance);
}

[HarmonyPatch(typeof(Engine), nameof(Engine.Execute))]
internal static class ProfileCoreEnginePatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(out RewardPerformance.Timing __state) => __state = RewardPerformance.Begin(RewardPerfStage.EngineExecute);
    static void Postfix(RewardPerformance.Timing __state) => RewardPerformance.End(__state);
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        => RewardPerformance.ProfileEngineCalls(instructions);
}

[HarmonyPatch(typeof(NRewardButton), "OnRelease")]
internal static class ProfileRewardButtonReleasePatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(NRewardButton __instance, out object? __state)
    {
        if (__instance.Reward is not { } reward) { __state = null; return; }
        var previous = RewardPerformance.CurrentTraceForPatch;
        RewardPerformance.StartClick(reward, out var releaseScope);
        __state = releaseScope;
        _ = previous;
    }
    static Exception? Finalizer(Exception? __exception, object? __state)
    {
        if (__state is RewardPerformance.ReleaseScope scope)
            RewardPerformance.RestoreRelease(scope, __exception);
        return __exception;
    }
}

[HarmonyPatch(typeof(NRewardButton), "GetReward")]
internal static class ProfileNativeRewardTaskPatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(out RewardPerformance.Timing __state) => __state = RewardPerformance.Begin(RewardPerfStage.ButtonTask);
    static void Postfix(NRewardButton __instance, ref Task __result, RewardPerformance.Timing __state)
    {
        if (__state.Trace is not { } trace || __state.StartedAt == 0) return;
        RewardPerformance.CompleteButton(__result, trace, __state, __instance);
    }
}

[HarmonyPatch(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.SelectLocalReward))]
internal static class ProfileNativeRewardSelectionPatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(out RewardPerformance.Timing __state) => __state = RewardPerformance.Begin(RewardPerfStage.NativeSelect);
    static void Postfix(ref Task<bool> __result, RewardPerformance.Timing __state)
    {
        if (__state.Trace != null && __state.StartedAt != 0)
            __result = RewardPerformance.CompleteNativeSelect(__result, __state);
    }
}

[HarmonyPatch(typeof(RaceSession), nameof(RaceSession.Submit))]
internal static class ProfileRaceSubmitPatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(Command command, out object __state) => __state = RewardPerformance.StartSubmit(command);
    static void Postfix(ref Task __result, object __state)
    {
        if (__state is RewardPerformance.SubmitScope scope)
            RewardPerformance.FinishSubmit(__result, scope, ref __result);
    }
}

[HarmonyPatch(typeof(RaceSession), "OnMessage")]
internal static class ProfileRaceMessageDispatchPatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(RaceSession __instance, RaceMessage message, out object __state)
    {
        var scope = RewardPerformance.StartMessage(__instance, message, out _);
        __state = scope;
    }
    static Exception? Finalizer(Exception? __exception, object? __state)
    {
        if (__state is RewardPerformance.MessageScope scope)
            RewardPerformance.FinishMessage(scope, __exception);
        return __exception;
    }
}

[HarmonyPatch(typeof(RaceSession), "PublishState")]
internal static class ProfilePublishStatePatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(out RewardPerformance.Timing __state) => __state = RewardPerformance.Begin(RewardPerfStage.PublishState);
    static void Postfix(RewardPerformance.Timing __state) => RewardPerformance.End(__state);
}

[HarmonyPatch(typeof(RaceMessage), nameof(RaceMessage.Compress))]
internal static class ProfileStateCompressionPatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(out RewardPerformance.Timing __state) => __state = RewardPerformance.Begin(RewardPerfStage.Compression);
    static void Postfix(RewardPerformance.Timing __state) => RewardPerformance.End(__state);
}

[HarmonyPatch(typeof(RaceMessage), nameof(RaceMessage.Expand))]
internal static class ProfileStateExpansionPatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(out RewardPerformance.Timing __state) => __state = RewardPerformance.Begin(RewardPerfStage.NetworkStateExpand);
    static void Postfix(RewardPerformance.Timing __state) => RewardPerformance.End(__state);
}

[HarmonyPatch(typeof(RaceRuntime), nameof(RaceRuntime.Checkpoint))]
internal static class ProfileCheckpointPatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(out RewardPerformance.Timing __state) => __state = RewardPerformance.Begin(RewardPerfStage.Checkpoint);
    static void Postfix(RewardPerformance.Timing __state) => RewardPerformance.End(__state);
}

[HarmonyPatch(typeof(RaceRecovery), nameof(RaceRecovery.SendSnapshot))]
internal static class ProfileRecoverySnapshotPatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(out RewardPerformance.Timing __state) => __state = RewardPerformance.Begin(RewardPerfStage.SnapshotSend);
    static void Postfix(RewardPerformance.Timing __state) => RewardPerformance.End(__state);
}

[HarmonyPatch(typeof(RaceRecovery), nameof(RaceRecovery.Capture))]
internal static class ProfileRecoveryCapturePatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(out RewardPerformance.Timing __state) => __state = RewardPerformance.Begin(RewardPerfStage.RecoveryCapture);
    static void Postfix(RewardPerformance.Timing __state) => RewardPerformance.End(__state);
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.ToSave), [typeof(AbstractRoom)])]
internal static class ProfileNativeToSavePatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(out RewardPerformance.Timing __state) => __state = RewardPerformance.Begin(RewardPerfStage.NativeToSave);
    static void Postfix(RewardPerformance.Timing __state) => RewardPerformance.End(__state);
}

[HarmonyPatch(typeof(SerializableRun), nameof(SerializableRun.Serialize))]
internal static class ProfileNativePacketSerializePatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(out RewardPerformance.Timing __state) => __state = RewardPerformance.Begin(RewardPerfStage.NativePacketSerialize);
    static void Postfix(RewardPerformance.Timing __state) => RewardPerformance.End(__state);
}

[HarmonyPatch(typeof(RewardRecovery), nameof(RewardRecovery.Capture))]
internal static class ProfileRewardSnapshotPatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(out RewardPerformance.Timing __state) => __state = RewardPerformance.Begin(RewardPerfStage.RewardSnapshot);
    static void Postfix(RewardPerformance.Timing __state) => RewardPerformance.End(__state);
}

[HarmonyPatch(typeof(RewardRerollPersistence), nameof(RewardRerollPersistence.CaptureVisible))]
internal static class ProfileRerollSnapshotPatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(out RewardPerformance.Timing __state) => __state = RewardPerformance.Begin(RewardPerfStage.RerollSnapshot);
    static void Postfix(RewardPerformance.Timing __state) => RewardPerformance.End(__state);
}

[HarmonyPatch(typeof(NativeCheckpoint), nameof(NativeCheckpoint.EncodeCard))]
internal static class ProfileRewardCardPacketPatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(out RewardPerformance.Timing __state) => __state = RewardPerformance.Begin(RewardPerfStage.RewardCardPacket);
    static void Postfix(RewardPerformance.Timing __state) => RewardPerformance.End(__state);
}

[HarmonyPatch(typeof(LatestFileWriter), nameof(LatestFileWriter.Enqueue))]
internal static class ProfileSaveQueuePatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(string path, string contents, out RewardPerformance.Timing __state)
        => __state = RewardPerformance.BeginSaveQueue(path, contents);
    static void Postfix(RewardPerformance.Timing __state) => RewardPerformance.End(__state);
}

[HarmonyPatch(typeof(LatestFileWriter), nameof(LatestFileWriter.AtomicWrite))]
internal static class ProfileSaveDiskWritePatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(string path, string contents, out RewardPerformance.Timing __state)
        => __state = RewardPerformance.BeginDiskWrite(path, contents);
    static Exception? Finalizer(Exception? __exception, RewardPerformance.Timing __state)
    {
        RewardPerformance.EndDiskWrite(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(NRewardsScreen), "RewardCollectedFrom")]
internal static class ProfileRewardUiUpdatePatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(out RewardPerformance.Timing __state) => __state = RewardPerformance.Begin(RewardPerfStage.UiUpdate);
    static void Postfix(RewardPerformance.Timing __state) => RewardPerformance.End(__state);
}

[HarmonyPatch(typeof(RaceUi), nameof(RaceUi.ChooseEffect))]
internal static class ProfileModeChooseEffectPatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(out RewardPerformance.Timing __state) => __state = RewardPerformance.Begin(RewardPerfStage.ModeChooseEffect);
    static void Postfix(ref Task<bool> __result, RewardPerformance.Timing __state)
    {
        if (__state.Trace != null && __state.StartedAt != 0)
            __result = RewardPerformance.CompleteModeChoice(__result, __state);
    }
}

[HarmonyPatch(typeof(RaceUi), nameof(RaceUi.Choose))]
internal static class ProfileModeDialogPatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(out RewardPerformance.Timing __state) => __state = RewardPerformance.Begin(RewardPerfStage.ModeDialogWait);
    static void Postfix(ref Task<int> __result, RewardPerformance.Timing __state)
    {
        if (__state.Trace != null && __state.StartedAt != 0)
            __result = RewardPerformance.CompleteDialog(__result, __state);
    }
}


[HarmonyPatch(typeof(RaceRecovery), nameof(RaceRecovery.Accept))]
internal static class ProfileBackupAcceptPatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(out RewardPerformance.Timing __state) => __state=RewardPerformance.Begin(RewardPerfStage.BackupAccept);
    static void Postfix(RewardPerformance.Timing __state) => RewardPerformance.End(__state);
}

[HarmonyPatch(typeof(SerializableRun), nameof(SerializableRun.Deserialize))]
internal static class ProfileBackupDeserializePatch
{
    static bool Prepare() => RewardPerformance.Enabled;
    static void Prefix(out RewardPerformance.Timing __state) => __state=RewardPerformance.Begin(RewardPerfStage.BackupDeserialize);
    static void Postfix(RewardPerformance.Timing __state) => RewardPerformance.End(__state);
}
