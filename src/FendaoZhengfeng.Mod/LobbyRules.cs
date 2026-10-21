using FendaoZhengfeng.Core;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;

namespace FendaoZhengfeng.Mod;

internal sealed record LobbyRules(string Version, bool Enabled, bool Debug, HashSet<string> Effects, bool Resume, string ResumeBundle = "")
{
    internal static LobbyRules Local() => new(Catalog.RulesVersion, RaceConfig.ModeEnabled, RaceConfig.DebugMode, RaceConfig.EnabledIds(), RaceRuntime.ResumeRequested);
    // Every modded participant enters this handshake, including clients whose local mode is disabled.
    internal static async Task<LobbyRules> Agree(StartRunLobby lobby)
    {
        var transport = lobby.NetService;
        bool host = transport.Type == NetGameType.Host;
        ulong hostId = host ? transport.NetId : ((INetClientGameService)transport).NetClient!.HostNetId;
        var expected = lobby.Players.Select(p => p.id).ToHashSet();
        var ready = new HashSet<ulong> { transport.NetId };
        LobbyRules? agreed = host ? Local() : null;
        RecoveryArchive? archive = host && agreed!.Resume && RaceRuntime.ResumeFromHostArchive ? RaceRecovery.Read(transport.NetId.ToString()) : null;
        LobbyRules ForPlayer(ulong id)
        {
            if (archive == null) return agreed!;
            string playerId = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var match = StateJson.Deserialize<Match>(archive.MatchJson);
            if (!expected.Select(peer => peer.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToHashSet().SetEquals(match.Racers.Keys)) throw new RuleException("恢复比赛需要原来的玩家名单。");
            if (!archive.Players.TryGetValue(playerId, out var snapshot)) throw new RuleException("缺少该玩家的恢复备份。");
            var bundle = new NativeCheckpoint.Bundle("0.5.0", archive.RulesVersion, match.Revision, archive.MatchJson, snapshot.NativePacket, snapshot.AppliedReceipts, snapshot.BattleKey);
            return agreed! with { Enabled = true, Debug = match.DebugMode, Effects = match.EnabledEffects, ResumeBundle = RaceMessage.Compress(StateJson.Serialize(bundle)) };
        }
        string? error = null; bool committed = false;
        if (transport is INetHostGameService hostService)
            foreach (ulong peer in expected.Where(id => id != transport.NetId)) hostService.SetPeerReadyForBroadcasting(peer);
        void Receive(RaceMessage message, ulong sender)
        {
            if (!expected.Contains(sender)) return;
            try
            {
                if (message.Kind == "rules_hello" && host)
                    transport.SendMessage(new RaceMessage { Kind = "rules", Content = StateJson.Serialize(ForPlayer(sender)) }, sender);
                else if (message.Kind == "rules" && !host && sender == hostId)
                {
                    var rules = StateJson.Deserialize<LobbyRules>(message.Content);
                    if (rules.Version != Catalog.RulesVersion || rules.Effects.Any(id => !Catalog.All.Any(e => e.Id == id))) throw new RuleException("房主规则与本机模组版本不兼容。");
                    agreed = rules;
                    transport.SendMessage(new RaceMessage { Kind = "rules_ready", Content = rules.Version }, hostId);
                }
                else if (message.Kind == "rules_ready" && host && message.Content == Catalog.RulesVersion) ready.Add(sender);
                else if (message.Kind == "rules_commit" && !host && sender == hostId && agreed != null) committed = true;
            }
            catch (Exception exception) { error = exception.Message; }
        }
        transport.RegisterMessageHandler<RaceMessage>(Receive);
        try
        {
            long deadline = Environment.TickCount64 + 20000;
            while (!committed)
            {
                if (error != null) throw new RuleException(error);
                if (Environment.TickCount64 > deadline) throw new RuleException("房主配置同步超时，请确认所有玩家安装并启用了同版本分道争锋。");
                transport.Update();
                if (host)
                {
                    if (ready.SetEquals(expected))
                    {
                        transport.SendMessage(new RaceMessage { Kind = "rules_commit" }); committed = true;
                    }
                    else foreach (var peer in expected.Where(peer => peer != transport.NetId))
                        transport.SendMessage(new RaceMessage { Kind = "rules", Content = StateJson.Serialize(ForPlayer(peer)) }, peer);
                }
                else transport.SendMessage(new RaceMessage { Kind = "rules_hello" });
                if (!committed) await Task.Delay(100);
            }
            return host ? ForPlayer(transport.NetId) : agreed!;
        }
        finally { transport.UnregisterMessageHandler<RaceMessage>(Receive); }
    }
}
