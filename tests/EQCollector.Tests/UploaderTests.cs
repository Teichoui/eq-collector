using System.Net;
using System.Text;
using System.Text.Json;
using EQCollector.Core;
using Xunit;

namespace EQCollector.Tests;

public class UploaderTests
{
    /// <summary>A stand-in for the website: records every request and answers from a function.</summary>
    private sealed class FakeSite : HttpMessageHandler
    {
        public readonly List<(string Path, string Body, string? Auth)> Requests = new();
        public Func<string, JsonElement, HttpResponseMessage>? Answer;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            string path = request.RequestUri!.AbsolutePath;
            Requests.Add((path, body, request.Headers.Authorization?.ToString()));
            var json = body.Length == 0 ? JsonDocument.Parse("{}").RootElement : JsonDocument.Parse(body).RootElement;
            return Answer!(path, json);
        }

        public static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "eqc-test-" + Guid.NewGuid().ToString("N"));

    private static IReadOnlyDictionary<string, object?> Item(long id, string name = "Thing", string? madeBy = null)
    {
        var d = new Dictionary<string, object?> { ["id"] = id, ["name"] = name, ["icon"] = 1 };
        if (madeBy != null) d["madeby"] = madeBy;
        return d;
    }

    private static IReadOnlyDictionary<string, object?> Aa(long rankId, string? title = "Rank") =>
        new Dictionary<string, object?> { ["rank_id"] = rankId, ["aa_id"] = 7, ["rank"] = 1, ["title"] = title };

    private static readonly IReadOnlyCollection<IReadOnlyDictionary<string, object?>> NoAa = new List<IReadOnlyDictionary<string, object?>>();

    private static CollectorUploader Make(FakeSite site, string dir) =>
        new("https://example.test", "tok123", dir, handler: site) { RateLimitWait = TimeSpan.Zero };

    // ---- the address ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("norrathcodex.com", "https://norrathcodex.com/")]
    [InlineData("https://norrathcodex.com/some/path?x=1", "https://norrathcodex.com/")]
    [InlineData("http://192.168.1.10:30090", "http://192.168.1.10:30090/")]
    [InlineData("http://localhost:8000/", "http://localhost:8000/")]
    public void Addresses_that_are_safe_are_accepted(string input, string expected) =>
        Assert.Equal(expected, CollectorUploader.NormalizeServer(input).ToString());

    [Theory]
    [InlineData("http://norrathcodex.com")]
    [InlineData("http://8.8.8.8")]
    [InlineData("http://collector.lan")]
    [InlineData("http://printer.local")]
    [InlineData("ftp://norrathcodex.com")]
    [InlineData("")]
    [InlineData("not a url at all")]
    public void The_token_is_never_sent_in_the_clear_over_the_internet(string input) =>
        Assert.Throws<ArgumentException>(() => CollectorUploader.NormalizeServer(input));

    // ---- items ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_the_items_the_site_lacks_are_sent_and_nothing_is_sent_twice()
    {
        var dir = TempDir();
        try
        {
            var site = new FakeSite
            {
                Answer = (path, json) => path switch
                {
                    "/api/collector/check" => FakeSite.Ok("{\"missing_items\":[2,3]}"),
                    "/api/collector/upload" => FakeSite.Ok("{\"items_added\":2,\"items_queued\":0,\"items_skipped\":0,\"aa_saved\":0,\"aa_skipped\":0}"),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                },
            };
            using var up = Make(site, dir);

            var first = await up.UploadAsync(new[] { Item(1), Item(2), Item(3, madeBy: "Somebody") }, NoAa);
            Assert.Equal(3, first.ItemsChecked);
            Assert.Equal(2, first.ItemsSent);
            Assert.Equal(2, first.ItemsAdded);

            var upload = site.Requests.Single(r => r.Path == "/api/collector/upload");
            Assert.Equal("Bearer tok123", upload.Auth);
            Assert.DoesNotContain("Somebody", upload.Body);              // a crafter's name is a player's name
            Assert.DoesNotContain("\"id\":1,", upload.Body);              // item 1 is already on the site

            int before = site.Requests.Count;
            var second = await up.UploadAsync(new[] { Item(1), Item(2), Item(3) }, NoAa);
            Assert.Equal(0, second.ItemsChecked);
            Assert.Equal(before, site.Requests.Count);                  // not even asked again

            CollectorUploader.ForgetSharedStates();                       // as if the program had been restarted
            using var again = Make(site, dir);                           // a new run remembers too
            await again.UploadAsync(new[] { Item(1), Item(2), Item(3) }, NoAa);
            Assert.Equal(before, site.Requests.Count);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_bad_item_is_isolated_and_not_retried_while_the_rest_go_through()
    {
        var dir = TempDir();
        try
        {
            var site = new FakeSite
            {
                Answer = (path, json) =>
                {
                    if (path == "/api/collector/check") return FakeSite.Ok("{\"missing_items\":[1,2,3,4]}");
                    bool hasBad = json.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("id").GetInt64() == 3);
                    return hasBad
                        ? new HttpResponseMessage((HttpStatusCode)422) { Content = new StringContent("{\"message\":\"bad\"}") }
                        : FakeSite.Ok($"{{\"items_added\":{json.GetProperty("items").GetArrayLength()},\"items_queued\":0}}");
                },
            };
            using var up = Make(site, dir);

            var r = await up.UploadAsync(new[] { Item(1), Item(2), Item(3), Item(4) }, NoAa);
            Assert.Equal(1, r.Rejected);
            Assert.Equal(3, r.ItemsAdded);

            int before = site.Requests.Count;
            await up.UploadAsync(new[] { Item(3) }, NoAa);
            Assert.Equal(before, site.Requests.Count);                  // the bad one is not sent again
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_refused_token_stops_with_an_auth_error_and_nothing_is_remembered()
    {
        var dir = TempDir();
        try
        {
            var site = new FakeSite { Answer = (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized) };
            using var up = Make(site, dir);
            await Assert.ThrowsAsync<UploadAuthException>(() => up.UploadAsync(new[] { Item(1) }, NoAa));
            Assert.Equal(0, up.KnownItems);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task An_unreachable_site_is_reported_as_unavailable_and_the_items_stay_unsent()
    {
        var dir = TempDir();
        try
        {
            var site = new FakeSite { Answer = (_, _) => throw new HttpRequestException("no route") };
            using var up = Make(site, dir);
            await Assert.ThrowsAsync<UploadUnavailableException>(() => up.UploadAsync(new[] { Item(1) }, NoAa));
            Assert.Equal(0, up.KnownItems);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Too_many_requests_is_waited_out_and_retried()
    {
        var dir = TempDir();
        try
        {
            int calls = 0;
            var site = new FakeSite
            {
                Answer = (path, _) => ++calls == 1 ? new HttpResponseMessage((HttpStatusCode)429) : FakeSite.Ok("{\"missing_items\":[]}"),
            };
            using var up = Make(site, dir);
            var r = await up.UploadAsync(new[] { Item(1) }, NoAa);
            Assert.Equal(1, r.ItemsChecked);
            Assert.Equal(2, calls);
            Assert.Equal(1, up.KnownItems);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ---- AA ranks ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AA_ranks_are_sent_once_and_again_only_when_they_change()
    {
        var dir = TempDir();
        try
        {
            var site = new FakeSite { Answer = (_, json) => FakeSite.Ok($"{{\"aa_saved\":{json.GetProperty("aa").GetArrayLength()}}}") };
            using var up = Make(site, dir);

            var first = await up.UploadAsync(new List<IReadOnlyDictionary<string, object?>>(), new[] { Aa(1, null), Aa(2) });
            Assert.Equal(2, first.AaSent);

            var same = await up.UploadAsync(new List<IReadOnlyDictionary<string, object?>>(), new[] { Aa(1, null), Aa(2) });
            Assert.Equal(0, same.AaSent);

            var changed = await up.UploadAsync(new List<IReadOnlyDictionary<string, object?>>(), new[] { Aa(1, "Now with a title"), Aa(2) });
            Assert.Equal(1, changed.AaSent);                            // only the one whose text changed
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Saved_files_can_be_uploaded_from_a_folder()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "items.jsonl"), "{\"id\":10,\"name\":\"A\"}\n{ damaged\n{\"id\":11,\"name\":\"B\"}\n");
            File.WriteAllText(Path.Combine(dir, "aa.jsonl"), "{\"rank_id\":5,\"aa_id\":1,\"rank\":1,\"title\":\"T\"}\n");
            var site = new FakeSite
            {
                Answer = (path, json) => path == "/api/collector/check"
                    ? FakeSite.Ok("{\"missing_items\":[10,11]}")
                    : FakeSite.Ok("{\"items_added\":2,\"aa_saved\":1}"),
            };
            using var up = Make(site, dir);
            var r = await up.UploadFolderAsync(dir);
            Assert.Equal(2, r.ItemsChecked);                            // the damaged line is skipped
            Assert.Equal(2, r.ItemsAdded);
            Assert.Equal(1, r.AaSaved);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- the background loop ----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_background_upload_keeps_items_queued_while_the_site_is_down_and_sends_them_when_it_returns()
    {
        var dir = TempDir();
        try
        {
            bool down = true;
            var site = new FakeSite
            {
                Answer = (path, json) =>
                {
                    if (down) throw new HttpRequestException("down");
                    return path == "/api/collector/check" ? FakeSite.Ok("{\"missing_items\":[1]}") : FakeSite.Ok("{\"items_added\":1}");
                },
            };
            using var uploader = Make(site, dir);
            var batches = new Queue<UploadBatch>();
            batches.Enqueue(new UploadBatch(new() { Item(1) }, null));
            var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var auto = new AutoUploader(uploader, () => batches.Count > 0 ? batches.Dequeue() : new UploadBatch(new(), null), messages.Enqueue) { Interval = TimeSpan.FromMilliseconds(30) };
            auto.Start();

            await WaitFor(() => messages.Any(m => m.StartsWith("Upload will retry")));
            Assert.Equal(0, auto.ItemsAdded);

            down = false;
            await WaitFor(() => messages.Any(m => m.Contains("reachable again")), seconds: 15);   // logged after the successful round; the pause after a failure grows, so allow time
            Assert.Equal(1, auto.ItemsAdded);
            await auto.StopAsync(TimeSpan.FromSeconds(2));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_refused_token_stops_the_background_upload_with_one_message()
    {
        var dir = TempDir();
        try
        {
            var site = new FakeSite { Answer = (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized) };
            using var uploader = Make(site, dir);
            var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var auto = new AutoUploader(uploader, () => new UploadBatch(new() { Item(1) }, null), messages.Enqueue) { Interval = TimeSpan.FromMilliseconds(20) };
            auto.Start();
            await WaitFor(() => !auto.Active);
            await Task.Delay(150);
            Assert.Single(messages.Where(m => m.StartsWith("Upload stopped")));
            Assert.Single(site.Requests);                               // it did not keep knocking
            await auto.StopAsync(TimeSpan.FromSeconds(1));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_reply_of_the_wrong_shape_is_unavailable_not_a_crash()
    {
        var dir = TempDir();
        try
        {
            foreach (var reply in new[] { "[1,2]", "{}", "{\"missing_items\":[\"x\"]}", "{\"missing_items\":5}", "not json" })
            {
                var site = new FakeSite { Answer = (_, _) => FakeSite.Ok(reply) };
                using var up = Make(site, dir);
                await Assert.ThrowsAsync<UploadUnavailableException>(() => up.UploadAsync(new[] { Item(1) }, NoAa));
            }
            var wrongCount = new FakeSite { Answer = (_, _) => FakeSite.Ok("{\"aa_saved\":\"1\"}") };
            using var up2 = Make(wrongCount, dir);
            await Assert.ThrowsAsync<UploadUnavailableException>(() => up2.UploadAsync(new List<IReadOnlyDictionary<string, object?>>(), new[] { Aa(1) }));
            var ping = new FakeSite { Answer = (_, _) => FakeSite.Ok("[]") };
            using var up3 = Make(ping, dir);
            await Assert.ThrowsAsync<UploadUnavailableException>(() => up3.PingAsync());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Saved_files_can_be_read_while_the_capture_still_has_them_open()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "items.jsonl");
            using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);       // like the engine's open items.jsonl
            writer.Write(Encoding.UTF8.GetBytes("{\"id\":1,\"name\":\"A\"}\n"));
            writer.Flush();
            Assert.Single(CollectorUploader.ReadJsonLines(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task The_background_upload_first_sends_what_earlier_runs_saved()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "items.jsonl"), "{\"id\":42,\"name\":\"Left over\"}\n");
            var site = new FakeSite
            {
                Answer = (path, _) => path == "/api/collector/check" ? FakeSite.Ok("{\"missing_items\":[42]}") : FakeSite.Ok("{\"items_added\":1}"),
            };
            using var uploader = Make(site, dir);
            using var auto = new AutoUploader(uploader, () => new UploadBatch(new(), null), null, ct => uploader.UploadFolderAsync(dir, ct)) { Interval = TimeSpan.FromMilliseconds(20) };
            auto.Start();
            await WaitFor(() => uploader.KnownItems == 1);
            Assert.True(await auto.StopAsync(TimeSpan.FromSeconds(2)));
            Assert.Contains(site.Requests, r => r.Path == "/api/collector/upload" && r.Body.Contains("Left over"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_final_attempt_that_fails_is_reported_as_not_everything_sent()
    {
        var dir = TempDir();
        try
        {
            var site = new FakeSite { Answer = (_, _) => throw new HttpRequestException("down") };
            using var uploader = Make(site, dir);
            int calls = 0;
            using var auto = new AutoUploader(uploader, () => calls++ == 0 ? new UploadBatch(new() { Item(1) }, null) : new UploadBatch(new(), null)) { Interval = TimeSpan.FromHours(1) };
            Assert.False(await auto.StopAsync(TimeSpan.FromSeconds(2)));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Two_uploaders_on_one_folder_do_not_overwrite_each_others_memory()
    {
        var dir = TempDir();
        try
        {
            var site = new FakeSite
            {
                Answer = (path, json) => path == "/api/collector/check" ? FakeSite.Ok("{\"missing_items\":[]}") : FakeSite.Ok("{}"),
            };
            using var a = Make(site, dir);
            using var b = Make(site, dir);
            await a.UploadAsync(new[] { Item(1) }, NoAa);
            await b.UploadAsync(new[] { Item(2) }, NoAa);
            Assert.Equal(2, a.KnownItems);                                // one memory, not two stale copies
            Assert.Equal(2, b.KnownItems);
            CollectorUploader.ForgetSharedStates();
            using var c = Make(site, dir);
            Assert.Equal(2, c.KnownItems);                                // and what is on disk has both
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private static async Task WaitFor(Func<bool> condition, int seconds = 5)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out waiting");
            await Task.Delay(20);
        }
    }
}
