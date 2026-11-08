using System.Collections.Concurrent;

namespace Realtime.Connections;

/// <summary>
/// Tracks every live connection by user (plan's connection registry — the
/// bridge between "add USER to group at runtime" and SignalR's
/// connection-keyed group ops). In-memory for now: single node per plan.
/// </summary>
/// <remarks>
/// Two layers, v1.1 addressing model (docs/CONTRACT.md):
/// 1. per-SOCKET presence — the transport id churns with every reconnect;
/// 2. LOGICAL connections — a server-minted UUIDv7 id stable per
///    (identity, instance tag) across reconnects. Endpoints address the
///    logical id; the hub binds/unbinds sockets under it at connect/disconnect.
/// Persistence is deliberately absent: a logical id survives client
/// reconnects, not server restarts — senders treat 404 as the cue to fall
/// back to the user-channel lane.
/// </remarks>
public interface IConnectionRegistry
{
    // ── per-socket presence (existing; presence counts + membership ops) ──
    void Add(string userId, string socketId);

    void Remove(string userId, string socketId);

    IReadOnlyCollection<string> ConnectionsFor(string userId);

    // ── logical connection registry (v1.1) ──────────────────────────────
    /// <summary>Binds <paramref name="socketId"/> to the logical id for
    /// <paramref name="identityKey"/> + <paramref name="instanceTag"/>,
    /// minting a UUIDv7 on first sight (no tag → per-socket id, i.e. legacy
    /// one-socket semantics). Returns the logical id.</summary>
    string Bind(string identityKey, string instanceTag, string socketId);

    /// <summary>Drops the socket from its logical id. A tagged logical id
    /// stays bound (empty) so a later reconnect re-binds to the same id;
    /// an untagged (per-socket) id is torn down with its last socket.</summary>
    void Unbind(string identityKey, string socketId);

    /// <summary>Live sockets currently bound to a logical id.</summary>
    IReadOnlyCollection<string> SocketsFor(string connectionId);

    /// <summary>The identity key (<c>{client_id}|{sub}</c>) owning a logical
    /// id — the partition check for connection-scoped endpoints.</summary>
    bool TryGetOwner(string connectionId, out string ownerKey);

    /// <summary>Bound logical connections owned by an identity key
    /// (<c>{client_id}|{sub}</c>) — the partition-scoped inventory that backs
    /// <c>GET /api/users/{sub}/connections</c>. Includes boundless persistent
    /// (tagged) ids: they are live addresses even with zero sockets.</summary>
    IReadOnlyCollection<ConnectionSnapshot> SnapshotsFor(string identityKey);

    /// <summary>EVERY bound logical connection in the process — the
    /// cross-partition operator inventory that backs <c>GET /api/connections</c>.
    /// Deliberately ignores the partition wall: CONTRACT §4 exception (owner
    /// decision 2026-11-08, second after the global unicast lane) —
    /// see the endpoint's contract notes for the information-leak caveat.</summary>
    IReadOnlyCollection<ConnectionSnapshot> Snapshots();
}

/// <summary>One logical connection in a connection inventory (v1.1 additive
/// reads, 2026-11-08): the owner identity key, the server-minted id, its
/// instance tag (null when the id is per-socket legacy semantics), and its
/// live socket count. The owner key decodes via
/// <see cref="Realtime.Groups.IGroupPartitioner"/> in the endpoints.</summary>
public sealed record ConnectionSnapshot(string OwnerKey, string ConnectionId, string? InstanceTag, bool Persistent, int Sockets);

public sealed class InMemoryConnectionRegistry : IConnectionRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _byUser = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _bySocket = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _idByInstance = new(StringComparer.Ordinal); // identity|tag → logical id
    private readonly ConcurrentDictionary<string, ConnectionEntry> _byLogical = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _logicalBySocket = new(StringComparer.Ordinal); // socket → logical id

    private sealed class ConnectionEntry(string ownerKey, string instanceKey, bool persistent)
    {
        public readonly string OwnerKey = ownerKey;
        public readonly string InstanceKey = instanceKey;
        /// <summary>true = tagged instance: the id is kept while boundless,
        /// so a later reconnect re-binds to the SAME id.</summary>
        public readonly bool Persistent = persistent;
        public readonly ConcurrentDictionary<string, byte> Sockets = new(StringComparer.Ordinal);
    }

    // ── per-socket presence ─────────────────────────────────────────────
    public void Add(string userId, string socketId)
    {
        _byUser.GetOrAdd(userId, _ => new(StringComparer.Ordinal)).TryAdd(socketId, 0);
        _bySocket[socketId] = userId;
    }

    public void Remove(string userId, string socketId)
    {
        if (_byUser.TryGetValue(userId, out var set))
        {
            set.TryRemove(socketId, out _);
            if (set.IsEmpty) _byUser.TryRemove(userId, out _);
        }
        // only when this user still owns the mapping (guard against
        // duplicate Remove calls after a reconnect took the socket over)
        if (_bySocket.TryGetValue(socketId, out var owner) && owner == userId)
            _bySocket.TryRemove(socketId, out _);
    }

    public IReadOnlyCollection<string> ConnectionsFor(string userId) =>
        _byUser.TryGetValue(userId, out var set) ? [.. set.Keys] : [];

    // ── logical connections ─────────────────────────────────────────────
    public string Bind(string identityKey, string instanceTag, string socketId)
    {
        var persistent = !string.IsNullOrWhiteSpace(instanceTag);
        var tag = persistent ? instanceTag : socketId;   // untagged → per-socket logical id (legacy semantics)

        var instanceKey = $"{identityKey}|{Uri.EscapeDataString(tag)}";
        // stacking the instance after the FIRST '|' (the partition
        // separator) is safe: GroupPartitioner only ever splits there,
        // same discipline as Physical().

        ConnectionEntry entry;
        while (true)
        {
            if (_idByInstance.TryGetValue(instanceKey, out var existing))
            {
                entry = _byLogical[existing];
                break;
            }
            var minted = Guid.CreateVersion7().ToString();   // net10: uuidv7, time-ordered
            if (_idByInstance.TryAdd(instanceKey, minted))
            {
                entry = new ConnectionEntry(identityKey, instanceKey, persistent);
                _byLogical[minted] = entry;
                break;
            }
            // lost the mint race — take the winner's id
        }

        entry.Sockets.TryAdd(socketId, 0);
        _logicalBySocket[socketId] = _idByInstance[instanceKey];
        return _idByInstance[instanceKey];
    }

    public void Unbind(string identityKey, string socketId)
    {
        if (!_logicalBySocket.TryRemove(socketId, out var logicalId)) return; // never bound / already unbound
        if (!_byLogical.TryGetValue(logicalId, out var entry) ||
            !SocketsFor(logicalId).Contains(socketId)) return;                 // not ours (defensive)

        entry.Sockets.TryRemove(socketId, out _);
        // persistent (tagged) ids stay bound with zero sockets so a later
        // reconnect re-binds to the SAME id; per-socket ids die with their
        // last socket.
        if (!entry.Persistent && entry.Sockets.IsEmpty)
        {
            if (_byLogical.TryRemove(logicalId, out var removed))
                _idByInstance.TryRemove(removed.InstanceKey, out _);
        }
    }

    public IReadOnlyCollection<string> SocketsFor(string connectionId) =>
        _byLogical.TryGetValue(connectionId, out var entry) ? [.. entry.Sockets.Keys] : [];

    public bool TryGetOwner(string connectionId, out string ownerKey)
    {
        ownerKey = string.Empty;
        return _byLogical.TryGetValue(connectionId, out var entry) && (ownerKey = entry.OwnerKey) is not null;
    }

    public IReadOnlyCollection<ConnectionSnapshot> SnapshotsFor(string identityKey) =>
        [.. _byLogical
            .Where(kv => kv.Value.OwnerKey == identityKey)
            .Select(kv => SnapshotOf(kv.Key, kv.Value))
            .OrderBy(c => c.ConnectionId, StringComparer.Ordinal)];   // uuidv7 → mint order

    public IReadOnlyCollection<ConnectionSnapshot> Snapshots() =>
        [.. _byLogical
            .Select(kv => SnapshotOf(kv.Key, kv.Value))
            .OrderBy(c => c.OwnerKey, StringComparer.Ordinal)
            .ThenBy(c => c.ConnectionId, StringComparer.Ordinal)];

    private ConnectionSnapshot SnapshotOf(string connectionId, ConnectionEntry entry)
    {
        // tagged entries store the escaped tag after the owner key's '|' —
        // same discipline Bind used to build the instance key; untagged
        // entries (per-socket ids) have no stable tag to show
        var tag = entry.Persistent
            ? Uri.UnescapeDataString(entry.InstanceKey[(entry.OwnerKey.Length + 1)..])
            : null;
        return new ConnectionSnapshot(entry.OwnerKey, connectionId, tag, entry.Persistent, entry.Sockets.Count);
    }
}
