using Microsoft.AspNetCore.SignalR;
using Realtime.Auth;
using Realtime.Connections;

namespace Realtime.Hubs;

/// <summary>
/// The WS bus. Clients are read-only (plan §2): publications flow out via
/// Clients.User / Clients.Group / Clients.Client — clients never publish.
/// </summary>
public sealed class RealtimeHub(IConnectionRegistry registry, ILogger<RealtimeHub> logger) : Hub
{
    // ── Phase-0 spike convenience — every connection self-joins the demo
    // group so a bare publish→receive e2e can run. Phase 2 replaces this
    // with registry-driven membership (connect-time + runtime group ops).
    public const string SpikeDemoGroup = "demo";

    public override async Task OnConnectedAsync()
    {
        var user = Context.UserIdentifier ?? "(unknown)";
        registry.Add(user, Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, SpikeDemoGroup);
        logger.LogInformation("connected: user={User} connection={ConnectionId}", user, Context.ConnectionId);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.UserIdentifier is { } user) registry.Remove(user, Context.ConnectionId);
        logger.LogInformation("disconnected: user={User} connection={ConnectionId} error={Error}",
            Context.UserIdentifier, Context.ConnectionId, exception?.Message);
        return Task.CompletedTask;
    }
}
