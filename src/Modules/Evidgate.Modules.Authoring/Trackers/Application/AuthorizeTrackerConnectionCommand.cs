using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Application.Commands;
using Evidgate.Modules.Authoring.Contracts;
using Mediator;

namespace Evidgate.Modules.Authoring.Trackers.Application;

/// <summary>
/// Starts a connection: stores a pending consent for the caller and the project and answers the provider's consent URL.
/// Nothing is connected by this call.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Provider">The tracker from the route.</param>
internal sealed record AuthorizeTrackerConnectionCommand(Guid ProjectId, TrackerProvider Provider)
    : ICommand<Result<TrackerAuthorizeResponse>>;

/// <summary>Handler of <see cref="AuthorizeTrackerConnectionCommand"/>.</summary>
/// <param name="db">The tenant-scoped database, for the project check.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller; the consent is bound to its user.</param>
/// <param name="tenant">The request's tenant context, for the project scope check.</param>
/// <param name="store">The connection store.</param>
/// <param name="provider">The provider clients.</param>
/// <param name="clock">The clock the consent lifetime is measured on.</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("tracker.connection.authorize")]
[Permission(Permissions.Project.Write)]
internal sealed class AuthorizeTrackerConnectionCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    ITrackerConnectionStore store,
    ITrackerProviderClient provider,
    TimeProvider clock,
    IAuditTrail audit) : ICommandHandler<AuthorizeTrackerConnectionCommand, Result<TrackerAuthorizeResponse>>
{
    /// <summary>How long a consent can be completed.</summary>
    internal static readonly TimeSpan ConsentLifetime = TimeSpan.FromMinutes(10);

    /// <summary>429: the user holds the maximum number of unspent consents; one is finished or expires.</summary>
    private static readonly Error TooManyConsents = new(
        ApiErrorCode.RateLimited,
        "this user has too many open tracker connections in progress; finish one or wait for one to expire",
        429,
        Retryable: true);

    /// <inheritdoc />
    public async ValueTask<Result<TrackerAuthorizeResponse>> Handle(
        AuthorizeTrackerConnectionCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(AuthorizeTrackerConnectionCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<TrackerAuthorizeResponse>(scopeError);
        }

        if (principals.Current?.UserId is not { } userId)
        {
            return Result.Failure<TrackerAuthorizeResponse>(Error.Forbidden("a tracker connection is made by a signed-in member"));
        }

        // The project is looked up first: another tenant's project answers its 404 whatever this deployment is configured with.
        var orgId = scopes.Require().OrgId;
        if (await AuthoringProjectProbe.MissingAsync(db, orgId, command.ProjectId, cancellationToken).ConfigureAwait(false) is { } missing)
        {
            return Result.Failure<TrackerAuthorizeResponse>(missing);
        }

        if (!provider.IsConfigured(command.Provider))
        {
            return Result.Failure<TrackerAuthorizeResponse>(Error.Conflict(
                ApiErrorCode.TrackerProviderUnavailable,
                "this deployment has no OAuth client for that tracker"));
        }

        // A connection that needs reauthorisation may be reconnected in place; any other live one must be disconnected first.
        var live = await store.FindLiveAsync(command.ProjectId, command.Provider, cancellationToken).ConfigureAwait(false);
        if (live is { State: not TrackerConnectionState.NeedsReauth })
        {
            return Result.Failure<TrackerAuthorizeResponse>(Error.Conflict(
                ApiErrorCode.TrackerConnectionExists,
                "the project already has a connection to that tracker; disconnect it first"));
        }

        var state = TrackerState.New(orgId);
        var verifier = command.Provider == TrackerProvider.Linear ? TrackerState.NewVerifier() : null;
        var consent = new PendingConsent(orgId, command.ProjectId, userId, command.Provider, provider.RedirectUri(command.Provider), verifier);
        if (!await store.SaveConsentAsync(TrackerState.Hash(state), consent, ConsentLifetime, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<TrackerAuthorizeResponse>(TooManyConsents);
        }

        audit.Describe(new AuditObject(
            "tracker_connection",
            command.ProjectId.ToString("D", CultureInfo.InvariantCulture),
            Metadata: new Dictionary<string, string>
            {
                ["provider"] = TrackerProviders.ToWire(command.Provider),
                ["outcome"] = "consent_started",
            },
            ProjectId: command.ProjectId));

        return Result.Success(new TrackerAuthorizeResponse(
            provider.Authorize(command.Provider, state, verifier),
            clock.GetUtcNow().Add(ConsentLifetime)));
    }
}
