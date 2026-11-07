using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Realtime.Groups;

namespace Realtime.Auth;

/// <summary>
/// Hub identity = the VERIFIED user's fan-out address. Partitioned per
/// <c>client_id</c> (plan R4 extended to the connection plane): the same
/// physical user's tokens from two services yield two disjoint
/// <c>UserIdentifier</c>s, so Clients.User and all user-keyed registry
/// state (and future user membership/sends) can never cross a service
/// boundary. The key is the same encoded composite the group partitioner
/// uses for group names — one transform, two planes.
/// </summary>
public sealed class SubUserIdProvider(IGroupPartitioner partitioner) : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
    {
        if (connection.User is not { } user) return null;
        return user.GetSubject() is { Length: > 0 } sub
            ? partitioner.Physical(partitioner.PartitionOf(user), sub)
            : null;
    }
}
