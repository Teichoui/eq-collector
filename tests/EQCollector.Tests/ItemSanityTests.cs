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
    [InlineData("lore", "Hidden\u0018control")]
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
    [InlineData("name", "A")]
    [InlineData("name", "Rune of 3")]
    public void Edge_values_that_are_real_are_accepted(string column, object value)
    {
        var d = Good();
        d[column] = value;
        Assert.Null(ItemSanity.Problem(d));
    }

    [Fact]
    public void Normal_text_with_tabs_and_new_lines_is_not_a_control_character_problem()
    {
        var d = Good();
        d["lore"] = "line one\nline two\tindented\r\n";
        Assert.Null(ItemSanity.Problem(d));
    }

    // ---- the uploader never sends one ---------------------------------------------------------------------------

    private sealed class RecordingSite : HttpMessageHandler
    {
        public readonly List<string> Bodies = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/api/collector/manifest") return new HttpResponseMessage(HttpStatusCode.NotFound);
            Bodies.Add(path + " " + body);
            string json = path == "/api/collector/check"
                ? "{\"missing_items\":[176009]}"
                : "{\"items_added\":1,\"items_queued\":0,\"items_skipped\":0,\"aa_saved\":0,\"aa_skipped\":0}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task A_garbled_record_in_the_saved_items_is_never_checked_or_sent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eqc-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var site = new RecordingSite();
            using var up = new CollectorUploader("https://example.test", "tok", dir, handler: site) { RateLimitWait = TimeSpan.Zero };
            var items = new List<IReadOnlyDictionary<string, object?>> { Good(), Garbled() };
            await up.UploadAsync(items, new List<IReadOnlyDictionary<string, object?>>());

            string all = string.Join("\n", site.Bodies);
            Assert.Contains("176009", all);
            Assert.DoesNotContain("1943797760", all);
            Assert.DoesNotContain("P8OA", all);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
