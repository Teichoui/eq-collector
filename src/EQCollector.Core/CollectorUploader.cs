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
public sealed record UploadSummary(int ItemsChecked, int ItemsSent, int ItemsAdded, int ItemsQueued, int AaSent, int AaSaved, int Rejected);

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

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly HttpClient _http;
    private readonly string _statePath;
    private readonly Action<string>? _log;
    private readonly string _server;
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
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.BaseAddress = new Uri(_server + "/");
        _http.Timeout = TimeSpan.FromSeconds(90);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("EQCollector/" + (typeof(CollectorUploader).Assembly.GetName().Version?.ToString(3) ?? "0"));
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        Directory.CreateDirectory(stateFolder);
        _statePath = Path.Combine(stateFolder, "upload-state.json");
        _statePath = Path.GetFullPath(_statePath);
        lock (SharedStates)
        {
            if (!SharedStates.TryGetValue(_statePath, out var state) || !string.Equals(state.Server, _server, StringComparison.OrdinalIgnoreCase))
                SharedStates[_statePath] = state = UploadState.Load(_statePath, _server);
            _state = state;
        }
    }

    public string Server => _server;

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

    /// <summary>Checks the token against the server.</summary>
    public async Task<PingResult> PingAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "api/collector/ping"), ct);
        var doc = await ReadJson(response, ct);
        return new PingResult(Bool(doc, "ok"), Bool(doc, "trusted"), Text(doc, "site"));
    }

    /// <summary>
    /// Uploads what the server does not have. <paramref name="items"/> are items.jsonl records (with an "id");
    /// <paramref name="aa"/> are aa.jsonl records (with a "rank_id"). Safe to call repeatedly with the same records.
    /// </summary>
    public async Task<UploadSummary> UploadAsync(IReadOnlyCollection<IReadOnlyDictionary<string, object?>> items,
        IReadOnlyCollection<IReadOnlyDictionary<string, object?>> aa, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try { return await UploadCoreAsync(items, aa, ct); }
        finally { Gate.Release(); }
    }

    private async Task<UploadSummary> UploadCoreAsync(IReadOnlyCollection<IReadOnlyDictionary<string, object?>> items,
        IReadOnlyCollection<IReadOnlyDictionary<string, object?>> aa, CancellationToken ct)
    {
        int checkedCount = 0, sent = 0, added = 0, queued = 0, aaSent = 0, aaSaved = 0, rejected = 0;

        // ---- items: ask which ids the site lacks, send only those --------------------------------------------------
        var fresh = new Dictionary<long, IReadOnlyDictionary<string, object?>>();
        foreach (var item in items)
        {
            if (TryId(item, "id", out long id) && !_state.Items.Contains(id) && !_state.Rejected.Contains(id)) fresh[id] = Strip(item);
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
                foreach (long id in batch) _state.Items.Add(id);     // handled: added, queued, skipped by the server, or rejected locally
                _state.Save(_statePath);
            }
        }

        // ---- AA ranks: sent when new or changed -----------------------------------------------------------------------
        var pending = new List<(long Id, string Hash, IReadOnlyDictionary<string, object?> Record)>();
        foreach (var rank in aa)
        {
            if (!TryId(rank, "rank_id", out long rid)) continue;
            string hash = Hash(rank);
            if (!_state.Aa.TryGetValue(rid, out var known) || known != hash) pending.Add((rid, hash, rank));
        }
        foreach (var batch in pending.Chunk(AaBatch))
        {
            using var response = await SendAsync(() => JsonRequest("api/collector/upload", new Dictionary<string, object?> { ["aa"] = batch.Select(b => b.Record).ToList() }), ct);
            var doc = await ReadJson(response, ct);
            aaSent += batch.Length;
            aaSaved += Int(doc, "aa_saved");
            // Ranks the server declined (a non-trusted token) are remembered too, or every round would send them again.
            foreach (var b in batch) _state.Aa[b.Id] = b.Hash;
            _state.Save(_statePath);
        }

        var summary = new UploadSummary(checkedCount, sent, added, queued, aaSent, aaSaved, rejected);
        if (sent > 0 || aaSent > 0) _log?.Invoke($"Uploaded: {added} new items added, {queued} items queued for review, {aaSaved} AA ranks saved.");
        return summary;
    }

    /// <summary>Uploads items.jsonl and aa.jsonl from a results folder (what an earlier run saved).</summary>
    public Task<UploadSummary> UploadFolderAsync(string folder, CancellationToken ct = default)
        => UploadAsync(ReadJsonLines(Path.Combine(folder, "items.jsonl")), ReadJsonLines(Path.Combine(folder, "aa.jsonl")), ct);

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
                _state.Rejected.Add(id);
                _log?.Invoke($"The website rejected item {id}; it will not be sent again.");
                return new ItemResult(0, 0, 0, 1);
            }
            int half = batch.Count / 2;
            var a = await SendItemsAsync(batch.Take(half).ToList(), ct);
            var b = await SendItemsAsync(batch.Skip(half).ToList(), ct);
            return new ItemResult(a.Sent + b.Sent, a.Added + b.Added, a.Queued + b.Queued, a.Rejected + b.Rejected);
        }

        var doc = await ReadJson(response, ct);
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
            var retryAfter = response.Headers.RetryAfter?.Delta;
            string detail = await SafeBody(response);
            response.Dispose();

            if (code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new UploadAuthException("The website did not accept the token. Check it at your account page and paste it again.");
            if (code == (HttpStatusCode)429 && attempt < MaxRateLimitRetries)
            {
                await Task.Delay(retryAfter ?? RateLimitWait, ct);
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
        public HashSet<long> Rejected { get; set; } = new();
        public Dictionary<long, string> Aa { get; set; } = new();

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

        public void Save(string path)
        {
            try
            {
                var temp = path + ".tmp";
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
