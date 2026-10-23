using FendaoZhengfeng.Core;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using Environment = System.Environment;

namespace FendaoZhengfeng.Mod;

// A host archive contains each isolated run, rather than pretending that the
// native cooperative save represents these independent singleplayer worlds.
internal sealed record RecoverySnapshot(string Player, string Character, string NativePacket,
    string BattleKey, string[] AppliedReceipts, string[] AppliedTrades, long CapturedAt, SavedCombatRewards? Rewards = null, Command[]? CompletedClaims = null);
internal sealed record RecoveryArchive(string RulesVersion, string Host, string MatchJson,
    Dictionary<string, RecoverySnapshot> Players, long SavedAt);

internal sealed class RaceRecovery(RaceSession session)
{
    private readonly Dictionary<string, RecoverySnapshot> players = [];
    private readonly Dictionary<string, Dictionary<string, Receipt>> journal = [];
    private long lastCapture;
    private readonly HashSet<string> readyPlayers = [];
    private string saveRequest = "";
    private bool saving;
    private bool remoteHold;
    private bool draining, remoteDraining;
    private int holdGeneration;
    private bool previouslyPaused;
    internal bool SuspendPeriodicCaptureForVerification { get; set; }
    internal bool IsSaving => saving;
    internal bool Frozen => saving && !draining || remoteHold;
    internal bool NativeGrantBlocked => saving || remoteDraining || remoteHold;
    internal bool IsDraining => draining;
    internal int BackupCount => players.Count;
    internal static string ArchivePath(string host) => Environment.GetEnvironmentVariable("FDZF_NATIVE_VERIFY") == "1"
        ? Path.Combine(Environment.GetEnvironmentVariable("FDZF_REPORT_DIR") ?? throw new InvalidOperationException(L10n.T("未指定验证目录。")), "recovery", host + ".json")
        : ProjectSettings.GlobalizePath("user://FendaoZhengfeng/recovery/" + host + ".json");

    internal void ObserveReceipts()
    {
        if (!session.IsHost) return;
        foreach (var racer in session.Engine.State.Racers.Values)
        {
            if (!journal.TryGetValue(racer.Id, out var receipts)) journal[racer.Id] = receipts = [];
            foreach (var receipt in racer.Receipts) receipts[receipt.Id] = receipt;
        }
    }
    internal static RecoverySnapshot Capture(RaceSession session)
    {
        if(NativeRewardGrant.IsApplying(session) || NativeEventStep.IsApplying(session)) throw new InvalidOperationException(L10n.T("事件或奖励执行过程中不能采集恢复快照。"));
        var run = RaceRuntime.State ?? throw new InvalidOperationException(L10n.T("当前没有比赛。"));
        var save = RunManager.Instance.ToSave(null);
        if (run.CurrentRoom is CombatRoom combat)
        {
            // CombatRoom.ToSerializable rejects unfinished nested event combat.
            // The encounter identity and parent are sufficient to recreate its
            // opening, with no reference to disposed scene nodes or turn data.
            save.PreFinishedRoom = new SerializableRoom
            {
                RoomType = combat.RoomType, EncounterId = combat.Encounter.Id,
                IsPreFinished = combat.IsPreFinished || session.Me.WonBattles.Contains(session.Me.BattleKey), GoldProportion = combat.GoldProportion,
                ParentEventId = combat.ParentEventId, ShouldResumeParentEvent = combat.ShouldResumeParentEventAfterCombat,
                EncounterState = combat.Encounter.SaveCustomState(),
                ExtraRewards = combat.ExtraRewards.ToDictionary(pair => pair.Key.NetId, pair => pair.Value.Select(r => r.ToSerializable()).ToList())
            };
        }
        else if (run.CurrentRoom is MerchantRoom merchant)
            save.PreFinishedRoom = new SerializableRoom { RoomType = RoomType.Shop,
                EncounterState = new() { [MerchantRecovery.SaveKey] = StateJson.Serialize(MerchantRecovery.Capture(merchant)) } };
        else if (run.CurrentRoom is RestSiteRoom camp)
            save.PreFinishedRoom = new SerializableRoom { RoomType = RoomType.RestSite,
                EncounterState = new() { [CampRecovery.SaveKey] = StateJson.Serialize(CampRecovery.Capture(camp)) } };
        else if (run.CurrentRoom is EventRoom openEvent && EventRecovery.CaptureOpen(openEvent) is { } openPage)
        {
            save.PreFinishedRoom = new SerializableRoom { RoomType = RoomType.Event, EventId = openEvent.CanonicalEvent.Id,
                EncounterState = new() { [EventRecovery.OpenKey] = StateJson.Serialize(openPage) } };
        }
        else if (run.CurrentRoom is EventRoom finishedEvent && finishedEvent.LocalMutableEvent.IsFinished
            && finishedEvent.LocalMutableEvent.LayoutType == MegaCrit.Sts2.Core.Events.EventLayoutType.Default)
        {
            save.PreFinishedRoom = finishedEvent.ToSerializable();
            save.PreFinishedRoom.EncounterState = new() { [EventRecovery.FinishedKey] = StateJson.Serialize(EventRecovery.CaptureFinished(finishedEvent)) };
        }
        else if (run.CurrentRoom is { IsPreFinished: true } room) save.PreFinishedRoom = room.ToSerializable();
        var writer = new PacketWriter(); save.Serialize(writer);
        return new(session.LocalId, run.Players.Single().Character.Id.ToString(),
            Convert.ToBase64String(writer.Buffer, 0, writer.BytePosition), session.Me.InBattle ? session.Me.BattleKey : "",
            session.AppliedReceiptIds(), RaceModifier.Active is { } modifier ? StateJson.Deserialize<string[]>(modifier.AppliedTrades) : [],
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), run.CurrentRoom is CombatRoom rewardRoom ? RewardRecovery.Capture(rewardRoom) : null, NativeClaimJournal.Capture(session));
    }
    internal RecoverySnapshot? SendSnapshot()
    {
        if (session.IsMock || !RaceRuntime.Live || session.WaitingAtCheckpoint || NativeRewardGrant.IsApplying(session) || NativeEventStep.IsApplying(session) || !session.IsHost && !session.Transport.IsConnected) return null;
        var snapshot = Capture(session); lastCapture = Environment.TickCount64;
        if (session.IsHost) Accept(snapshot, session.LocalId);
        else if (session.Transport.IsConnected) session.Transport.SendMessage(new RaceMessage { Kind = "recovery_snapshot", Content = RaceMessage.Compress(StateJson.Serialize(snapshot)) }, session.HostId);
        return snapshot;
    }
    internal void Tick()
    {
        if (session.IsMock || !RaceRuntime.Live || session.Me.ResetPending || session.WaitingAtCheckpoint || Frozen || SuspendPeriodicCaptureForVerification) return;
        if (Environment.TickCount64 - lastCapture >= 3000) SendSnapshot();
    }
    internal void Accept(RecoverySnapshot snapshot, string sender)
    {
        if (!session.IsHost || snapshot.Player != sender || !session.Engine.State.Racers.ContainsKey(sender)) throw new RuleException(L10n.T("恢复备份身份不匹配。"));
        if (snapshot.NativePacket.Length is < 1 or > 2500000 || snapshot.AppliedReceipts.Length > 100000 || snapshot.AppliedTrades.Length > 10000) throw new RuleException(L10n.T("恢复备份超出限制。"));
        // Validate the binary save before replacing the last usable snapshot.
        var reader = new PacketReader(); reader.Reset(Convert.FromBase64String(snapshot.NativePacket));
        var save = new MegaCrit.Sts2.Core.Saves.SerializableRun(); save.Deserialize(reader);
        if (save.Players.Count != 1) throw new RuleException(L10n.T("恢复备份必须是独立单人进度。"));
        if (snapshot.CompletedClaims is { } claims && (claims.Length > 1000
            || claims.Any(c => c.Actor != sender || c.Kind != "claim" || c.Amount < 0)))
            throw new RuleException(L10n.T("原生奖励完成凭证无效。"));
        players[sender] = snapshot;
        if (journal.TryGetValue(sender, out var receipts)) foreach (string id in snapshot.AppliedReceipts) receipts.Remove(id);
    }
    internal void RequireNativeClaimBackup(Command claim,string sender)
    {
        if(claim.Actor!=sender || claim.Kind!="claim") throw new RuleException(L10n.T("原生奖励领取身份不匹配。"));
        if(session.Engine.State.ProcessedCommands.Contains(claim.Id)) return;
        if(!players.TryGetValue(sender,out var snapshot) || snapshot.CompletedClaims?.Contains(claim)!=true)
            throw new RuleException(L10n.T("尚未收到这次原生奖励的完成备份，领取未确认。"));
    }
    internal async Task SaveHost(bool keepPaused = false)
    {
        if (!session.IsHost || session.IsMock) throw new RuleException(L10n.T("比赛存档由房主保存。"));
        if (saving) throw new RuleException(L10n.T("正在保存比赛。"));
        saving = true; draining = true; bool wasPaused = RunManager.Instance.IsPaused; bool saved = false;
        try
        {
            ObserveReceipts();
            long requestAt = Environment.TickCount64;
            saveRequest=Guid.NewGuid().ToString("N");readyPlayers.Clear();
            if (session.Transport.IsConnected) session.Transport.SendMessage(new RaceMessage { Kind = "recovery_request",Content=saveRequest });
            await NativeEventStep.WhenIdle(session);
            // A disconnected peer must never prevent the host saving. Connected
            // peers get a short opportunity to replace their periodic backup.
            while (session.Transport.IsConnected && Environment.TickCount64 - requestAt < 5500)
            {
                var connected = session.Transport is MegaCrit.Sts2.Core.Multiplayer.NetHostGameService host
                    ? host.ConnectedPeers.Select(peer => peer.peerId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()
                    : session.Engine.State.Racers.Keys.Where(id => id != session.LocalId).ToArray();
                if (connected.All(id => readyPlayers.Contains(id))) break;
                await Task.Delay(20);
            }
            if(session.Transport is MegaCrit.Sts2.Core.Multiplayer.NetHostGameService liveHost
                && liveHost.ConnectedPeers.Any(peer=>!readyPlayers.Contains(peer.peerId.ToString(System.Globalization.CultureInfo.InvariantCulture))))
                throw new IOException(L10n.T("在线玩家尚未完成奖励发放，未覆盖旧存档，请稍后重试。"));
            draining=false;RunManager.Instance.IsPaused=true;
            Accept(Capture(session), session.LocalId);
            var missing = session.Engine.State.Racers.Keys.Where(id => !players.ContainsKey(id)).ToList();
            if (missing.Count > 0) throw new RuleException(L10n.T("尚未收到这些玩家的房间备份，未覆盖旧存档：") + string.Join("、", missing));
            var match = StateJson.Deserialize<Match>(StateJson.Serialize(session.Engine.State));
            foreach (var snapshot in players.Values)
                match = RewardClaimRecovery.Merge(match, snapshot.Player, snapshot.CompletedClaims ?? []);
            foreach (var racer in match.Racers.Values)
            {
                var snapshot = players[racer.Id]; var applied = snapshot.AppliedReceipts.ToHashSet();
                if (journal.TryGetValue(racer.Id, out var receipts))
                    foreach (var receipt in receipts.Values.Where(receipt => !applied.Contains(receipt.Id)))
                        if (racer.Receipts.All(existing => existing.Id != receipt.Id)) racer.Receipts.Add(receipt);
                // A native snapshot may precede the peer acknowledging a card
                // transfer. Replay precisely the sides absent from that save.
                var trades = snapshot.AppliedTrades.ToHashSet();
                foreach (var trade in match.Trades)
                {
                    if (trade.Seller == racer.Id) trade.Removed = trades.Contains("remove:" + trade.Id);
                    if (trade.Buyer == racer.Id) trade.Delivered = trades.Contains("deliver:" + trade.Id);
                }
            }
            var archive = new RecoveryArchive(Catalog.RulesVersion, session.LocalId, StateJson.Serialize(match), new(players), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            string path = ArchivePath(session.LocalId); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await Task.Run(() => RaceSession.AtomicWrite(path, StateJson.Serialize(archive)));
            saved = true;
        }
        finally
        {
            saving = false;draining=false;
            if (saved && keepPaused) { remoteHold = true; previouslyPaused = wasPaused; }
            else
            {
                RunManager.Instance.IsPaused = wasPaused;
                if (session.Transport.IsConnected) session.Transport.SendMessage(new RaceMessage { Kind = "recovery_release" });
            }
        }
    }
    internal void ConfirmReady(string sender,string request)
    { if(saving && request==saveRequest && players.ContainsKey(sender)) readyPlayers.Add(sender); }
    internal async Task HoldAndSendSnapshot(string request)
    {
        int generation=++holdGeneration;remoteDraining=true;
        try {
            await NativeEventStep.WhenIdle(session);
            if(generation!=holdGeneration) return;
            if (!remoteHold) { previouslyPaused = RunManager.Instance.IsPaused; remoteHold = true; RunManager.Instance.IsPaused = true; }
            remoteDraining=false;
            if(SendSnapshot()!=null) session.Transport.SendMessage(new RaceMessage {Kind="recovery_ready",Content=request},session.HostId);
        } catch(Exception error) {
            if(generation==holdGeneration) remoteDraining=false;
            RaceUi.Error(L10n.T("无法采集本次保存快照：")+L10n.T(error.Message));
        }
    }
    internal void Release()
    {
        holdGeneration++;remoteDraining=false;
        if (remoteHold) { remoteHold = false; RunManager.Instance.IsPaused = previouslyPaused; }
    }
    internal static RecoveryArchive Read(string host)
    {
        var archive = StateJson.Deserialize<RecoveryArchive>(File.ReadAllText(ArchivePath(host)));
        if (archive.Host != host) throw new InvalidDataException(L10n.T("比赛存档房主不匹配。"));
        var match = MatchCompatibility.Read(archive.RulesVersion, archive.MatchJson);
        return archive with { RulesVersion = Catalog.RulesVersion, MatchJson = StateJson.Serialize(match) };
    }
}
