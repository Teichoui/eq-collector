namespace EQCollector.Core;

/// <summary>What the capture found since the last round: new items, and the AA ranks (null when nothing changed).</summary>
public sealed record UploadBatch(List<IReadOnlyDictionary<string, object?>> Items, List<IReadOnlyDictionary<string, object?>>? Aa);

/// <summary>
/// Runs <see cref="CollectorUploader"/> in the background during a live capture: every few seconds it takes what the capture
/// found and sends what the website lacks. A website or network problem never touches the capture -- the items stay queued
/// and are retried with a growing pause; a refused token stops uploading with one message.
/// </summary>
public sealed class AutoUploader : IDisposable
{
    private readonly CollectorUploader _uploader;
    private readonly Func<UploadBatch> _take;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<IReadOnlyDictionary<string, object?>> _items = new();
    private List<IReadOnlyDictionary<string, object?>>? _aa;
    private Task? _loop;
    private int _itemsAdded, _itemsQueued, _aaSaved;
    private volatile bool _stopped;

    /// <summary>How often a round runs when all is well.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(20);

    public int ItemsAdded => Volatile.Read(ref _itemsAdded);
    public int ItemsQueued => Volatile.Read(ref _itemsQueued);
    public int AaSaved => Volatile.Read(ref _aaSaved);

    /// <summary>False once the website refused the token.</summary>
    public bool Active => !_stopped;

    public AutoUploader(CollectorUploader uploader, Func<UploadBatch> take, Action<string>? log = null)
    {
        _uploader = uploader;
        _take = take;
        _log = log;
    }

    public void Start() => _loop ??= Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        var wait = Interval;
        bool failing = false;
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(wait, _cts.Token); }
            catch (OperationCanceledException) { return; }

            var outcome = await RoundAsync(_cts.Token);
            if (outcome == Outcome.AuthFailed) return;
            if (outcome == Outcome.Unavailable)
            {
                wait = TimeSpan.FromSeconds(Math.Min(300, Math.Max(wait.TotalSeconds, Interval.TotalSeconds) * 2));
                failing = true;
            }
            else
            {
                if (failing) _log?.Invoke("The website is reachable again.");
                failing = false;
                wait = Interval;
            }
        }
    }

    private enum Outcome { Ok, Unavailable, AuthFailed }

    private async Task<Outcome> RoundAsync(CancellationToken ct)
    {
        try
        {
            var batch = _take();
            _items.AddRange(batch.Items);
            if (batch.Aa != null) _aa = batch.Aa;
            if (_items.Count == 0 && _aa == null) return Outcome.Ok;

            var summary = await _uploader.UploadAsync(_items, _aa ?? new List<IReadOnlyDictionary<string, object?>>(), ct);
            Interlocked.Add(ref _itemsAdded, summary.ItemsAdded);
            Interlocked.Add(ref _itemsQueued, summary.ItemsQueued);
            Interlocked.Add(ref _aaSaved, summary.AaSaved);
            _items.Clear();
            _aa = null;
            return Outcome.Ok;
        }
        catch (UploadAuthException ex)
        {
            _stopped = true;
            _log?.Invoke("Upload stopped: " + ex.Message);
            return Outcome.AuthFailed;
        }
        catch (UploadUnavailableException ex)
        {
            _log?.Invoke("Upload will retry: " + ex.Message);
            return Outcome.Unavailable;
        }
        catch (OperationCanceledException)
        {
            return Outcome.Ok;
        }
    }

    /// <summary>Stops the loop and makes one last attempt to send what is still waiting (bounded by <paramref name="finalWait"/>).</summary>
    public async Task StopAsync(TimeSpan finalWait)
    {
        _cts.Cancel();
        try { if (_loop != null) await _loop; } catch (OperationCanceledException) { }
        if (_stopped) return;
        using var final = new CancellationTokenSource(finalWait);
        await RoundAsync(final.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();      // not disposed: the loop may still be reading its token
    }
}
