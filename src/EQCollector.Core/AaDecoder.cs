namespace EQCollector.Core;

public sealed class AaSlot
{
    public int Slot { get; init; }
    public int Spa { get; init; }
    public long Base1 { get; init; }
    public long Base2 { get; init; }
}

/// <summary>One rank of one AA, as the server sends it.</summary>
public sealed class AaRecord
{
    public int RankId { get; init; }            // the rank's own id (EQEmu's aa_ranks.id)
    public int HotkeySid { get; init; }
    public int HotkeySid2 { get; init; }
    public int TitleSid { get; init; }
    public int DescSid { get; init; }
    public uint ReqLevel { get; init; }
    public uint Cost { get; init; }
    public uint AaId { get; init; }             // the ability this rank belongs to
    public uint Rank { get; init; }
    public List<uint> ReqSkills { get; init; } = new();
    public List<uint> ReqRanks { get; init; } = new();
    public uint Type { get; init; }
    public int SpellId { get; init; }
    public uint TimerId { get; init; }
    public uint ReuseSeconds { get; init; }
    public int ClassMask { get; init; }
    public uint MaxRank { get; init; }
    public int PrevDescSid { get; init; }
    public int NextDescSid { get; init; }
    public uint TotalCost { get; init; }
    public uint Expansion { get; init; }
    public int Category { get; init; }
    public uint Expansion2 { get; init; }
    public List<AaSlot> Slots { get; init; } = new();
    public double CapturedAt { get; set; }
}

/// <summary>
/// Decodes alternate-advancement (AA) records out of the packets the game server sends when AAs are bought or reset.
///
/// CREDIT: the layout of the AA record and the list of well-known AAs used to find the right packet type come from
/// kauffman12's eqaainfo project (https://github.com/kauffman12/eqaainfo, parse.py), used with the author's permission
/// and credited here as they asked. The code is our own rewrite.
/// </summary>
public static class AaDecoder
{
    public static readonly string[] ClassAbbr = { "WAR", "CLR", "PAL", "RNG", "SHD", "DRU", "MNK", "BRD", "ROG", "SHM", "NEC", "WIZ", "MAG", "ENC", "BST", "BER", "MERC" };
    public static readonly string[] Types = { "Unknown", "General", "Archetype", "Class", "Special", "Focus" };
    public static readonly Dictionary<int, string> Categories = new()
    {
        [2] = "Progression", [5] = "Veteran Reward", [6] = "Tradeskill", [7] = "Expendable", [8] = "Racial Innate", [9] = "Everquest", [11] = "Item Effect",
    };

    public static List<string> ClassesFromMask(int mask)
    {
        var names = new List<string>();
        for (int i = 0; i < ClassAbbr.Length; i++)
        {
            if ((mask >> i & 1) != 0) names.Add(ClassAbbr[i]);
        }
        return names;
    }

    /// <summary>One AA rank from a server packet's data (the bytes after the application opcode). Throws <see cref="ParseException"/>.</summary>
    public static AaRecord Decode(byte[] data, bool bit64)
    {
        var cur = new ByteCursor(data);
        int rankId = cur.I32();
        cur.U8();                                  // always 1
        int hotkey = cur.I32();
        int hotkey2 = cur.I32();
        int titleSid = cur.I32();
        if (titleSid == -1) throw new ParseException("no title");
        int descSid = cur.I32();
        uint reqLevel = cur.U32();
        uint cost = cur.U32();
        uint aaId = cur.U32();
        uint rank = cur.U32();

        uint count = cur.U32();
        if (count >= 5) throw new ParseException("prerequisite count");
        var skills = new List<uint>();
        for (uint i = 0; i < count; i++)
        {
            uint v = cur.U32();
            if (v > 0) skills.Add(v);
        }

        count = cur.U32();
        if (count >= 5) throw new ParseException("prerequisite rank count");
        var ranks = new List<uint>();
        for (uint i = 0; i < count; i++)
        {
            uint v = cur.U32();
            if (v > 0) ranks.Add(v);
        }

        uint type = cur.U32();
        int spellId = cur.I32();
        if (cur.U32() != 1) throw new ParseException("spell id width (64-bit client?)");
        uint timer = cur.U32();
        uint reuse = cur.U32();
        int classMask = cur.U16();
        int berserker = cur.U16();
        int mask = (classMask >> 1) + (berserker != 0 ? 32768 : 0);
        uint maxRank = cur.U32();
        int prev = cur.I32();
        int next = cur.I32();
        uint total = cur.U32();
        cur.Skip(10);
        uint expansion = cur.U32();
        int category = cur.I32();
        uint expansion2 = cur.U32();
        cur.Skip(9);

        uint peek = cur.Left >= 4 ? BitConverter.ToUInt32(data, cur.Position) : 0;
        if (peek >= 256) cur.Skip(1);              // a byte added in the 2024 beta
        uint slotCount = cur.U32();
        if (slotCount >= 256) throw new ParseException("slot count");

        var slots = new List<AaSlot>();
        for (uint i = 0; i < slotCount; i++)
        {
            int spa; long base1, base2; int number;
            if (bit64)
            {
                spa = cur.I32(); base1 = cur.I64(); base2 = cur.I64(); number = cur.I32();
            }
            else
            {
                spa = cur.I32(); base1 = cur.I32(); base2 = cur.I32(); number = cur.I32();
            }
            slots.Add(new AaSlot { Slot = number, Spa = spa, Base1 = base1, Base2 = base2 });
        }

        return new AaRecord
        {
            RankId = rankId, HotkeySid = hotkey, HotkeySid2 = hotkey2, TitleSid = titleSid, DescSid = descSid,
            ReqLevel = reqLevel, Cost = cost, AaId = aaId, Rank = rank, ReqSkills = skills, ReqRanks = ranks,
            Type = type, SpellId = spellId, TimerId = timer, ReuseSeconds = reuse, ClassMask = mask, MaxRank = maxRank,
            PrevDescSid = prev, NextDescSid = next, TotalCost = total, Expansion = expansion, Category = category,
            Expansion2 = expansion2, Slots = slots,
        };
    }
}

/// <summary>
/// Works out which application opcode carries AAs and decodes them. The opcode changes with client patches, so it is
/// found by counting server packets that contain the bytes of AAs everybody has (Battle Ready, Packrat, Innate
/// Metabolism ...): the opcode with the most such packets (and at least two) is the AA opcode.
/// </summary>
public sealed class AaFinder
{
    private const int MinAaPacket = 100;
    private const int MaxBuffered = 40000;
    private const long MaxBufferedBytes = 64L * 1024 * 1024;   // while the AA packet type is still unknown, at most this much is kept

    private static readonly byte[][] WellKnown = BuildWellKnown();

    private readonly Dictionary<ushort, int> _hits = new();
    private readonly Dictionary<(ushort, bool), int> _width = new();
    private readonly Queue<(ushort Opcode, byte[] Data, double Time)> _buffer = new();
    private readonly Dictionary<(uint, uint), AaRecord> _records = new();
    private ushort? _opcode;
    private bool _bit64 = true;

    private long _bufferedBytes;

    /// <param name="opcode">The AA packet type when it is already known (from a probe of the same capture).</param>
    /// <param name="bit64">Whether that capture's slots are the 64-bit layout (see <see cref="Bit64"/>).</param>
    public AaFinder(ushort? opcode = null, bool bit64 = true)
    {
        _opcode = opcode;
        _bit64 = bit64;
    }

    /// <summary>True when the effect slots in the AA packets are the 64-bit layout (found along with the opcode).</summary>
    public bool Bit64 => _bit64;

    public ushort? Opcode => _opcode;
    private const int MaxRecords = 200_000;
    public int Count => _records.Count;
    public IEnumerable<AaRecord> Records => _records.Values.OrderBy(r => r.AaId).ThenBy(r => r.Rank);

    /// <summary>Called for every application packet; returns a rank when this packet was an AA that decoded.</summary>
    public AaRecord? OnPacket(AppPacket p)
    {
        if (p.ClientToServer || p.Data.Length < MinAaPacket) return null;

        if (_opcode == null)
        {
            _buffer.Enqueue((p.Opcode, p.Data, p.Timestamp));
            _bufferedBytes += p.Data.Length;
            while (_buffer.Count > MaxBuffered || _bufferedBytes > MaxBufferedBytes)
                _bufferedBytes -= _buffer.Dequeue().Data.Length;
            foreach (var pattern in WellKnown)
            {
                if (Contains(p.Data, pattern))
                {
                    _hits[p.Opcode] = _hits.GetValueOrDefault(p.Opcode) + 1;
                    _width[(p.Opcode, pattern.Length == 28)] = _width.GetValueOrDefault((p.Opcode, pattern.Length == 28)) + 1;
                    break;
                }
            }
            var best = _hits.OrderByDescending(kv => kv.Value).FirstOrDefault();
            if (best.Value < 2) return null;

            _opcode = best.Key;
            _bit64 = _width.GetValueOrDefault((best.Key, true)) >= _width.GetValueOrDefault((best.Key, false));
            foreach (var old in _buffer.Where(b => b.Opcode == _opcode).ToList()) Accept(old.Data, old.Time);   // packets from before it was known
            _buffer.Clear();
            _bufferedBytes = 0;
            return null;
        }

        return p.Opcode == _opcode ? Accept(p.Data, p.Timestamp) : null;
    }

    private AaRecord? Accept(byte[] data, double time)
    {
        try
        {
            var record = AaDecoder.Decode(data, _bit64);
            record.CapturedAt = time;
            if (_records.Count >= MaxRecords && !_records.ContainsKey((record.AaId, record.Rank))) return null;     // a real table has a few thousand ranks
            _records[(record.AaId, record.Rank)] = record;
            return record;
        }
        catch (ParseException)
        {
            return null;
        }
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        return haystack.AsSpan().IndexOf(needle) >= 0;
    }

    private static byte[][] BuildWellKnown()
    {
        byte[] B(params int[] v) => v.Select(x => (byte)x).ToArray();
        return new[]
        {
            // 32-bit layout (four int32 per slot): count, spa, base1, base2, slot
            B(1, 0, 0, 0, 107, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0),      // Battle Ready 1
            B(1, 0, 0, 0, 107, 1, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0),      // Battle Ready 2
            B(1, 0, 0, 0, 107, 1, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0),      // Battle Ready 3
            B(1, 0, 0, 0, 107, 1, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0),      // Battle Ready 4
            B(16, 0, 0, 0, 83, 1, 0, 0, 40, 0, 0, 0, 36, 147, 0, 0, 1, 0, 0, 0),  // Banestrike 1
            B(1, 0, 0, 0, 246, 0, 0, 0, 110, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0),    // Innate Lung Capacity 1
            B(1, 0, 0, 0, 233, 0, 0, 0, 125, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0),    // Innate Metabolism 2
            B(1, 0, 0, 0, 233, 0, 0, 0, 150, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0),    // Innate Metabolism 3
            B(1, 0, 0, 0, 221, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0),      // Packrat 1
            B(1, 0, 0, 0, 221, 0, 0, 0, 33, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0),     // Packrat 11
            // 64-bit layout (int32, int32, int64, int64, int32)
            B(1, 0, 0, 0, 107, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0),    // Battle Ready 1
            B(1, 0, 0, 0, 107, 1, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0),    // Battle Ready 2
            B(1, 0, 0, 0, 107, 1, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0),    // Battle Ready 3
            B(1, 0, 0, 0, 107, 1, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0),    // Battle Ready 4
            B(16, 0, 0, 0, 83, 1, 0, 0, 40, 0, 0, 0, 0, 0, 0, 0, 36, 147, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0), // Banestrike 1
            B(1, 0, 0, 0, 246, 0, 0, 0, 110, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0),  // Innate Lung Capacity 1
            B(1, 0, 0, 0, 233, 0, 0, 0, 110, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0),  // Innate Metabolism 1
            B(1, 0, 0, 0, 233, 0, 0, 0, 125, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0),  // Innate Metabolism 2
            B(1, 0, 0, 0, 233, 0, 0, 0, 150, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0),  // Innate Metabolism 3
            B(1, 0, 0, 0, 221, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0),    // Packrat 1
            B(1, 0, 0, 0, 221, 0, 0, 0, 33, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0),   // Packrat 11
        };
    }
}
