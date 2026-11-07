using System.Collections.Concurrent;

namespace Realtime.Connections;

/// <summary>
/// Group service (plan §5.4 component 2): partition-scoped group records +
/// membership bookkeeping. Group records are keyed by the PUBLISHER's
/// partition (the caller's verified client_id — plan R4); member keys are
/// the physical user identities <c>{client_id}|{sub}</c> (the exact
/// <c>UserIdentifier</c> <see cref="SubUserIdProvider"/> produces), so the
/// connect-time placement and the runtime member ops operate on the same
/// key space. Membership is stored: the hub re-applies it on every connect.
/// In-memory, single node (per plan).
/// </summary>
public interface IGroupRegistry
{
    bool Create(string partition, string groupId);

    bool Exists(string partition, string groupId);

    /// <summary>Every partitioned group the member identity belongs to — hub joins these at connect.</summary>
    IReadOnlyList<(string Partition, string Group)> MembershipsFor(string memberKey);

    bool IsMember(string partition, string groupId, string memberKey);

    void AddMember(string partition, string groupId, string memberKey);

    bool RemoveMember(string partition, string groupId, string memberKey);
}

public sealed class InMemoryGroupRegistry : IGroupRegistry
{
    // U+001F is the key separator — no realistic client_id contains it.
    private const char KeySep = '\u001F';

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _membersByGroup = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _groupsByMember = new(StringComparer.Ordinal);

    private static string GroupKey(string partition, string groupId) => $"{partition}{KeySep}{groupId}";

    public bool Create(string partition, string groupId)
    {
        if (string.IsNullOrEmpty(partition) || string.IsNullOrEmpty(groupId)) return false;
        var key = GroupKey(partition, groupId);
        _membersByGroup.GetOrAdd(key, _ => new(StringComparer.Ordinal));
        return true;
    }

    public bool Exists(string partition, string groupId) =>
        _membersByGroup.ContainsKey(GroupKey(partition, groupId));

    public bool IsMember(string partition, string groupId, string memberKey) =>
        _membersByGroup.TryGetValue(GroupKey(partition, groupId), out var members) && members.ContainsKey(memberKey);

    public IReadOnlyList<(string Partition, string Group)> MembershipsFor(string memberKey)
    {
        if (!_groupsByMember.TryGetValue(memberKey, out var set)) return [];
        var result = new List<(string, string)>(set.Count);
        foreach (var key in set.Keys)
        {
            var sep = key.IndexOf(KeySep);
            result.Add((key[..sep], key[(sep + 1)..]));
        }
        return result;
    }

    public void AddMember(string partition, string groupId, string memberKey)
    {
        var groupKey = GroupKey(partition, groupId);
        _membersByGroup.GetOrAdd(groupKey, _ => new(StringComparer.Ordinal)).TryAdd(memberKey, 0);
        _groupsByMember.GetOrAdd(memberKey, _ => new(StringComparer.Ordinal)).TryAdd(groupKey, 0);
    }

    public bool RemoveMember(string partition, string groupId, string memberKey)
    {
        if (!_membersByGroup.TryGetValue(GroupKey(partition, groupId), out var members))
            return false;
        var removed = members.TryRemove(memberKey, out _);
        _groupsByMember.TryGetValue(memberKey, out var groups);
        groups?.TryRemove(GroupKey(partition, groupId), out _);
        return removed;
    }
}
