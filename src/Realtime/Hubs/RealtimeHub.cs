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
    IConnectionAborts aborts,
    ILogger<RealtimeHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var idKey = Context.UserIdentifier ?? "(unknown)";
        registry.Add(idKey, Context.ConnectionId);
        aborts.Track(Context.ConnectionId, Context);   // disconnect-by-id support (§5.3)

        // ── logical connection bind (v1.1 addressing model, CONTRACT) ───
        // A tagged instance (negotiate query `?instance=<stable tag>`)
        // re-binds to the SAME server-minted UUIDv7 id on every reconnect;
        // any stale socket still bound to it is superseded. Untagged
        // sockets get a fresh per-socket id (legacy one-socket semantics).
        var instanceTag = Context.GetHttpContext()?.Request.Query["instance"].ToString() ?? "";
        var logicalId = registry.Bind(idKey, instanceTag, Context.ConnectionId);
        var superseded = registry.SocketsFor(logicalId)
            .Where(socket => socket != Context.ConnectionId)
            .Select(stale => (stale, aborted: aborts.TryAbort(stale)))
            .ToList();

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

        // ── id delivery: to the socket, not via invoke (§1: the hub has no
        // client-invocable methods) — a server-published envelope on THIS
        // connection hands over the logical id (and the connection-scoped
        // send endpoints address it from here on).
        await Clients.Client(Context.ConnectionId).SendAsync("event", new Dictionary<string, object?>
        {
            ["type"] = "connection_registered",
            ["connection_id"] = logicalId,
            ["socket_id"] = Context.ConnectionId,
            ["instance_tagged"] = instanceTag.Length > 0,
            ["superseded"] = superseded.Count(static s => s.aborted),
            ["from"] = "server",
            ["sent_at"] = DateTime.UtcNow.ToString("o"),
        });

        logger.LogInformation(
            "connected: user={User} connection={ConnectionId} socket={SocketId} tagged={Tagged} superseded={Superseded} partition={Partition} groups={Groups} user_channel={HasChannel}",
            idKey, logicalId, Context.ConnectionId, instanceTag.Length > 0, superseded.Count, partitioner.PartitionOf(principal!), memberships.Count, sub is { Length: > 0 });
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.UserIdentifier is { } user)
        {
            registry.Remove(user, Context.ConnectionId);   // per-socket presence
            registry.Unbind(user, Context.ConnectionId);   // logical bind
        }
        aborts.Untrack(Context.ConnectionId);
        logger.LogInformation("disconnected: user={User} connection={ConnectionId} error={Error}",
            Context.UserIdentifier, Context.ConnectionId, exception?.Message);
        return Task.CompletedTask;
    }
}
