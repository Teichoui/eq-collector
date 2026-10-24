using System.Net;
using System.Text;
using System.Text.Json;
using EQCollector.Core;
using Xunit;

namespace EQCollector.Tests;

/// <summary>A mis-decoded item record (noise from a scan that lined up on the wrong bytes) must never be kept or uploaded.</summary>
public class ItemSanityTests
{
    private static Dictionary<string, object?> Good() => new()
    {
        ["id"] = 176009L, ["name"] = "Quellious Heritage Crate", ["lore"] = "A crate", ["price"] = 0L, ["weight"] = 10L,
        ["nodestroy"] = 0L, ["noground"] = 0L, ["nonpc"] = 0L, ["nodrop"] = 1L, ["collectible"] = -51L,
    };

    /// <summary>The record that reached the review queue as "P8OA" (values as the collector saved it).</summary>
    private static Dictionary<string, object?> Garbled() => new()
    {
        ["id"] = 1943797760L, ["name"] = "P8OA", ["price"] = 4004708352L, ["weight"] = 17073L, ["procrate"] = -765348096L,
        ["procname"] = "\u0001", ["extraname"] = "\u0018", ["nodestroy"] = -46L, ["noground"] = -115L, ["nonpc"] = 1L,
    };

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void A_normal_record_is_accepted() => Assert.Null(ItemSanity.Problem(Good()));

    [Fact]
    public void The_garbled_record_that_reached_the_queue_is_refused() => Assert.NotNull(ItemSanity.Problem(Garbled()));

    [Fact]
    public void It_works_on_records_read_back_from_json()
    {
        var back = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(Good()))!;
        Assert.Null(ItemSanity.Problem(back));
        back = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(Garbled()))!;
        Assert.NotNull(ItemSanity.Problem(back));
    }

    [Theory]
    [InlineData("id", 0L)]
    [InlineData("id", -5L)]
    [InlineData("id", 5_000_001L)]
    [InlineData("price", -1L)]
    [InlineData("price", 2_147_483_648L)]
    [InlineData("nodestroy", 2L)]
    [InlineData("noground", -1L)]
    [InlineData("nonpc", 126L)]
    [InlineData("name", "")]
    [InlineData("name", "   ")]
    [InlineData("name", "16710 ")]
    [InlineData("name", "Bad\u0001Name")]
    [InlineData("name", "Next\u0085Line")]       // a C1 control character
    [InlineData("name", "Del\u007fete")]
    [InlineData("lore", "Hidden\u0018control")]
    [InlineData("lore", "C1\u009fcontrol")]
    public void Impossible_values_are_refused(string column, object value)
    {
        var d = Good();
        d[column] = value;
        Assert.NotNull(ItemSanity.Problem(d));
    }

    [Fact]
    public void A_name_that_is_too_long_is_refused() { var d = Good(); d["name"] = new string('x', ItemSanity.MaxNameLength + 1); Assert.NotNull(ItemSanity.Problem(d)); }

    [Theory]
    [InlineData("id", 1L)]
    [InlineData("id", 5_000_000L)]
    [InlineData("price", 2_147_483_647L)]
    [InlineData("nodestroy", 1L)]
    [InlineData("noground", 1L)]
    [InlineData("nonpc", 1L)]
    [InlineData("name", "A")]
    [InlineData("name", "Rune of 3")]
    [InlineData("name", "Ünïcode Sword 剣 Меч")]
    public void Edge_values_that_are_real_are_accepted(string column, object value)
    {
        var d = Good();
        d[column] = value;
        Assert.Null(ItemSanity.Problem(d));
    }

    [Fact]
    public void A_name_of_exactly_the_longest_length_is_accepted() { var d = Good(); d["name"] = new string('x', ItemSanity.MaxNameLength); Assert.Null(ItemSanity.Problem(d)); }

    [Fact]
    public void Normal_text_with_tabs_and_new_lines_is_not_a_control_character_problem()
    {
        var d = Good();
        d["lore"] = "line one\nline two\tindented\r\n";
        Assert.Null(ItemSanity.Problem(d));
    }

    // ---- the name is required, and must really be text ----------------------------------------------------------------

    [Fact]
    public void A_record_without_a_name_is_refused() { var d = Good(); d.Remove("name"); Assert.NotNull(ItemSanity.Problem(d)); }

    [Fact]
    public void A_record_with_a_null_name_is_refused() { var d = Good(); d["name"] = null; Assert.NotNull(ItemSanity.Problem(d)); }

    [Theory]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("{\"a\":1}")]
    [InlineData("[\"x\"]")]
    [InlineData("null")]
    public void A_name_that_is_not_text_is_refused(string jsonValue)
    {
        var d = Good();
        d["name"] = Json(jsonValue);
        Assert.NotNull(ItemSanity.Problem(d));
    }

    // ---- numbers must be whole numbers of a real kind ------------------------------------------------------------------

    [Fact]
    public void Boxed_integers_of_every_width_are_accepted()
    {
        foreach (object id in new object[] { (sbyte)5, (byte)5, (short)5, (ushort)5, 5, 5u, 5L, 5UL })
        {
            var d = Good();
            d["id"] = id;
            Assert.Null(ItemSanity.Problem(d));
        }
    }

    [Theory]
    [InlineData(1.0)]          // a double, even one that is a whole number, is not an integer field value
    [InlineData(1.5)]
    public void A_floating_point_id_is_refused(double value) { var d = Good(); d["id"] = value; Assert.NotNull(ItemSanity.Problem(d)); }

    [Fact]
    public void Other_non_integer_kinds_are_refused()
    {
        foreach (object bad in new object[] { 1.5f, 1.5m, true, 'a', "12", ulong.MaxValue })
        {
            var d = Good();
            d["id"] = bad;
            Assert.NotNull(ItemSanity.Problem(d));
            d = Good();
            d["price"] = bad;
            Assert.NotNull(ItemSanity.Problem(d));
        }
    }

    [Fact]
    public void A_flag_of_one_point_four_is_not_treated_as_one()
    {
        var d = Good();
        d["nodestroy"] = 1.4;
        Assert.NotNull(ItemSanity.Problem(d));
    }

    [Theory]
    [InlineData("\"5\"")]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("{\"a\":1}")]
    [InlineData("null")]
    public void Json_values_that_are_not_whole_numbers_are_refused(string jsonValue)
    {
        var d = Good();
        d["id"] = Json(jsonValue);
        Assert.NotNull(ItemSanity.Problem(d));
        d = Good();
        d["price"] = Json(jsonValue);
        if (jsonValue != "null") Assert.NotNull(ItemSanity.Problem(d));      // a null price is "not present", the one place absence is allowed
    }

    // ---- the decoder's own record ------------------------------------------------------------------------------------------

    private static ItemRecord Record(Action<object[]>? change = null)
    {
        var values = new object[ItemDecoder.Columns.Length];
        values[ItemDecoder.ColumnIndex["id"]] = 176009;
        values[ItemDecoder.ColumnIndex["name"]] = "Quellious Heritage Crate";
        change?.Invoke(values);
        return new ItemRecord(values);
    }

    [Fact]
    public void A_decoded_record_that_looks_real_is_accepted() => Assert.Null(ItemSanity.Problem(Record()));

    [Fact]
    public void A_decoded_record_with_a_garbled_id_is_refused() =>
        Assert.NotNull(ItemSanity.Problem(Record(v => v[ItemDecoder.ColumnIndex["id"]] = 1943797760L)));

    [Fact]
    public void A_decoded_record_with_a_control_character_is_refused() =>
        Assert.NotNull(ItemSanity.Problem(Record(v => v[ItemDecoder.ColumnIndex["lore"]] = "x\u0001y")));

    [Fact]
    public void A_decoded_record_with_the_wrong_number_of_fields_is_refused_not_thrown_on() =>
        Assert.Equal("wrong number of fields", ItemSanity.Problem(new ItemRecord(new object[3])));

    // ---- the uploader never sends one ----------------------------------------------------------------------------------------

    private sealed class RecordingSite : HttpMessageHandler
    {
        public readonly List<string> Bodies = new();
        public string UploadReply = "{\"items_added\":1,\"items_queued\":0,\"items_skipped\":0,\"aa_saved\":0,\"aa_skipped\":0}";
        public string MissingReply = "{\"missing_items\":[176009]}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/api/collector/manifest") return new HttpResponseMessage(HttpStatusCode.NotFound);
            Bodies.Add(path + " " + body);
            string json = path == "/api/collector/check" ? MissingReply : UploadReply;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "eqc-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_garbled_record_in_the_saved_items_file_is_never_checked_or_sent_and_is_counted()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllLines(Path.Combine(dir, "items.jsonl"), new[] { JsonSerializer.Serialize(Good()), JsonSerializer.Serialize(Garbled()) });
            var site = new RecordingSite();
            var log = new List<string>();
            using var up = new CollectorUploader("https://example.test", "tok", dir, handler: site, log: log.Add) { RateLimitWait = TimeSpan.Zero };

            var first = await up.UploadFolderAsync(dir);
            string all = string.Join("\n", site.Bodies);
            Assert.Contains("176009", all);
            Assert.DoesNotContain("1943797760", all);
            Assert.DoesNotContain("P8OA", all);
            Assert.Equal(1, first.ItemsGarbled);
            Assert.Contains(log, l => l.Contains("garbled"));

            int logged = log.Count(l => l.Contains("garbled"));
            var second = await up.UploadFolderAsync(dir);                    // read again on the next round: still counted, said only once
            Assert.Equal(1, second.ItemsGarbled);
            Assert.Equal(logged, log.Count(l => l.Contains("garbled")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task An_item_the_website_names_as_rejected_is_not_marked_handled_and_is_not_resent_unless_it_changes()
    {
        var dir = TempDir();
        try
        {
            var site = new RecordingSite
            {
                MissingReply = "{\"missing_items\":[176009]}",
                UploadReply = "{\"items_added\":0,\"items_queued\":0,\"items_skipped\":1,\"rejected_item_ids\":[176009],\"aa_saved\":0,\"aa_skipped\":0}",
            };
            using var up = new CollectorUploader("https://example.test", "tok", dir, handler: site) { RateLimitWait = TimeSpan.Zero };
            var none = new List<IReadOnlyDictionary<string, object?>>();

            var summary = await up.UploadAsync(new List<IReadOnlyDictionary<string, object?>> { Good() }, none);
            Assert.Equal(1, summary.Rejected);
            Assert.Equal(0, up.KnownItems);                                  // not recorded as handled

            int requests = site.Bodies.Count;
            await up.UploadAsync(new List<IReadOnlyDictionary<string, object?>> { Good() }, none);
            Assert.Equal(requests, site.Bodies.Count);                       // unchanged: nothing is asked or sent again

            var changed = Good();
            changed["price"] = 5L;
            await up.UploadAsync(new List<IReadOnlyDictionary<string, object?>> { changed }, none);
            Assert.True(site.Bodies.Count > requests);                       // a corrected record is tried again
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
