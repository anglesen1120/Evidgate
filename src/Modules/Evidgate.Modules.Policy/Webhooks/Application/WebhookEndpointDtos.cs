namespace Evidgate.Modules.Policy.Webhooks.Application;

/// <summary>One registered endpoint as every read and write answers it; the stored secret is never part of it.</summary>
/// <param name="Id">The endpoint.</param>
/// <param name="ProjectId">The project whose evaluations it receives.</param>
/// <param name="Url">The https target, in the normalised form the guard accepted.</param>
/// <param name="EventTypes">The event types delivered; today only <c>evaluation.created</c>.</param>
/// <param name="State"><c>active</c> or <c>disabled</c>; a deleted endpoint is not served.</param>
/// <param name="SecretHint">The last four characters of the signing secret, enough to tell two secrets apart.</param>
/// <param name="SecretVersion">The signing secret's version.</param>
/// <param name="Version">The concurrency token; the <c>ETag</c> and the <c>If-Match</c> of a change.</param>
/// <param name="CreatedAt">Creation, UTC.</param>
/// <param name="UpdatedAt">Last write, UTC.</param>
internal sealed record WebhookEndpointView(
    Guid Id,
    Guid ProjectId,
    string Url,
    string[] EventTypes,
    string State,
    string SecretHint,
    int SecretVersion,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The 201 of a registration: the endpoint's fields plus the one and only time the signing secret is shown.</summary>
/// <param name="Id">The endpoint.</param>
/// <param name="ProjectId">The project whose evaluations it receives.</param>
/// <param name="Url">The https target.</param>
/// <param name="EventTypes">The event types delivered.</param>
/// <param name="State">The endpoint state.</param>
/// <param name="SecretHint">The last four characters of the secret.</param>
/// <param name="SecretVersion">The signing secret's version.</param>
/// <param name="Version">The concurrency token.</param>
/// <param name="CreatedAt">Creation, UTC.</param>
/// <param name="UpdatedAt">Last write, UTC.</param>
/// <param name="Secret">The plaintext signing secret; never readable again.</param>
internal sealed record WebhookEndpointCreated(
    Guid Id,
    Guid ProjectId,
    string Url,
    string[] EventTypes,
    string State,
    string SecretHint,
    int SecretVersion,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Secret)
{
    /// <summary>The registration answer for a stored endpoint and its freshly generated secret.</summary>
    /// <param name="view">The stored endpoint.</param>
    /// <param name="secret">The plaintext secret.</param>
    public static WebhookEndpointCreated From(WebhookEndpointView view, string secret)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new(
            view.Id, view.ProjectId, view.Url, view.EventTypes, view.State, view.SecretHint, view.SecretVersion,
            view.Version, view.CreatedAt, view.UpdatedAt, secret);
    }

    /// <inheritdoc />
    public override string ToString() => nameof(WebhookEndpointCreated);
}

/// <summary>Body of <c>POST /v1/projects/{p}/webhook-endpoints</c>.</summary>
/// <param name="Url">The https target.</param>
/// <param name="EventTypes">The event types to receive; omitted means every supported type.</param>
internal sealed record WebhookEndpointCreateRequest(string? Url, string[]? EventTypes);

/// <summary>Body of <c>PATCH /v1/projects/{p}/webhook-endpoints/{e}</c>; an omitted field is unchanged.</summary>
/// <param name="Url">A new https target; re-checked by the same guard as a registration.</param>
/// <param name="EventTypes">A new event type list.</param>
/// <param name="State"><c>active</c> or <c>disabled</c>.</param>
internal sealed record WebhookEndpointUpdateRequest(string? Url, string[]? EventTypes, string? State);

/// <summary>Cursor page of a project's endpoints.</summary>
/// <param name="Items">Endpoints in creation order.</param>
/// <param name="NextCursor">Opaque cursor, or null on the last page.</param>
internal sealed record WebhookEndpointPage(IReadOnlyList<WebhookEndpointView> Items, string? NextCursor);

/// <summary>One delivery of one event to one endpoint.</summary>
/// <param name="Id">The delivery.</param>
/// <param name="EndpointId">The endpoint it goes to.</param>
/// <param name="EventType">The event type.</param>
/// <param name="EventId">The event id (the evaluation id), also sent as a header.</param>
/// <param name="State"><c>queued</c>, <c>sending</c>, <c>delivered</c>, <c>retrying</c> or <c>dead_letter</c>.</param>
/// <param name="Attempts">Attempts made so far.</param>
/// <param name="NextAttemptAt">When the next attempt is due, if one is scheduled.</param>
/// <param name="LastError">The closed code of the last failed attempt; never an address or a response body.</param>
/// <param name="LastStatusCode">The HTTP status of the last attempt, if the endpoint answered.</param>
/// <param name="CreatedAt">Creation, UTC.</param>
/// <param name="UpdatedAt">Last change, UTC.</param>
internal sealed record WebhookDeliveryView(
    Guid Id,
    Guid EndpointId,
    string EventType,
    Guid EventId,
    string State,
    int Attempts,
    DateTimeOffset? NextAttemptAt,
    string? LastError,
    int? LastStatusCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Cursor page of a project's deliveries, newest first.</summary>
/// <param name="Items">Deliveries, newest first.</param>
/// <param name="NextCursor">Opaque cursor, or null on the last page.</param>
internal sealed record WebhookDeliveryPage(IReadOnlyList<WebhookDeliveryView> Items, string? NextCursor);

/// <summary>The answer of an operator replay: the deliveries that left the dead letter state.</summary>
/// <param name="EventId">The event the operator named.</param>
/// <param name="DeliveryIds">The deliveries queued again.</param>
internal sealed record WebhookReplayResult(Guid EventId, IReadOnlyList<Guid> DeliveryIds);
