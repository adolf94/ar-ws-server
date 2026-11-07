using System.Text.RegularExpressions;

namespace Realtime.Negotiate;

/// <summary>
/// ALLOWED_ORIGINS matcher. Each entry is an exact origin (scheme://host[:port]);
/// one '*' stands for exactly one subdomain label, e.g. https://*.adolfrey.com.
/// Shared by the CORS policy and the hub handshake fence so both enforce the same rule.
/// </summary>
public sealed class OriginAllowList
{
    private readonly Regex[] _patterns;

    public OriginAllowList(IEnumerable<string> origins)
    {
        _patterns = origins
            .Select(o => o.TrimEnd('/'))
            .Where(o => o.Length > 0)
            .Select(ToRegex)
            .ToArray();
    }

    public bool IsAllowed(string? origin) =>
        !string.IsNullOrEmpty(origin) && _patterns.Any(p => p.IsMatch(origin));

    private static Regex ToRegex(string origin)
    {
        var parts = origin.Split('*').Select(Regex.Escape);
        return new Regex($"^{string.Join("[a-z0-9-]+", parts)}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
