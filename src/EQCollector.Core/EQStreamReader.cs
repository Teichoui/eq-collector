using System.IO.Compression;
using System.IO.Hashing;

namespace EQCollector.Core;

/// <summary>One application packet: the game's own opcode and data, after the session layer has been taken off.</summary>
public readonly record struct AppPacket(ushort Opcode, byte[] Data, double Timestamp, bool ClientToServer, int ClientPort);

/// <summary>
/// Reads the EverQuest UDP session layer and hands every application packet to a callback.
///
/// The layer, from the outside in:
///   - every UDP payload starts with a 2-byte BIG-endian session opcode;
///   - 0x01 / 0x02 are the handshake (the response carries the session key the packet checksums use);
///   - 0x09 is a data packet, 0x0d a fragment of a larger one, 0x03 several packets bundled together, 0x05 a disconnect;
///   - data, fragment and combined packets carry a 2-byte checksum at the end (CRC-32 of session key + opcode + body,
///     low 16 bits) and, right after the opcode, a flag byte: 0x5a means the rest is zlib-compressed, 0xa5 means it is not
///     (a packet with neither flag has no flag byte at all);
///   - inside a data packet: a 2-byte sequence number, then the application packet -- a 2-byte LITTLE-endian
///     application opcode and its data. Application opcode 0x1900 means several application packets follow, each
///     prefixed with its length (one byte, or 0xff and a 2-byte big-endian length).
///
/// The input is untrusted (a capture file from anyone, or live traffic), so every length is checked, decompression and
/// fragment buffers are capped, and a bad packet is skipped, never fatal.
///
/// CREDIT: the session/handshake/checksum/fragment handling follows kauffman12's eqaainfo project (lib/eqreader.py,
/// https://github.com/kauffman12/eqaainfo), used with the author's permission and credited here as they asked.
/// The code is our own rewrite.
/// </summary>
public sealed class EQStreamReader
{
    private const int SessionRequest = 0x01;
    private const int SessionResponse = 0x02;
    private const int Combined = 0x03;
    private const int Disconnect = 0x05;
    private const int Packet = 0x09;
    private const int Fragment = 0x0D;
    private const int AppCombined = 0x1900;

    private const double FragmentTimeoutSeconds = 60;
    private const int MaxClients = 256;                       // sessions followed at once; the oldest is dropped beyond this
    private const int MaxFragments = 1024;                    // unfinished fragments kept per session and direction
    private const int MaxFragmentBytes = 16 * 1024 * 1024;    // ... and their total size
    private const int MaxAppPacket = 16 * 1024 * 1024;        // the largest reassembled or decompressed packet accepted

    private sealed class Fragments
    {
        public readonly Dictionary<ushort, (byte[] Part, double Time)> Parts = new();
        public int Bytes;
    }

    private sealed class Client
    {
        public uint ClientIp;
        public int ClientPort;
        public uint ServerIp;
        public int Session;
        public byte[] Key = Array.Empty<byte>();
        public int MaxLength;
        public double LastSeen;
        public readonly Fragments ToServer = new();
        public readonly Fragments ToClient = new();
    }

    private readonly Action<AppPacket> _onPacket;
    private readonly Dictionary<(uint Ip, int Port), Client> _clients = new();

    /// <summary>How many game sessions have been set up since the reader was created (a new zone is a new session).</summary>
    public int SessionsSeen { get; private set; }

    /// <summary>Called when the callback passed to the constructor throws; the packet is dropped and reading carries on.</summary>
    public Action<Exception>? OnCallbackError { get; set; }

    public EQStreamReader(Action<AppPacket> onPacket) => _onPacket = onPacket;

    /// <summary>One UDP datagram's payload. A bad packet is skipped, never fatal.</summary>
    public void Feed(uint srcIp, uint dstIp, int srcPort, int dstPort, byte[] payload, double timestamp)
    {
        try
        {
            Process(srcIp, dstIp, srcPort, dstPort, payload, timestamp, false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // malformed or hostile packet (the callback's own exceptions are handled in Deliver)
        }
    }

    private void Deliver(AppPacket packet)
    {
        try
        {
            _onPacket(packet);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            OnCallbackError?.Invoke(ex);
        }
    }

    private static int Be16(byte[] b, int o = 0) => (b[o] << 8) | b[o + 1];
    private static uint Be32(byte[] b, int o = 0) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

    /// <summary>b[start..start+length) (or to the end when length is -1); throws when that does not fit inside b.</summary>
    private static byte[] Slice(byte[] b, int start, int length = -1)
    {
        if (length < 0) length = b.Length - start;
        if (start < 0 || length < 0 || start > b.Length - length) throw new ParseException("slice outside the packet");
        var r = new byte[length];
        Buffer.BlockCopy(b, start, r, 0, length);
        return r;
    }

    private static bool CrcOk(Client client, int opcode, byte[] body)
    {
        if (body.Length < 2) return false;
        var packet = new byte[client.Key.Length + 2 + body.Length - 2];
        Buffer.BlockCopy(client.Key, 0, packet, 0, client.Key.Length);
        packet[client.Key.Length] = (byte)(opcode >> 8);
        packet[client.Key.Length + 1] = (byte)opcode;
        Buffer.BlockCopy(body, 0, packet, client.Key.Length + 2, body.Length - 2);
        return (Crc32.HashToUInt32(packet) & 0xFFFF) == (uint)Be16(body, body.Length - 2);
    }

    /// <summary>
    /// The packet's content with the flag byte and the trailing checksum taken off (and inflated when it was compressed).
    /// A bundled sub-packet has neither. The checksum was already verified by the caller.
    /// </summary>
    private static byte[] Uncompress(int opcode, byte[] body, bool isSub)
    {
        if (isSub || body.Length == 0) return body;
        if (body[0] == 0x5A) return Inflate(body, 1);
        if (opcode == Combined) return body[0] == 0xA5 ? Slice(body, 1) : body;   // the bundle loop ignores the trailing bytes
        int start = body[0] == 0xA5 ? 1 : 0;
        return Slice(body, start, body.Length - start - 2);
    }

    private static byte[] Inflate(byte[] body, int offset)
    {
        using var input = new MemoryStream(body, offset, body.Length - offset);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = z.Read(buffer, 0, buffer.Length)) > 0)      // bytes after the compressed stream (the checksum) are ignored
        {
            if (output.Length + n > MaxAppPacket) throw new InvalidDataException("decompressed packet too large");
            output.Write(buffer, 0, n);
        }
        return output.ToArray();
    }

    // ---- sessions ---------------------------------------------------------------------------------------------------

    private void AddClient(uint clientIp, int clientPort, uint serverIp, int maxLength, int session, byte[] key, double ts)
    {
        if (key.Length > 0) SessionsSeen++;                  // a request has no key yet; count a session when its response gives one
        if (!_clients.ContainsKey((clientIp, clientPort)) && _clients.Count >= MaxClients)
        {
            var oldest = _clients.Values.MinBy(c => c.LastSeen)!;
            _clients.Remove((oldest.ClientIp, oldest.ClientPort));
        }
        _clients[(clientIp, clientPort)] = new Client
        {
            ClientIp = clientIp, ClientPort = clientPort, ServerIp = serverIp, MaxLength = maxLength, Session = session, Key = key, LastSeen = ts,
        };
    }

    /// <summary>The session a packet belongs to, and which way it is going.</summary>
    private Client? FindClient(uint srcIp, int srcPort, uint dstIp, int dstPort, out bool toServer)
    {
        if (_clients.TryGetValue((dstIp, dstPort), out var c) && c.ServerIp == srcIp)
        {
            toServer = false;
            return c;
        }
        if (_clients.TryGetValue((srcIp, srcPort), out c) && c.ServerIp == dstIp)
        {
            toServer = true;
            return c;
        }
        toServer = false;
        return null;
    }

    private void Process(uint srcIp, uint dstIp, int srcPort, int dstPort, byte[] buf, double ts, bool isSub)
    {
        if (buf.Length < 2) return;
        int opcode = Be16(buf);
        var body = Slice(buf, 2);

        var client = FindClient(srcIp, srcPort, dstIp, dstPort, out bool clientToServer);
        if (client != null)
        {
            if (buf.Length > client.MaxLength) return;
            client.LastSeen = ts;
        }

        // nothing but the handshake means anything until a session has been seen
        if (client == null && opcode != SessionRequest && opcode != SessionResponse) return;

        switch (opcode)
        {
            case SessionRequest:
                if (body.Length == 22)
                    AddClient(srcIp, srcPort, dstIp, 512, (int)Be32(body, 4), Array.Empty<byte>(), ts);
                break;

            case SessionResponse:
                if (body.Length == 19)
                {
                    int session = (int)Be32(body);
                    var key = new[] { body[7], body[6], body[5], body[4] };   // the four key bytes, reversed
                    int maxLength = (int)Be32(body, 11);
                    if (_clients.TryGetValue((dstIp, dstPort), out var existing))
                    {
                        // A response only counts for the session it answers (the pending request, or the established session
                        // again). A stray or delayed one must not replace a session that is working.
                        if (existing.ServerIp == srcIp && existing.Session == session)
                            AddClient(dstIp, dstPort, srcIp, maxLength, session, key, ts);
                    }
                    else
                    {
                        // No session known for this endpoint: the response alone names both ends, the session id, the key and
                        // the packet size limit, so a capture that starts after the client's request (or lost it) still
                        // decodes. Later checksums reject a session that only looked like one.
                        AddClient(dstIp, dstPort, srcIp, maxLength, session, key, ts);
                    }
                }
                break;

            case Disconnect:
                if (body.Length == 9 && client != null) _clients.Remove((client.ClientIp, client.ClientPort));
                break;

            case Combined:
                if (client != null && CrcOk(client, opcode, body))
                {
                    var data = Uncompress(opcode, body, false);
                    int pos = 0;
                    while (data.Length - pos > 6)
                    {
                        int size = data[pos++];
                        if (size > data.Length - pos) break;                  // a length that runs past the packet
                        Process(srcIp, dstIp, srcPort, dstPort, Slice(data, pos, size), ts, true);
                        pos += size;
                    }
                }
                break;

            case Packet:
                if (client != null && (isSub || CrcOk(client, opcode, body)))
                {
                    var data = Uncompress(opcode, body, isSub);
                    AppPackets(Slice(data, 2), ts, clientToServer, client.ClientPort);   // drop the sequence number
                }
                break;

            case Fragment:
                if (client != null && (isSub || CrcOk(client, opcode, body)))
                {
                    var part = Uncompress(opcode, body, isSub);
                    var fragments = clientToServer ? client.ToServer : client.ToClient;
                    var done = AddFragment(fragments, (ushort)Be16(part), Slice(part, 2), ts);
                    if (done != null) AppPackets(done, ts, clientToServer, client.ClientPort);
                }
                break;
        }
    }

    // ---- fragments --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Keeps a fragment and returns the whole payload once a run of consecutive fragments (sequence numbers are 16 bits
    /// and wrap) adds up to the length the first one declares in its first four bytes (big-endian).
    /// </summary>
    private static byte[]? AddFragment(Fragments f, ushort seq, byte[] part, double ts)
    {
        // forget what is too old, and keep the buffer bounded
        foreach (var key in f.Parts.Where(kv => ts - kv.Value.Time > FragmentTimeoutSeconds).Select(kv => kv.Key).ToList())
            Remove(f, key);
        while (f.Parts.Count >= MaxFragments || f.Bytes + part.Length > MaxFragmentBytes)
        {
            if (f.Parts.Count == 0) return null;
            Remove(f, f.Parts.MinBy(kv => kv.Value.Time).Key);
        }

        if (f.Parts.TryGetValue(seq, out var old)) f.Bytes -= old.Part.Length;
        f.Parts[seq] = (part, ts);
        f.Bytes += part.Length;

        // the run this fragment is in: walk back to its first fragment, then forward adding bytes up to the declared size
        ushort start = seq;
        for (int guard = 0; guard < MaxFragments && f.Parts.ContainsKey(unchecked((ushort)(start - 1))); guard++) start = unchecked((ushort)(start - 1));

        // Only lengths are added up while walking the run; the bytes are copied once, when the run is complete (a payload
        // in n fragments would otherwise cost n copies of everything so far).
        var run = new List<(ushort Key, byte[] Part)>();
        long total = 0;
        long size = -1;
        for (ushort key = start; f.Parts.TryGetValue(key, out var piece) && run.Count < MaxFragments; key = unchecked((ushort)(key + 1)))
        {
            run.Add((key, piece.Part));
            total += piece.Part.Length;

            // Past the declared size: the fragments so far are a message whose end was lost, and this one starts the next.
            // Drop the stale ones and carry on from this fragment.
            if (size >= 0 && total > size + 4)
            {
                for (int i = 0; i < run.Count - 1; i++) Remove(f, run[i].Key);
                run = new List<(ushort Key, byte[] Part)> { (key, piece.Part) };
                total = piece.Part.Length;
                size = -1;
            }

            if (size < 0)
            {
                if (total < 4) continue;
                size = DeclaredSize(run);
                if (size > MaxAppPacket) return null;                // not a first fragment (yet): the first may arrive later
            }
            if (total == size + 4)
            {
                var payload = new byte[size];
                int skip = 4;                                        // the length prefix is not part of the payload
                int written = 0;
                foreach (var (_, bytes) in run)
                {
                    int from = Math.Min(skip, bytes.Length);
                    skip -= from;
                    Buffer.BlockCopy(bytes, from, payload, written, bytes.Length - from);
                    written += bytes.Length - from;
                }
                foreach (var (k, _) in run) Remove(f, k);
                return payload;
            }
            if (total > size + 4) return null;                       // more than this run declares: not a real payload
        }
        return null;                                                 // incomplete: wait for more (old fragments time out)
    }

    /// <summary>The big-endian length in the first four bytes of a run of fragments (they may span the first two).</summary>
    private static long DeclaredSize(List<(ushort Key, byte[] Part)> run)
    {
        uint size = 0;
        int taken = 0;
        foreach (var (_, bytes) in run)
        {
            for (int i = 0; i < bytes.Length && taken < 4; i++, taken++) size = (size << 8) | bytes[i];
            if (taken == 4) break;
        }
        return size;
    }

    private static void Remove(Fragments f, ushort key)
    {
        if (f.Parts.Remove(key, out var old)) f.Bytes -= old.Part.Length;
    }

    // ---- application packets ----------------------------------------------------------------------------------------

    private void AppPackets(byte[] data, double ts, bool clientToServer, int clientPort)
    {
        if (data.Length < 2) return;
        int opcode = data[0] | (data[1] << 8);
        if (opcode != AppCombined)
        {
            if (data.Length > 2) Deliver(new AppPacket((ushort)opcode, Slice(data, 2), ts, clientToServer, clientPort));
            return;
        }

        int pos = 2;
        while (data.Length - pos > 3)
        {
            int size = data[pos++];
            if (size == 0xFF)
            {
                if (data.Length - pos < 2) break;
                size = Be16(data, pos);
                pos += 2;
            }
            if (size > data.Length - pos) break;                     // a length that runs past the packet
            var packet = Slice(data, pos, size);
            pos += size;
            if (packet.Length < 2) continue;
            int inner = packet[0] | (packet[1] << 8);
            int skip = 2;
            if (inner == 0 && packet.Length > 2)
            {
                inner = packet[2] << 8;
                skip = 3;
            }
            if (packet.Length > skip) Deliver(new AppPacket((ushort)inner, Slice(packet, skip), ts, clientToServer, clientPort));
        }
    }
}
