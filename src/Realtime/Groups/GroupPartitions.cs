using System.Security.Claims;
using Realtime.Auth;

namespace Realtime.Groups;

/// <summary>
/// Per-service group scoping (plan R4 / plan §5.4 component 2): every
/// logical group name lives inside the partition of the caller's verified
/// <c>client_id</c>. Two services publishing to "chat" never collide, and a
/// service can only ever speak into its own partition — the publisher scope
/// alone would otherwise make the whole namespace one shared room.
/// </summary>
public interface IGroupPartitioner
{
    /// <summary>
    /// The physical SignalR group key for a logical group inside a client
    /// partition. Both components are URLEncoded so a client_id containing
    /// URI characters (e.g. <c>api://…</c>) cannot forge a separator fake.
    /// </summary>
    string Physical(string? clientId, string logicalGroup);

    /// <summary>The cross-service global user-channel key (owner decision
    /// 2026-11-07): joined at connect regardless of the client's partition;
    /// SEND side lands with Phase 2 publish/un.</summary>
    string UserChannelKey(string sub);

    /// <summary>The partition a principal speaks/reads in ("server" if the token has none).</summary>
    string PartitionOf(ClaimsPrincipal principal);
}

public sealed class GroupPartitioner : IGroupPartitioner
{
    public const string FallbackPartition = "server";

    /// Reserved pseudo-partition: reaches every connection of one sub, no client boundary.
    public const string UserChannelPartition = "user_channel";

    // one '|' after a URLEncoded first component: safe joiner, debuggable
    public string Physical(string? clientId, string logicalGroup) =>
        $"{Uri.EscapeDataString(Effective(clientId))}|{Uri.EscapeDataString(logicalGroup)}";

    public string UserChannelKey(string sub) => Physical(UserChannelPartition, sub);

    public string PartitionOf(ClaimsPrincipal principal)
    {
        var clientId = Effective(principal.GetClientId());
        // a token whose client_id claims the reserved pseudo-partition is
        // boxed into the fallback partition: the global user-channel
        // namespace is only ever reachable through UserChannelKey.
        return clientId == UserChannelPartition ? FallbackPartition : clientId;
    }

    private static string Effective(string? clientId) =>
        string.IsNullOrWhiteSpace(clientId) ? FallbackPartition : clientId;
}
