using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Webhooks.Domain;

namespace Evidgate.Modules.Policy.Webhooks.Application;

/// <summary>The limits of the endpoint routes.</summary>
internal static class WebhookEndpointLimits
{
    /// <summary>Live (not deleted) endpoints one project may hold.</summary>
    public const int MaxEndpointsPerProject = 20;

    /// <summary>Longest stored URL, the column's own check.</summary>
    public const int MaxUrlLength = 2048;

    /// <summary>Default page size of the list routes.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Largest page size of the list routes.</summary>
    public const int MaxLimit = 200;
}

/// <summary>The refusals the endpoint routes answer with.</summary>
internal static class WebhookEndpointErrors
{
    /// <summary>The project already holds <see cref="WebhookEndpointLimits.MaxEndpointsPerProject"/> live endpoints.</summary>
    public const string EndpointLimitReached = "endpoint_limit_reached";

    /// <summary>The project already has a live endpoint with this URL.</summary>
    public const string EndpointUrlTaken = "endpoint_url_taken";

    /// <summary>No delivery of the event is dead-lettered on an active endpoint, so a replay has nothing to move.</summary>
    public const string DeliveryNotDeadLetter = "delivery_not_dead_letter";

    /// <summary>The caller is not an active member of the org, so no creator can be recorded.</summary>
    public static readonly Error NoMembership = new(
        ApiErrorCode.NoMembership,
        "the caller has no active membership in this organization",
        403);

    /// <summary>The project does not exist, is deleted or belongs to another tenant.</summary>
    public static readonly Error ProjectNotFound = Error.NotFound("no project with that id is visible to this tenant");

    /// <summary>The endpoint does not exist in this project, is deleted or belongs to another tenant.</summary>
    public static readonly Error EndpointNotFound = Error.NotFound("no webhook endpoint with that id is visible in this project");

    /// <summary>The project row or the endpoint row is held by another writer past the bounded wait.</summary>
    public static readonly Error Busy = new(
        ApiErrorCode.Unavailable,
        "the webhook endpoints of this project are being changed; retry shortly",
        503,
        true);

    /// <summary>The 422 of a URL the guard refused; names the rule class, never an address.</summary>
    /// <param name="reason">One of <see cref="WebhookUrlReasons"/>.</param>
    public static Error UrlBlocked(string? reason) => Error.Unprocessable(
        WebhookErrors.EndpointUrlBlocked,
        "the webhook URL is not an allowed delivery target",
        [new ErrorDetail("/url", reason ?? WebhookUrlReasons.Scheme)]);

    /// <summary>The 409 of a project at its endpoint cap.</summary>
    public static Error LimitReached() => Error.Conflict(
        EndpointLimitReached,
        string.Create(
            CultureInfo.InvariantCulture,
            $"a project may hold at most {WebhookEndpointLimits.MaxEndpointsPerProject} webhook endpoints"));

    /// <summary>The 409 of a URL the project already receives events at.</summary>
    public static Error UrlTaken() => Error.Conflict(
        EndpointUrlTaken,
        "this project already has a webhook endpoint with that URL");
}

/// <summary>Request checks shared by the endpoint commands.</summary>
internal static class WebhookEndpointRules
{
    /// <summary>The event types an endpoint may subscribe to.</summary>
    private static readonly string[] SupportedEventTypes = [EvaluationCreatedPayload.EventType];

    /// <summary>The supported types an omitted list stands for.</summary>
    public static string[] DefaultEventTypes => [.. SupportedEventTypes];

    /// <summary>Null when <paramref name="eventTypes"/> is empty-free, distinct and supported, else the 422.</summary>
    /// <param name="eventTypes">The requested list.</param>
    public static Error? CheckEventTypes(string[]? eventTypes)
    {
        if (eventTypes is null)
        {
            return null;
        }

        if (eventTypes.Length == 0
            || eventTypes.Length != eventTypes.Distinct(StringComparer.Ordinal).Count()
            || eventTypes.Any(static type => !SupportedEventTypes.Contains(type, StringComparer.Ordinal)))
        {
            return Error.Invalid(
                "event_types must list each supported event type once",
                [new ErrorDetail("/event_types", "unsupported")]);
        }

        return null;
    }

    /// <summary>
    /// The stored form of an accepted target: the normalised absolute URL, so two spellings of one address
    /// are one row. Null when normalisation pushed it past the column's length check.
    /// </summary>
    /// <param name="target">The guard's accepted target.</param>
    public static string? StoredUrl(WebhookTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var url = target.Uri.AbsoluteUri;

        return url.Length > WebhookEndpointLimits.MaxUrlLength ? null : url;
    }

    /// <summary>Parses the quoted integer <c>If-Match</c> of a change; absent is 422, anything else malformed is 412.</summary>
    /// <param name="raw">The header value.</param>
    /// <param name="rowVersion">The version the caller read.</param>
    public static Error? IfMatch(string? raw, out int rowVersion)
    {
        rowVersion = 0;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return Error.Invalid("If-Match is required to change a webhook endpoint", [new ErrorDetail("If-Match", "required")]);
        }

        var value = raw.Trim();

        if (value.StartsWith("W/", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        if (value.Length < 3
            || value[0] != '"'
            || value[^1] != '"'
            || !int.TryParse(value[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out rowVersion))
        {
            return Error.PreconditionFailed("If-Match is not a webhook endpoint version this API issued");
        }

        return null;
    }

    /// <summary>Parses a page size; absent is the default, anything outside 1–200 is 422.</summary>
    /// <param name="raw">The query value.</param>
    /// <param name="limit">The page size to use.</param>
    public static Error? Limit(string? raw, out int limit)
    {
        limit = WebhookEndpointLimits.DefaultLimit;

        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 1
            || parsed > WebhookEndpointLimits.MaxLimit)
        {
            return Error.Invalid(
                string.Create(CultureInfo.InvariantCulture, $"limit must be an integer between 1 and {WebhookEndpointLimits.MaxLimit} (00 §9.1)"),
                [new ErrorDetail("limit", "out_of_range")]);
        }

        limit = parsed;

        return null;
    }
}
