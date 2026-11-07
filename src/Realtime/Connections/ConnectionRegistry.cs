using System.Collections.Concurrent;

namespace Realtime.Connections;

/// <summary>
/// Tracks every live connection by user (plan's connection registry — the
/// bridge between "add USER to group at runtime" and SignalR's
/// connection-keyed group ops). In-memory for now: single node per plan.
/// </summary>
public interface IConnectionRegistry
{
    void Add(string userId, string connectionId);

    void Remove(string userId, string connectionId);

    IReadOnlyCollection<string> ConnectionsFor(string userId);
}

public sealed class InMemoryConnectionRegistry : IConnectionRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _byUser = new(StringComparer.Ordinal);

    public void Add(string userId, string connectionId) =>
        _byUser.GetOrAdd(userId, _ => new(StringComparer.Ordinal)).TryAdd(connectionId, 0);

    public void Remove(string userId, string connectionId)
    {
        if (_byUser.TryGetValue(userId, out var set))
        {
            set.TryRemove(connectionId, out _);
            if (set.IsEmpty) _byUser.TryRemove(userId, out _);
        }
    }

    public IReadOnlyCollection<string> ConnectionsFor(string userId) =>
        _byUser.TryGetValue(userId, out var set) ? [.. set.Keys] : [];
}
