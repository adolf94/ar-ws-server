using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace Realtime.Auth;

/// <summary>Maps the verified <c>sub</c> claim (mapped or raw) to Context.UserIdentifier / Clients.User().</summary>
public sealed class SubUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User?.GetSubject();
}
