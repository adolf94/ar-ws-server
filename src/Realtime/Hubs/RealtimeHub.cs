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
    IGroupPartitioner partitioner,
    ILogger<RealtimeHub> logger) : Hub
{
    // ── Phase-0 spike convenience — every connection self-joins the demo
    // group INSIDE ITS OWN PARTITION, so a bare publish→receive e2e runs
    // with per-service scoping already active (plan R4). Phase 1 replaces
    // the auto-join with registry-driven membership; the partition holds.
    public const string SpikeDemoGroup = "demo";

    public override async Task OnConnectedAsync()
    {
        var user = Context.UserIdentifier ?? "(unknown)";
        registry.Add(user, Context.ConnectionId);

        // ── connect-time placement (owner spec, 2026-11-07) ─────────────
        // 1. per-service identity: no group needed — SubUserIdProvider's
        //    {client_id}|{sub} UserIdentifier is the built-in user address.
        // 2. per-service groups: spike-only demo auto-join, inside the
        //    partition (Phase 1 membership replaces it).
        // 3. global user channel: user_channel|{sub} — joined regardless of
        //    the client's partition so a future send-to-user can reach the
        //    user no matter which service they connected through. Send side
        //    is Phase 2 (owner decision: plan it with publish).
        var principal = Context.User;
        var sub = principal?.GetSubject();
        var joins = new List<Task>
        {
            Groups.AddToGroupAsync(
                Context.ConnectionId,
                partitioner.Physical(partitioner.PartitionOf(principal!), SpikeDemoGroup)),
        };
        if (sub is { Length: > 0 })
            joins.Add(Groups.AddToGroupAsync(Context.ConnectionId, partitioner.UserChannelKey(sub)));
        await Task.WhenAll(joins);

        logger.LogInformation(
            "connected: user={User} connection={ConnectionId} partition={Partition} user_channel={HasChannel}",
            user, Context.ConnectionId, partitioner.PartitionOf(principal!), sub is { Length: > 0 });
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.UserIdentifier is { } user) registry.Remove(user, Context.ConnectionId);
        logger.LogInformation("disconnected: user={User} connection={ConnectionId} error={Error}",
            Context.UserIdentifier, Context.ConnectionId, exception?.Message);
        return Task.CompletedTask;
    }
}
