using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EQCollector.Core;

/// <summary>The server refused the token (or the account); uploading is pointless until the token is fixed.</summary>
public sealed class UploadAuthException : Exception
{
    public UploadAuthException(string message) : base(message) { }
}

/// <summary>The server or the network could not be reached right now; try again later.</summary>
public sealed class UploadUnavailableException : Exception
{
    public UploadUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>What one upload round did.</summary>
public sealed record UploadSummary(int ItemsChecked, int ItemsSent, int ItemsAdded, int ItemsQueued, int AaSent, int AaSaved, int Rejected, int ItemsAlreadyOnSite = 0, int AaQueued = 0);

/// <summary>What the server said about the token.</summary>
public sealed record PingResult(bool Ok, bool Trusted, string Site);

/// <summary>
/// Sends what the collector found to the website (norrathcodex.com): the items it does not have yet and the AA ranks. The
/// app first asks which of its item ids the site lacks and sends only those, so a normal login is a handful of requests.
/// What has been sent is remembered in upload-state.json next to the results, so nothing is sent twice (an AA rank is sent
/// again only if its text changed, e.g. once the EverQuest folder is set). Never throws into a capture: callers catch
/// <see cref="UploadAuthException"/> (stop uploading) and <see cref="UploadUnavailableException"/> (retry later).
/// </summary>
public sealed class CollectorUploader : IDisposable
{
    public const int ItemBatch = 50;
    public const int AaBatch = 200;
    private const int CheckBatch = 2000;
    private const int MaxRateLimitRetries = 3;
    private static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly HttpClient _http;
    private readonly string _statePath;
    private readonly Action<string>? _log;
    private readonly string _server;
    private readonly string _tokenId;                  // which token this is (not the token): declined AA ranks are per token
    private readonly UploadState _state;

    // Several uploaders can exist at once (the background one and a manual "upload saved files"): they take turns, and they
    // share one in-memory copy of each state file, so none writes a stale copy over another's newer one.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, UploadState> SharedStates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long to wait when the server says "too many requests" and gives no time (tests set it to zero).</summary>
    public TimeSpan RateLimitWait { get; set; } = TimeSpan.FromSeconds(20);

    public CollectorUploader(string serverUrl, string token, string stateFolder, Action<string>? log = null, HttpMessageHandler? handler = null)
    {
        var uri = NormalizeServer(serverUrl);
        _server = uri.ToString().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("An upload token is needed.", nameof(token));
        _log = log;
        _tokenId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())))[..12];
        _http = handler != null ? new HttpClient(handler) : new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });
        _http.BaseAddress = new Uri(_server + "/");
        _http.Timeout = TimeSpan.FromSeconds(90);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("EQCollector/" + ((System.Reflection.Assembly.GetEntryAssembly() ?? typeof(CollectorUploader).Assembly).GetName().Version?.ToString(3) ?? "0"));
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        Directory.CreateDirectory(stateFolder);
        _statePath = Path.Combine(stateFolder, "upload-state.json");
        _statePath = Path.GetFullPath(_statePath);
        Manifest = SiteManifest.Load(ManifestPath, _server);
        lock (SharedStates)
        {
            if (!SharedStates.TryGetValue(_statePath, out var state) || !string.Equals(state.Server, _server, StringComparison.OrdinalIgnoreCase))
                SharedStates[_statePath] = state = UploadState.Load(_statePath, _server);
            _state = state;
        }
    }

    public string Server => _server;

    /// <summary>The website's list of what it has, as last pulled (null before the first pull succeeded). Kept in site-manifest.json.</summary>
    public SiteManifest? Manifest { get; private set; }

    /// <summary>How old the pulled list may get before the next upload round pulls it again.</summary>
    public TimeSpan ManifestMaxAge { get; set; } = TimeSpan.FromMinutes(30);

    private DateTimeOffset _manifestRetryAfter;       // after a failed pull, not again before this

    private string ManifestPath => Path.Combine(Path.GetDirectoryName(_statePath)!, "site-manifest.json");

    /// <summary>Items and AA ranks already confirmed by the server (this app's memory of it).</summary>
    public int KnownItems => _state.Items.Count;
    public int KnownAa => _state.Aa.Count;

    /// <summary>Forgets the shared in-memory state of every state file (tests that start a "new run").</summary>
    internal static void ForgetSharedStates() { lock (SharedStates) SharedStates.Clear(); }

    /// <summary>
    /// The address must be https, so the token is never sent in the clear -- except to this computer or a private network
    /// address (a test site on the LAN). Returns the site root, without a path.
    /// </summary>
    public static Uri NormalizeServer(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("Enter the website address.");
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            throw new ArgumentException("That is not a web address.");
        if (uri.Scheme == "http" && !IsLocalHost(uri.Host))
            throw new ArgumentException("Use https:// -- the token must not be sent unencrypted over the internet.");
        return new Uri(uri.GetLeftPart(UriPartial.Authority));
    }

    private static bool IsLocalHost(string host)
    {
        // A name is only trusted when it is "localhost": any other name (.lan, .local, ...) could be pointed anywhere by DNS or
        // a hosts file, so a private-network test site is addressed by its numeric address.
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168));
    }

    /// <summary>Pulls the website's list of items and AA ranks and keeps a copy next to the results.</summary>
    public async Task<SiteManifest> RefreshManifestAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "api/collector/manifest"), ct);
        var doc = await ReadJson(response, ct);
        var now = DateTimeOffset.UtcNow;
        var manifest = SiteManifest.Parse(doc, now) ?? throw Unexpected();
        SiteManifest.Save(ManifestPath, _server, doc, now);
        Manifest = manifest;
        _log?.Invoke($"Pulled the website's list: it has {manifest.ItemCount:N0} items and {manifest.AaCount:N0} AA ranks.");
        return manifest;
    }

    /// <summary>Pulls the list when there is none or it is older than <see cref="ManifestMaxAge"/>. Trouble reaching the site is not fatal:
    /// the uploader falls back to asking it, so only a refused token is thrown.</summary>
    private async Task EnsureManifestAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (Manifest != null && now - Manifest.FetchedAt < ManifestMaxAge) return;
        if (now < _manifestRetryAfter) return;
        try { await RefreshManifestAsync(ct); }
        catch (UploadUnavailableException ex)
        {
            _manifestRetryAfter = now.AddMinutes(5);
            _log?.Invoke("Could not pull the website's list (" + ex.Message + "); asking the site about each item instead.");
        }
    }

    /// <summary>Checks the token against the server.</summary>
    public async Task<PingResult> PingAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "api/collector/ping"), ct);
        var doc = await ReadJson(response, ct);
        if (!Bool(doc, "ok")) throw Unexpected();         // a wrong address or a proxy answering 200 with something else
        return new PingResult(true, Bool(doc, "trusted"), Text(doc, "site"));
    }

    /// <summary>
    /// Uploads what the server does not have. <paramref name="items"/> are items.jsonl records (with an "id");
    /// <paramref name="aa"/> are aa.jsonl records (with a "rank_id"). Safe to call repeatedly with the same records.
    /// </summary>
    public async Task<UploadSummary> UploadAsync(IReadOnlyCollection<IReadOnlyDictionary<string, object?>> items,
        IReadOnlyCollection<IReadOnlyDictionary<string, object?>> aa, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            // Another process (the command line while the window runs) may share the results folder: take its file lock and
            // fold in what it recorded before deciding what to send.
            using var fileLock = await AcquireFileLockAsync(ct);
            _state.MergeFrom(UploadState.Load(_statePath, _server));
            await EnsureManifestAsync(ct);
            return await UploadCoreAsync(items, aa, ct);
        }
        finally { Gate.Release(); }
    }

    private async Task<UploadSummary> UploadCoreAsync(IReadOnlyCollection<IReadOnlyDictionary<string, object?>> items,
        IReadOnlyCollection<IReadOnlyDictionary<string, object?>> aa, CancellationToken ct)
    {
        int checkedCount = 0, sent = 0, added = 0, queued = 0, aaSent = 0, aaSaved = 0, aaQueued = 0, rejected = 0, onSite = 0;
        var manifest = Manifest;

        // ---- items: ask which ids the site lacks, send only those --------------------------------------------------
        var fresh = new Dictionary<long, IReadOnlyDictionary<string, object?>>();
        foreach (var item in items)
        {
            if (!TryId(item, "id", out long id) || _state.Items.Contains(id)) continue;
            if (manifest != null && manifest.HasItem(id))
            {
                onSite++;                                  // the pulled list says the site has it: nothing to ask or send. Not
                continue;                                  // remembered in the state file: a list can be stale, and is re-read each round
            }
            var stripped = Strip(item);
            // An item the website rejected is skipped only while its record is unchanged; a corrected record is tried again.
            if (_state.RejectedHashes.TryGetValue(id, out var rejectedHash) && rejectedHash == Hash(stripped)) continue;
            fresh[id] = stripped;
        }
        checkedCount = fresh.Count;

        foreach (var chunk in fresh.Keys.Chunk(CheckBatch))
        {
            var missing = await CheckAsync(chunk, ct);
            var missingSet = new HashSet<long>(missing);
            foreach (long id in chunk.Where(i => !missingSet.Contains(i))) _state.Items.Add(id);   // the site has it already
            _state.Save(_statePath);

            foreach (var batch in missing.Where(fresh.ContainsKey).Chunk(ItemBatch))
            {
                var result = await SendItemsAsync(batch.Select(i => fresh[i]).ToList(), ct);
                sent += result.Sent; added += result.Added; queued += result.Queued; rejected += result.Rejected;
                foreach (long id in batch)
                {
                    if (_state.RejectedHashes.TryGetValue(id, out var refusedHash) && refusedHash == Hash(fresh[id])) continue;   // rejected as it is: tried again only if it changes
                    _state.Items.Add(id);                            // handled: added, queued, or skipped by the server
                    _state.RejectedHashes.Remove(id);
                }
                _state.Save(_statePath);
            }
        }

        // ---- AA ranks: sent when new or changed -----------------------------------------------------------------------
        if (_state.DeclinedFor != _tokenId)
        {
            _state.Declined.Clear();                      // another token may be allowed to store what the last one was not
            _state.DeclinedFor = _tokenId;
        }
        var latest = new Dictionary<long, IReadOnlyDictionary<string, object?>>();
        foreach (var rank in aa)
        {
            if (TryId(rank, "rank_id", out long rid)) latest[rid] = rank;                    // a later record of a rank replaces an earlier one
        }
        var pending = new List<(long Id, string Hash, IReadOnlyDictionary<string, object?> Record)>();
        foreach (var (rid, rank) in latest)
        {
            string hash = Hash(rank);
            bool already = _state.Aa.TryGetValue(rid, out var known) && known == hash;
            bool declined = _state.Declined.TryGetValue(rid, out var refused) && refused == hash;
            if (!already && !declined && manifest != null && manifest.TryGetAa(rid, out bool siteHasText) && (siteHasText || !HasText(rank)))
            {
                continue;                                  // the site has this rank (with its text, or we would add none); not remembered, as above
            }
            if (!already && !declined) pending.Add((rid, hash, rank));
        }
        foreach (var batch in pending.Chunk(AaBatch))
        {
            using var response = await SendAsync(() => JsonRequest("api/collector/upload", new Dictionary<string, object?> { ["aa"] = batch.Select(b => b.Record).ToList() }), ct);
            var doc = await ReadJson(response, ct);
            RequireCounters(doc, "aa_saved", "aa_skipped", "aa_queued");
            aaSent += batch.Length;
            aaSaved += Int(doc, "aa_saved");
            aaQueued += Int(doc, "aa_queued");
            // Ranks the server declined (a non-trusted token) are remembered for this token only, or every round would send
            // them again -- and a trusted token later still sends them.
            bool declined = Int(doc, "aa_saved") == 0 && Int(doc, "aa_queued") == 0 && Int(doc, "aa_skipped") > 0;     // the whole batch refused (a token that may not store AAs)
            foreach (var b in batch)
            {
                if (declined) _state.Declined[b.Id] = b.Hash;
                else _state.Aa[b.Id] = b.Hash;
            }
            _state.Save(_statePath);
        }

        _state.Save(_statePath);
        var summary = new UploadSummary(checkedCount, sent, added, queued, aaSent, aaSaved, rejected, onSite, aaQueued);
        if (sent > 0 || aaSent > 0) _log?.Invoke($"Uploaded: {added} new items added, {queued} items and {aaQueued} AA ranks queued for review, {aaSaved} AA ranks saved.");
        return summary;
    }

    /// <summary>How long to wait for another EQ Collector process that is uploading from the same results folder.</summary>
    public TimeSpan LockWait { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// An exclusive lock file next to the state file, held for one round. Waits for another process; if it stays busy the round
    /// is not run at all (reported as unavailable, so it is retried) -- going on without the lock could lose the other
    /// process's record of what it sent.
    /// </summary>
    private async Task<IDisposable?> AcquireFileLockAsync(CancellationToken ct)
    {
        var path = _statePath + ".lock";
        var until = DateTime.UtcNow + LockWait;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                if (DateTime.UtcNow >= until) throw new UploadUnavailableException("Another EQ Collector is uploading from this results folder; this will be tried again shortly.");
                await Task.Delay(100, ct);                // held by another process
            }
            catch (UnauthorizedAccessException)
            {
                return null;                              // cannot create a lock file here: carry on without one
            }
        }
    }

    /// <summary>Uploads items.jsonl and aa.jsonl from a results folder (what an earlier run saved).</summary>
    public Task<UploadSummary> UploadFolderAsync(string folder, CancellationToken ct = default)
        => UploadAsync(ReadJsonLines(Path.Combine(folder, "items.jsonl")), ReadAaLines(folder), ct);

    /// <summary>aa.jsonl, then aa.partial.jsonl (where a run writes when it could not read the old aa.jsonl completely); the later file wins.</summary>
    private static List<IReadOnlyDictionary<string, object?>> ReadAaLines(string folder)
    {
        string main = Path.Combine(folder, "aa.jsonl"), partial = Path.Combine(folder, "aa.partial.jsonl");
        // Both files count (a rank may exist only in the fallback file); for a rank in both, the newer file wins, because
        // once aa.jsonl can be written again the old fallback file is the stale one.
        bool partialNewer = File.Exists(partial) && (!File.Exists(main) || File.GetLastWriteTimeUtc(partial) >= File.GetLastWriteTimeUtc(main));
        var older = ReadJsonLines(partialNewer ? main : partial);
        older.AddRange(ReadJsonLines(partialNewer ? partial : main));       // later entries replace earlier ones for the same rank
        return older;
    }

    public static List<IReadOnlyDictionary<string, object?>> ReadJsonLines(string path)
    {
        var list = new List<IReadOnlyDictionary<string, object?>>();
        if (!File.Exists(path)) return list;
        // Shared read: the capture may still have the file open for writing.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, object?>>(line);
                if (d != null) list.Add(d);
            }
            catch (JsonException)
            {
                // a damaged line is skipped, like everywhere else
            }
        }
        return list;
    }

    // ---- requests ---------------------------------------------------------------------------------------------------

    private async Task<List<long>> CheckAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        using var response = await SendAsync(() => JsonRequest("api/collector/check", new Dictionary<string, object?> { ["item_ids"] = ids.ToList() }), ct);
        var doc = await ReadJson(response, ct);
        var missing = new List<long>();
        if (doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty("missing_items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt64(out long id)) throw Unexpected();
                missing.Add(id);
            }
        }
        else throw Unexpected();
        return missing;
    }

    private sealed record ItemResult(int Sent, int Added, int Queued, int Rejected);

    /// <summary>Sends a batch; if the server rejects it as invalid, splits it so one bad item cannot block the rest.</summary>
    private async Task<ItemResult> SendItemsAsync(IReadOnlyList<IReadOnlyDictionary<string, object?>> batch, CancellationToken ct)
    {
        using var response = await SendAsync(() => JsonRequest("api/collector/upload", new Dictionary<string, object?> { ["items"] = batch }), ct, allowUnprocessable: true);
        if (response.StatusCode == (HttpStatusCode)422)
        {
            if (batch.Count == 1)
            {
                TryId(batch[0], "id", out long id);
                _state.RejectedHashes[id] = Hash(batch[0]);
                _log?.Invoke($"The website rejected item {id}; it will not be sent again unless its data changes.");
                return new ItemResult(0, 0, 0, 1);
            }
            int half = batch.Count / 2;
            var a = await SendItemsAsync(batch.Take(half).ToList(), ct);
            var b = await SendItemsAsync(batch.Skip(half).ToList(), ct);
            return new ItemResult(a.Sent + b.Sent, a.Added + b.Added, a.Queued + b.Queued, a.Rejected + b.Rejected);
        }

        var doc = await ReadJson(response, ct);
        RequireCounters(doc, "items_added", "items_queued", "items_skipped");
        return new ItemResult(batch.Count, Int(doc, "items_added"), Int(doc, "items_queued"), 0);
    }

    private static HttpRequestMessage JsonRequest(string path, object body)
        => new(HttpMethod.Post, path) { Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json") };

    /// <summary>One request with the usual failure handling: auth errors and "unavailable" become exceptions, 429 is waited out.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct, bool allowUnprocessable = false)
    {
        for (int attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(make(), ct);
            }
            catch (HttpRequestException ex)
            {
                throw new UploadUnavailableException("Could not reach the website: " + ex.Message, ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new UploadUnavailableException("The website did not answer in time.", ex);
            }

            if (response.IsSuccessStatusCode) return response;
            if (allowUnprocessable && response.StatusCode == (HttpStatusCode)422) return response;

            var code = response.StatusCode;
            var retryAfter = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
            string detail = await SafeBody(response);
            response.Dispose();

            if (code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new UploadAuthException("The website did not accept the token. Check it at your account page and paste it again.");
            if (code == (HttpStatusCode)429 && attempt < MaxRateLimitRetries)
            {
                var wait = retryAfter ?? RateLimitWait;
                if (wait > MaxRateLimitWait) throw new UploadUnavailableException("The website asked to wait too long between requests; it will be tried again later.");
                await Task.Delay(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, ct);
                continue;
            }
            throw new UploadUnavailableException($"The website answered {(int)code}{(detail.Length > 0 ? ": " + detail : "")}");
        }
    }

    private static async Task<string> SafeBody(HttpResponseMessage response)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync();
            return text.Length > 200 ? text[..200] : text;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return "";
        }
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new UploadUnavailableException("The website's answer was not what was expected (is the address right?).", ex);
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>The website always answers an upload with its counters; a reply without them (a proxy, an old server) is not an acknowledgement.</summary>
    private static void RequireCounters(JsonElement doc, params string[] names)
    {
        if (doc.ValueKind != JsonValueKind.Object || !names.Any(n => doc.TryGetProperty(n, out _))) throw Unexpected();
    }

    private static bool HasText(IReadOnlyDictionary<string, object?> rank)
        => rank.TryGetValue("title", out var v) && v switch
        {
            string s => s.Length > 0,
            JsonElement { ValueKind: JsonValueKind.String } e => (e.GetString() ?? "").Length > 0,
            _ => false,
        };

    private static UploadUnavailableException Unexpected() => new("The website's answer was not what was expected (is the address right?).");

    // A reply of the wrong shape (a wrong address, a proxy, a changed server) is "unavailable", never a crash.
    private static int Int(JsonElement doc, string name)
    {
        if (doc.ValueKind != JsonValueKind.Object) throw Unexpected();
        if (!doc.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int n)) throw Unexpected();
        return n;
    }

    private static bool Bool(JsonElement doc, string name)
    {
        if (doc.ValueKind != JsonValueKind.Object) throw Unexpected();
        return doc.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True;
    }

    private static string Text(JsonElement doc, string name)
        => doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Drops fields that are not game data: the crafter's name is a player's name.</summary>
    private static IReadOnlyDictionary<string, object?> Strip(IReadOnlyDictionary<string, object?> item)
        => item.ContainsKey("madeby") ? item.Where(kv => kv.Key != "madeby").ToDictionary(kv => kv.Key, kv => kv.Value) : item;

    private static bool TryId(IReadOnlyDictionary<string, object?> d, string key, out long id)
    {
        id = 0;
        if (!d.TryGetValue(key, out var v) || v == null) return false;
        switch (v)
        {
            case JsonElement e when e.ValueKind == JsonValueKind.Number: return e.TryGetInt64(out id);
            case JsonElement: return false;
            default:
                try { id = Convert.ToInt64(v); return true; }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return false; }
        }
    }

    private static string Hash(IReadOnlyDictionary<string, object?> record)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value), Json))))[..16];

    public void Dispose() => _http.Dispose();

    // ---- what has been sent --------------------------------------------------------------------------------------------

    private sealed class UploadState
    {
        public string Server { get; set; } = "";
        public HashSet<long> Items { get; set; } = new();
        public Dictionary<long, string> RejectedHashes { get; set; } = new();
        public Dictionary<long, string> Aa { get; set; } = new();
        public Dictionary<long, string> Declined { get; set; } = new();     // AA ranks the server would not store for DeclinedFor's token
        public string DeclinedFor { get; set; } = "";

        public static UploadState Load(string path, string server)
        {
            try
            {
                if (File.Exists(path))
                {
                    var s = JsonSerializer.Deserialize<UploadState>(File.ReadAllText(path));
                    if (s != null && string.Equals(s.Server, server, StringComparison.OrdinalIgnoreCase)) return s;   // another site: start over
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // an unreadable memory only means some things are sent again
            }
            return new UploadState { Server = server };
        }

        /// <summary>Adds what another process recorded (this process's own entries win where both have one).</summary>
        public void MergeFrom(UploadState other)
        {
            Items.UnionWith(other.Items);
            foreach (var kv in other.RejectedHashes) RejectedHashes.TryAdd(kv.Key, kv.Value);
            foreach (var kv in other.Aa) Aa.TryAdd(kv.Key, kv.Value);
            if (DeclinedFor == other.DeclinedFor) foreach (var kv in other.Declined) Declined.TryAdd(kv.Key, kv.Value);
        }

        public void Save(string path)
        {
            try
            {
                var temp = path + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(this), new UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // not being able to remember is not worth stopping for
            }
        }
    }
}
