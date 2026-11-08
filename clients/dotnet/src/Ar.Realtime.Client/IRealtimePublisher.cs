using System.Text.Json.Nodes;

namespace Ar.Realtime.Client;

/// <summary>
/// The publisher doors of the realtime microservice (CONTRACT §4). All
/// scoping comes from the bearer's <c>client_id</c> — never from a body.
/// Every call sends the Cloudflare-safe User-Agent; the reasoning prescribes
/// batching deltas into reasonable chunks rather than one call per token.
/// </summary>
public interface IRealtimePublisher
{
    /// <summary>POST /api/publish — one envelope into one group.</summary>
    Task<PublishResult> PublishAsync(string group, JsonObject data, CancellationToken ct = default);

    /// <summary>POST /api/broadcast — one envelope into several groups; unknown groups are reported (own partition only).</summary>
    Task<BroadcastResult> BroadcastAsync(IEnumerable<string> groups, JsonObject data, CancellationToken ct = default);

    /// <summary>POST /api/users/{sub}/send — cross-service unicast over user_channel|{sub}; NOT partition-scoped by design.</summary>
    Task<SendResult> SendToUserAsync(string userSub, JsonObject data, CancellationToken ct = default);

    /// <summary>
    /// Coalesced single-flight handle over the same lane — opt-in (handover §3):
    /// one in-flight publish per handle, batches self-tune to RTT, ordering
    /// preserved. The handle owns exactly ONE user stream.
    /// </summary>
    ICoalescedSendHandle CoalescedSendToUser(string userSub, CoalesceOptions? options = null);

    /// <summary>POST /api/connections/{id}/send — partition-scoped (404 unknown / 403 foreign).</summary>
    Task<SendResult> SendToConnectionAsync(string connectionId, JsonObject data, CancellationToken ct = default);

    /// <summary>POST /api/connections/{id}/disconnect — same scoping as send; force-closes a socket.</summary>
    Task<DisconnectResult> DisconnectAsync(string connectionId, CancellationToken ct = default);

    /// <summary>POST /api/groups — creates inside the caller's partition; idempotent; history opt-in.</summary>
    Task<GroupCreateResult> CreateGroupAsync(string name,
        bool? history = null, int? historySize = null, int? historyTtlSeconds = null, CancellationToken ct = default);

    /// <summary>POST /api/groups/{g}/members — member must be a user connected through THIS client's partition.</summary>
    Task<MemberAddResult> AddMemberAsync(string group, string userId, CancellationToken ct = default);

    /// <summary>DELETE /api/groups/{g}/members?user_id=… — also evicts live sockets.</summary>
    Task<MemberRemoveResult> RemoveMemberAsync(string group, string userId, CancellationToken ct = default);

    /// <summary>GET /api/groups — a partition-private listing.</summary>
    Task<IReadOnlyList<GroupRow>> ListGroupsAsync(CancellationToken ct = default);

    /// <summary>GET /api/groups/{g}/presence — identity rows + live-socket state.</summary>
    Task<IReadOnlyList<PresenceRow>> PresenceAsync(string group, CancellationToken ct = default);

    /// <summary>GET /api/groups/{g}/history?limit= — ascending tail, most recent LAST.</summary>
    Task<HistoryResult> HistoryAsync(string group, int? limit = null, CancellationToken ct = default);

    /// <summary>DELETE /api/groups/{g}/history — clears the ring.</summary>
    Task<HistoryClearResult> ClearHistoryAsync(string group, CancellationToken ct = default);
}
