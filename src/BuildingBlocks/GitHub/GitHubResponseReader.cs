using System.Globalization;
using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.BuildingBlocks.SharedKernel.Serialization;

namespace Evidgate.BuildingBlocks.SharedKernel.GitHub;

/// <summary>
/// The failure vocabulary of the App client. Every call returns one of these instead of throwing, so a caller can
/// tell "retry later" from "the binding is wrong" without parsing status codes.
/// </summary>
public static class GitHubErrorCodes
{
    /// <summary>No App id is configured; no request was sent.</summary>
    public const string NotConfigured = "github_not_configured";

    /// <summary>GitHub answered 404: the installation, repository, tree or blob does not exist.</summary>
    public const string NotFound = "github_not_found";

    /// <summary>GitHub refused the App JWT or the installation token (401).</summary>
    public const string Unauthorized = "github_unauthorized";

    /// <summary>GitHub refused the call's permission on the resource (403, not a rate limit).</summary>
    public const string Forbidden = "github_forbidden";

    /// <summary>GitHub rate-limited the call; the error carries the <c>Retry-After</c> delay.</summary>
    public const string RateLimited = "github_rate_limited";

    /// <summary>GitHub is unavailable (5xx, 408 or a transport failure); the call may be retried later.</summary>
    public const string Unavailable = "github_unavailable";

    /// <summary>The answer was not the JSON this client understands, or its shape was refused.</summary>
    public const string ResponseInvalid = "github_response_invalid";

    /// <summary>The answer exceeded the size or entry cap and was refused instead of buffered.</summary>
    public const string ResponseTooLarge = "github_response_too_large";

    /// <summary>GitHub answered with a status this client does not map.</summary>
    public const string Failed = "github_failed";

    /// <summary>A call parameter was outside the shape this client accepts; nothing was sent.</summary>
    public const string RequestInvalid = "github_request_invalid";

    /// <summary>The permission set of a feature is empty, so minting refuses instead of asking for every permission.</summary>
    public const string PermissionMapEmpty = "github_permission_map_empty";

    /// <summary>The installation no longer granted a permission the feature needs.</summary>
    public const string PermissionNotGranted = "permission_not_granted";
}

/// <summary>Reads one GitHub answer under a byte cap, then hands it to the endpoint's own parser.</summary>
public static class GitHubResponseReader
{
    private const long FallbackDelaySeconds = 60;

    /// <summary>
    /// Reads <paramref name="response"/>'s body with at most <paramref name="maxBytes"/> bytes and parses it as JSON.
    /// The body is refused unread when the declared length already exceeds the cap, and truncated reading stops at the
    /// cap instead of buffering an unbounded answer.
    /// </summary>
    /// <param name="response">The answer whose body is read. Its content stream is disposed.</param>
    /// <param name="maxBytes">Largest accepted body, in bytes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<Result<JsonDocument>> ReadJsonAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        // A declared length over the cap is refused before the stream is opened.
        var declared = response.Content.Headers.ContentLength;
        if (declared > maxBytes)
        {
            return Result.Failure<JsonDocument>(TooLarge());
        }

        BoundedJsonBody read;
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            read = await BoundedStreamBody
                .ReadJsonAsync(stream, maxBytes, requireObject: true, cancellationToken, declared)
                .ConfigureAwait(false);
        }

        return read.Status switch
        {
            BoundedBodyStatus.Ok => Result.Success(read.Document!),
            BoundedBodyStatus.TooLarge => Result.Failure<JsonDocument>(TooLarge()),
            _ => Result.Failure<JsonDocument>(Invalid()),
        };
    }

    /// <summary>The error for a body over the cap.</summary>
    public static Error TooLarge() =>
        new(GitHubErrorCodes.ResponseTooLarge, "The GitHub response exceeded the configured size cap.", 502);

    /// <summary>The error for a body that is not the JSON this client understands.</summary>
    public static Error Invalid() =>
        new(GitHubErrorCodes.ResponseInvalid, "The GitHub response was not the expected JSON document.", 502);

    /// <summary>The error for a call whose base address or parameters are outside the accepted shape.</summary>
    public static Error InvalidParameters() =>
        new(GitHubErrorCodes.RequestInvalid, "The GitHub call parameters are not valid.", 400);

    /// <summary>The error for a mapping this client could not use (missing or malformed member).</summary>
    public static Error Unusable() =>
        new(GitHubErrorCodes.ResponseInvalid, "The GitHub response did not carry the fields this client needs.", 502);

    /// <summary>The error for a transport failure, a timeout of ours or a pipeline timeout.</summary>
    public static Error Unavailable() =>
        new(GitHubErrorCodes.Unavailable, "GitHub could not be reached; the call may be retried later.", 503, Retryable: true);

    /// <summary>
    /// Maps one non-success status to a typed failure. Rate limits carry GitHub's delay so the caller can schedule a
    /// later call; the HTTP pipeline does not retry rate-limit responses.
    /// </summary>
    /// <param name="response">The answer to map.</param>
    /// <param name="now">The current instant, for an absolute <c>Retry-After</c> date.</param>
    public static Error MapStatus(HttpResponseMessage response, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new(
                GitHubErrorCodes.Unauthorized,
                "GitHub refused the credentials of this call.",
                401),
            HttpStatusCode.NotFound => new(
                GitHubErrorCodes.NotFound,
                "GitHub reports that the addressed resource does not exist.",
                404),
            HttpStatusCode.Forbidden when IsRateLimit(response) => RateLimited(response, now),
            HttpStatusCode.Forbidden => new(
                GitHubErrorCodes.Forbidden,
                "GitHub refused this App's permission on the addressed resource.",
                403),
            HttpStatusCode.TooManyRequests => RateLimited(response, now),
            HttpStatusCode.RequestTimeout or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => new(
                    GitHubErrorCodes.Unavailable,
                    "GitHub is unavailable; the call may be retried later.",
                    503,
                    Retryable: true),
            _ => new(
                GitHubErrorCodes.Failed,
                "GitHub answered with a status this client does not accept.",
                502),
        };
    }

    private static bool IsRateLimit(HttpResponseMessage response) =>
        response.Headers.RetryAfter is not null
        || (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            && remaining.FirstOrDefault() == "0");

    private static Error RateLimited(HttpResponseMessage response, DateTimeOffset now)
    {
        var delay = RetryAfterSeconds(response, now);

        return new(
            GitHubErrorCodes.RateLimited,
            "GitHub rate-limited this call; retry no earlier than the reported delay.",
            429,
            Retryable: true,
            Details: [new ErrorDetail("Retry-After", "rate_limited", null, null, delay)]);
    }

    private static long RetryAfterSeconds(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return Bound(delta.TotalSeconds);
        }

        if (header?.Date is { } date)
        {
            return Bound((date - now).TotalSeconds);
        }

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset)
            && long.TryParse(reset.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            return Bound(unixSeconds - now.ToUnixTimeSeconds());
        }

        return FallbackDelaySeconds;
    }

    /// <summary>
    /// Reports the delay the server asked for, never rounded down to zero: a caller is told to retry no earlier than
    /// that, and shortening it would retry into the same limit.
    /// </summary>
    private static long Bound(double seconds) =>
        (long)Math.Max(Math.Ceiling(seconds), 1);
}

/// <summary>Small helpers shared by the endpoint parsers; every failure is a typed <see cref="Error"/>.</summary>
public static class GitHubJson
{
    /// <summary>Reads a positive numeric id from <paramref name="property"/>.</summary>
    public static bool TryGetId(JsonElement element, string property, out long id)
    {
        id = 0;
        return element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out id)
            && id > 0;
    }

    /// <summary>Reads a non-empty string of at most <paramref name="maxLength"/> characters.</summary>
    public static bool TryGetString(JsonElement element, string property, int maxLength, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(property, out var raw) || raw.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = raw.GetString();
        if (string.IsNullOrEmpty(text) || text.Length > maxLength)
        {
            return false;
        }

        value = text;
        return true;
    }

    /// <summary>Reads any JSON string; the caller applies its own cap when the value is unbounded in shape.</summary>
    public static bool TryGetPropertyString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(property, out var raw) || raw.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = raw.GetString();
        if (text is null)
        {
            return false;
        }

        value = text;
        return true;
    }
}
