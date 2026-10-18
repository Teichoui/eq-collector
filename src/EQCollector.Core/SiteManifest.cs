using System.Text;
using System.Text.Json;

namespace EQCollector.Core;

/// <summary>
/// What the website already has, pulled to this PC (site-manifest.json in the results folder): every item id as ranges and
/// every AA rank id with whether the site has its text. The uploader uses it to decide locally what is new, so it only ever
/// sends that. It is a list of ids, not the item data -- a megabyte instead of a hundred.
/// </summary>
public sealed class SiteManifest
{
    private readonly (long Start, long End)[] _ranges;
    private readonly Dictionary<long, bool> _aa;

    public DateTimeOffset FetchedAt { get; }
    public int ItemCount { get; }
    public int AaCount => _aa.Count;

    private SiteManifest((long, long)[] ranges, Dictionary<long, bool> aa, int itemCount, DateTimeOffset fetchedAt)
    {
        _ranges = ranges;
        _aa = aa;
        ItemCount = itemCount;
        FetchedAt = fetchedAt;
    }

    /// <summary>Is this item id on the website?</summary>
    public bool HasItem(long id)
    {
        int lo = 0, hi = _ranges.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            var (start, end) = _ranges[mid];
            if (id < start) hi = mid - 1;
            else if (id > end) lo = mid + 1;
            else return true;
        }
        return false;
    }

    /// <summary>Is this AA rank on the website, and does it have its title text?</summary>
    public bool TryGetAa(long rankId, out bool hasText) => _aa.TryGetValue(rankId, out hasText);

    /// <summary>Reads the website's answer; null when it is not a manifest (wrong shape).</summary>
    public static SiteManifest? Parse(JsonElement doc, DateTimeOffset fetchedAt)
    {
        if (doc.ValueKind != JsonValueKind.Object
            || !doc.TryGetProperty("item_ranges", out var ranges) || ranges.ValueKind != JsonValueKind.String
            || !doc.TryGetProperty("aa", out var aa) || aa.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<(long, long)>();
        foreach (var part in (ranges.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var ends = part.Split('-');
            if (ends.Length is < 1 or > 2 || !long.TryParse(ends[0], out long a) || !long.TryParse(ends[^1], out long b) || b < a || a < 0 || list.Count >= 2_000_000) return null;
            list.Add((a, b));
        }
        list.Sort();

        var aaMap = new Dictionary<long, bool>();
        foreach (var pair in aa.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2
                || pair[0].ValueKind != JsonValueKind.Number || !pair[0].TryGetInt64(out long rid)
                || pair[1].ValueKind != JsonValueKind.Number || !pair[1].TryGetInt32(out int text)) return null;
            aaMap[rid] = text != 0;
        }

        long counted = 0;
        foreach (var (start, end) in list) counted = Math.Min(int.MaxValue, counted + Math.Min(int.MaxValue, end - start + 1));      // no overflow on absurd ranges
        int count = doc.TryGetProperty("item_count", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out int n) ? n : (int)counted;
        return new SiteManifest(list.ToArray(), aaMap, count, fetchedAt);
    }

    // ---- the copy kept on this PC --------------------------------------------------------------------------------------

    /// <summary>Writes the website's answer plus when and from where it was pulled (atomic; a failure only means pulling again).</summary>
    public static void Save(string path, string server, JsonElement doc, DateTimeOffset fetchedAt)
    {
        try
        {
            var temp = path + "." + Environment.ProcessId + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteString("server", server);
                w.WriteString("fetched_at", fetchedAt);
                w.WritePropertyName("manifest");
                doc.WriteTo(w);
                w.WriteEndObject();
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the copy on disk is a convenience
        }
    }

    /// <summary>The saved copy, if it is from this server and readable.</summary>
    public static SiteManifest? Load(string path, string server)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 32 * 1024 * 1024) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("server", out var s) || !string.Equals(s.GetString(), server, StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("fetched_at", out var f) || !f.TryGetDateTimeOffset(out var fetched)
                || !root.TryGetProperty("manifest", out var m)) return null;
            if (fetched > DateTimeOffset.UtcNow.AddMinutes(5)) return null;       // a date in the future would keep a stale list "fresh" forever
            return Parse(m, fetched);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
