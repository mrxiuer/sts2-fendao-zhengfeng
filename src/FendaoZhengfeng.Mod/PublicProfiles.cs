using FendaoZhengfeng.Core;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace FendaoZhengfeng.Mod;

internal static class PublicProfiles
{
    private static readonly Dictionary<string, (long Revision, Player Player)> cache = [];
    internal static void Reset() => cache.Clear();
    internal static PublicProfile Capture(RunState run)
    {
        var player = run.Players.Single();
        var writer = new PacketWriter(); player.ToSerializable().Serialize(writer);
        string packet = Convert.ToBase64String(writer.Buffer, 0, writer.BytePosition);
        RouteStep? location = run.CurrentMapCoord is { } coord ? new(run.CurrentActIndex, coord.row, coord.col) : null;
        return new(packet, player.Creature.CurrentHp, player.Creature.MaxHp, run.CurrentActIndex, run.ActFloor, run.CurrentRoom?.RoomType.ToString() ?? "", location, player.Gold, RaceModifier.Active?.CaptureMarket());
    }
    internal static Player? Read(Racer racer)
    {
        if (racer.PublicPacket.Length == 0) return null;
        if (cache.TryGetValue(racer.Id, out var previous) && previous.Revision == racer.ProfileRevision) return previous.Player;
        var reader = new PacketReader(); reader.Reset(Convert.FromBase64String(racer.PublicPacket));
        var save = new SerializablePlayer(); save.Deserialize(reader);
        var player = Player.FromSerializable(save);
        // This isolated read-only run never enters RunManager, combat or the live player's collections.
        _ = RunState.CreateForTest(players: [player], seed: RaceSession.Current!.Engine.State.Seed);
        cache[racer.Id] = (racer.ProfileRevision, player); return player;
    }
}
