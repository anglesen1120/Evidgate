using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Catalog.Infrastructure.GitHub;

/// <summary>
/// The failure vocabulary of the App client. Every call returns one of these instead of throwing,
/// so a sync task can tell "retry later" from "the binding is wrong" without parsing status codes.
/// </summary>
internal static class GitHubErrorCodes
{
    /// <summary>No App id is configured; no request was sent.</summary>
    internal const string NotConfigured = "github_not_configured";

    /// <summary>GitHub answered 404: the installation, repository, tree or blob does not exist.</summary>
    internal const string NotFound = "github_not_found";

    /// <summary>GitHub refused the App JWT or the installation token (401).</summary>
    internal const string Unauthorized = "github_unauthorized";

    /// <summary>GitHub refused the call's permission on the resource (403, not a rate limit).</summary>
    internal const string Forbidden = "github_forbidden";

    /// <summary>GitHub rate-limited the call; the error carries the <c>Retry-After</c> delay.</summary>
    internal const string RateLimited = "github_rate_limited";

    /// <summary>GitHub is unavailable (5xx, 408 or a transport failure); the call may be retried later.</summary>
    internal const string Unavailable = "github_unavailable";

    /// <summary>The answer was not the JSON this client understands, or its shape was refused.</summary>
    internal const string ResponseInvalid = "github_response_invalid";

    /// <summary>The answer exceeded the size or entry cap and was refused instead of buffered.</summary>
    internal const string ResponseTooLarge = "github_response_too_large";

    /// <summary>GitHub answered with a status this client does not map.</summary>
    internal const string Failed = "github_failed";

    /// <summary>A call parameter was outside the shape this client accepts; nothing was sent.</summary>
    internal const string RequestInvalid = "github_request_invalid";
}

/// <summary>Reads one GitHub answer under a byte cap, then hands it to the endpoint's own parser.</summary>
internal static class GitHubResponseReader
{
    private const int ChunkBytes = 8192;
    private const int MaxJsonDepth = 64;

    /// <summary>
    /// Reads <paramref name="response"/>'s body with at most <paramref name="maxBytes"/> bytes and
    /// parses it as JSON. The body is refused unread when the declared length already exceeds the
    /// cap, and truncated reading stops at the cap instead of buffering an unbounded answer.
    /// </summary>
    /// <param name="response">The answer whose body is read. Its content stream is disposed.</param>
    /// <param name="maxBytes">Largest accepted body, in bytes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    internal static async Task<Result<JsonDocument>> ReadJsonAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
        {
            return Result.Failure<JsonDocument>(TooLarge());
        }

        var body = await ReadBytesAsync(response.Content, maxBytes, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return Result.Failure<JsonDocument>(TooLarge());
        }

        try
        {
            return Result.Success(JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = MaxJsonDepth }));
        }
        catch (JsonException)
        {
            return Result.Failure<JsonDocument>(Invalid());
        }
    }

    /// <summary>The error for a body over the cap.</summary>
    internal static Error TooLarge() =>
        new(GitHubErrorCodes.ResponseTooLarge, "The GitHub response exceeded the configured size cap.", 502);

    /// <summary>The error for a body that is not the JSON this client understands.</summary>
    internal static Error Invalid() =>
        new(GitHubErrorCodes.ResponseInvalid, "The GitHub response was not the expected JSON document.", 502);

    /// <summary>The error for a call whose base address or parameters are outside the accepted shape.</summary>
    internal static Error InvalidParameters() =>
        new(GitHubErrorCodes.RequestInvalid, "The GitHub call parameters are not valid.", 400);

    /// <summary>The error for a mapping this client could not use (missing or malformed member).</summary>
    internal static Error Unusable() =>
        new(GitHubErrorCodes.ResponseInvalid, "The GitHub response did not carry the fields this client needs.", 502);

    /// <summary>The error for a transport failure, a timeout of ours or a pipeline timeout.</summary>
    internal static Error Unavailable() =>
        new(GitHubErrorCodes.Unavailable, "GitHub could not be reached; the call may be retried later.", 503, Retryable: true);

    /// <summary>
    /// Maps one non-success status to a typed failure. A rate limit keeps the delay GitHub asked for
    /// in a detail, so the caller (or the pipeline above it) retries no earlier than that.
    /// </summary>
    /// <param name="response">The answer to map.</param>
    /// <param name="now">The current instant, for an absolute <c>Retry-After</c> date.</param>
    internal static Error MapStatus(HttpResponseMessage response, DateTimeOffset now)
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
            return (long)Math.Clamp(delta.TotalSeconds, 0, 3600);
        }

        if (header?.Date is { } date)
        {
            return (long)Math.Clamp((date - now).TotalSeconds, 0, 3600);
        }

        return 60;
    }

    private static async Task<byte[]?> ReadBytesAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new MemoryStream(Math.Min(maxBytes, 65536));
        var chunk = new byte[ChunkBytes];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Small helpers shared by the endpoint parsers; every failure is a typed <see cref="Error"/>.</summary>
internal static class GitHubJson
{
    /// <summary>Reads a positive numeric id from <paramref name="property"/>.</summary>
    internal static bool TryGetId(JsonElement element, string property, out long id)
    {
        id = 0;
        return element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out id)
            && id > 0;
    }

    /// <summary>Reads a non-empty string of at most <paramref name="maxLength"/> characters.</summary>
    internal static bool TryGetString(JsonElement element, string property, int maxLength, out string value)
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
    internal static bool TryGetPropertyString(JsonElement element, string property, out string value)
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
