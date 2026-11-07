using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using Ar.Realtime.Client;

namespace Ar.Realtime.Client.Tests;

/// <summary>
/// Publisher-surface conformance against a RUNNING server (mock lane —
/// only mock mints arbitrary client_ids/subs). Orchestrated by
/// dev/test-clients.sh dotnet; when SRV is unset the whole suite self-skips
/// so `dotnet test` stays green without a stack.
///
/// The 429 path uses its OWN client_id partition ('rate-dotnet') so no
/// ordering dependency with the other tests' budget.
/// </summary>
public class PublisherConformanceTests : IAsyncLifetime
{
    private static readonly string Srv =
        Environment.GetEnvironmentVariable("SRV") ?? "";
    private static readonly string MockAuth =
        Environment.GetEnvironmentVariable("MOCK_AUTH") ?? "http://localhost:9001";

    private string _publisherToken = "";
    private string _rateToken = "";
    private RealtimePublisher _pub = null!;
    private RealtimePublisher _ratePub = null!;
    private readonly List<RealtimePublisher> _disposables = [];

    private static bool Skip => string.IsNullOrWhiteSpace(Srv);

    public async Task InitializeAsync()
    {
        if (Skip) return;
        _publisherToken = await MintAsync("dotnet-test", "api://ws-server/app:publisher");
        _rateToken = await MintAsync("rate-dotnet", "api://ws-server/app:publisher");
        _pub = NewPublisher(_publisherToken);
        _ratePub = NewPublisher(_rateToken);
    }

    public Task DisposeAsync()
    {
        foreach (var publisher in _disposables) publisher.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return Task.CompletedTask;
    }

    private RealtimePublisher NewPublisher(string token, bool track = true)
    {
        var publisher = new RealtimePublisher(new RealtimePublisherOptions
        {
            BaseUrl = Srv,
            AccessTokenProvider = new StaticAccessTokenProvider(token),
            UserAgentSuffix = "xunit-conformance",
        });
        if (track) _disposables.Add(publisher);
        return publisher;
    }

    private static async Task<string> MintAsync(string clientId, string scope)
    {
        using var http = new HttpClient();
        var response = await http.PostAsync($"{MockAuth}/token", new StringContent(
            $$"""{"sub":"svc","scope":"{{scope}}","client_id":"{{clientId}}"}""",
            Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(body)!.AsObject()["access_token"]!.GetValue<string>()!;
    }

    private static JsonObject Envelope(string type, string text) => new()
    {
        ["type"] = type,
        ["text"] = text,
    };

    [Fact]
    public async Task PB1_Publish_returns_partition_and_group()
    {
        var result = await _pub.PublishAsync("dotnet-demo", Envelope("chat_receive", "from the dotnet client"));
        Assert.True(result.Ok);
        Assert.Equal("dotnet-demo", result.Group);
        Assert.Equal("dotnet-test", result.Partition);
    }

    [Fact]
    public async Task PB2_Broadcast_reports_unknown_groups_partition_private()
    {
        await _pub.CreateGroupAsync("dotnet-demo");   // publish does NOT create records — both must exist
        await _pub.CreateGroupAsync("dotnet-demo2");
        var result = await _pub.BroadcastAsync(["dotnet-demo", "dotnet-demo2", "ghost-group"],
            Envelope("chat_receive", "broadcast"));
        Assert.Equal(["dotnet-demo", "dotnet-demo2"], result.GroupsDelivered);
        Assert.Equal(["ghost-group"], result.UnknownGroups); // caller's partition only
        Assert.Equal("dotnet-test", result.Partition);
    }

    [Fact]
    public async Task PB3_SendLanes_map_shapes_and_errors()
    {
        var user = await _pub.SendToUserAsync("42", Envelope("notify_receive", "user send"));
        Assert.Equal("42", user.UserId);

        var ghost = await Assert.ThrowsAsync<RealtimeApiException>(
            () => _pub.SendToConnectionAsync("no-such-conn", Envelope("notify_receive", "x")));
        Assert.Equal(404, ghost.StatusCode);
        Assert.Equal("connection_not_found", ghost.ErrorCode);

        var noEnvelope = await Assert.ThrowsAsync<RealtimeValidationException>(
            () => _pub.PublishAsync("dotnet-demo", new JsonObject { ["text"] = "no type" }));
        Assert.Contains("data.type", noEnvelope.Message);
    }

    [Fact]
    public async Task PB4_GroupCreate_is_idempotent_with_history_opt_in()
    {
        var first = await _pub.CreateGroupAsync("dotnet-hist", history: true, historySize: 5);
        Assert.True(first.Created);
        Assert.True(first.HistoryEnabled);

        var second = await _pub.CreateGroupAsync("dotnet-hist");
        Assert.False(second.Created);
        Assert.True(second.HistoryEnabled); // idempotent enable survives re-post
    }

    [Fact]
    public async Task PB5_Members_listing_presence_history_clear()
    {
        var add = await _pub.AddMemberAsync("dotnet-hist", "42");
        Assert.Equal("dotnet-test", add.MemberClientId);
        Assert.Equal(0, add.ConnectionsJoined); // no live socket for that identity here
        Assert.False((await _pub.RemoveMemberAsync("dotnet-hist", "43")).Removed); // absent member

        var listing = await _pub.ListGroupsAsync();
        Assert.Contains(listing, row => row.Group == "dotnet-hist");

        var presence = await _pub.PresenceAsync("dotnet-hist");
        var row = Assert.Single(presence);
        Assert.Equal("42", row.UserId);
        Assert.False(row.Online);

        // history: opt-in ring picks up the publish from this identity
        await _pub.PublishAsync("dotnet-hist", Envelope("chat_receive", "ring entry"));
        var history = await _pub.HistoryAsync("dotnet-hist", limit: 10);
        Assert.Equal(1, history.Total);
        Assert.Equal("dotnet-hist", history.Group);

        var cleared = await _pub.ClearHistoryAsync("dotnet-hist");
        Assert.Equal(1, cleared.Cleared);
        Assert.Equal(0, (await _pub.HistoryAsync("dotnet-hist")).Total);
    }

    [Fact]
    public async Task PB6_Disconnect_unknown_maps_404()
    {
        var ghost = await Assert.ThrowsAsync<RealtimeApiException>(
            () => _pub.DisconnectAsync("no-such-conn"));
        Assert.Equal(404, ghost.StatusCode);
        Assert.Equal("connection_not_found", ghost.ErrorCode);
    }

    [Fact]
    public async Task PB7_RateLimit_maps_429_envelope_with_retry_after()
    {
        // own partition ('rate-dotnet'), ~40 slots remain — burst until tripped
        RealtimeApiException? rate = null;
        try
        {
            for (var attempt = 0; attempt < 120; attempt++)
            {
                await _ratePub.PublishAsync("ghost-group", Envelope("chat_receive", "burst"));
            }
        }
        catch (RealtimeApiException exception) when (exception.StatusCode == 429)
        {
            rate = exception;
        }
        Assert.NotNull(rate);
        Assert.Equal("rate_limited", rate!.ErrorCode);
        Assert.NotNull(rate.RetryAfterSeconds);
        Assert.True(rate.RetryAfterSeconds > 0, "Retry-After must be the window-rollover seconds");
        // the OTHER partition is untouched — one call right back must succeed
        var other = await _pub.PublishAsync("dotnet-demo", Envelope("chat_receive", "probe"));
        Assert.True(other.Ok);
    }

    [Fact]
    public async Task UA1_UserAgent_is_ours_not_sdk_default()
    {
        // local echo first probes a free port, then captures the merge
        var port = FreePort();
        var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/api/");   // must cover /api/publish
        listener.Start();
        try
        {
            var serveTask = listener.GetContextAsync();
            // dedicated publisher pointed at the echo; publish → 404 → exception,
            // while we capture the exact UA + Authorization the lane emitted
            await using var probe = new RealtimePublisher(new RealtimePublisherOptions
            {
                BaseUrl = $"http://127.0.0.1:{port}",
                AccessTokenProvider = new StaticAccessTokenProvider("echo-bearer"),
                UserAgentSuffix = "xunit-ua",
            });
            // 404 from the echo (no /api/publish route) → exception, headers captured
            await Assert.ThrowsAnyAsync<Exception>(() => probe.PublishAsync("whatever", Envelope("chat_receive", "ua")));

            var context = await serveTask.WaitAsync(TimeSpan.FromSeconds(5));
            var ua = context.Request.UserAgent;
            var bearer = context.Request.Headers["Authorization"];
            context.Response.StatusCode = 200;
            context.Response.Close();

            Assert.Contains(RealtimePublisher.DefaultUserAgent, ua);
            Assert.Contains("xunit-ua", ua);
            Assert.Equal("Bearer echo-bearer", bearer);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }
}
