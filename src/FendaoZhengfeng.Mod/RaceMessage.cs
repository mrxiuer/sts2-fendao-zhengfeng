using System.IO.Compression;
using System.Text;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace FendaoZhengfeng.Mod;

public sealed class RaceMessage : INetMessage
{
    public string Kind { get; set; } = "";
    public string Content { get; set; } = "";
    public bool ShouldBroadcast => false;
    public bool ShouldBuffer => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public void Serialize(PacketWriter writer)
    {
        writer.WriteByte(1);
        Write(writer, Kind, 32); Write(writer, Content, 768 * 1024);
    }
    public void Deserialize(PacketReader reader)
    {
        if (reader.ReadByte() != 1) throw new InvalidDataException(L10n.T("分道争锋通信协议不兼容。"));
        Kind = Read(reader, 32); Content = Read(reader, 768 * 1024);
    }
    private static void Write(PacketWriter writer, string value, int max)
    { var bytes = Encoding.UTF8.GetBytes(value); if (bytes.Length > max) throw new InvalidDataException(L10n.T("消息过大。")); writer.WriteInt(bytes.Length); writer.WriteBytes(bytes, bytes.Length); }
    private static string Read(PacketReader reader, int max)
    { int len = reader.ReadInt(); if (len < 0 || len > max || len > reader.Buffer.Length - reader.BitPosition / 8) throw new InvalidDataException(L10n.T("消息长度无效。")); var bytes = new byte[len]; reader.ReadBytes(bytes, len); return Encoding.UTF8.GetString(bytes); }
    public static string Compress(string value)
    { using var output = new MemoryStream(); using (var zip = new BrotliStream(output, CompressionLevel.Fastest, true)) zip.Write(Encoding.UTF8.GetBytes(value)); return Convert.ToBase64String(output.ToArray()); }
    public static string Expand(string value)
    {
        using var input = new MemoryStream(Convert.FromBase64String(value)); using var zip = new BrotliStream(input, CompressionMode.Decompress); using var output = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = zip.Read(buffer)) > 0) { if (output.Length + read > 4 * 1024 * 1024) throw new InvalidDataException(L10n.T("解压消息超出限制。")); output.Write(buffer, 0, read); }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
