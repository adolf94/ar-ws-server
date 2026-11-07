using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Realtime.Auth;
using Realtime.Groups;
using Realtime.Hubs;

namespace Realtime.Api;

/// <summary>
/// The server API (plan §5.3, Phase-0 subset): app backends publish over
/// plain HTTP with Bearer + publisher scope — real status codes, real JSON
/// errors (plan R8). Every request's logical group is scoped to the caller's
/// <c>client_id</c> partition (plan R4, <see cref="IGroupPartitioner"/>).
/// Members/presence/history arrive in Phases 1–2.
/// </summary>
public static class RealtimeApiEndpoints
{
    public sealed class PublishRequest
    {
        public string? Group { get; set; }
        public JsonElement Data { get; set; }
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

        return endpoints;
    }
}
