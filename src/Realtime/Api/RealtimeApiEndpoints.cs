using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Realtime.Auth;
using Realtime.Connections;
using Realtime.Groups;
using Realtime.Hubs;

namespace Realtime.Api;

/// <summary>
/// The server API (plan §5.3): app backends publish and manage groups over
/// plain HTTP with Bearer + publisher scope — real status codes, real JSON
/// errors (plan R8). The bearer's verified <c>client_id</c> is the ONE scope
/// key (plan R4, owner spec 2026-11-07): it partitions POST /api/groups,
/// the members endpoints, and POST /api/publish; group membership is closed
/// to the caller's own users. Cross-service unicast is the exception:
/// POST /api/users/{sub}/send addresses the global <c>user_channel|{sub}</c>
/// (owner spec: "send data to the user regardless of client_id"), governed
/// by the same publisher policy for now. History/presence in Phases 2–3.
/// </summary>
public static class RealtimeApiEndpoints
{
    public sealed class PublishRequest
    {
        public string? Group { get; set; }
        public JsonElement Data { get; set; }
    }

    public sealed class SendUserRequest
    {
        public JsonElement Data { get; set; }
    }

    public sealed class BroadcastRequest
    {
        public List<string>? Groups { get; set; }
        public JsonElement Data { get; set; }
    }

    public sealed class CreateGroupRequest
    {
        public string? Name { get; set; }
    }

    public sealed class GroupMemberRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("user_id")]
        public string? UserId { get; set; }

        // NOTE: no client_id in the body, ever (owner spec, 2026-11-07):
        // the Authorization bearer's client_id IS the scope — the group's
        // partition AND the member's connection partition in one.
    }

    public static IEndpointConventionBuilder MapRealtimeApi(this IEndpointRouteBuilder app) =>
        app.MapGroup("/api").MapEndpoints();

    private static RouteGroupBuilder MapEndpoints(this IEndpointRouteBuilder app)
    {
        var endpoints = app.MapGroup("");

        endpoints.MapPost("/publish",
            [Authorize(AuthenticationSchemes = AuthSchemes.Idp, Policy = "publisher")]
            async (PublishRequest request, HttpContext http, IHubContext<RealtimeHub> hubContext, IGroupPartitioner partitioner) =>
            {
                if (string.IsNullOrWhiteSpace(request.Group))
                    return Results.BadRequest(new { error = "group_required", detail = "body.group is required" });

                if (request.Data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                    return Results.BadRequest(new { error = "data_required", detail = "body.data carries the event (plan §5.3)" });

                var data = JsonNode.Parse(request.Data.GetRawText());
                if (data is null)
                    return Results.BadRequest(new { error = "data_invalid", detail = "body.data must be a JSON value" });

                var partition = partitioner.PartitionOf(http.User);

                // event convention: identity lives in data.type; provenance stamped on top
                data["from"] = (string?)http.User.GetClientId() ?? "server";
                data["sent_at"] = DateTime.UtcNow.ToString("o");

                await hubContext.Clients.Group(partitioner.Physical(partition, request.Group)).SendAsync("event", data);
                return Results.Ok(new { ok = true, group = request.Group, partition });
            });

        // ── POST /api/broadcast — one envelope into several groups (plan
        // §5.3), all names auto-scoped to the caller's partition like
        // publish. Unknown groups are skipped and REPORTED (only the
        // caller's own partition is inspected — no cross-partition info).
        endpoints.MapPost("/broadcast",
            [Authorize(AuthenticationSchemes = AuthSchemes.Idp, Policy = "publisher")]
            async (BroadcastRequest request, HttpContext http,
                IHubContext<RealtimeHub> hubContext, IGroupRegistry groups,
                IGroupPartitioner partitioner) =>
            {
                var requested = request.Groups?.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().ToList() ?? [];
                if (requested.Count == 0)
                    return Results.BadRequest(new { error = "groups_required", detail = "body.groups needs at least one group name" });

                if (request.Data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                    return Results.BadRequest(new { error = "data_required", detail = "body.data carries the event (plan §5.3)" });

                var data = JsonNode.Parse(request.Data.GetRawText());
                if (data is null)
                    return Results.BadRequest(new { error = "data_invalid", detail = "body.data must be a JSON value" });

                var partition = partitioner.PartitionOf(http.User);
                var known = requested.Where(g => groups.Exists(partition, g)).ToList();
                var unknown = requested.Except(known).ToList();

                data["from"] = (string?)http.User.GetClientId() ?? "server";
                data["sent_at"] = DateTime.UtcNow.ToString("o");

                // one wire pass, SignalR fans out to all groups listed
                await hubContext.Clients.Groups(known.Select(g => partitioner.Physical(partition, g))).SendAsync("event", data);
                return Results.Ok(new
                {
                    ok = true,
                    partition,
                    groups_delivered = known,
                    unknown_groups = unknown,
                });
            });

        // ── GET /api/groups — the caller's partition only (plan §5.3:
        // "list + subscriber counts"): persisted member count + how many
        // member identities currently hold live sockets.
        endpoints.MapGet("/groups",
            [Authorize(AuthenticationSchemes = AuthSchemes.Idp, Policy = "publisher")]
            (HttpContext http, IGroupRegistry groups, IConnectionRegistry connections, IGroupPartitioner partitioner) =>
            {
                var partition = partitioner.PartitionOf(http.User);
                var listing = groups.List(partition).Select(g => new
                {
                    group = g.Group,
                    members = g.Members.Count,
                    // distinct identities with ≥1 live socket right now
                    online = g.Members.Count(m => connections.ConnectionsFor(m).Count > 0),
                });
                return Results.Ok(new { ok = true, partition, groups = listing });
            });

        // ── POST /api/users/{sub}/send — cross-service unicast (owner spec,
        // 2026-11-07): addresses the global user_channel|{sub} joined at
        // connect, so the sub is the ONLY address needed and fan-out ignores
        // which client_id the user connected through. Same envelope rules;
        // `from` = the CALLING service's client_id. No store-and-forward.
        endpoints.MapPost("/users/{sub}/send",
            [Authorize(AuthenticationSchemes = AuthSchemes.Idp, Policy = "publisher")]
            async (string sub, SendUserRequest request, HttpContext http,
                IHubContext<RealtimeHub> hubContext, IGroupPartitioner partitioner) =>
            {
                if (string.IsNullOrWhiteSpace(sub))
                    return Results.BadRequest(new { error = "user_id_required", detail = "route user_id (sub) is required" });

                if (request.Data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                    return Results.BadRequest(new { error = "data_required", detail = "body.data carries the event (plan §5.3)" });

                var data = JsonNode.Parse(request.Data.GetRawText());
                if (data is null)
                    return Results.BadRequest(new { error = "data_invalid", detail = "body.data must be a JSON value" });

                data["from"] = (string?)http.User.GetClientId() ?? "server";
                data["sent_at"] = DateTime.UtcNow.ToString("o");

                // NOT partition-scoped with the caller's client: the channel
                // deliberately spans every service the user connects through.
                await hubContext.Clients.Group(partitioner.UserChannelKey(sub)).SendAsync("event", data);
                return Results.Ok(new { ok = true, user_id = sub });
            });

        // ── POST /api/connections/{id}/send — per-socket send, PARTITION
        // SCOPED (owner spec, 2026-11-07): the caller may only reach sockets
        // whose connection identity lives inside its own client partition —
        // 404 for unknown sockets, 403 for a foreign partition. This is the
        // scoped counterpart of the global user_channel unicast above.
        endpoints.MapPost("/connections/{id}/send",
            [Authorize(AuthenticationSchemes = AuthSchemes.Idp, Policy = "publisher")]
            async (string id, SendUserRequest request, HttpContext http,
                IHubContext<RealtimeHub> hubContext, IGroupPartitioner partitioner,
                IConnectionRegistry connections) =>
            {
                if (string.IsNullOrWhiteSpace(id))
                    return Results.BadRequest(new { error = "connection_id_required", detail = "route connection id is required" });

                if (request.Data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                    return Results.BadRequest(new { error = "data_required", detail = "body.data carries the event (plan §5.3)" });

                var data = JsonNode.Parse(request.Data.GetRawText());
                if (data is null)
                    return Results.BadRequest(new { error = "data_invalid", detail = "body.data must be a JSON value" });

                if (!connections.TryGetUser(id, out var identityKey))
                    return Results.NotFound(new { error = "connection_not_found", detail = "no live connection with this id in this process" });

                var partition = partitioner.PartitionOf(http.User);
                if (partitioner.IdentityPartition(identityKey) != partition)
                    return Results.Json(
                        new { error = "connection_not_in_partition", detail = "target connection belongs to another client_id partition" },
                        statusCode: StatusCodes.Status403Forbidden);   // scoped per owner, 2026-11-07

                data["from"] = (string?)http.User.GetClientId() ?? "server";
                data["sent_at"] = DateTime.UtcNow.ToString("o");

                await hubContext.Clients.Client(id).SendAsync("event", data);
                return Results.Ok(new { ok = true, connection_id = id, connection_partition = partition });
            });

        // ── POST /api/groups — create a group record, auto-scoped to the
        // caller's partition (owner spec: "post /groups should also auto add
        // the client_id"). Idempotent: 201 on create, 200 when it exists.
        endpoints.MapPost("/groups",
            [Authorize(AuthenticationSchemes = AuthSchemes.Idp, Policy = "publisher")]
            (CreateGroupRequest request, HttpContext http, IGroupRegistry groups, IGroupPartitioner partitioner) =>
            {
                if (string.IsNullOrWhiteSpace(request.Name))
                    return Results.BadRequest(new { error = "group_name_required", detail = "body.name is required" });

                var partition = partitioner.PartitionOf(http.User);
                var created = groups.Create(partition, request.Name);
                return created
                    ? Results.Created($"/api/groups/{Uri.EscapeDataString(request.Name)}",
                        new { ok = true, created = true, group = request.Name, partition })
                    : Results.Ok(new { ok = true, created = false, group = request.Name, partition });
            });

        // ── POST /api/groups/{g}/members — add a member (owner spec: body
        // carries the member's ACTUAL client_id + sub = which service's
        // partition the user is connected through; the endpoint auto-prepends
        // the CALLER's client_id to the group name). Applies live immediately
        // to every current socket of that identity, and persists the
        // membership so reconnects re-apply it hub-side.
        endpoints.MapPost("/groups/{group}/members",
            [Authorize(AuthenticationSchemes = AuthSchemes.Idp, Policy = "publisher")]
            async (string group, GroupMemberRequest request, HttpContext http,
                IHubContext<RealtimeHub> hubContext, IGroupRegistry groups,
                IConnectionRegistry connections, IGroupPartitioner partitioner) =>
            {
                if (string.IsNullOrWhiteSpace(group))
                    return Results.BadRequest(new { error = "group_required", detail = "group name is required" });

                if (string.IsNullOrWhiteSpace(request.UserId))
                    return Results.BadRequest(new { error = "user_id_required", detail = "body.user_id (sub) is required" });

                var partition = partitioner.PartitionOf(http.User);
                if (!groups.Exists(partition, group))
                    return Results.NotFound(new { error = "group_not_found", detail = $"create it first: POST /api/groups" });

                // member identity = the caller's own partition (bearer's
                // client_id) + the body's sub — i.e. the member must be a
                // user connected through THIS client (cross-service fan-out
                // is user_channel|{sub}, not group membership).
                var memberKey = partitioner.Physical(partition, request.UserId);
                groups.AddMember(partition, group, memberKey);

                var memberConnections = connections.ConnectionsFor(memberKey);
                foreach (var connectionId in memberConnections)
                    await hubContext.Groups.AddToGroupAsync(connectionId, partitioner.Physical(partition, group));

                return Results.Ok(new
                {
                    ok = true,
                    group,
                    group_partition = partition,
                    member = new { user_id = request.UserId, client_id = partition },
                    connections_joined = memberConnections.Count,
                });
            });

        // ── DELETE /api/groups/{g}/members?user_id=…&client_id=… — remove a
        // member; also evicts the member's live sockets from the group now.
        endpoints.MapDelete("/groups/{group}/members",
            [Authorize(AuthenticationSchemes = AuthSchemes.Idp, Policy = "publisher")]
            async (string group,
                [AsParameters] MembershipQuery query, HttpContext http,
                IHubContext<RealtimeHub> hubContext, IGroupRegistry groups,
                IConnectionRegistry connections, IGroupPartitioner partitioner) =>
            {
                if (string.IsNullOrWhiteSpace(group))
                    return Results.BadRequest(new { error = "group_required", detail = "group name is required" });

                if (string.IsNullOrWhiteSpace(query.UserId))
                    return Results.BadRequest(new { error = "user_id_required", detail = "user_id query parameter is required" });

                var partition = partitioner.PartitionOf(http.User);
                if (!groups.Exists(partition, group))
                    return Results.NotFound(new { error = "group_not_found", detail = "group does not exist in this partition" });

                var memberKey = partitioner.Physical(partition, query.UserId);
                var removed = groups.RemoveMember(partition, group, memberKey);

                foreach (var connectionId in connections.ConnectionsFor(memberKey))
                    await hubContext.Groups.RemoveFromGroupAsync(connectionId, partitioner.Physical(partition, group));

                return Results.Ok(new
                {
                    ok = true,
                    group,
                    group_partition = partition,
                    member = new { user_id = query.UserId, client_id = partition },
                    removed,
                });
            });

        return endpoints;
    }

    public sealed class MembershipQuery
    {
        [FromQuery(Name = "user_id")] public string? UserId { get; set; }
        // client_id deliberately absent: bearer scopes it (owner spec).
    }
}
