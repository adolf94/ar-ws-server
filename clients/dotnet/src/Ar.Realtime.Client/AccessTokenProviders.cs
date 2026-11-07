using Ar.Auth.OpenId;

namespace Ar.Realtime.Client;

/// <summary>
/// Token plumbing (owner spec: depend on ar-auth-client-*).
/// The transport stays auth-agnostic; adapters wire an Ar.Auth client into it.
/// </summary>
public interface IAccessTokenProvider
{
    /// <summary>Called per API call — return a FRESH IdP bearer or a
    /// cached-but-still-valid one (implementations decide).</summary>
    Task<string> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// Adapts the sibling <c>Ar.Auth.OpenId</c> SDK:
/// <see cref="IArAuthClient.ClientCredentialsAsync"/> production with a
/// cache until (ExpiresIn − 60 s), re-requesting on expiry. Construct with
/// an <see cref="ArAuthClient"/> that has ClientId/ClientSecret configured;
/// the requested scope must cover <c>PUBLISH_REQUIRED_SCOPE</c>.
/// </summary>
public sealed class ArAuthAccessTokenProvider : IAccessTokenProvider
{
    private readonly IArAuthClient _client;
    private readonly string? _scope;
    private readonly object _gate = new();
    private string? _cached;
    private DateTimeOffset _untilUtc;

    public ArAuthAccessTokenProvider(IArAuthClient client, string? scope = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _scope = scope;
    }

    public async Task<string> GetAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_cached is not null && DateTimeOffset.UtcNow < _untilUtc) return _cached;
        }
        var token = await _client.ClientCredentialsAsync(_scope);
        // re-mint a minute early — clock skew is free; mid-flight 401s are not
        var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Max(30, token.ExpiresIn - 60));
        lock (_gate)
        {
            _cached = token.AccessToken;
            _untilUtc = until;
        }
        return token.AccessToken;
    }
}

/// <summary>Splits flags (tests): every call gets a new bearer.</summary>
public sealed class FixedAccessTokenProvider(Func<Task<string>> factory) : IAccessTokenProvider
{
    public Task<string> GetAsync(CancellationToken ct = default) => factory();
}

/// <summary>One value for everything — e.g. the caller vends a current token.</summary>
public sealed class StaticAccessTokenProvider(string token) : IAccessTokenProvider
{
    public Task<string> GetAsync(CancellationToken ct = default) => Task.FromResult(token);
}
