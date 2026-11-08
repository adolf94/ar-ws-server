using System.Text.Json.Nodes;

namespace Ar.Realtime.Client;

/// <summary>Parity record with the python SDK's <c>CoalesceStats</c> (handover §3.2).</summary>
public sealed record CoalesceStats(
    int Offered,
    int Flushed,
    int Dropped,
    int Publishes,
    Exception? LastError);

/// <summary>Knobs — deliberately minimal (handover §3): a memory bound only.</summary>
public sealed class CoalesceOptions
{
    /// <summary>Queued-but-unflushed budget in chars; on overflow the OLDEST
    /// pending chunks are shed (freshest reasoning wins). Default 64_000, as python.</summary>
    public int MaxPendingChars { get; init; } = 64_000;
}

/// <summary>Handle of one coalesced stream to <c>user_channel|{sub}</c>.</summary>
public interface ICoalescedSendHandle : IAsyncDisposable
{
    /// <summary>Validate + enqueue. Never touches HTTP, never blocks the hot path.</summary>
    void Offer(JsonObject data);

    /// <summary>Flush the remainder and wait for the in-flight request to land.
    /// Propagates the last failed batch exactly once, then serves idempotent stats.</summary>
    Task<CoalesceStats> CompleteAsync(CancellationToken ct = default);

    /// <summary>Current counters — parity with python's stats readout.</summary>
    CoalesceStats Snapshot();
}

/// <summary>
/// Single-flight coalescing on either send lane (handover §3): at most ONE
/// publish outstanding per handle. The sequential consumer loop below IS the
/// ordering guarantee — request N+1 is never sent before N's response, so a
/// retried batch can never reorder. Chunks offered while a request is in
/// flight ride the NEXT flush; the flush fires the instant the response
/// lands, so batch size self-tunes to generation_rate × RTT with no timers.
///
/// Lanes (parity with python): <c>user</c> → user_channel|{sub} fan-out;
/// <c>connection</c> → ONE socket (per-TAB reasoning streams — each tab
/// subscribes a distinct stream). The connection lane is partition-scoped
/// server-side: unknown/foreign ids surface as the non-429 drop path.
///
/// Wire shape (CONTRACT §3.1): one request per flush carrying the whole
/// batch — <c>{ "type": streamType, "batch": [chunk…], "batch_v": 1 }</c>.
/// </summary>
public sealed class CoalescedSendHandle : ICoalescedSendHandle
{
    private const int BatchV = 1;

    private readonly RealtimePublisher _publisher;
    private readonly string _target;
    private readonly bool _connectionLane;                 // user (user_channel|sub) vs connection (ONE socket)
    private readonly int _maxPendingChars;

    private readonly object _lock = new();
    private List<JsonObject> _pending = [];        // accepted, budgeted, not yet detached
    private long _pendingChars;
    private string? _streamType;                   // envelope identity = first chunk's type

    private int _offered, _flushed, _dropped, _publishes;
    private Exception? _lastError;
    private bool _completing;                      // set by CompleteAsync: Offer() rejects after
    private bool _errorSurfaced;                   // CompleteAsync raises ONCE

    private readonly TaskCompletionSource _workerDone =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CoalescedSendHandle(RealtimePublisher publisher, bool connectionLane, string target, CoalesceOptions? options = null)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        if (string.IsNullOrWhiteSpace(target))
            throw new RealtimeValidationException(connectionLane
                ? "user_id is required for the connection lane: connection_id is required"
                : "user_id is required");
        if (((options ?? new CoalesceOptions()).MaxPendingChars) < 1)
            throw new RealtimeValidationException("MaxPendingChars must be positive");
        _target = target;
        _connectionLane = connectionLane;
        _maxPendingChars = (options ?? new CoalesceOptions()).MaxPendingChars;

        // dedicated long-running worker: a single publisher lane owns the flushes
        Task.Factory.StartNew(RunLoopAsync, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    // ── producer hot path (Offer never touches HTTP) ────────────────────────
    public void Offer(JsonObject data)
    {
        RealtimePublisher.EnsureEnvelope(data);          // CONTRACT §3, fail fast off the wire

        lock (_lock)
        {
            if (_completing)
                throw new RealtimeValidationException(
                    "coalesce handle is drained — open a new one per stream");

            _offered++;
            _pending.Add(data);
            _pendingChars += data.ToJsonString().Length;

            // budget is on PENDING only: an in-flight batch is already detached
            // and protected — shed the OLDEST pending chunks, the newest always
            // survives the burst, even the one that alone busts the budget.
            while (_pendingChars > _maxPendingChars && _pending.Count > 1)
            {
                var oldest = _pending[0];
                _pendingChars -= oldest.ToJsonString().Length;
                _pending.RemoveAt(0);
                _dropped++;
            }

            Monitor.Pulse(_lock);
        }
    }

    // ── stream end ──────────────────────────────────────────────────────────
    public async Task<CoalesceStats> CompleteAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_completing)
            {
                _completing = true;
                Monitor.Pulse(_lock);            // wake the lane if it's parked
            }
        }
        await _workerDone.Task.WaitAsync(ct).ConfigureAwait(false);

        var surfaced = _errorSurfaced;
        _errorSurfaced = true;
        var stats = Snapshot();
        if (!surfaced && stats.LastError is { } raised)
            throw raised;
        return stats;
    }

    public CoalesceStats Snapshot()
    {
        lock (_lock)
        {
            return new CoalesceStats(_offered, _flushed, _dropped, _publishes, _lastError);
        }
    }

    // ── the single publisher lane ───────────────────────────────────────────
    private async Task RunLoopAsync()
    {
        try
        {
            while (true)
            {
                List<JsonObject> batch;
                lock (_lock)
                {
                    while (_pending.Count == 0 && !_completing)
                        Monitor.Wait(_lock);
                    if (_completing && _pending.Count == 0)
                        break;                           // stream completed, all flushed
                    batch = _pending;                    // detach = budget no longer sees them
                    _pending = [];
                    _pendingChars = 0;
                }

                await FlushBatchAsync(batch).ConfigureAwait(false);
            }
        }
        catch (Exception fatal)
        {
            // the python worker rule: a dead lane must NEVER leave
            // CompleteAsync() waiting forever — record loud, resolve the waiter
            lock (_lock) _lastError =fatal;
        }
        finally
        {
            _workerDone.TrySetResult();
        }
    }

    private async Task FlushBatchAsync(List<JsonObject> batch)
    {
        var envelope = Envelope(batch);

        // Each attempt serializes the envelope FRESH: JsonObject is a node graph,
        // and SendToUserAsync parents its argument into the request body — a
        // reused envelope instance would throw "node already has a parent" on
        // the retry (python's dict payload doesn't; .NET must not, either).
        async Task Send()
        {
            var wire = (JsonObject)JsonNode.Parse(envelope.ToJsonString())!;
            if (_connectionLane)
                await _publisher.SendToConnectionAsync(_target, wire).ConfigureAwait(false);
            else
                await _publisher.SendToUserAsync(_target, wire).ConfigureAwait(false);
        }

        try
        {
            await Send().ConfigureAwait(false);
            lock (_lock) { _flushed += batch.Count; _publishes++; }
        }
        catch (RealtimeApiException rate)
            when (rate.StatusCode == 429 && rate.RetryAfterSeconds is { } seconds && seconds > 0)
        {
            // handover §3.4: honor Retry-After, re-send the SAME batch once;
            // a second 429 (or any other failure) drops it — loud on stats,
            // stream keeps going, CompleteAsync surfaces the last one.
            await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
            try
            {
                await Send().ConfigureAwait(false);
                lock (_lock) { _flushed += batch.Count; _publishes++; }
            }
            catch (Exception second)
            {
                lock (_lock) { _dropped += batch.Count; _lastError = second; }
            }
        }
        catch (Exception error)
        {
            lock (_lock) { _dropped += batch.Count; _lastError = error; }
        }
    }

    private JsonObject Envelope(List<JsonObject> batch)
    {
        string streamType;
        lock (_lock)
        {
            // CONTRACT §3 identity: the stream's type rides outside; every chunk
            // keeps its full payload inside `batch`, verbatim.
            _streamType ??= batch[0]["type"]!.GetValue<string>();
            streamType = _streamType;
        }
        return new JsonObject
        {
            ["type"] = streamType,
            ["batch"] = new JsonArray(batch.Cast<JsonNode?>().ToArray()),
            ["batch_v"] = BatchV,
        };
    }

    public async ValueTask DisposeAsync() => await CompleteAsync().ConfigureAwait(false);
}
