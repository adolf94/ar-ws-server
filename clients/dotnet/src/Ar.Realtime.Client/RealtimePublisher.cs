using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;

namespace Ar.Realtime.Client;

/// <summary>implementation over <see cref="HttpClient"/> — CONTRACT v1.</summary>
public sealed class RealtimePublisher : IRealtimePublisher, IAsyncDisposable
{
    /// <summary>owner requirement: the service is behind Cloudflare; every
    /// lane must present an honest client identity, never SDK defaults.</summary>
    public const string DefaultUserAgent = "realtime-client-dotnet";

    private readonly HttpClient _http;
    private readonly IAccessTokenProvider _tokens;
    private readonly string _base;         // e.g. https://realtime.example (no trailing /)
    private readonly bool _disposeHttp;

    public RealtimePublisher(RealtimePublisherOptions options, HttpClient? http = null)
    {
        var opts = options ?? throw new ArgumentNullException(nameof(options));
        _base = opts.BaseUrl.TrimEnd('/');
        _tokens = opts.AccessTokenProvider ?? throw new ArgumentNullException(nameof(options));
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _disposeHttp = http is null;

        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"{DefaultUserAgent}/0.1.0 ({opts.UserAgentSuffix ?? "ar-auth publisher"})");
        }
    }

    // ── send lanes (the reasoning stream addresses) ──────────────────────────
    public async Task<PublishResult> PublishAsync(string group, JsonObject data, CancellationToken ct = default)
    {
        data = EnsureEnvelope(data);
        var body = await PostJsonAsync("/api/publish", new JsonObject
        {
            ["group"] = group,
            ["data"] = data,
        }, ct);
        return new PublishResult(body["ok"]!.GetValue<bool>(), body["group"]!.GetValue<string>(), body["partition"]!.GetValue<string>());
    }

    public async Task<BroadcastResult> BroadcastAsync(IEnumerable<string> groups, JsonObject data, CancellationToken ct = default)
    {
        data = EnsureEnvelope(data);
        var list = groups?.Where(g => !string.IsNullOrWhiteSpace(g)).ToArray().Distinct().ToArray() ?? [];
        if (list.Length == 0)
            throw new RealtimeValidationException("groups needs at least one group name");

        var body = await PostJsonAsync("/api/broadcast", new JsonObject
        {
            ["groups"] = new JsonArray(list.Select(g => (JsonNode?)JsonValue.Create(g)).ToArray()),
            ["data"] = data,
        }, ct);
        return new BroadcastResult(
            body["ok"]!.GetValue<bool>(),
            body["partition"]!.GetValue<string>(),
            AsStrings(body["groups_delivered"]),
            AsStrings(body["unknown_groups"]));
    }

    public async Task<SendResult> SendToUserAsync(string userSub, JsonObject data, CancellationToken ct = default)
    {
        data = EnsureEnvelope(data);
        var body = await PostJsonAsync($"/api/users/{Uri.EscapeDataString(userSub)}/send", new JsonObject { ["data"] = data }, ct);
        return new SendResult(body["ok"]!.GetValue<bool>(), body["user_id"]!.GetValue<string>(), null, null);
    }

    public async Task<SendResult> SendToConnectionAsync(string connectionId, JsonObject data, CancellationToken ct = default)
    {
        data = EnsureEnvelope(data);
        var body = await PostJsonAsync($"/api/connections/{Uri.EscapeDataString(connectionId)}/send", new JsonObject { ["data"] = data }, ct);
        return new SendResult(body["ok"]!.GetValue<bool>(), null, body["connection_id"]!.GetValue<string>(), body["connection_partition"]!.GetValue<string>());
    }

    public async Task<DisconnectResult> DisconnectAsync(string connectionId, CancellationToken ct = default)
    {
        var body = await PostJsonAsync($"/api/connections/{Uri.EscapeDataString(connectionId)}/disconnect", null, ct);
        return new DisconnectResult(
            body["ok"]!.GetValue<bool>(), body["connection_id"]!.GetValue<string>(),
            body["connection_partition"]!.GetValue<string>(), body["aborted"]!.GetValue<bool>());
    }

    // ── group ops ────────────────────────────────────────────────────────────
    public async Task<GroupCreateResult> CreateGroupAsync(string name,
        bool? history = null, int? historySize = null, int? historyTtlSeconds = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new RealtimeValidationException("name is required");
        var payload = new JsonObject
        {
            ["name"] = name,
            ["history"] = history,
            ["history_size"] = historySize,
            ["history_ttl_seconds"] = historyTtlSeconds,
        };
        // JsonNode: null members vanish on serialize → server sees omission
        var body = await PostJsonAsync("/api/groups", payload, ct);
        return new GroupCreateResult(
            body["ok"]!.GetValue<bool>(), body["created"]!.GetValue<bool>(),
            body["group"]!.GetValue<string>(), body["partition"]!.GetValue<string>(),
            body["history_enabled"]!.GetValue<bool>());
    }

    public async Task<MemberAddResult> AddMemberAsync(string group, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new RealtimeValidationException("user_id is required");
        var body = await PostJsonAsync($"/api/groups/{Uri.EscapeDataString(group)}/members",
            new JsonObject { ["user_id"] = userId }, ct);
        return new MemberAddResult(
            body["ok"]!.GetValue<bool>(), body["group"]!.GetValue<string>(),
            body["group_partition"]!.GetValue<string>(),
            body["member"]!["user_id"]!.GetValue<string>(), body["member"]!["client_id"]!.GetValue<string>(),
            body["connections_joined"]!.GetValue<int>());
    }

    public async Task<MemberRemoveResult> RemoveMemberAsync(string group, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new RealtimeValidationException("user_id is required");
        var body = await DeleteJsonAsync($"/api/groups/{Uri.EscapeDataString(group)}/members?user_id={Uri.EscapeDataString(userId)}", ct);
        return new MemberRemoveResult(
            body["ok"]!.GetValue<bool>(), body["group"]!.GetValue<string>(),
            body["group_partition"]!.GetValue<string>(),
            body["member"]!["user_id"]!.GetValue<string>(), body["member"]!["client_id"]!.GetValue<string>(),
            body["removed"]!.GetValue<bool>());
    }

    public async Task<IReadOnlyList<GroupRow>> ListGroupsAsync(CancellationToken ct = default)
    {
        var body = await GetJsonAsync("/api/groups", ct);
        return body["groups"]!.AsArray().Select(row => new GroupRow(
            row!["group"]!.GetValue<string>(), row["members"]!.GetValue<int>(), row["online"]!.GetValue<int>())).ToArray();
    }

    public async Task<IReadOnlyList<PresenceRow>> PresenceAsync(string group, CancellationToken ct = default)
    {
        var body = await GetJsonAsync($"/api/groups/{Uri.EscapeDataString(group)}/presence", ct);
        return body["presence"]!.AsArray().Select(row => new PresenceRow(
            row!["user_id"]!.GetValue<string>(), row["client_id"]!.GetValue<string>(),
            row["online"]!.GetValue<bool>(), row["connections"]!.GetValue<int>())).ToArray();
    }

    public async Task<HistoryResult> HistoryAsync(string group, int? limit = null, CancellationToken ct = default)
    {
        var path = $"/api/groups/{Uri.EscapeDataString(group)}/history" + (limit is null ? "" : $"?limit={limit}");
        var body = await GetJsonAsync(path, ct);
        return new HistoryResult(
            body["ok"]!.GetValue<bool>(), body["group"]!.GetValue<string>(),
            body["group_partition"]!.GetValue<string>(), body["history_enabled"]!.GetValue<bool>(),
            body["total"]!.GetValue<int>(),
            body["history"]!.AsArray().Where(n => n is not null).Cast<JsonNode>().ToArray());
    }

    public async Task<HistoryClearResult> ClearHistoryAsync(string group, CancellationToken ct = default)
    {
        var body = await DeleteJsonAsync($"/api/groups/{Uri.EscapeDataString(group)}/history", ct);
        return new HistoryClearResult(
            body["ok"]!.GetValue<bool>(), body["group"]!.GetValue<string>(),
            body["group_partition"]!.GetValue<string>(), body["cleared"]!.GetValue<int>());
    }

    // ── plumbing ─────────────────────────────────────────────────────────────
    /// <summary>
    /// CONTRACT §3: the event's IDENTITY lives in data.type — from/sent_at
    /// are server-stamped, so the only client-side duty is the discriminator.
    /// </summary>
    private static JsonObject EnsureEnvelope(JsonObject data)
    {
        if (data is null) throw new RealtimeValidationException("data carries the event (docs/CONTRACT.md §3)");
        if (data["type"]?.GetValueKind() is not JsonValueKind.String)
            throw new RealtimeValidationException("data.type is required — it is the event-identity wire convention");
        return data;
    }

    private Task<JsonObject> PostJsonAsync(string path, JsonObject? payload, CancellationToken ct)
        => SendAsync(HttpMethod.Post, path, payload, ct);

    private Task<JsonObject> GetJsonAsync(string path, CancellationToken ct)
        => SendAsync(HttpMethod.Get, path, null, ct);

    private Task<JsonObject> DeleteJsonAsync(string path, CancellationToken ct)
        => SendAsync(HttpMethod.Delete, path, null, ct);

    private async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonObject? payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, _base + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetAsync(ct));
        if (payload is not null)
        {
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        }
        using var response = await _http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode) throw MapFailure(response.StatusCode, raw, response);
        if (string.IsNullOrWhiteSpace(raw)) throw new RealtimeApiException((int)response.StatusCode, "empty_body", "expected a JSON envelope");
        if (JsonNode.Parse(raw) is not JsonObject body)
            throw new RealtimeApiException((int)response.StatusCode, "body_invalid", raw);
        return body;
    }

    private static RealtimeApiException MapFailure(
        System.Net.HttpStatusCode status, string raw, HttpResponseMessage response)
    {
        string errorCode = status.ToString();
        string? detail = raw;
        try
        {
            if (JsonNode.Parse(raw) is JsonObject body)
            {
                errorCode = body["error"]?.GetValue<string>() ?? errorCode;
                detail = body["detail"]?.GetValue<string>();
            }
        }
        catch (JsonException) { /* not JSON: keep raw text in detail */ }

        long? retryAfter = null;
        if ((int)status == 429 && response.Headers.RetryAfter?.Delta is { } delta)
        {
            var seconds = (long)delta.TotalSeconds;
            if (seconds > 0) retryAfter = seconds;
        }
        else if ((int)status == 429 && response.Headers.RetryAfter?.Date is { } date)
        {
            retryAfter = Math.Max(1, (long)(date - DateTimeOffset.UtcNow).TotalSeconds);
        }
        return new RealtimeApiException((int)status, errorCode, detail, retryAfter);
    }

    private static string[] AsStrings(JsonNode? node)
        => node?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? [];

    public ValueTask DisposeAsync()
    {
        if (_disposeHttp) _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
