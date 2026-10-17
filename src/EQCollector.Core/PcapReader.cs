namespace EQCollector.Core;

/// <summary>A UDP datagram as the session reader wants it (IPv4 only, which is all the game uses).</summary>
public readonly record struct UdpPacket(uint SrcIp, uint DstIp, int SrcPort, int DstPort, byte[] Payload, double Time);

/// <summary>Pulls the UDP payload out of a captured link-layer frame.</summary>
public static class FrameParser
{
    public const int LinkNull = 0;
    public const int LinkEthernet = 1;
    public const int LinkRaw = 101;
    public const int LinkLinuxSll = 113;
    public const int LinkRawIpv4 = 228;
    public const int LinkLinuxSll2 = 276;

    public static bool TryParseUdp(int linkType, byte[] frame, double time, out UdpPacket packet)
    {
        packet = default;
        int offset;
        switch (linkType)
        {
            case LinkEthernet:
            {
                if (frame.Length < 14) return false;
                int etherType = (frame[12] << 8) | frame[13];
                offset = 14;
                // VLAN tags (802.1Q, and 802.1ad "Q-in-Q"), possibly stacked
                for (int tags = 0; (etherType == 0x8100 || etherType == 0x88A8) && tags < 4; tags++)
                {
                    if (frame.Length < offset + 4) return false;
                    etherType = (frame[offset + 2] << 8) | frame[offset + 3];
                    offset += 4;
                }
                if (etherType != 0x0800) return false;
                break;
            }
            case LinkRaw:
            case LinkRawIpv4:
                offset = 0;
                break;
            case LinkLinuxSll:
                if (frame.Length < 16 || ((frame[14] << 8) | frame[15]) != 0x0800) return false;
                offset = 16;
                break;
            case LinkLinuxSll2:
                if (frame.Length < 20 || ((frame[0] << 8) | frame[1]) != 0x0800) return false;
                offset = 20;
                break;
            case LinkNull:
                offset = 4;
                break;
            default:
                return false;
        }

        if (frame.Length < offset + 20 || (frame[offset] >> 4) != 4) return false;
        int ihl = (frame[offset] & 0x0F) * 4;
        if (ihl < 20 || frame[offset + 9] != 17) return false;                       // UDP only
        int fragment = ((frame[offset + 6] & 0x3F) << 8) | frame[offset + 7];          // more-fragments flag or an offset
        if (fragment != 0) return false;

        // The lengths in the headers must agree with each other and with what was captured: a frame cut short by the
        // capture's snap length would otherwise be passed on as a (wrong) shorter datagram.
        int totalLength = (frame[offset + 2] << 8) | frame[offset + 3];
        int udp = offset + ihl;
        if (totalLength < ihl + 8 || offset + totalLength > frame.Length) return false;
        int udpLength = (frame[udp + 4] << 8) | frame[udp + 5];
        if (udpLength < 8 || udpLength > totalLength - ihl) return false;
        int payloadLength = udpLength - 8;
        if (payloadLength == 0) return false;

        var payload = new byte[payloadLength];
        Buffer.BlockCopy(frame, udp + 8, payload, 0, payloadLength);
        packet = new UdpPacket(
            ((uint)frame[offset + 12] << 24) | ((uint)frame[offset + 13] << 16) | ((uint)frame[offset + 14] << 8) | frame[offset + 15],
            ((uint)frame[offset + 16] << 24) | ((uint)frame[offset + 17] << 16) | ((uint)frame[offset + 18] << 8) | frame[offset + 19],
            (frame[udp] << 8) | frame[udp + 1],
            (frame[udp + 2] << 8) | frame[udp + 3],
            payload,
            time);
        return true;
    }
}

/// <summary>
/// Reads classic pcap and pcapng files, one frame at a time (no libpcap or Npcap needed). A damaged or truncated file
/// ends the enumeration where the damage starts (or throws <see cref="InvalidDataException"/> when it is not a capture
/// file at all); it never loops or allocates from a length it has not checked.
/// </summary>
public static class PcapReader
{
    public readonly record struct Frame(int LinkType, double Time, byte[] Data);

    private const uint MaxFrame = 0x1000000;       // 16 MB: far more than any real frame

    public static IEnumerable<Frame> ReadFrames(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream);

        if (stream.Length < 24) throw new InvalidDataException("too short to be a capture file");
        uint first = reader.ReadUInt32();
        stream.Position = 0;

        IEnumerable<Frame> frames = first == 0x0A0D0D0A ? ReadPcapNg(stream, reader) : ReadClassic(stream, reader);
        foreach (var f in frames) yield return f;
    }

    /// <summary>Every UDP datagram in a capture file.</summary>
    public static IEnumerable<UdpPacket> ReadUdp(string path)
    {
        foreach (var frame in ReadFrames(path))
        {
            if (FrameParser.TryParseUdp(frame.LinkType, frame.Data, frame.Time, out var udp)) yield return udp;
        }
    }

    private static IEnumerable<Frame> ReadClassic(FileStream stream, BinaryReader reader)
    {
        uint magic = reader.ReadUInt32();
        bool swapped, nano;
        switch (magic)
        {
            case 0xA1B2C3D4: swapped = false; nano = false; break;
            case 0xD4C3B2A1: swapped = true; nano = false; break;
            case 0xA1B23C4D: swapped = false; nano = true; break;
            case 0x4D3CB2A1: swapped = true; nano = true; break;
            default: throw new InvalidDataException("not a pcap or pcapng file");
        }

        uint U32() { var v = reader.ReadUInt32(); return swapped ? SwapU32(v) : v; }

        reader.ReadBytes(16);                      // version, zone, sigfigs, snaplen
        int linkType = (int)U32();

        while (stream.Position + 16 <= stream.Length)
        {
            uint sec = U32();
            uint frac = U32();
            uint included = U32();
            U32();                                 // original length
            if (included > MaxFrame || stream.Position + included > stream.Length) yield break;
            var data = reader.ReadBytes((int)included);
            yield return new Frame(linkType, sec + frac / (nano ? 1e9 : 1e6), data);
        }
    }

    private static IEnumerable<Frame> ReadPcapNg(FileStream stream, BinaryReader reader)
    {
        var interfaces = new List<(int LinkType, double Tick)>();
        bool swapped = false;

        uint U32() { var v = reader.ReadUInt32(); return swapped ? SwapU32(v) : v; }
        int U16() { var v = reader.ReadUInt16(); return swapped ? SwapU16(v) : v; }

        while (stream.Position + 12 <= stream.Length)
        {
            long blockStart = stream.Position;
            uint rawType = reader.ReadUInt32();

            if (rawType == 0x0A0D0D0A)             // section header: its byte-order magic says how this section is written
            {
                if (stream.Position + 12 > stream.Length) yield break;
                uint rawLength = reader.ReadUInt32();
                uint order = reader.ReadUInt32();
                if (order == 0x1A2B3C4D) swapped = false;
                else if (order == 0x4D3C2B1A) swapped = true;
                else yield break;                  // not a byte-order magic: the file is damaged here
                uint sectionLength = swapped ? SwapU32(rawLength) : rawLength;
                if (sectionLength < 28 || sectionLength % 4 != 0 || blockStart + sectionLength > stream.Length) yield break;
                stream.Position = blockStart + sectionLength - 4;
                uint sectionTrailer = U32();
                if (sectionTrailer != sectionLength) yield break;       // the closing length copy disagrees: damaged
                interfaces.Clear();
                stream.Position = blockStart + sectionLength;
                continue;
            }

            uint total = U32();
            uint type = swapped ? SwapU32(rawType) : rawType;
            if (total < 12 || total % 4 != 0 || blockStart + total > stream.Length) yield break;
            long blockEnd = blockStart + total;
            long afterHeader = stream.Position;
            stream.Position = blockEnd - 4;
            if (U32() != total) yield break;                             // the closing length copy disagrees: damaged
            stream.Position = afterHeader;

            switch (type)
            {
                case 0x00000001:                   // interface description
                {
                    if (total < 20) yield break;
                    int linkType = U16();
                    U16();
                    U32();                         // snap length
                    double tick = 1e-6;
                    long optionsEnd = blockEnd - 4;
                    while (stream.Position + 4 <= optionsEnd)
                    {
                        int code = U16();
                        int len = U16();
                        if (code == 0) break;
                        int padded = (len + 3) & ~3;
                        if (stream.Position + padded > optionsEnd) break;           // an option longer than the block
                        var value = reader.ReadBytes(padded);
                        if (code == 9 && len == 1)  // if_tsresol
                        {
                            tick = (value[0] & 0x80) != 0 ? Math.Pow(2, -(value[0] & 0x7F)) : Math.Pow(10, -value[0]);
                        }
                    }
                    interfaces.Add((linkType, tick));
                    break;
                }

                case 0x00000006:                   // enhanced packet
                {
                    if (total < 32) yield break;
                    uint id = U32();
                    uint high = U32();
                    uint low = U32();
                    uint captured = U32();
                    U32();                         // original length
                    ulong padded = ((ulong)captured + 3UL) & ~3UL;
                    if (captured > MaxFrame || padded > total - 32UL) yield break;
                    if (id >= interfaces.Count) break;                              // an interface that was never described
                    var data = reader.ReadBytes((int)captured);
                    var (linkType, tick) = interfaces[(int)id];
                    yield return new Frame(linkType, (((ulong)high << 32) | low) * tick, data);
                    break;
                }

                case 0x00000002:                   // packet block (obsolete, but older pcapng writers still use it)
                {
                    if (total < 32) yield break;
                    int id = U16();
                    U16();                         // drops count
                    uint high = U32();
                    uint low = U32();
                    uint captured = U32();
                    U32();                         // original length
                    ulong padded = ((ulong)captured + 3UL) & ~3UL;
                    if (captured > MaxFrame || padded > total - 32UL) yield break;
                    if (id >= interfaces.Count) break;
                    var data = reader.ReadBytes((int)captured);
                    var (linkType, tick) = interfaces[id];
                    yield return new Frame(linkType, (((ulong)high << 32) | low) * tick, data);
                    break;
                }

                case 0x00000003:                   // simple packet
                {
                    if (total < 16 || interfaces.Count == 0) break;
                    uint original = U32();
                    long captured = Math.Min(original, total - 16L);
                    if (captured > MaxFrame) yield break;
                    var data = reader.ReadBytes((int)captured);
                    yield return new Frame(interfaces[0].LinkType, 0, data);
                    break;
                }
            }

            stream.Position = blockEnd;
        }
    }

    private static uint SwapU32(uint v) => ((v & 0xFF) << 24) | ((v & 0xFF00) << 8) | ((v >> 8) & 0xFF00) | (v >> 24);
    private static ushort SwapU16(ushort v) => (ushort)((v << 8) | (v >> 8));
}
