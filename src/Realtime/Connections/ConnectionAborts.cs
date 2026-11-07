using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Realtime.Connections;

/// <summary>
/// Tracks live HubCallerContexts so the server API can abort connections
/// (`POST /api/connections/{id}/disconnect`, plan §5.3). IHubContext alone
/// cannot force-close a socket; the hub registers its context at connect.
/// In-memory, single node.
/// </summary>
public interface IConnectionAborts
{
    void Track(string connectionId, HubCallerContext context);

    void Untrack(string connectionId);

    bool TryAbort(string connectionId);
}

public sealed class InMemoryConnectionAborts : IConnectionAborts
{
    private readonly ConcurrentDictionary<string, HubCallerContext> _live = new(StringComparer.Ordinal);

    public void Track(string connectionId, HubCallerContext context) => _live[connectionId] = context;

    public void Untrack(string connectionId) => _live.TryRemove(connectionId, out _);

    public bool TryAbort(string connectionId)
    {
        if (!_live.TryGetValue(connectionId, out var context)) return false;
        context.Abort();
        return true;
    }
}
