using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using Ar.Realtime.Client;

namespace Ar.Realtime.Client.Tests;

/// <summary>
/// Coalesced send-to-user (PC gates) — parity with python's
/// tests/test_coalescing.py. Runs against local HttpListener stubs; NO live
/// stack required (never self-skips, runs under plain `dotnet test`).
///
///   PC-1 Offer() never blocks the caller (validate + enqueue only)
///   PC-2 single-flight: never more than one in-flight publish per handle
///   PC-3 observed event order matches Offer order
///   PC-4 CompleteAsync(): flush remainder, propagate last error ONCE, closed after
///   PC-5 429: honor Retry-After → same-batch retry once, then drop + continue
///   PC-6 MaxPendingChars bounds memory (drop-oldest, newest wins)
///   PC-7 wire shape: batch envelope, chunks verbatim, one request per flush
/// </summary>
public class CoalescedSendTests
{
    // ── stub plumbing (the "server" a producer would talk to) ───────────────
    private sealed class StubServer : IDisposable
    {
        public readonly List<JsonObject> Envelopes = [];
        public readonly List<int> ScriptedStatuses = [];
        public ManualResetEventSlim? UnblockFirst;      // park request #1
        public TimeSpan Delay = TimeSpan.Zero;
        public int MaxFlight;

        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly object _lock = new();
        private int _statusIndex, _flight;

        private StubServer(int port)
        {
            BaseUrl = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _ = Task.Run(() => LoopAsync(_cts.Token));
        }

        public string BaseUrl { get; }

        public static StubServer Start()
        {
            var port = FreePort();
            return new StubServer(port);
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (Exception) when (ct.IsCancellationRequested) { return; }
                _ = Task.Run(() => HandleAsync(context, ct), ct);
            }
        }

        private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
        {
            var raw = await new StreamReader(context.Request.InputStream, Encoding.UTF8)
                .ReadToEndAsync(ct).ConfigureAwait(false);
            var body = JsonNode.Parse(raw)!.AsObject();

            int status;
            lock (_lock)
            {
                status = _statusIndex < ScriptedStatuses.Count ? ScriptedStatuses[_statusIndex++] : 200;
                Envelopes.Add(body);
            }

            if (UnblockFirst is { } gate && Envelopes.Count == 1)
                await Task.Run(() => gate.Wait(TimeSpan.FromSeconds(10)), ct).ConfigureAwait(false);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct).ConfigureAwait(false);

            lock (_lock)
            {
                _flight++;
                MaxFlight = Math.Max(MaxFlight, _flight);
            }
            await Task.Delay(50, ct).ConfigureAwait(false);
            lock (_lock) _flight--;

            // error-shaped envelopes for non-2xx — CONTRACT §2, real-server shape
            var responses = status == 200
                ? "{\"ok\":true,\"user_id\":\"u\",\"group\":\"g\",\"partition\":\"p\",\"connection_id\":\"c\",\"connection_partition\":\"p\"}"
                : status switch
                {
                    404 => "{\"error\":\"connection_not_found\",\"detail\":\"status 404\"}",
                    403 => "{\"error\":\"connection_not_in_partition\",\"detail\":\"status 403\"}",
                    _ => "{\"error\":\"request_failed\",\"detail\":\"status " + status + "\"}",
                };
            var payload = Encoding.UTF8.GetBytes(responses);
            context.Response.StatusCode = status;
            if (status == 429) context.Response.Headers["Retry-After"] = "1";
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload, ct).ConfigureAwait(false);
            context.Response.Close();
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Close();
            _cts.Dispose();
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
            finally { probe.Stop(); }
        }
    }

    private static (RealtimePublisher Publisher, StubServer Stub) NewLane()
    {
        var stub = StubServer.Start();
        var publisher = new RealtimePublisher(new RealtimePublisherOptions
        {
            BaseUrl = stub.BaseUrl,
            AccessTokenProvider = new StaticAccessTokenProvider("coalesce-bearer"),
            UserAgentSuffix = "xunit-coalesce",
        });
        return (publisher, stub);
    }

    private static JsonObject Chunk(string text) => new()
    {
        ["type"] = "reclassifyThinking",
        ["text"] = text,
    };

    private static List<string> OnWire(StubServer stub)
        => stub.Envelopes.Select(e => (e["data"]!["batch"]!.AsArray()
            .Select(c => c!["text"]!.GetValue<string>()).ToArray()))
            .Aggregate(new List<string>(), (flat, ary) =>
            {
                flat.AddRange(ary);
                return flat;
            });

    /// <summary>Wire attempts collapse: a 429-honored retry sends the batch twice,
    /// DELIVERY counts it once. Dedupe consecutive identical attempts.</summary>
    private static List<string> Delivered(StubServer stub)
    {
        var batches = stub.Envelopes.Select(e => e["data"]!["batch"]!.AsArray()
            .Select(c => c!["text"]!.GetValue<string>()).ToArray()).ToArray();
        List<string>? previous = null;
        var delivered = new List<string>();
        foreach (var batch in batches)
        {
            var current = batch.ToList();
            if (previous is not null && previous.SequenceEqual(current)) continue;
            delivered.AddRange(current);
            previous = current;
        }
        return delivered;
    }

    // ── gates ───────────────────────────────────────────────────────────────
    [Fact]
    public async Task PC1_Offer_never_blocks_on_http()
    {
        var (publisher, stub) = NewLane();
        stub.Delay = TimeSpan.FromMilliseconds(600);
        await using var handle = publisher.CoalescedSendToUser("u1");
        handle.Offer(Chunk("first"));                             // starts the in-flight publish
        await Task.Delay(300);                                    // let flush #1 detach + go in flight

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var name in new[] { "a", "b", "c" }) handle.Offer(Chunk(name));
        stopwatch.Stop();

        var stats = await handle.CompleteAsync();
        Assert.Equal(4, stats.Offered);
        Assert.Equal(4, stats.Flushed);
        Assert.Equal(0, stats.Dropped);
        Assert.Equal(2, stats.Publishes);
        Assert.Null(stats.LastError);
        Assert.True(stopwatch.ElapsedMilliseconds < 50, $"offer hot path blocked: {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(["first"], OnWire(stub).Take(1).ToArray());
    }

    [Fact]
    public async Task PC2_single_flight_with_slow_server()
    {
        var (publisher, stub) = NewLane();
        stub.Delay = TimeSpan.FromMilliseconds(30);
        await using var handle = publisher.CoalescedSendToUser("u2");

        foreach (var name in new[] { "c1", "c2", "c3", "c4" })
        {
            handle.Offer(Chunk(name));
            await Task.Delay(30);
        }
        var stats = await handle.CompleteAsync();

        Assert.Equal(0, stats.Dropped);
        Assert.Null(stats.LastError);
        Assert.Equal(1, stub.MaxFlight);
    }

    [Fact]
    public async Task PC3_order_matches_offer_order()
    {
        var (publisher, stub) = NewLane();
        stub.Delay = TimeSpan.FromMilliseconds(20);
        await using var handle = publisher.CoalescedSendToUser("u3");

        for (var i = 0; i < 40; i++) handle.Offer(Chunk($"n{i:00}"));
        var stats = await handle.CompleteAsync();

        Assert.Equal(40, stats.Flushed);
        Assert.Equal(0, stats.Dropped);
        var expected = Enumerable.Range(0, 40).Select(i => $"n{i:00}").ToArray();
        Assert.Equal(expected, OnWire(stub));
    }

    [Fact]
    public async Task PC4_complete_stats_and_last_error_propagation()
    {
        var (publisher, stub) = NewLane();
        stub.ScriptedStatuses.AddRange([409, 200, 200]);
        await using var handle = publisher.CoalescedSendToUser("u4");

        handle.Offer(Chunk("d1"));
        await Task.Delay(250);                                    // flush [d1] → 409 (deterministic boundary)
        handle.Offer(Chunk("d2"));
        await Task.Delay(250);                                    // flush [d2] → 200
        handle.Offer(Chunk("succeeds"));

        var raised = await Assert.ThrowsAsync<RealtimeApiException>(() => handle.CompleteAsync());
        Assert.Equal(409, raised.StatusCode);

        var stats = await handle.CompleteAsync();                 // idempotent readout, no re-raise
        Assert.Equal(2, stats.Flushed);
        Assert.Equal(2, stats.Publishes);
        Assert.Equal(1, stats.Dropped);                           // the failed batch
        Assert.Equal(409, ((RealtimeApiException)stats.LastError!).StatusCode);
        var wirePc4 = stub.Envelopes.Select(e => e["data"]!["batch"]!.AsArray()
            .Select(c => c!["text"]!.GetValue<string>()).ToArray()).ToArray();
        Assert.Equal([["d1"], ["d2"], ["succeeds"]], wirePc4);    // failed attempt rides the wire, too

        Assert.Throws<RealtimeValidationException>(() => handle.Offer(Chunk("too late")));
    }

    [Fact]
    public async Task PC5_four29_honors_retry_then_drops_after_one_retry()
    {
        // A: 429 once on flush #1 — SAME batch retried after Retry-After, intact
        var (publisherA, stubA) = NewLane();
        stubA.ScriptedStatuses.AddRange([429, 200, 200]);
        await using var handleA = publisherA.CoalescedSendToUser("u5");
        handleA.Offer(Chunk("r1"));
        handleA.Offer(Chunk("r2"));
        await Task.Delay(250);                                    // first flush detaches (r1 alone or r1+r2 — scheduler)
        handleA.Offer(Chunk("r3"));                               // rides flush #2 (after the honored retry)
        handleA.Offer(Chunk("r4"));
        var statsA = await handleA.CompleteAsync();

        var wireA = stubA.Envelopes.Select(e => e["data"]!["batch"]!.AsArray()
            .Select(c => c!["text"]!.GetValue<string>()).ToArray()).ToArray();
        Assert.Equal(1, stubA.MaxFlight);                          // single-flight held
        Assert.Equal(wireA[0], wireA[1]);                          // 429 retry re-sends the SAME batch
        Assert.Equal(["r1", "r2", "r3", "r4"], Delivered(stubA));  // nothing lost, order kept
        Assert.Equal(2, statsA.Publishes);                         // flush #1 + flush #2
        Assert.Equal(0, statsA.Dropped);
        Assert.Null(statsA.LastError);                             // recovered 429 is not an error state

        // B: 429 twice on one batch — one retry, then drop, stream keeps going
        var (publisherB, stubB) = NewLane();
        stubB.ScriptedStatuses.AddRange([429, 429, 200, 200, 200]);
        await using var handleB = publisherB.CoalescedSendToUser("u5b");
        handleB.Offer(Chunk("g1"));
        await Task.Delay(250);                                    // flush [g1]: 429 → honored retry @+1s → 429 → dropped
        handleB.Offer(Chunk("g2"));                               // the stream continues after the drop
        handleB.Offer(Chunk("g3"));

        var raised = await Assert.ThrowsAsync<RealtimeApiException>(() => handleB.CompleteAsync());
        Assert.Equal(429, raised.StatusCode);                     // the dropped batch propagates

        var statsB = await handleB.CompleteAsync();
        Assert.Equal(3, statsB.Offered);
        Assert.Equal(1, statsB.Dropped);                          // the failed batch
        Assert.Equal(2, statsB.Flushed);                          // g2+g3 survive, in order
        Assert.Equal(1, statsB.Publishes);
        var wireB = stubB.Envelopes.Select(e => e["data"]!["batch"]!.AsArray()
            .Select(c => c!["text"]!.GetValue<string>()).ToArray()).ToArray();
        Assert.Equal(3, wireB.Length);                            // attempt + honored retry + post-drop flush
        Assert.Equal(wireB[0], wireB[1]);                         // retry re-sends the SAME batch
        Assert.Equal(["g2", "g3"], wireB[^1]);
    }

    [Fact]
    public async Task PC6_max_pending_chars_bounds_memory_drop_oldest()
    {
        var (publisher, stub) = NewLane();
        stub.UnblockFirst = new ManualResetEventSlim(false);      // flush #1 suspends on the stub
        await using var handle = publisher.CoalescedSendToUser("u6", new CoalesceOptions
        {
            MaxPendingChars = 40,
        });

        handle.Offer(Chunk("held"));                              // flush #1 suspends
        await Task.Delay(200);                                    // detach 'held' so the pile sheds only the pile
        var piled = Enumerable.Range(0, 10).Select(i => Chunk($"p{i:00}" + "yyyyyyyyyy")).ToArray();
        foreach (var chunk in piled) handle.Offer(chunk);
        await Task.Delay(200);

        stub.UnblockFirst.Set();                                  // release the hanging round-trip
        var stats = await handle.CompleteAsync();

        Assert.Equal(11, stats.Offered);
        Assert.True(stats.Dropped > 0, "budget must shed something with ~400 chars pending");
        Assert.Equal(11, stats.Flushed + stats.Dropped);
        var order = OnWire(stub);
        Assert.Equal("held", order[0]);                           // the in-flight flush is untouched
        var tail = order.Skip(1).ToArray();
        Assert.Equal(piled.TakeLast(tail.Length).Select(c => c["text"]!.GetValue<string>()).ToArray(), tail);
    }

    [Fact]
    public async Task PC7_batch_envelope_wire_shape()
    {
        var (publisher, stub) = NewLane();
        await using var handle = publisher.CoalescedSendToUser("u7");

        handle.Offer(Chunk("w1"));
        handle.Offer(Chunk("w2"));
        await Task.Delay(500);                                    // flush #1 lands: [w1, w2]
        handle.Offer(Chunk("w3"));
        await handle.CompleteAsync();

        // batch COUNT is scheduling-dependent under a fast link ([w1] vs [w1,w2]
        // as flush #1) — the CONTRACT is the wire SHAPE + the offer order
        Assert.InRange(stub.Envelopes.Count, 2, 3);
        var first = stub.Envelopes[0]["data"]!.AsObject();
        Assert.Equal("reclassifyThinking", first["type"]!.GetValue<string>());     // outer identity
        Assert.Equal(1, first["batch_v"]!.GetValue<int>());
        Assert.NotNull(first["batch"]);                                                          // chunks verbatim
        var flattened = OnWire(stub);
        Assert.Equal(["w1", "w2", "w3"], flattened);
    }

    [Fact]
    public void Offer_validates_type_eagerly_like_send_lanes()
    {
        var (publisher, stub) = NewLane();
        var handle = publisher.CoalescedSendToUser("v1");
        Assert.Throws<RealtimeValidationException>(() => handle.Offer(new JsonObject { ["text"] = "no type" }));
        Assert.Throws<RealtimeValidationException>(() => handle.Offer(null!));
        Assert.Empty(stub.Envelopes);                             // nothing ever hit the wire
    }

    // ── PC-8: the connection lane (per-tab CoT streams) ──────────────────────

    [Fact]
    public async Task PC8a_connection_lane_carries_order_and_envelope()
    {
        var (publisher, stub) = NewLane();
        await using var handle = publisher.CoalescedSendToConnection("tab-1");
        handle.Offer(Chunk("t1.1"));
        handle.Offer(Chunk("t1.2"));
        await Task.Delay(500);                                    // flush #1: [t1.1, t1.2]
        handle.Offer(Chunk("t1.3"));
        var stats = await handle.CompleteAsync();

        // flush COUNT is scheduler-dependent (t1.1 may detach before t1.2) —
        // the contract is: no drops, every chunk delivered exactly once, order kept
        Assert.InRange(stats.Publishes, 2, 3);
        Assert.Equal(3, stats.Flushed);
        Assert.Equal(0, stats.Dropped);
        Assert.Equal(["t1.1", "t1.2", "t1.3"], Delivered(stub));
        Assert.Equal("reclassifyThinking", stub.Envelopes[0]["data"]!["type"]!.GetValue<string>());
        Assert.Equal(1, stub.Envelopes[0]["data"]!["batch_v"]!.GetValue<int>());
    }

    [Fact]
    public async Task PC8b_connection_lane_dead_socket_drops_without_hang()
    {
        var (publisher, stub) = NewLane();
        // every flush before the survivor fails 404 — whether the pre-settle
        // chunks batch together or split is scheduler-dependent; the CONTRACT
        // is: 404 ⇒ same-batch-once is NOT retried (non-429), the batch drops,
        // the stream continues, CompleteAsync raises the last one exactly once
        stub.ScriptedStatuses.AddRange([404, 404, 200, 200, 200]);
        await using var handle = publisher.CoalescedSendToConnection("gone-tab");
        handle.Offer(Chunk("c1"));
        handle.Offer(Chunk("c2"));
        await Task.Delay(300);                                    // these flush(s) fail 404
        handle.Offer(Chunk("survivor-1"));                        // stream continues on this handle

        var raised = await Assert.ThrowsAsync<RealtimeApiException>(() => handle.CompleteAsync());
        Assert.Equal(404, raised.StatusCode);
        Assert.Equal("connection_not_found", raised.ErrorCode);

        var stats = await handle.CompleteAsync();
        Assert.Equal(2, stats.Dropped);                            // c1 + c2 — never leaked, always accounted
        Assert.Equal(1, stats.Flushed);                            // only the survivor batch
        Assert.Equal(1, stats.Publishes);
        var batch = stub.Envelopes[^1]["data"]!["batch"]!.AsArray();
        Assert.Equal(["survivor-1"], batch.Select(c => c!["text"]!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void PC8c_connection_lane_rejects_bad_targets_eagerly()
    {
        var (publisher, stub) = NewLane();
        Assert.Throws<RealtimeValidationException>(() => publisher.CoalescedSendToConnection(""));
        Assert.Throws<RealtimeValidationException>(() => publisher.CoalescedSendToConnection("   "));
        Assert.Empty(stub.Envelopes);
    }
}
