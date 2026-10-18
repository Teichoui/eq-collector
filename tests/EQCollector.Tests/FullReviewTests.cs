using System.Net;
using System.Text;
using System.Text.Json;
using EQCollector.Core;
using Xunit;

namespace EQCollector.Tests;

/// <summary>Tests for the fixes that came out of the full Codex review of the whole app.</summary>
public class FullReviewTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "eqc-test-" + Guid.NewGuid().ToString("N"));

    private sealed class OneAnswer : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _answer;
        public OneAnswer(Func<HttpResponseMessage> answer) => _answer = answer;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(_answer());
    }

    // ---- the token is only sent over https (or to this computer) -------------------------------------------------------

    [Fact]
    public void A_private_network_address_may_not_use_http_unless_explicitly_allowed()
    {
        string? before = Environment.GetEnvironmentVariable("EQC_ALLOW_INSECURE_LAN");
        try
        {
            Environment.SetEnvironmentVariable("EQC_ALLOW_INSECURE_LAN", null);
            Assert.Throws<ArgumentException>(() => CollectorUploader.NormalizeServer("http://192.168.1.10:30090"));
            Assert.Throws<ArgumentException>(() => CollectorUploader.NormalizeServer("http://10.0.0.5"));
            Assert.Equal("http://localhost:8000/", CollectorUploader.NormalizeServer("http://localhost:8000").ToString());      // this computer is always fine
            Assert.Equal("http://127.0.0.1/", CollectorUploader.NormalizeServer("http://127.0.0.1").ToString());

            Environment.SetEnvironmentVariable("EQC_ALLOW_INSECURE_LAN", "1");
            Assert.Equal("http://192.168.1.10:30090/", CollectorUploader.NormalizeServer("http://192.168.1.10:30090").ToString());
            Assert.Throws<ArgumentException>(() => CollectorUploader.NormalizeServer("http://8.8.8.8"));                        // never a public address
        }
        finally { Environment.SetEnvironmentVariable("EQC_ALLOW_INSECURE_LAN", before); }
    }

    // ---- what the website sends back is bounded ---------------------------------------------------------------------------

    [Fact]
    public async Task An_enormous_answer_is_refused_instead_of_filling_memory()
    {
        var dir = TempDir();
        try
        {
            var huge = new string('a', 9 * 1024 * 1024);
            using var up = new CollectorUploader("https://example.test", "tok", dir, handler: new OneAnswer(() =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"x\":\"" + huge + "\"}", Encoding.UTF8, "application/json") }));
            await Assert.ThrowsAsync<UploadUnavailableException>(() => up.PingAsync());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ---- local files that were edited or damaged ---------------------------------------------------------------------------

    [Fact]
    public void A_state_file_full_of_nulls_does_not_crash_the_uploader()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "upload-state.json"), "{\"Server\":\"https://example.test\",\"Items\":null,\"RejectedHashes\":null,\"Aa\":null,\"Declined\":null,\"DeclinedFor\":null,\"Version\":2}");
            using var up = new CollectorUploader("https://example.test", "tok", dir);
            Assert.Equal(0, up.KnownItems);
            Assert.Equal(0, up.KnownAa);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_saved_site_list_dated_in_the_future_is_not_trusted()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "site-manifest.json");
            var doc = "{\"server\":\"https://example.test/\",\"fetched_at\":\"2099-01-01T00:00:00+00:00\",\"manifest\":{\"item_count\":3,\"item_ranges\":\"1-3\",\"aa\":[]}}";
            File.WriteAllText(path, doc);
            Assert.Null(SiteManifest.Load(path, "https://example.test/"));

            File.WriteAllText(path, doc.Replace("2099-01-01", DateTime.UtcNow.ToString("yyyy-MM-dd")));
            Assert.NotNull(SiteManifest.Load(path, "https://example.test/"));          // a normal date is fine
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Absurd_item_ranges_do_not_overflow()
    {
        var json = "{\"item_ranges\":\"0-9223372036854775000,9223372036854775001-9223372036854775800\",\"aa\":[]}";
        var m = SiteManifest.Parse(JsonDocument.Parse(json).RootElement, DateTimeOffset.UtcNow);
        Assert.NotNull(m);
        Assert.Equal(int.MaxValue, m!.ItemCount);
        Assert.True(m.HasItem(5));
    }

    // ---- AA files ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Fallback_files_with_any_name_are_merged_and_then_removed()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "aa.jsonl"), "{\"aa_id\":1,\"rank\":1,\"title\":\"main\"}\n");
            File.WriteAllText(Path.Combine(dir, "aa.partial.jsonl"), "{\"aa_id\":2,\"rank\":1,\"title\":\"plain fallback\"}\n");
            File.WriteAllText(Path.Combine(dir, "aa.partial-20261017-120000-abc123.jsonl"), "{\"aa_id\":3,\"rank\":1,\"title\":\"unique fallback\"}\n");
            using (var engine = new CollectorEngine(dir))
            {
                Assert.Equal(3, engine.FlushAa());
            }
            var text = File.ReadAllText(Path.Combine(dir, "aa.jsonl"));
            Assert.Contains("main", text);
            Assert.Contains("plain fallback", text);
            Assert.Contains("unique fallback", text);
            Assert.Empty(Directory.GetFiles(dir, "aa.partial*.jsonl"));                  // merged, so gone
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_damaged_line_in_aa_jsonl_is_copied_aside_before_it_is_dropped()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "aa.jsonl");
            File.WriteAllText(path, "{\"aa_id\":1,\"rank\":1,\"title\":\"ok\"}\n{ this line is damaged\n");
            using (var engine = new CollectorEngine(dir))
            {
                Assert.Equal(1, engine.FlushAa());
            }
            Assert.DoesNotContain("damaged", File.ReadAllText(path));                    // the rewritten file is clean
            var backup = Assert.Single(Directory.GetFiles(dir, "aa.jsonl.damaged-*"));
            Assert.Contains("this line is damaged", File.ReadAllText(backup));          // and the original is kept
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- capture files -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_cut_off_capture_file_is_reported_and_a_complete_one_is_not()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "x.pcap");
            using (var w = new PcapWriter(path, 1))
            {
                w.Write(1.0, new byte[100]);
                w.Write(2.0, new byte[100]);
            }
            var damaged = new List<long>();
            Assert.Equal(2, PcapReader.ReadFrames(path, damaged.Add).Count());
            Assert.Empty(damaged);                                                       // a clean file says nothing

            var bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes[..^10]);                                      // cut the last frame short
            Assert.Single(PcapReader.ReadFrames(path, damaged.Add).ToList());
            Assert.Single(damaged);                                                      // and the cut is reported
        }
        finally { Directory.Delete(dir, true); }
    }
}
