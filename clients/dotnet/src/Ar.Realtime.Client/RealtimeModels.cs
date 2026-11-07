using System.Text.Json.Nodes;

namespace Ar.Realtime.Client;

/// <summary>
/// Result of — POST /api/publish {ok, group, partition} (CONTRACT §4),
/// delivery itself is asynchronous (fire-and-forget to live receivers).
/// </summary>
public sealed record PublishResult(bool Ok, string Group, string Partition);

/// <summary>POST /api/broadcast — one wire pass over several groups.</summary>
public sealed record BroadcastResult(
    bool Ok,
    string Partition,
    IReadOnlyList<string> GroupsDelivered,
    IReadOnlyList<string> UnknownGroups);

/// <summary>POST /api/groups — idempotent create (201 fresh / 200 exists).</summary>
public sealed record GroupCreateResult(bool Ok, bool Created, string Group, string Partition, bool HistoryEnabled);

/// <summary>POST /api/groups/{g}/members — live join count = sockets enrolled now.</summary>
public sealed record MemberAddResult(bool Ok, string Group, string GroupPartition, string UserId, string MemberClientId, int ConnectionsJoined);

/// <summary>POST …/members DELETE — <see langword="Removed"/> = rows actually deleted.</summary>
public sealed record MemberRemoveResult(bool Ok, string Group, string GroupPartition, string UserId, string MemberClientId, bool Removed);

/// <summary>GET /api/groups — one row per group in the caller's partition.</summary>
public sealed record GroupRow(string Group, int Members, int Online);

/// <summary>GET /api/groups/{g}/presence — member identity rows + live sockets.</summary>
public sealed record PresenceRow(string UserId, string ClientId, bool Online, int Connections);

/// <summary>GET /api/groups/{g}/history — ascending tail (most recent LAST).</summary>
public sealed record HistoryResult(bool Ok, string Group, string GroupPartition, bool HistoryEnabled, int Total, IReadOnlyList<JsonNode> Items);

/// <summary>DELETE /api/groups/{g}/history — disabled buffer → Cleared = 0.</summary>
public sealed record HistoryClearResult(bool Ok, string Group, string GroupPartition, int Cleared);

/// <summary>send lane results share {ok, (user_id|connection_id|…)}.</summary>
public sealed record SendResult(bool Ok, string? UserId, string? ConnectionId, string? ConnectionPartition);

/// <summary>POST /api/connections/{id}/disconnect — Aborted=false means close raced.</summary>
public sealed record DisconnectResult(bool Ok, string ConnectionId, string ConnectionPartition, bool Aborted);
