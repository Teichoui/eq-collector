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
    private string _fallbackName = "aa.partial.jsonl";   // where this run's ranks go while aa.jsonl cannot be read
    private readonly List<string> _partialFiles = new();  // fallback files merged in; deleted once aa.jsonl has been written
    private bool _aaDamaged;                          // aa.jsonl held a line that is not valid JSON (it is dropped on the next write)
    private bool _aaBackupMade;
    private StreamWriter? _itemsFile;
    private bool _writingFailed;
    private bool _aaDirty;                         // ranks found since the file was last written
    private readonly List<IReadOnlyDictionary<string, object?>> _uploadItems = new();   // found since the last TakeUpload
    private bool _uploadAaChanged;
    private long _aaNextAutoSave;                  // Now() before which no automatic write is tried; 0 = no limit yet
    internal Func<long> Now = () => System.Diagnostics.Stopwatch.GetTimestamp();

    public string OutputFolder { get; }
    public long Packets { get; private set; }
    public long AppPackets { get; private set; }
    public int Sessions => _reader.SessionsSeen;
    public int ItemCount => _items.Count;

    /// <summary>Times an item record was found but dropped because its values are impossible (see <see cref="ItemSanity"/>);
    /// the same garbled record seen again counts again.</summary>
    public int GarbledItems => _garbledItems;
    private int _garbledItems;
    public int AaCount => _aa.Count;
    public ushort? AaOpcode => _aa.Opcode;
    public bool AaBit64 => _aa.Bit64;

    /// <summary>Keep each new item (and the ranks' changes) for <see cref="TakeUpload"/>; set when uploading to the website.</summary>
    public bool CollectForUpload { get; set; }

    /// <summary>Write aa.jsonl while ranks arrive (used for live capture); a file run writes it once at the end.</summary>
    public bool AutoSaveAa { get; set; }

    /// <summary>The shortest time between two automatic writes of aa.jsonl.</summary>
    public TimeSpan AutoSaveInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait before trying again after an automatic write failed.</summary>
    public TimeSpan AutoSaveRetry { get; set; } = TimeSpan.FromSeconds(30);
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
        try
        {
            if (File.Exists(path))
            {
                var loaded = ReadAaLines(path, out int damaged);              // only kept when the whole file was read
                foreach (var (key, line) in loaded) _aaOnDisk[key] = line;
                _aaDamaged = damaged > 0;
                if (damaged > 0) Report($"{damaged:N0} damaged line(s) in aa.jsonl are left out; the original is copied to a .damaged file before it is rewritten.");
                if (_aaOnDisk.Count > 0) _log?.Invoke($"{_aaOnDisk.Count:N0} AA ranks are already in aa.jsonl; this run's ranks will be added to them.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A partial read must never be written back over the original, so aa.jsonl is left alone and this run's
            // ranks go to a fallback file instead.
            _aaLoadFailed = true;
            _aaOnDisk.Clear();
            try
            {
                // Fallback files left by earlier failed runs keep what they have: this run's ranks are added to them, never
                // written over them. (A fallback file that cannot be read makes this run write a file of its own.)
                foreach (var file in PartialFiles()) foreach (var (key, line) in ReadAaLines(file, out _)) _aaOnDisk[key] = line;
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
                _aaOnDisk.Clear();
                _fallbackName = $"aa.partial-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.jsonl";
            }

            Report("Could not read the existing aa.jsonl completely: " + ex.Message + ". It will not be replaced; this run's ranks are saved to " + _fallbackName + ".");
            return;
        }

        // aa.jsonl can be written again, so fallback files left by earlier runs are folded in (their ranks were captured later
        // than what aa.jsonl held then) and removed once aa.jsonl has been written.
        foreach (var file in PartialFiles())
        {
            try
            {
                var fromPartial = ReadAaLines(file, out _);
                foreach (var (key, line) in fromPartial) _aaOnDisk[key] = line;
                _partialFiles.Add(file);
                _log?.Invoke($"{fromPartial.Count:N0} AA ranks from an earlier {Path.GetFileName(file)} will be merged into aa.jsonl.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // left alone: it is merged next time
            }
        }
    }

    /// <summary>Every fallback file in the results folder (aa.partial.jsonl and the uniquely named ones), oldest first.</summary>
    private IEnumerable<string> PartialFiles() => Directory.EnumerateFiles(OutputFolder, "aa.partial*.jsonl").OrderBy(File.GetLastWriteTimeUtc).ToList();

    private static Dictionary<(uint AaId, uint Rank), string> ReadAaLines(string path, out int damaged)
    {
        damaged = 0;
        var loaded = new Dictionary<(uint AaId, uint Rank), string>();
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
                else damaged++;
            }
            catch (JsonException)
            {
                damaged++;
            }
        }
        return loaded;
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
                if (_items.Count >= MaxItems)
                {
                    if (!_itemCapReported) { _itemCapReported = true; Report($"More than {MaxItems:N0} different items: no more are kept this run (a real capture holds a few thousand)."); }
                    continue;
                }
                if (ItemSanity.Problem(found.Item) is { } why)
                {
                    // The scan lined up on the wrong bytes: the record is noise, so it is neither kept nor uploaded.
                    if (++_garbledItems <= 3) Report($"Skipped a garbled item record ({why}).");
                    continue;
                }
                if (!_items.TryAdd(found.Item.Id, found.Item)) continue;
                if (!_onDisk.Contains(found.Item.Id) && AppendItem(found.Item)) _onDisk.Add(found.Item.Id);     // remembered as saved only once it was written
                if (CollectForUpload) _uploadItems.Add(ItemToDictionary(found.Item).ToDictionary(kv => kv.Key, kv => (object?)kv.Value));
                ItemFound?.Invoke(found.Item);
            }
        }

        bool hadOpcode = _aa.Opcode != null;
        int before = _aa.Count;
        var rank = _aa.OnPacket(p);
        if (_aa.Count != before) _aaDirty = true;     // the packets buffered while the AA opcode was unknown decode without returning a rank
        if (_aa.Count != before || rank != null) _uploadAaChanged = true;
        if (!hadOpcode && _aa.Opcode != null) _log?.Invoke($"AA packets found (application opcode 0x{_aa.Opcode:x})");
        if (rank != null)
        {
            _aaDirty = true;
            AaFound?.Invoke(rank);
        }
        MaybeAutoSaveAa();
    }

    internal void MarkAaChanged() => _aaDirty = true;

    /// <summary>
    /// What the capture found since the last call, for the website upload: the new items, and all AA ranks when any rank
    /// changed (null otherwise). Items already in items.jsonl from an earlier run are not included here, so a restart does
    /// not re-queue them -- <see cref="CollectorUploader.UploadFolderAsync"/> covers those.
    /// </summary>
    public UploadBatch TakeUpload()
    {
        var items = new List<IReadOnlyDictionary<string, object?>>(_uploadItems);
        _uploadItems.Clear();
        List<IReadOnlyDictionary<string, object?>>? aa = null;
        if (_uploadAaChanged)
        {
            _uploadAaChanged = false;
            aa = _aa.Records.Select(r => (IReadOnlyDictionary<string, object?>)AaToDictionary(r)).ToList();
        }
        return new UploadBatch(items, aa);
    }

    /// <summary>Saves aa.jsonl when ranks have arrived since the last write and the interval has passed. Called for every
    /// packet and by the caller's own timer (so the end of a burst is written even when the game goes quiet); the caller
    /// must not call it while a packet is being fed.</summary>
    public void MaybeAutoSaveAa()
    {
        if (!AutoSaveAa || !_aaDirty) return;
        long now = Now();
        if (now < _aaNextAutoSave) return;
        // A failed write (FlushAa reported why) is retried after AutoSaveRetry, not on every packet: a file that is briefly
        // locked recovers, and a permanent problem is not logged hundreds of times.
        if (FlushAa() == 0) _aaNextAutoSave = now + (long)(AutoSaveRetry.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
    }

    private const int MaxItems = 500_000;
    private bool _itemCapReported;

    /// <summary>Appends one item to items.jsonl; false when it was not written.</summary>
    private bool AppendItem(ItemRecord item)
    {
        if (_writingFailed) return false;
        string line;
        try
        {
            line = JsonSerializer.Serialize(ItemToDictionary(item), JsonOptions);        // first: a record that cannot be serialised (NaN) is skipped, not half-written
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            Report($"Item {item.Id} could not be saved ({ex.Message}); skipped.");
            return false;
        }

        try
        {
            if (_itemsFile == null)
            {
                bool needsNewline = false;
                if (File.Exists(ItemsPath) && new FileInfo(ItemsPath).Length > 0)
                {
                    using var probe = new FileStream(ItemsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    probe.Seek(-1, SeekOrigin.End);
                    needsNewline = probe.ReadByte() != '\n';                               // an earlier run was cut off mid-line: do not glue this record to it
                }
                _itemsFile = new StreamWriter(ItemsPath, append: true, new UTF8Encoding(false)) { AutoFlush = true };
                if (needsNewline) _itemsFile.Write('\n');
            }
            _itemsFile.WriteLine(line);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _writingFailed = true;
            Report($"Cannot write {ItemsPath}: {ex.Message}. Items are still counted but no longer saved.");
            return false;
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
        var path = Path.Combine(OutputFolder, _aaLoadFailed ? _fallbackName : "aa.jsonl");
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
            if (_aaDamaged && !_aaBackupMade && !_aaLoadFailed && File.Exists(path))
            {
                try { File.Copy(path, path + $".damaged-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: false); }       // the lines that are about to be dropped
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not worth stopping the save */ }
                _aaBackupMade = true;
            }
            File.Move(temp, path, overwrite: true);
            if (!_aaLoadFailed)
            {
                foreach (var file in _partialFiles.ToList())
                {
                    try { File.Delete(file); _partialFiles.Remove(file); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* merged again next time */ }
                }
            }
            foreach (var (key, line) in merged) _aaOnDisk[key] = line;
            _aaDirty = false;
            _aaNextAutoSave = Now() + (long)(AutoSaveInterval.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
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
            foreach (var udp in PcapReader.ReadUdp(capturePath, pos =>
                log?.Invoke($"WARNING: the capture file is damaged or cut short at byte {pos:N0}; these results cover only what came before that point.")))
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
