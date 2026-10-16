using System.IO.Compression;
using System.IO.Hashing;
using EQCollector.Core;
using Xunit;

namespace EQCollector.Tests;

/// <summary>Builds EQ session packets the way the game sends them and checks the reader unwraps them.</summary>
public class SessionReaderTests
{
    private const uint ClientIp = 0xC0A80105;   // 192.168.1.5
    private const uint ServerIp = 0x45AE0102;   // 69.174.1.2
    private const int ClientPort = 50000;
    private const int ServerPort = 9000;
    private const int Session = 0x01020304;
    private static readonly byte[] WireKey = { 1, 2, 3, 4 };
    private static readonly byte[] Key = { 4, 3, 2, 1 };       // the reader uses the key bytes reversed

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static byte[] Be16(int v) => new[] { (byte)(v >> 8), (byte)v };
    private static byte[] Be32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static byte[] WithCrc(int opcode, byte[] body)
    {
        var crc = Crc32.HashToUInt32(Concat(Key, Be16(opcode), body)) & 0xFFFF;
        return Concat(Be16(opcode), body, Be16((int)crc));
    }

    private static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    private static (EQStreamReader Reader, List<AppPacket> Got) Connected()
    {
        var got = new List<AppPacket>();
        var reader = new EQStreamReader(got.Add);
        // session request from the client (22 bytes after the opcode: 4 skipped, session id, padding)
        reader.Feed(ClientIp, ServerIp, ClientPort, ServerPort,
            Concat(Be16(1), new byte[4], Be32(Session), new byte[14]), 1);
        // session response from the server: session id, key, 3 bytes, max length, padding (19 after the opcode)
        reader.Feed(ServerIp, ClientIp, ServerPort, ClientPort,
            Concat(Be16(2), Be32(Session), WireKey, new byte[3], Be32(512), new byte[4]), 2);
        return (reader, got);
    }

    private static byte[] App(int appOpcode, byte[] data) => Concat(new[] { (byte)appOpcode, (byte)(appOpcode >> 8) }, data);

    private static byte[] ServerData(int seq, byte[] app, string flag)
    {
        var inner = Concat(Be16(seq), app);
        byte[] body = flag switch
        {
            "raw" => inner,
            "stored" => Concat(new byte[] { 0xA5 }, inner),
            _ => Concat(new byte[] { 0x5A }, Zlib(inner)),
        };
        return WithCrc(0x09, body);
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("stored")]
    [InlineData("zlib")]
    public void A_data_packet_gives_its_application_packet(string flag)
    {
        var (reader, got) = Connected();
        var payload = Enumerable.Range(0, 40).Select(i => (byte)(i + 10)).ToArray();   // first byte > 0xA5? no: avoids flag bytes
        reader.Feed(ServerIp, ClientIp, ServerPort, ClientPort, ServerData(1, App(0x30cf, payload), flag), 3);

        var p = Assert.Single(got);
        Assert.Equal(0x30cf, p.Opcode);
        Assert.False(p.ClientToServer);
        Assert.Equal(ClientPort, p.ClientPort);
        // a raw packet keeps its two checksum bytes at the end
        Assert.Equal(payload, p.Data.Take(payload.Length).ToArray());
    }

    [Fact]
    public void A_packet_with_a_bad_checksum_is_ignored()
    {
        var (reader, got) = Connected();
        var packet = ServerData(1, App(0x1111, new byte[30]), "stored");
        packet[^1] ^= 0xFF;
        reader.Feed(ServerIp, ClientIp, ServerPort, ClientPort, packet, 3);
        Assert.Empty(got);
    }

    [Fact]
    public void Packets_before_the_handshake_are_ignored()
    {
        var got = new List<AppPacket>();
        var reader = new EQStreamReader(got.Add);
        reader.Feed(ServerIp, ClientIp, ServerPort, ClientPort, ServerData(1, App(0x1111, new byte[30]), "stored"), 1);
        Assert.Empty(got);
    }

    [Fact]
    public void A_big_packet_split_into_fragments_is_put_back_together()
    {
        var (reader, got) = Connected();
        var big = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7 % 251)).ToArray();
        var app = App(0x77d4, big);
        var whole = Concat(Be32((uint)app.Length), app);       // the first fragment starts with the total length

        int chunk = 400;      // a fragment must fit the session's maximum packet length (512)
        int seq = 10;
        for (int offset = 0; offset < whole.Length; offset += chunk)
        {
            var part = whole.Skip(offset).Take(chunk).ToArray();
            var body = Concat(new byte[] { 0xA5 }, Be16(seq++), part);
            reader.Feed(ServerIp, ClientIp, ServerPort, ClientPort, WithCrc(0x0D, body), 3 + seq);
        }

        var p = Assert.Single(got);
        Assert.Equal(0x77d4, p.Opcode);
        Assert.Equal(big, p.Data);
    }

    [Fact]
    public void Packets_bundled_together_are_each_unwrapped()
    {
        var (reader, got) = Connected();
        byte[] SubPacket(int seq, int appOpcode, byte fill) =>
            Concat(Be16(0x09), Be16(seq), App(appOpcode, Enumerable.Repeat(fill, 12).ToArray()));

        var a = SubPacket(1, 0x1001, 0x11);
        var b = SubPacket(2, 0x1002, 0x22);
        var inner = Concat(new[] { (byte)a.Length }, a, new[] { (byte)b.Length }, b);
        var body = Concat(new byte[] { 0xA5 }, inner);
        reader.Feed(ServerIp, ClientIp, ServerPort, ClientPort, WithCrc(0x03, body), 3);

        Assert.Equal(new[] { 0x1001, 0x1002 }, got.Select(p => (int)p.Opcode).ToArray());
        Assert.All(got, p => Assert.Equal(12, p.Data.Length));
    }

    [Fact]
    public void An_application_bundle_is_split_by_its_length_prefixes()
    {
        var (reader, got) = Connected();
        byte[] Entry(int op, byte fill) => Concat(new[] { (byte)(12 + 2) }, App(op, Enumerable.Repeat(fill, 12).ToArray()));
        var bundle = App(0x1900, Concat(Entry(0x2001, 1), Entry(0x2002, 2)));
        reader.Feed(ServerIp, ClientIp, ServerPort, ClientPort, ServerData(1, bundle, "stored"), 3);

        Assert.Equal(new[] { 0x2001, 0x2002 }, got.Select(p => (int)p.Opcode).ToArray());
    }
}
