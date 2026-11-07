using Microsoft.AspNetCore.SignalR;
using Realtime.Auth;
using Realtime.Connections;
using Realtime.Groups;

namespace Realtime.Hubs;

/// <summary>
/// The WS bus. Clients are read-only (plan §2): publications flow out via
/// Clients.User / Clients.Group / Clients.Client — clients never publish.
/// </summary>
public sealed class RealtimeHub(
    IConnectionRegistry registry,
    IGroupRegistry groups,
    IGroupPartitioner partitioner,
    ILogger<RealtimeHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var idKey = Context.UserIdentifier ?? "(unknown)";
        registry.Add(idKey, Context.ConnectionId);

        var principal = Context.User;
        var sub = principal?.GetSubject();

        // ── connect-time placement (owner spec, 2026-11-07) ─────────────
        // 1. per-service identity: no group needed — SubUserIdProvider's
        //    {client_id}|{sub} UserIdentifier is the built-in user address.
        // 2. stored membership: every partition-scoped group the identity
        //    was added to via the members API — registry-driven, re-applied
        //    on connect and every reconnect (replaces the spike demo
        //    auto-join; plan §5.4 component 2).
        // 3. global user channel: user_channel|{sub} — joined regardless of
        //    the client's partition so the future send-to-user can reach the
        //    user no matter which service they connected through (send side
        //    is Phase 2 work; owner decision).
        var memberships = groups.MembershipsFor(idKey);
        var joins = new List<Task>(memberships.Count + 1);
        foreach (var (groupPartition, group) in memberships)
            joins.Add(Groups.AddToGroupAsync(
                Context.ConnectionId,
                partitioner.Physical(groupPartition, group)));
        if (sub is { Length: > 0 })
            joins.Add(Groups.AddToGroupAsync(Context.ConnectionId, partitioner.UserChannelKey(sub)));
        await Task.WhenAll(joins);

        logger.LogInformation(
            "connected: user={User} connection={ConnectionId} partition={Partition} groups={Groups} user_channel={HasChannel}",
            idKey, Context.ConnectionId, partitioner.PartitionOf(principal!), memberships.Count, sub is { Length: > 0 });
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.UserIdentifier is { } user) registry.Remove(user, Context.ConnectionId);
        logger.LogInformation("disconnected: user={User} connection={ConnectionId} error={Error}",
            Context.UserIdentifier, Context.ConnectionId, exception?.Message);
        return Task.CompletedTask;
    }
}
