namespace Ar.Realtime.Client;

/// <summary>HTTP envelope failure — CONTRACT §2: {error, detail} on every non-2xx.</summary>
public sealed class RealtimeApiException : Exception
{
    public RealtimeApiException(int statusCode, string errorCode, string? detail, long? retryAfterSeconds = null)
        : base($"{(System.Net.HttpStatusCode)statusCode} {errorCode}: {detail}")
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        Detail = detail;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public int StatusCode { get; }
    public string ErrorCode { get; }
    public string? Detail { get; }

    /// <summary>
    /// Populated on a <see cref="RealtimeApiException"/> with
    /// <see cref="StatusCode"/> 429 (rate_limited): seconds until the
    /// client_id's fixed window rolls over (CONTRACT §2.1). Honor it.
    /// </summary>
    public long? RetryAfterSeconds { get; }
}
