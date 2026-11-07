using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Realtime.Connections;

/// <summary>
/// Per-group history ring buffer (plan §5.4 component 4, Phase 3): opt-in
/// at group create, bounded size, optional per-entry TTL applied lazily at
/// read time. Stores the server-stamped <c>event</c> payloads verbatim —
/// from/sent_at included, exactly as fans out. Keys are the same
/// partition-scoped group records the <see cref="IGroupRegistry"/> uses
/// (caller's client_id + group id). Acts only on GROUP publishes;
/// unicast lanes are unbounded audiences and are not recorded.
/// In-memory, single node.
/// </summary>
public interface IHistoryBuffer
{
    void Enable(string groupKey, int size, int? ttlSeconds);

    bool IsEnabled(string groupKey);

    void Append(string groupKey, JsonNode data);

    /// <summary>Ascending tail of at most <paramref name="limit"/> alive
    /// (non-expired) entries, plus the total alive count.</summary>
    (IReadOnlyList<JsonNode> Items, int Total) Peek(string groupKey, int limit, DateTimeOffset nowUtc);

    int Clear(string groupKey);
}

public sealed class InMemoryHistoryBuffer : IHistoryBuffer
{
    private sealed record Entry(JsonNode Data, DateTime? ExpiresAtUtc);

    private sealed class Ring
    {
        public readonly object Lock = new();
        public int Size = 100;
        public int? TtlSeconds;
        public readonly List<Entry> Items = [];
    }

    private readonly ConcurrentDictionary<string, Ring> _rings = new(StringComparer.Ordinal);

    // generous cap so one oversized create can't eat the process
    private const int MaxSize = 10_000;
    private const int DefaultSize = 100;

    public void Enable(string groupKey, int size, int? ttlSeconds)
    {
        var ring = _rings.GetOrAdd(groupKey, _ => new Ring());
        lock (ring.Lock)
        {
            ring.Size = Math.Clamp(size, 1, MaxSize);
            ring.TtlSeconds = ttlSeconds is > 0 ? ttlSeconds : null;
        }
    }

    public bool IsEnabled(string groupKey) => _rings.ContainsKey(groupKey);

    public void Append(string groupKey, JsonNode data)
    {
        if (!_rings.TryGetValue(groupKey, out var ring)) return;
        lock (ring.Lock)
        {
            // clone: one stamped instance may already be fanned out to
            // several groups; buffers must not share mutable nodes
            var ttl = ring.TtlSeconds is { } t ? DateTimeOffset.UtcNow.AddSeconds(t).UtcDateTime : (DateTime?)null;
            ring.Items.Add(new Entry(JsonNode.Parse(data.ToJsonString()) ?? new JsonObject(), ttl));
            while (ring.Items.Count > ring.Size)
                ring.Items.RemoveAt(0);
        }
    }

    public (IReadOnlyList<JsonNode> Items, int Total) Peek(string groupKey, int limit, DateTimeOffset nowUtc)
    {
        if (limit < 1) limit = 1;
        if (limit > MaxSize) limit = MaxSize;
        if (!_rings.TryGetValue(groupKey, out var ring))
            return (Array.Empty<JsonNode>(), 0);
        lock (ring.Lock)
        {
            var alive = ring.Items
                .Where(e => e.ExpiresAtUtc is null || e.ExpiresAtUtc > nowUtc.UtcDateTime)
                .Select(e => e.Data)
                .ToList();
            var page = alive.Count > limit ? alive.Skip(alive.Count - limit).ToList() : alive;
            return (page, alive.Count);
        }
    }

    public int Clear(string groupKey)
    {
        if (!_rings.TryGetValue(groupKey, out var ring)) return 0;
        lock (ring.Lock)
        {
            var cleared = ring.Items.Count;
            ring.Items.Clear();
            return cleared;
        }
    }
}
