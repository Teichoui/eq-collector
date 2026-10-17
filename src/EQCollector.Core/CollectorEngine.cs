using System.Text;
using System.Text.Json;

namespace EQCollector.Core;

/// <summary>
/// Feeds captured UDP packets through the session reader and the item and AA decoders, and writes what it finds:
///   items.jsonl  one line per distinct item (all 282 fields), appended as each is found. Items already in the file
///                from an earlier run are not written again;
///   aa.jsonl     one line per AA rank, with title, description and spell name when the client's files are loaded.
///                <see cref="FlushAa"/> merges this run's ranks into what the file already holds (the newer capture
///                wins for a rank both have), so a short or aborted run never shrinks a complete table. With
///                <see cref="AutoSaveAa"/> on (live capture) it runs by itself while ranks arrive, at most once per
///                <see cref="AutoSaveInterval"/>, because the whole file is rewritten each time.
/// Works the same for a capture file and for live traffic. A problem writing to the output folder is reported through
/// <see cref="Error"/> and stops the writing, but never throws into the capture.
/// </summary>
public sealed class CollectorEngine : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly EQStreamReader _reader;
    private readonly AaFinder _aa;
    private readonly ClientStrings _strings;
    private readonly Dictionary<long, ItemRecord> _items = new();    // this run
    private readonly HashSet<long> _onDisk = new();                   // ids already in items.jsonl
    private readonly Action<string>? _log;
    private readonly Dictionary<(uint AaId, uint Rank), string> _aaOnDisk = new();   // lines already in aa.jsonl
    private bool _aaLoadFailed;                    // aa.jsonl could not be read completely: it must not be replaced
    private StreamWriter? _itemsFile;
    private bool _writingFailed;
    private bool _aaDirty;                         // ranks found since the file was last written
    private bool _aaAutoSaveFailed;                // a write failed: stop retrying on every packet (the error was reported once)
    private long _lastAaSave;                      // Now() when aa.jsonl was last written; 0 = never
    internal Func<long> Now = () => System.Diagnostics.Stopwatch.GetTimestamp();

    public string OutputFolder { get; }
    public long Packets { get; private set; }
    public long AppPackets { get; private set; }
    public int Sessions => _reader.SessionsSeen;
    public int ItemCount => _items.Count;
    public int AaCount => _aa.Count;
    public ushort? AaOpcode => _aa.Opcode;
    public bool AaBit64 => _aa.Bit64;

    /// <summary>Write aa.jsonl while ranks arrive (used for live capture); a file run writes it once at the end.</summary>
    public bool AutoSaveAa { get; set; }

    /// <summary>The shortest time between two automatic writes of aa.jsonl.</summary>
    public TimeSpan AutoSaveInterval { get; set; } = TimeSpan.FromSeconds(2);
    public IReadOnlyCollection<ItemRecord> Items => _items.Values;

    public event Action<ItemRecord>? ItemFound;
    public event Action<AaRecord>? AaFound;

    /// <summary>Something went wrong that did not stop the capture (a file could not be written, a handler threw).</summary>
    public event Action<string>? Error;

    public CollectorEngine(string outputFolder, ClientStrings? strings = null, Action<string>? log = null, ushort? aaOpcode = null, bool aaBit64 = true, bool sessionsInProgress = true)
    {
        OutputFolder = outputFolder;
        Directory.CreateDirectory(outputFolder);
        _strings = strings ?? ClientStrings.Empty;
        _log = log;
        _aa = new AaFinder(aaOpcode, aaBit64);
        _reader = new EQStreamReader(OnApp) { OnCallbackError = ex => Report("A handler failed: " + ex.Message), AcceptSessionsInProgress = sessionsInProgress };
        LoadExistingItemIds();
        LoadExistingAa();
    }

    private string ItemsPath => Path.Combine(OutputFolder, "items.jsonl");

    private void Report(string message)
    {
        _log?.Invoke(message);
        Error?.Invoke(message);
    }

    /// <summary>Reads the ranks already in aa.jsonl so a flush adds to them instead of replacing them.</summary>
    private void LoadExistingAa()
    {
        var path = Path.Combine(OutputFolder, "aa.jsonl");
        if (!File.Exists(path)) return;
        var loaded = new Dictionary<(uint AaId, uint Rank), string>();     // only kept when the whole file was read
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object
                        && root.TryGetProperty("aa_id", out var id) && id.TryGetUInt32(out var aaId)
                        && root.TryGetProperty("rank", out var rank) && rank.TryGetUInt32(out var r))
                        loaded[(aaId, r)] = line;
                }
                catch (JsonException)
                {
                }
            }
            foreach (var (key, line) in loaded) _aaOnDisk[key] = line;
            if (_aaOnDisk.Count > 0) _log?.Invoke($"{_aaOnDisk.Count:N0} AA ranks are already in aa.jsonl; this run's ranks will be added to them.");
        }
        catch (IOException ex)
        {
            // A partial read must never be written back over the original, so aa.jsonl is left alone and this run's
            // ranks go to aa.partial.jsonl instead.
            _aaLoadFailed = true;
            Report("Could not read the existing aa.jsonl completely: " + ex.Message + ". It will not be replaced; this run's ranks are saved to aa.partial.jsonl.");
        }
    }

    /// <summary>Reads the ids already in items.jsonl, so a second run (or a restart) does not write the same items again.</summary>
    private void LoadExistingItemIds()
    {
        if (!File.Exists(ItemsPath)) return;
        try
        {
            foreach (var line in File.ReadLines(ItemsPath))
            {
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("id", out var id) && id.TryGetInt64(out var value)) _onDisk.Add(value);
                }
                catch (JsonException)
                {
                }
            }
            if (_onDisk.Count > 0) _log?.Invoke($"{_onDisk.Count:N0} items are already in items.jsonl and will not be written again.");
        }
        catch (IOException ex)
        {
            Report("Could not read the existing items.jsonl: " + ex.Message);
        }
    }

    public void Feed(UdpPacket p)
    {
        Packets++;
        _reader.Feed(p.SrcIp, p.DstIp, p.SrcPort, p.DstPort, p.Payload, p.Time);
    }

    private void OnApp(AppPacket p)
    {
        AppPackets++;

        if (!p.ClientToServer && p.Data.Length > 200)
        {
            foreach (var found in ItemFinder.Find(p.Data))
            {
                if (!_items.TryAdd(found.Item.Id, found.Item)) continue;
                if (_onDisk.Add(found.Item.Id)) AppendItem(found.Item);
                ItemFound?.Invoke(found.Item);
            }
        }

        bool hadOpcode = _aa.Opcode != null;
        var rank = _aa.OnPacket(p);
        if (!hadOpcode && _aa.Opcode != null) _log?.Invoke($"AA packets found (application opcode 0x{_aa.Opcode:x})");
        if (rank != null)
        {
            _aaDirty = true;
            AaFound?.Invoke(rank);
        }
        MaybeAutoSaveAa();
    }

    internal void MarkAaChanged() => _aaDirty = true;

    /// <summary>Saves aa.jsonl when ranks have arrived since the last write and the interval has passed. Driven by packets,
    /// so the ranks at the end of a burst are written by the next packet (game traffic is continuous) or when it is stopped.</summary>
    internal void MaybeAutoSaveAa()
    {
        if (!AutoSaveAa || !_aaDirty || _aaAutoSaveFailed) return;
        long now = Now();
        if (_lastAaSave != 0 && now - _lastAaSave < AutoSaveInterval.TotalSeconds * System.Diagnostics.Stopwatch.Frequency) return;
        if (FlushAa() == 0 && _aa.Count > 0) _aaAutoSaveFailed = true;      // FlushAa reported why; do not retry for every packet
    }

    private void AppendItem(ItemRecord item)
    {
        if (_writingFailed) return;
        try
        {
            _itemsFile ??= new StreamWriter(ItemsPath, append: true, new UTF8Encoding(false)) { AutoFlush = true };
            _itemsFile.WriteLine(JsonSerializer.Serialize(ItemToDictionary(item), JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _writingFailed = true;
            Report($"Cannot write {ItemsPath}: {ex.Message}. Items are still counted but no longer saved.");
        }
    }

    public static Dictionary<string, object> ItemToDictionary(ItemRecord item)
    {
        var d = new Dictionary<string, object>();
        for (int i = 0; i < ItemDecoder.Columns.Length; i++) d.TryAdd(ItemDecoder.Columns[i], item.Values[i]);
        return d;
    }

    public Dictionary<string, object?> AaToDictionary(AaRecord r)
    {
        _strings.Titles.TryGetValue(r.TitleSid, out var title);
        _strings.Descriptions.TryGetValue(r.DescSid, out var desc);
        string? spell = r.SpellId > 0 && _strings.Spells.TryGetValue(r.SpellId, out var sn) ? sn : null;
        return new Dictionary<string, object?>
        {
            ["rank_id"] = r.RankId, ["aa_id"] = r.AaId, ["rank"] = r.Rank, ["max_rank"] = r.MaxRank,
            ["title_sid"] = r.TitleSid, ["desc_sid"] = r.DescSid, ["title"] = title,
            ["description"] = desc?.Replace("<br><br>", "\n").Replace("<br>", "\n"),
            ["cost"] = r.Cost, ["total_cost"] = r.TotalCost, ["req_level"] = r.ReqLevel,
            ["req_skills"] = r.ReqSkills, ["req_ranks"] = r.ReqRanks,
            ["type"] = r.Type, ["type_name"] = r.Type < AaDecoder.Types.Length ? AaDecoder.Types[r.Type] : null,
            ["category"] = r.Category, ["category_name"] = AaDecoder.Categories.GetValueOrDefault(r.Category),
            ["expansion"] = r.Expansion, ["expansion2"] = r.Expansion2,
            ["spell_id"] = r.SpellId, ["spell_name"] = spell,
            ["timer_id"] = r.TimerId, ["reuse_seconds"] = r.ReuseSeconds,
            ["class_mask"] = r.ClassMask, ["classes"] = AaDecoder.ClassesFromMask(r.ClassMask),
            ["prev_desc_sid"] = r.PrevDescSid, ["next_desc_sid"] = r.NextDescSid,
            ["slots"] = r.Slots.Select(s => new Dictionary<string, object> { ["slot"] = s.Slot, ["spa"] = s.Spa, ["base1"] = s.Base1, ["base2"] = s.Base2 }).ToList(),
        };
    }

    /// <summary>
    /// Writes aa.jsonl: the ranks already in the file plus this run's (this run's win for a rank both have). Nothing is
    /// written when there is nothing at all, and the swap is atomic, so the file is never emptied or half-written.
    /// Returns how many ranks the file holds (0 when it could not be written).
    /// </summary>
    public int FlushAa()
    {
        var path = Path.Combine(OutputFolder, _aaLoadFailed ? "aa.partial.jsonl" : "aa.jsonl");
        var temp = path + ".tmp";

        var merged = new Dictionary<(uint AaId, uint Rank), string>(_aaOnDisk);
        foreach (var r in _aa.Records) merged[(r.AaId, r.Rank)] = JsonSerializer.Serialize(AaToDictionary(r), JsonOptions);
        if (merged.Count == 0) return 0;

        try
        {
            using (var w = new StreamWriter(temp, false, new UTF8Encoding(false)))
            {
                foreach (var key in merged.Keys.OrderBy(k => k.AaId).ThenBy(k => k.Rank)) w.WriteLine(merged[key]);
            }
            File.Move(temp, path, overwrite: true);
            foreach (var (key, line) in merged) _aaOnDisk[key] = line;
            _aaDirty = false;
            _lastAaSave = Now();
            return merged.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report($"Cannot write {path}: {ex.Message}");
            try { File.Delete(temp); } catch (Exception) { /* the temporary file is only a leftover */ }
            return 0;
        }
    }
    public void Close()
    {
        try
        {
            _itemsFile?.Dispose();
        }
        catch (IOException)
        {
        }
        _itemsFile = null;
    }

    public void Dispose() => Close();

    /// <summary>
    /// Runs a whole capture file through the engine. The AA packet type (and slot width) is looked for first, in a quick
    /// pass that stops as soon as it is found, so the second pass can decode AAs as they arrive, including those before
    /// the first sign of it.
    /// </summary>
    public static CollectorEngine ProcessFile(string capturePath, string outputFolder, ClientStrings? strings = null,
        Action<string>? log = null, Action<long>? progress = null, CancellationToken cancel = default)
    {
        ushort? opcode = null;
        bool bit64 = true;
        {
            var finder = new AaFinder();
            var probe = new EQStreamReader(p => finder.OnPacket(p)) { AcceptSessionsInProgress = true };
            foreach (var udp in PcapReader.ReadUdp(capturePath))
            {
                cancel.ThrowIfCancellationRequested();
                probe.Feed(udp.SrcIp, udp.DstIp, udp.SrcPort, udp.DstPort, udp.Payload, udp.Time);
                if (finder.Opcode != null) break;
            }
            opcode = finder.Opcode;
            bit64 = finder.Bit64;
            log?.Invoke(opcode != null ? $"AA packet type: 0x{opcode:x} ({(bit64 ? "64" : "32")}-bit slots)" : "no AA packets in this capture");
        }

        var engine = new CollectorEngine(outputFolder, strings, log, opcode, bit64);
        try
        {
            foreach (var udp in PcapReader.ReadUdp(capturePath))
            {
                cancel.ThrowIfCancellationRequested();
                engine.Feed(udp);
                if (engine.Packets % 20000 == 0) progress?.Invoke(engine.Packets);
            }
            engine.FlushAa();
            engine.Close();
            return engine;
        }
        catch
        {
            engine.FlushAa();                // keep what was found before the run was cancelled or failed
            engine.Dispose();                // and release items.jsonl
            throw;
        }
    }
}
