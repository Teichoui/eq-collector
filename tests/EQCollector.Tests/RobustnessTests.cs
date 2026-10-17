using System.IO.Compression;
using System.IO.Hashing;
using EQCollector.Core;
using Xunit;

namespace EQCollector.Tests;

/// <summary>Behaviour on awkward or hostile input (found by review): wrap-around, raw packets, bombs, damaged files.</summary>
public class RobustnessTests
{
    private const uint Server = 0x45AE0102;
    private const int ServerPort = 9000;
    private static readonly byte[] WireKey = { 1, 2, 3, 4 };
    private static readonly byte[] Key = { 4, 3, 2, 1 };

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static byte[] Be16(int v) => new[] { (byte)(v >> 8), (byte)v };
    private static byte[] Be32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static byte[] WithCrc(int opcode, byte[] body)
    {
        var crc = Crc32.HashToUInt32(Concat(Key, Be16(opcode), body)) & 0xFFFF;
        return Concat(Be16(opcode), body, Be16((int)crc));
    }

    private static byte[] App(int appOpcode, byte[] data) => Concat(new[] { (byte)appOpcode, (byte)(appOpcode >> 8) }, data);

    private static void Connect(EQStreamReader reader, uint clientIp, int clientPort, int session = 0x01020304)
    {
        reader.Feed(clientIp, Server, clientPort, ServerPort, Concat(Be16(1), new byte[4], Be32((uint)session), new byte[14]), 1);
        reader.Feed(Server, clientIp, ServerPort, clientPort, Concat(Be16(2), Be32((uint)session), WireKey, new byte[3], Be32(512), new byte[4]), 2);
    }

    private static byte[] StoredData(int seq, byte[] app) => WithCrc(0x09, Concat(new byte[] { 0xA5 }, Be16(seq), app));

    [Fact]
    public void Fragments_that_cross_the_16_bit_sequence_wrap_are_joined()
    {
        var got = new List<AppPacket>();
        var reader = new EQStreamReader(got.Add);
        Connect(reader, 0xC0A80105, 50000);

        var big = Enumerable.Range(0, 1500).Select(i => (byte)(i * 11 % 253)).ToArray();
        var app = App(0x77d4, big);
        var whole = Concat(Be32((uint)app.Length), app);
        int seq = 65534;                                        // 65534, 65535, 0, 1, ...
        double t = 3;
        for (int offset = 0; offset < whole.Length; offset += 400)
        {
            var part = whole.Skip(offset).Take(400).ToArray();
            reader.Feed(Server, 0xC0A80105, ServerPort, 50000, WithCrc(0x0D, Concat(new byte[] { 0xA5 }, Be16(seq & 0xFFFF), part)), t++);
            seq++;
        }

        var p = Assert.Single(got);
        Assert.Equal(big, p.Data);
    }

    [Fact]
    public void Fragments_arriving_out_of_order_are_still_joined()
    {
        var got = new List<AppPacket>();
        var reader = new EQStreamReader(got.Add);
        Connect(reader, 0xC0A80105, 50000);

        var big = Enumerable.Range(0, 1000).Select(i => (byte)(i % 249)).ToArray();
        var app = App(0x1234, big);
        var whole = Concat(Be32((uint)app.Length), app);
        var pieces = Enumerable.Range(0, (whole.Length + 399) / 400).Select(i => (Seq: 100 + i, Part: whole.Skip(i * 400).Take(400).ToArray())).ToList();
        foreach (var piece in new[] { pieces[2], pieces[0], pieces[1] })
            reader.Feed(Server, 0xC0A80105, ServerPort, 50000, WithCrc(0x0D, Concat(new byte[] { 0xA5 }, Be16(piece.Seq), piece.Part)), 3);

        Assert.Equal(big, Assert.Single(got).Data);
    }

    [Fact]
    public void A_raw_packet_loses_its_checksum_bytes()
    {
        var got = new List<AppPacket>();
        var reader = new EQStreamReader(got.Add);
        Connect(reader, 0xC0A80105, 50000);

        var payload = Enumerable.Range(0, 40).Select(i => (byte)(i + 10)).ToArray();
        reader.Feed(Server, 0xC0A80105, ServerPort, 50000, WithCrc(0x09, Concat(Be16(1), App(0x30cf, payload))), 3);   // no flag byte

        Assert.Equal(payload, Assert.Single(got).Data);
    }

    [Fact]
    public void A_decompression_bomb_is_dropped_without_exhausting_memory()
    {
        var got = new List<AppPacket>();
        var reader = new EQStreamReader(got.Add);
        Connect(reader, 0xC0A80105, 50000);

        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                var zeros = new byte[1 << 20];
                for (int i = 0; i < 64; i++) z.Write(zeros);        // 64 MB of zeros
            }
            compressed = ms.ToArray();
        }
        Assert.True(compressed.Length < 500_000);                  // it fits in one datagram's worth of data many times over

        // a datagram can only be so big; the reader's limit is what matters, so feed the compressed stream as one packet
        reader.Feed(Server, 0xC0A80105, ServerPort, 50000, WithCrc(0x09, Concat(new byte[] { 0x5A }, compressed)), 3);
        Assert.Empty(got);
    }

    [Fact]
    public void Two_clients_on_the_same_port_do_not_mix_and_one_disconnect_leaves_the_other()
    {
        var got = new List<AppPacket>();
        var reader = new EQStreamReader(got.Add);
        Connect(reader, 0xC0A80105, 50000);
        Connect(reader, 0xC0A80106, 50000, session: 0x0A0B0C0D);   // another machine, same ephemeral port

        reader.Feed(Server, 0xC0A80105, ServerPort, 50000, StoredData(1, App(0x1111, new byte[30])), 3);
        reader.Feed(Server, 0xC0A80106, ServerPort, 50000, StoredData(1, App(0x2222, new byte[30])), 3);
        Assert.Equal(new[] { 0x1111, 0x2222 }, got.Select(p => (int)p.Opcode).ToArray());

        // the first client disconnects (a 9-byte disconnect body, from the client)
        reader.Feed(0xC0A80105, Server, 50000, ServerPort, Concat(Be16(5), new byte[9]), 4);
        got.Clear();
        reader.Feed(Server, 0xC0A80105, ServerPort, 50000, StoredData(2, App(0x1111, new byte[30])), 5);   // now ignored
        reader.Feed(Server, 0xC0A80106, ServerPort, 50000, StoredData(2, App(0x2222, new byte[30])), 5);   // still works
        Assert.Equal(new[] { 0x2222 }, got.Select(p => (int)p.Opcode).ToArray());
    }

    [Fact]
    public void A_throwing_handler_does_not_escape_the_reader()
    {
        var reader = new EQStreamReader(_ => throw new IOException("disk full"));
        var errors = new List<string>();
        reader.OnCallbackError = ex => errors.Add(ex.Message);
        Connect(reader, 0xC0A80105, 50000);

        reader.Feed(Server, 0xC0A80105, ServerPort, 50000, StoredData(1, App(0x1111, new byte[30])), 3);   // must not throw
        Assert.Equal(new[] { "disk full" }, errors);
    }

    // ---- capture files and frames -----------------------------------------------------------------------------------

    private static string TempFile(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static void Finishes(Func<int> action)
    {
        var task = Task.Run(action);
        Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "reading a damaged file did not finish");
    }

    [Fact]
    public void A_pcapng_with_a_zero_length_section_header_ends_instead_of_looping()
    {
        // type 0x0A0D0D0A, length 0, byte-order magic, then padding
        var bytes = Concat(new byte[] { 0x0A, 0x0D, 0x0D, 0x0A }, new byte[4], new byte[] { 0x4D, 0x3C, 0x2B, 0x1A }, new byte[64]);
        var path = TempFile(bytes);
        try { Finishes(() => PcapReader.ReadFrames(path).Count()); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_pcapng_with_a_packet_block_that_claims_more_than_it_holds_ends_cleanly()
    {
        var shb = Concat(new byte[] { 0x0A, 0x0D, 0x0D, 0x0A }, BitConverter.GetBytes(28u), BitConverter.GetBytes(0x1A2B3C4Du),
            BitConverter.GetBytes((ushort)1), BitConverter.GetBytes((ushort)0), BitConverter.GetBytes(-1L), BitConverter.GetBytes(28u));
        var idb = Concat(BitConverter.GetBytes(1u), BitConverter.GetBytes(20u), BitConverter.GetBytes((ushort)1), BitConverter.GetBytes((ushort)0),
            BitConverter.GetBytes(65535u), BitConverter.GetBytes(20u));
        // an enhanced packet block of 36 bytes that says it captured 0x7fffffff bytes
        var epb = Concat(BitConverter.GetBytes(6u), BitConverter.GetBytes(36u), BitConverter.GetBytes(0u), BitConverter.GetBytes(0u), BitConverter.GetBytes(0u),
            BitConverter.GetBytes(0x7FFFFFFFu), BitConverter.GetBytes(0x7FFFFFFFu), BitConverter.GetBytes(36u));
        var path = TempFile(Concat(shb, idb, epb));
        try { Finishes(() => PcapReader.ReadFrames(path).Count()); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_file_too_short_to_be_a_capture_is_reported()
    {
        var path = TempFile(new byte[] { 0xD4, 0xC3, 0xB2, 0xA1, 2, 0, 4, 0 });
        try { Assert.Throws<InvalidDataException>(() => PcapReader.ReadFrames(path).Count()); }
        finally { File.Delete(path); }
    }

    private static byte[] UdpFrame(int claimedTotalLength, int actualPayload)
    {
        var frame = new byte[14 + 20 + 8 + actualPayload];
        frame[12] = 0x08;                                            // ethertype IPv4
        frame[14] = 0x45;                                            // IPv4, 20-byte header
        frame[16] = (byte)(claimedTotalLength >> 8); frame[17] = (byte)claimedTotalLength;
        frame[23] = 17;                                              // UDP
        int udpLength = 8 + actualPayload;
        frame[14 + 20 + 4] = (byte)(udpLength >> 8); frame[14 + 20 + 5] = (byte)udpLength;
        return frame;
    }

    [Fact]
    public void A_frame_cut_short_by_the_capture_is_not_passed_on()
    {
        Assert.True(FrameParser.TryParseUdp(FrameParser.LinkEthernet, UdpFrame(20 + 8 + 10, 10), 0, out var ok));
        Assert.Equal(10, ok.Payload.Length);

        // the IP header says 100 bytes but only 10 were captured
        Assert.False(FrameParser.TryParseUdp(FrameParser.LinkEthernet, UdpFrame(20 + 8 + 100, 10), 0, out _));
    }

    [Fact]
    public void A_capture_written_by_the_PcapWriter_reads_back_the_same()
    {
        var path = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N") + ".pcap");
        var frames = new[] { UdpFrame(20 + 8 + 10, 10), UdpFrame(20 + 8 + 30, 30), UdpFrame(20 + 8 + 5, 5) };
        try
        {
            using (var writer = new PcapWriter(path, FrameParser.LinkEthernet))
            {
                for (int i = 0; i < frames.Length; i++) writer.Write(1000.5 + i, frames[i]);
                Assert.Equal(3, writer.Frames);
            }
            var read = PcapReader.ReadFrames(path).ToList();
            Assert.Equal(3, read.Count);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(frames[i], read[i].Data);
                Assert.Equal(FrameParser.LinkEthernet, read[i].LinkType);
                Assert.Equal(1000.5 + i, read[i].Time, 3);
            }
            Assert.Equal(3, PcapReader.ReadUdp(path).Count());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_stacked_vlan_frame_is_read()
    {
        var plain = UdpFrame(20 + 8 + 10, 10);
        // 802.1ad outer tag, 802.1Q inner tag, then IPv4
        var tagged = Concat(plain.Take(12).ToArray(), new byte[] { 0x88, 0xA8, 0, 1, 0x81, 0x00, 0, 2, 0x08, 0x00 }, plain.Skip(14).ToArray());
        Assert.True(FrameParser.TryParseUdp(FrameParser.LinkEthernet, tagged, 0, out var udp));
        Assert.Equal(10, udp.Payload.Length);
    }

    [Fact]
    public void Every_item_column_has_its_own_name()
    {
        // a repeated name would make the second value silently vanish from items.jsonl
        Assert.Equal(ItemDecoder.Columns.Length, ItemDecoder.Columns.Distinct().Count());
    }

    [Fact]
    public void Skipping_an_enormous_count_is_a_parse_error_not_an_overflow()
    {
        var cursor = new ByteCursor(new byte[10], 5);
        Assert.Throws<ParseException>(() => cursor.Skip(int.MaxValue));
    }

    [Fact]
    public void A_payload_in_a_thousand_fragments_is_joined_quickly()
    {
        var got = new List<AppPacket>();
        var reader = new EQStreamReader(got.Add);
        Connect(reader, 0xC0A80105, 50000);

        var big = new byte[1000 * 400 - 6];
        new Random(1).NextBytes(big);
        var app = App(0x77d4, big);                                  // 2-byte opcode + data
        var whole = Concat(Be32((uint)app.Length), app);             // 4-byte length prefix: exactly 1000 pieces of 400
        Assert.Equal(1000 * 400, whole.Length);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            var part = whole.Skip(i * 400).Take(400).ToArray();
            reader.Feed(Server, 0xC0A80105, ServerPort, 50000, WithCrc(0x0D, Concat(new byte[] { 0xA5 }, Be16(i), part)), 3);
        }

        Assert.Equal(big, Assert.Single(got).Data);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"took {timer.Elapsed}");
    }

    private static void SendFragments(EQStreamReader reader, byte[] app, int firstSeq, int chunk, int skipIndex = -1, double time = 3)
    {
        var whole = Concat(Be32((uint)app.Length), app);
        int index = 0;
        for (int offset = 0; offset < whole.Length; offset += chunk, index++)
        {
            if (index == skipIndex) continue;                                  // this fragment is "lost"
            var part = whole.Skip(offset).Take(chunk).ToArray();
            reader.Feed(Server, 0xC0A80105, ServerPort, 50000, WithCrc(0x0D, Concat(new byte[] { 0xA5 }, Be16((firstSeq + index) & 0xFFFF), part)), time);
        }
    }

    [Fact]
    public void A_lost_last_fragment_does_not_stop_the_next_message()
    {
        var got = new List<AppPacket>();
        var reader = new EQStreamReader(got.Add);
        Connect(reader, 0xC0A80105, 50000);

        var a = App(0x1111, Enumerable.Range(0, 1000).Select(i => (byte)(i % 200)).ToArray());   // 3 fragments: seq 10, 11, 12
        var b = App(0x2222, Enumerable.Range(0, 700).Select(i => (byte)(i % 190)).ToArray());     // 2 fragments: seq 13, 14
        SendFragments(reader, a, firstSeq: 10, chunk: 400, skipIndex: 2);       // the end of A never arrives
        SendFragments(reader, b, firstSeq: 13, chunk: 400);

        var p = Assert.Single(got);
        Assert.Equal(0x2222, p.Opcode);
        Assert.Equal(700, p.Data.Length);
    }

    [Fact]
    public void A_session_is_learned_from_its_response_when_the_request_was_not_captured()
    {
        var got = new List<AppPacket>();
        var reader = new EQStreamReader(got.Add);
        // no request: only the response, then data
        reader.Feed(Server, 0xC0A80105, ServerPort, 50000, Concat(Be16(2), Be32(0x01020304), WireKey, new byte[3], Be32(512), new byte[4]), 2);
        reader.Feed(Server, 0xC0A80105, ServerPort, 50000, StoredData(1, App(0x1111, new byte[30])), 3);
        Assert.Equal(0x1111, Assert.Single(got).Opcode);
    }

    [Fact]
    public void A_stray_response_does_not_replace_a_working_session()
    {
        var got = new List<AppPacket>();
        var reader = new EQStreamReader(got.Add);
        Connect(reader, 0xC0A80105, 50000);

        // a response for some other session id, with a different key, arrives for the same endpoint
        reader.Feed(Server, 0xC0A80105, ServerPort, 50000, Concat(Be16(2), Be32(0x7A7A7A7A), new byte[] { 9, 9, 9, 9 }, new byte[3], Be32(64), new byte[4]), 3);

        reader.Feed(Server, 0xC0A80105, ServerPort, 50000, StoredData(1, App(0x1111, new byte[30])), 4);   // the real session still decodes
        Assert.Equal(0x1111, Assert.Single(got).Opcode);
    }

    [Fact]
    public void A_new_session_with_no_captured_request_takes_over_once_its_packets_checksum()
    {
        var got = new List<AppPacket>();
        var reader = new EQStreamReader(got.Add);
        Connect(reader, 0xC0A80105, 50000);                                    // the old session, never seen to disconnect

        var newWireKey = new byte[] { 11, 12, 13, 14 };
        var newKey = new byte[] { 14, 13, 12, 11 };
        // the new session's response arrives (its request was not captured) ...
        reader.Feed(Server, 0xC0A80105, ServerPort, 50000, Concat(Be16(2), Be32(0x0BADF00D), newWireKey, new byte[3], Be32(512), new byte[4]), 3);
        // ... a packet from the OLD session still decodes (the response alone changed nothing) ...
        reader.Feed(Server, 0xC0A80105, ServerPort, 50000, StoredData(1, App(0x1111, new byte[30])), 4);
        // ... and the first packet that checksums under the NEW key makes it the session
        var body = Concat(new byte[] { 0xA5 }, Be16(1), App(0x2222, new byte[30]));
        var crc = Crc32.HashToUInt32(Concat(newKey, Be16(0x09), body)) & 0xFFFF;
        reader.Feed(Server, 0xC0A80105, ServerPort, 50000, Concat(Be16(0x09), body, Be16((int)crc)), 5);

        Assert.Equal(new[] { 0x1111, 0x2222 }, got.Select(p => (int)p.Opcode).ToArray());
        Assert.Equal(2, reader.SessionsSeen);                                  // the old session, then the promoted one
    }

    [Fact]
    public void A_new_session_on_another_server_takes_over_when_its_request_was_not_captured()
    {
        var got = new List<AppPacket>();
        var reader = new EQStreamReader(got.Add);
        Connect(reader, 0xC0A80105, 50000);                                    // an old session with the first server

        const uint server2 = 0x45AE0203;                                       // a zone change: another server, same client port
        var wire = new byte[] { 21, 22, 23, 24 };
        var key2 = new byte[] { 24, 23, 22, 21 };
        reader.Feed(server2, 0xC0A80105, ServerPort, 50000, Concat(Be16(2), Be32(0x0C0FFEE0), wire, new byte[3], Be32(512), new byte[4]), 3);
        var body = Concat(new byte[] { 0xA5 }, Be16(1), App(0x3333, new byte[30]));
        var crc = Crc32.HashToUInt32(Concat(key2, Be16(0x09), body)) & 0xFFFF;
        reader.Feed(server2, 0xC0A80105, ServerPort, 50000, Concat(Be16(0x09), body, Be16((int)crc)), 4);

        Assert.Equal(0x3333, Assert.Single(got).Opcode);
    }

    [Fact]
    public void A_retransmitted_response_does_not_reset_the_session_or_its_half_built_fragments()
    {
        var got = new List<AppPacket>();
        var reader = new EQStreamReader(got.Add);
        Connect(reader, 0xC0A80105, 50000);
        Assert.Equal(1, reader.SessionsSeen);

        var big = Enumerable.Range(0, 900).Select(i => (byte)(i % 211)).ToArray();
        var app = App(0x77d4, big);
        var whole = Concat(Be32((uint)app.Length), app);
        var pieces = Enumerable.Range(0, (whole.Length + 399) / 400).Select(i => whole.Skip(i * 400).Take(400).ToArray()).ToList();
        void Send(int index) =>
            reader.Feed(Server, 0xC0A80105, ServerPort, 50000, WithCrc(0x0D, Concat(new byte[] { 0xA5 }, Be16(300 + index), pieces[index])), 3);

        Send(0);
        // the server's response is retransmitted while the message is half-assembled
        reader.Feed(Server, 0xC0A80105, ServerPort, 50000, Concat(Be16(2), Be32(0x01020304), WireKey, new byte[3], Be32(512), new byte[4]), 3);
        for (int i = 1; i < pieces.Count; i++) Send(i);

        Assert.Equal(big, Assert.Single(got).Data);
        Assert.Equal(1, reader.SessionsSeen);
    }

    [Fact]
    public void A_retransmitted_request_does_not_turn_an_established_session_back_into_a_pending_one()
    {
        var got = new List<AppPacket>();
        var reader = new EQStreamReader(got.Add);
        Connect(reader, 0xC0A80105, 50000);

        // a late duplicate of the client's request (same session id) arrives after the session is up
        reader.Feed(0xC0A80105, Server, 50000, ServerPort, Concat(Be16(1), new byte[4], Be32(0x01020304), new byte[14]), 3);

        reader.Feed(Server, 0xC0A80105, ServerPort, 50000, StoredData(1, App(0x1111, new byte[30])), 4);   // still decodes
        Assert.Equal(0x1111, Assert.Single(got).Opcode);
        Assert.Equal(1, reader.SessionsSeen);
    }

    [Fact]
    public void A_pcapng_block_whose_closing_length_disagrees_ends_the_file_there()
    {
        var frame = UdpFrame(20 + 8 + 10, 10);
        int padded = (frame.Length + 3) & ~3;
        uint blockLength = (uint)(32 + padded);
        var shb = Concat(new byte[] { 0x0A, 0x0D, 0x0D, 0x0A }, BitConverter.GetBytes(28u), BitConverter.GetBytes(0x1A2B3C4Du),
            BitConverter.GetBytes((ushort)1), BitConverter.GetBytes((ushort)0), BitConverter.GetBytes(-1L), BitConverter.GetBytes(28u));
        var idb = Concat(BitConverter.GetBytes(1u), BitConverter.GetBytes(20u), BitConverter.GetBytes((ushort)1), BitConverter.GetBytes((ushort)0),
            BitConverter.GetBytes(65535u), BitConverter.GetBytes(20u));
        byte[] Epb(uint trailer) => Concat(BitConverter.GetBytes(6u), BitConverter.GetBytes(blockLength), BitConverter.GetBytes(0u), BitConverter.GetBytes(0u),
            BitConverter.GetBytes(0u), BitConverter.GetBytes((uint)frame.Length), BitConverter.GetBytes((uint)frame.Length), frame,
            new byte[padded - frame.Length], BitConverter.GetBytes(trailer));
        var path = TempFile(Concat(shb, idb, Epb(blockLength), Epb(blockLength + 4), Epb(blockLength)));   // the middle block is damaged
        try { Assert.Single(PcapReader.ReadFrames(path).ToList()); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_raw_capture_file_is_never_overwritten()
    {
        var path = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N") + ".pcap");
        try
        {
            using (var first = new PcapWriter(path, FrameParser.LinkEthernet)) first.Write(1, UdpFrame(20 + 8 + 10, 10));
            long length = new FileInfo(path).Length;
            Assert.Throws<IOException>(() => new PcapWriter(path, FrameParser.LinkEthernet));
            Assert.Equal(length, new FileInfo(path).Length);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void An_unreadable_client_file_is_reported_and_the_rest_still_loads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eqc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dbstr_us.txt"), "5^1^Innate Eminence^0^\n");
        File.WriteAllText(Path.Combine(dir, "spells_us.txt"), "100^Complete Heal^0\n");
        try
        {
            using (var locked = new FileStream(Path.Combine(dir, "spells_us.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var s = ClientStrings.LoadFolder(dir);                         // must not throw
                Assert.Single(s.Titles);
                Assert.Empty(s.Spells);
                Assert.Single(s.LoadErrors);
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_fragment_that_looks_like_a_start_does_not_cost_the_real_message_when_the_first_fragment_is_late()
    {
        var got = new List<AppPacket>();
        var reader = new EQStreamReader(got.Add);
        Connect(reader, 0xC0A80105, 50000);

        var big = new byte[1100];
        new Random(5).NextBytes(big);
        var app = App(0x77d4, big);                                    // whole = 4-byte length + app; pieces of 400
        var whole = Concat(Be32((uint)app.Length), app);
        // make the SECOND piece begin with 00 00 00 05, which, read as a "declared length", is small and plausible
        Buffer.BlockCopy(new byte[] { 0, 0, 0, 5 }, 0, whole, 400, 4);
        var pieces = Enumerable.Range(0, (whole.Length + 399) / 400).Select(i => whole.Skip(i * 400).Take(400).ToArray()).ToList();
        Assert.True(pieces.Count >= 3);

        void Send(int index) =>
            reader.Feed(Server, 0xC0A80105, ServerPort, 50000, WithCrc(0x0D, Concat(new byte[] { 0xA5 }, Be16(200 + index), pieces[index])), 3);
        Send(1);                                                       // looks like a start with a tiny declared length
        Send(2);                                                       // overshoots that "length"
        Send(0);                                                       // the real first fragment, late

        var p = Assert.Single(got);
        Assert.Equal(0x77d4, p.Opcode);
        Assert.Equal(big.Length, p.Data.Length);
    }

    [Fact]
    public void An_aa_file_that_cannot_be_read_is_never_replaced()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eqc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "aa.jsonl");
        const string original = "{\"aa_id\":1,\"rank\":1,\"title\":\"A1\"}\n";
        File.WriteAllText(path, original);
        try
        {
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))   // reading it will fail
            using (var engine = new CollectorEngine(dir))
            {
                Assert.Equal(0, engine.FlushAa());                        // nothing to write, and nothing replaced
            }
            Assert.Equal(original, File.ReadAllText(path));
            Assert.False(File.Exists(Path.Combine(dir, "aa.jsonl.tmp")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Flushing_AAs_never_shrinks_or_empties_an_existing_aa_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eqc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "aa.jsonl");
        File.WriteAllText(path, "{\"aa_id\":2,\"rank\":1,\"title\":\"B\"}\n{ broken\n{\"aa_id\":1,\"rank\":3,\"title\":\"A3\"}\n{\"aa_id\":1,\"rank\":1,\"title\":\"A1\"}\n");
        try
        {
            using (var engine = new CollectorEngine(dir))          // a run that found no AAs at all
            {
                Assert.Equal(3, engine.FlushAa());
            }
            var lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length);                          // the broken line is dropped, the rest kept, sorted
            Assert.Contains("\"A1\"", lines[0]);
            Assert.Contains("\"A3\"", lines[1]);
            Assert.Contains("\"B\"", lines[2]);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Flushing_with_no_AAs_anywhere_creates_no_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eqc-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var engine = new CollectorEngine(dir);
            Assert.Equal(0, engine.FlushAa());
            Assert.False(File.Exists(Path.Combine(dir, "aa.jsonl")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Spell_names_with_punctuation_are_read()
    {
        var path = TempFile(System.Text.Encoding.UTF8.GetBytes("100^Complete Heal^0^1\n101^Spirit of Wolf Rk. II^0^2\n102^Ro's Fiery Sundering, Rk. III!^0^3\n"));
        try
        {
            var s = new ClientStrings();
            s.LoadSpells(path);
            Assert.Equal("Spirit of Wolf Rk. II", s.Spells[101]);
            Assert.Equal("Ro's Fiery Sundering, Rk. III!", s.Spells[102]);
            Assert.Equal(3, s.Spells.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_string_id_too_big_for_an_int_skips_the_line_instead_of_stopping_the_load()
    {
        var spells = TempFile(System.Text.Encoding.UTF8.GetBytes("99999999999999999999^Too Big^0\n100^Complete Heal^0\n"));
        var dbstr = TempFile(System.Text.Encoding.UTF8.GetBytes("99999999999999999999^1^Too Big^0^\n5^1^Innate Eminence^0^\n"));
        try
        {
            var s = new ClientStrings();
            s.LoadSpells(spells);
            s.LoadDbStr(dbstr);
            Assert.Equal("Complete Heal", Assert.Single(s.Spells).Value);
            Assert.Equal("Innate Eminence", Assert.Single(s.Titles).Value);
        }
        finally { File.Delete(spells); File.Delete(dbstr); }
    }

    [Fact]
    public void An_existing_items_file_with_odd_lines_does_not_stop_the_collector_starting()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eqc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "items.jsonl"), "null\n[1,2]\n\"text\"\n42\n{ not json\n{\"id\": 5}\n\n");
        try
        {
            using var engine = new CollectorEngine(dir);          // must not throw
            Assert.Equal(0, engine.ItemCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_legacy_pcapng_packet_block_is_read()
    {
        var frame = UdpFrame(20 + 8 + 10, 10);
        int padded = (frame.Length + 3) & ~3;
        uint blockLength = (uint)(32 + padded);
        var shb = Concat(new byte[] { 0x0A, 0x0D, 0x0D, 0x0A }, BitConverter.GetBytes(28u), BitConverter.GetBytes(0x1A2B3C4Du),
            BitConverter.GetBytes((ushort)1), BitConverter.GetBytes((ushort)0), BitConverter.GetBytes(-1L), BitConverter.GetBytes(28u));
        var idb = Concat(BitConverter.GetBytes(1u), BitConverter.GetBytes(20u), BitConverter.GetBytes((ushort)1), BitConverter.GetBytes((ushort)0),
            BitConverter.GetBytes(65535u), BitConverter.GetBytes(20u));
        var pb = Concat(BitConverter.GetBytes(2u), BitConverter.GetBytes(blockLength), BitConverter.GetBytes((ushort)0), BitConverter.GetBytes((ushort)0),
            BitConverter.GetBytes(0u), BitConverter.GetBytes(0u), BitConverter.GetBytes((uint)frame.Length), BitConverter.GetBytes((uint)frame.Length),
            frame, new byte[padded - frame.Length], BitConverter.GetBytes(blockLength));
        var path = TempFile(Concat(shb, idb, pb));
        try
        {
            var frames = PcapReader.ReadFrames(path).ToList();
            Assert.Single(frames);
            Assert.Equal(frame, frames[0].Data);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_AA_finder_keeps_the_slot_width_it_is_told()
    {
        Assert.False(new AaFinder(0x30cf, bit64: false).Bit64);
        Assert.True(new AaFinder(0x30cf).Bit64);
    }
}
