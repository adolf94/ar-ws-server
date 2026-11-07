using System.Text.Json.Nodes;

namespace Ar.Realtime.Client;

/// <summary>Thrown <em>before any wire traffic</em> — client-side validation.</summary>
public sealed class RealtimeValidationException(string message) : Exception(message);

/// <summary>Options contract: base URL + tokens (docs/CONTRACT.md §1).</summary>
public sealed class RealtimePublisherOptions
{
    /// <summary>Service base, e.g. <c>https://realtime.example.com</c> — no /api.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// Supplies IdP bearer with the publisher scope
    /// (<c>api://ws-server/app:publisher</c> default). Built-ins:
    /// <see cref="ArAuthAccessTokenProvider"/> (Ar.Auth.OpenId),
    /// <see cref="StaticAccessTokenProvider"/>, <see cref="FixedAccessTokenProvider"/>.
    /// </summary>
    public required IAccessTokenProvider AccessTokenProvider { get; init; }

    /// <summary>appended to the UA (visibility in Cloudflare logs).</summary>
    public string? UserAgentSuffix { get; init; }
}
