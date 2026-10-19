using System.Security.Cryptography;
using System.Text;
using Godot;
using FendaoZhengfeng.Core;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using Engine = FendaoZhengfeng.Core.Engine;
using Environment = System.Environment;

namespace FendaoZhengfeng.Mod;

public sealed class RaceSession : IDisposable
{
    public static RaceSession? Current { get; private set; }
    public INetGameService Transport { get; }
    private bool localVerification;
    public bool IsMock { get; }
    public bool IsHost => localVerification || Transport.Type == NetGameType.Host;
    public ulong HostId { get; }
    public string LocalId => Transport.NetId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public Engine Engine { get; private set; }
    public Racer Me => Engine.State.Racers[LocalId];
    private readonly Dictionary<string, TaskCompletionSource> pending = [];
    private readonly HashSet<ulong> hello = [];
    private readonly HashSet<string> appliedReceipts = [];
    private readonly HashSet<string> ghostReservations = [];
    private readonly SemaphoreSlim commandGate = new(1);
    private bool disposed;
    private bool sendingTelemetry;
    private long lastTelemetry;
    private string lastPublicProfile = "";
    public bool PauseRequested { get; private set; }
    public bool Paused { get; private set; }
    public bool WaitingAtCheckpoint => checkpointGate != null;
    private TaskCompletionSource? checkpointGate;
    private string? checkpointPacket;
    private readonly HashSet<ulong> checkpointReady = [];
    private string handshake = "";
    private string Handshake => handshake;
    public (Match Match, RunState Run, MegaCrit.Sts2.Core.Saves.SerializableRun Save)? Restored { get; }
    private string ResumePath => Environment.GetEnvironmentVariable("FDZF_NATIVE_VERIFY") == "1"
        ? Path.Combine(Environment.GetEnvironmentVariable("FDZF_REPORT_DIR") ?? throw new InvalidOperationException("验证报告目录未指定。"), "resume", LocalId + ".json")
        : ProjectSettings.GlobalizePath("user://FendaoZhengfeng/resume/" + LocalId + ".json");
    public event Action? Changed;
    private RaceSession(string seed, bool mock = false)
    {
        if (!mock && Environment.GetEnvironmentVariable("FDZF_NATIVE_VERIFY") != "1") throw new InvalidOperationException("本地验证未启用。");
        IsMock = mock;
        Current?.Dispose(); localVerification = true; Transport = new MegaCrit.Sts2.Core.Multiplayer.NetSingleplayerGameService(); HostId = 1;
        Engine = new(new Match { Id = mock ? "mock-" + Guid.NewGuid().ToString("N")[..8] : "native-verification", Seed = seed, Racers = new() { ["1"] = new() { Id = "1", Name = mock ? "本地玩家" : "验证玩家" }, ["2"] = new() { Id = "2", Name = mock ? "模拟对手" : "验证对手", Health = 80, MaxHealth = 80 } } });
        Engine.State.EnabledEffects = RaceConfig.EnabledIds(); Engine.State.DebugMode = RaceConfig.DebugMode;
        PublicProfiles.Reset(); Current = this;
    }
    internal static RaceSession CreateVerification(string seed) => new(seed);
    internal static RaceSession CreateMock(string seed) => new(seed, true);
    internal RaceSession(StartRunLobby lobby, string seed, LobbyRules rules)
    {
        Current?.Dispose(); Current = this; Transport = lobby.NetService;
        HostId = IsHost ? Transport.NetId : ((INetClientGameService)Transport).NetClient?.HostNetId ?? throw new InvalidOperationException("找不到房主。");
        var m = new Match { Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((Transport.GetRawLobbyIdentifier() ?? "local") + seed)))[..24], Seed = seed };
        int slot = 0;
        foreach (var p in lobby.Players) m.Racers[p.id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new Racer { Id = p.id.ToString(System.Globalization.CultureInfo.InvariantCulture), Name = $"{++slot}号 · {p.character.Title.GetFormattedText()}" };
        m.EnabledEffects = rules.Effects.ToHashSet(); m.DebugMode = rules.Debug;
        Engine = new(m); PublicProfiles.Reset(); hello.Add(Transport.NetId);
        if (RaceRuntime.ResumeRequested)
        {
            if (!File.Exists(ResumePath)) throw new InvalidOperationException("没有完整的共同检查点，请所有玩家选择新开局或先完成共同存档。");
            Restored = NativeCheckpoint.Read(ResumePath);
            if (!Restored.Value.Match.Racers.Keys.Order().SequenceEqual(m.Racers.Keys.Order())) throw new InvalidOperationException("恢复比赛必须使用原来的玩家名单。");
            if (Restored.Value.Run.Players.Single().Character.Id != lobby.LocalPlayer.character.Id) throw new InvalidOperationException("恢复比赛时请选择原来的角色。");
            Engine = new(Restored.Value.Match);
        }
        handshake = Catalog.RulesVersion + "|" + (Restored == null ? "new:" + Engine.State.Seed : "resume:" + Engine.State.Id + ":" + Engine.State.Revision);
        Transport.RegisterMessageHandler<RaceMessage>(OnMessage); Transport.Disconnected += OnDisconnected;
        Transport.SetBufferMessages(false);
        if (Transport is INetHostGameService host) foreach (var id in lobby.Players.Select(p => p.id).Where(id => id != Transport.NetId)) host.SetPeerReadyForBroadcasting(id);
    }
    public async Task WaitForHandshake()
    {
        long started = Environment.TickCount64;
        while ((IsHost ? hello.Count < Engine.State.Racers.Count : !hello.Contains(HostId)))
        {
            if (Environment.TickCount64 - started > 20000) throw new InvalidOperationException("分道争锋握手超时，请确认所有玩家启用了同版本 MOD，并采用房主的本局配置。");
            Transport.SendMessage(new RaceMessage { Kind = "hello", Content = Handshake });
            await Task.Delay(200);
        }
        if (IsHost) Broadcast();
    }
    public void Tick()
    {
        if (disposed) return; Transport.Update();
        if ((!localVerification || IsMock) && !sendingTelemetry && !WaitingAtCheckpoint && Environment.TickCount64 - lastTelemetry >= 1000 && RaceRuntime.Live && RaceRuntime.State is { } state)
        {
            lastTelemetry = Environment.TickCount64;
            var profile = StateJson.Serialize(PublicProfiles.Capture(state));
            if (profile != lastPublicProfile && !Me.Eliminated && !Me.ResetPending)
            {
                sendingTelemetry = true;
                RaceUi.Run(async () => { try { await Submit(New("profile") with { Profile = profile }); lastPublicProfile = profile; } finally { sendingTelemetry = false; } });
            }
        }
    }
    public Command New(string kind, string key = "", string effect = "", int amount = 0, string value = "", bool boss = false, Route route = Route.Disrupt)
        => new(Guid.NewGuid().ToString("N"), LocalId, kind, key, effect, amount, RunManager.Instance.DebugOnlyGetState()?.CurrentActIndex ?? 0, RunManager.Instance.DebugOnlyGetState()?.ActFloor ?? 0, value, boss, route, RunManager.Instance.DebugOnlyGetState()?.Players.Single().Creature.MaxHp ?? 0);
    public async Task Submit(Command command)
    {
        await commandGate.WaitAsync();
        try
        {
            if (disposed) throw new InvalidOperationException("比赛连接已关闭。");
            if (WaitingAtCheckpoint && command.Kind != "ack") throw new RuleException("个人进度已进入共同检查点，请等待房主继续比赛。");
            if (IsHost) { Engine.Execute(command); Persist(); Broadcast(); Changed?.Invoke(); return; }
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); pending.Add(command.Id, done);
            try { Transport.SendMessage(new RaceMessage { Kind = "command", Content = StateJson.Serialize(command) }); await done.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            finally { pending.Remove(command.Id); }
        }
        finally { commandGate.Release(); }
    }
    private void OnMessage(RaceMessage message, ulong sender)
    {
        if (disposed || !Engine.State.Racers.ContainsKey(sender.ToString(System.Globalization.CultureInfo.InvariantCulture))) return;
        try
        {
            if (message.Kind == "hello")
            {
                if (message.Content != Handshake) throw new RuleException("规则版本或恢复检查点不一致。");
                hello.Add(sender);
                if (IsHost) { Transport.SendMessage(new RaceMessage { Kind = "welcome", Content = Handshake }, sender); Broadcast(); }
            }
            else if (message.Kind == "welcome" && sender == HostId && message.Content == Handshake) hello.Add(sender);
            else if (message.Kind == "command" && IsHost)
            {
                var c = StateJson.Deserialize<Command>(message.Content);
                if (c.Actor != sender.ToString(System.Globalization.CultureInfo.InvariantCulture)) throw new RuleException("操作身份不匹配。");
                try { Engine.Execute(c); Persist(); Broadcast(); Changed?.Invoke(); }
                catch (Exception e) { Transport.SendMessage(new RaceMessage { Kind = "reject", Content = c.Id + "\n" + e.Message }, sender); }
            }
            else if (message.Kind == "state" && sender == HostId && !IsHost)
            {
                var state = StateJson.Deserialize<Match>(RaceMessage.Expand(message.Content));
                if (state.Id != Engine.State.Id || state.Version != Catalog.RulesVersion || state.Revision < Engine.State.Revision) return;
                Engine = new(state); Persist(); foreach (var pair in pending) if (state.ProcessedCommands.Contains(pair.Key)) pair.Value.TrySetResult(); Changed?.Invoke();
            }
            else if (message.Kind == "reject" && sender == HostId)
            { var split = message.Content.IndexOf('\n'); if (split > 0 && pending.TryGetValue(message.Content[..split], out var t)) t.TrySetException(new RuleException(message.Content[(split + 1)..])); }
            else if (message.Kind == "pause_begin" && sender == HostId) { PauseRequested = true; RaceUi.Refresh(); }
            else if (message.Kind == "pause_ready" && IsHost) { checkpointReady.Add(sender); TryCommitPause(); }
            else if (message.Kind == "pause_commit" && sender == HostId && !IsHost)
            {
                var shared = StateJson.Deserialize<Match>(RaceMessage.Expand(message.Content));
                if (shared.Id != Engine.State.Id || shared.Version != Catalog.RulesVersion || shared.Revision < Engine.State.Revision) throw new RuleException("共同检查点版本不匹配。");
                Engine = new(shared); CommitPause();
            }
            else if (message.Kind == "pause_continue" && sender == HostId) ContinueLocal();
        }
        catch (Exception e) { RaceUi.Error("比赛消息处理失败：" + e.Message); }
    }
    private void Broadcast()
    { Transport.SendMessage(new RaceMessage { Kind = "state", Content = RaceMessage.Compress(StateJson.Serialize(Engine.State)) }); }
    public string SaveDirectory => ProjectSettings.GlobalizePath("user://FendaoZhengfeng/" + Engine.State.Id + "/" + LocalId);
    private void Persist()
    {
        if (localVerification || Environment.GetEnvironmentVariable("FDZF_NATIVE_VERIFY") == "1") return;
        Directory.CreateDirectory(SaveDirectory);
        AtomicWrite(Path.Combine(SaveDirectory, "match.json"), StateJson.Serialize(Engine.State));
    }
    internal static void AtomicWrite(string path, string contents)
    { var temp = path + ".tmp"; File.WriteAllText(temp, contents, Encoding.UTF8); File.Move(temp, path, true); }
    public bool ReceiptApplied(string id) => appliedReceipts.Contains(id);
    public void MarkReceiptApplied(string id) => appliedReceipts.Add(id);
    public void RequestPause()
    {
        if (!IsHost) throw new RuleException("共同暂停由房主发起。");
        if (Engine.State.Racers.Values.Any(p => p.Eliminated || p.Finished)) throw new RuleException("已有玩家结束本局；请在全员仍在爬塔时保存共同检查点。");
        if (PauseRequested) return;
        PauseRequested = true; checkpointReady.Clear(); Transport.SendMessage(new RaceMessage { Kind = "pause_begin" }); RaceUi.Refresh();
    }
    public async Task ArriveAtCheckpoint()
    {
        if (!PauseRequested || checkpointGate != null) return;
        await RaceUi.WhenIdle();
        if (Me.InBattle) throw new RuleException("尚未结算战斗，不能创建共同检查点。");
        var save = RunManager.Instance.ToSave(null); var writer = new MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketWriter(); save.Serialize(writer);
        checkpointPacket = Convert.ToBase64String(writer.Buffer, 0, writer.BytePosition);
        checkpointGate = new TaskCompletionSource();
        if (IsHost) { checkpointReady.Add(Transport.NetId); TryCommitPause(); } else Transport.SendMessage(new RaceMessage { Kind = "pause_ready" });
        RaceUi.Refresh(); await checkpointGate.Task;
    }
    private void TryCommitPause()
    {
        if (!IsHost || !PauseRequested || checkpointReady.Count != Engine.State.Racers.Count) return;
        if (Engine.State.Racers.Values.Any(p => p.InBattle)) throw new RuleException("有玩家仍在战斗，不能提交共同检查点。");
        Transport.SendMessage(new RaceMessage { Kind = "pause_commit", Content = RaceMessage.Compress(StateJson.Serialize(Engine.State)) }); CommitPause();
    }
    private void CommitPause()
    {
        if (checkpointPacket == null || checkpointGate == null) throw new RuleException("个人检查点尚未就绪。");
        var bundle = new NativeCheckpoint.Bundle("0.3.3", Catalog.RulesVersion, Engine.State.Revision, StateJson.Serialize(Engine.State), checkpointPacket);
        Directory.CreateDirectory(Path.GetDirectoryName(ResumePath)!); AtomicWrite(ResumePath, StateJson.Serialize(bundle)); Paused = true;
        RaceUi.Error("共同检查点已保存。可等待房主继续，或全员退出后重建房间并勾选恢复共同检查点。"); RaceUi.Refresh();
    }
    public void ContinueRace()
    {
        if (!IsHost) throw new RuleException("只有房主可以继续比赛。");
        Transport.SendMessage(new RaceMessage { Kind = "pause_continue" }); ContinueLocal();
    }
    private void ContinueLocal()
    { PauseRequested = false; Paused = false; checkpointReady.Clear(); checkpointPacket = null; var gate = checkpointGate; checkpointGate = null; gate?.TrySetResult(); RaceUi.Refresh(); }
    public bool TryReserveGhost()
    {
        int outstanding = ghostReservations.Count(id => !Engine.State.ProcessedCommands.Contains(id));
        if (Me.Ghosts <= outstanding) return false;
        var command = New("ghost"); ghostReservations.Add(command.Id);
        RaceUi.Run(async () => { try { await Submit(command); } finally { ghostReservations.Remove(command.Id); } });
        return true;
    }
    private void OnDisconnected(NetErrorInfo error)
    { foreach (var task in pending.Values) task.TrySetException(new IOException("比赛通信已断开。")); RaceUi.Error("比赛通信中断，当前存档已保留；不要继续作出不可逆奖励选择。"); }
    public void Dispose()
    {
        if (disposed) return; disposed = true; Transport.UnregisterMessageHandler<RaceMessage>(OnMessage); Transport.Disconnected -= OnDisconnected;
        foreach (var task in pending.Values) task.TrySetCanceled(); if (Current == this) Current = null;
        checkpointGate?.TrySetCanceled();
        if (Transport.IsConnected) Transport.Disconnect(NetError.Quit);
    }
}
