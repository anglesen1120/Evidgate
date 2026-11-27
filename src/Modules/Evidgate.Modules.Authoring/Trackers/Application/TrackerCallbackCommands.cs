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
/// Unit one of the callback: takes the single-use consent for the caller. It commits before the provider is called, so
/// a code that fails to exchange cannot be retried with the same state. A refusal rolls the unit back, so a consent
/// presented by the wrong user, for another provider or outside the credential's project stays unused.
/// </summary>
/// <param name="Provider">The provider of the callback route.</param>
/// <param name="State">The untrusted <c>state</c> query value.</param>
/// <param name="Code">The untrusted authorization code; only its presence and length are read here.</param>
/// <param name="ProviderError">The tracker's <c>error</c> when the user declined, else null.</param>
internal sealed record TakeTrackerConsentCommand(TrackerProvider Provider, string? State, string? Code, string? ProviderError)
    : ICommand<Result<PendingConsent>>
{
    /// <summary>The type name only: the state is a credential.</summary>
    public override string ToString() => nameof(TakeTrackerConsentCommand);
}

/// <summary>
/// Unit two of the callback: stores the connection for tokens already exchanged outside any transaction, with its audit
/// entry in the same commit.
/// </summary>
/// <param name="Consent">The consent unit one took.</param>
/// <param name="Token">The tokens the provider issued; held in memory only until the store protects them.</param>
internal sealed record CompleteTrackerConnectionCommand(PendingConsent Consent, TrackerToken Token)
    : ICommand<Result<TrackerConnectionDto>>
{
    /// <summary>The type name only: the command holds a token and a PKCE verifier.</summary>
    public override string ToString() => nameof(CompleteTrackerConnectionCommand);
}

/// <summary>Handler of <see cref="TakeTrackerConsentCommand"/>.</summary>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller.</param>
/// <param name="tenant">The request's tenant context, for the project scope check.</param>
/// <param name="store">The connection store.</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("tracker.connection.consent_take")]
[Permission(Permissions.Project.Write)]
internal sealed class TakeTrackerConsentCommandHandler(
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    ITrackerConnectionStore store,
    IAuditTrail audit) : ICommandHandler<TakeTrackerConsentCommand, Result<PendingConsent>>
{
    /// <inheritdoc />
    public async ValueTask<Result<PendingConsent>> Handle(TakeTrackerConsentCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(TakeTrackerConsentCommand));

        ArgumentNullException.ThrowIfNull(command);

        // Shape and length caps come first, after the permission stage and before any lookup, so a hostile value
        // never reaches the database. A declined consent carries an `error` and no code.
        var declined = !string.IsNullOrEmpty(command.ProviderError);
        if (TrackerState.Org(command.State) is null
            || (declined && command.ProviderError!.Length > TrackerState.MaxErrorLength)
            || (!declined && (string.IsNullOrEmpty(command.Code) || command.Code.Length > TrackerState.MaxCodeLength)))
        {
            return Result.Failure<PendingConsent>(TrackerErrors.ConsentInvalid);
        }

        if (principals.Current?.UserId is not { } userId)
        {
            return Result.Failure<PendingConsent>(Error.Forbidden("a tracker connection is made by a signed-in member"));
        }

        var lookup = await store.TakeConsentAsync(TrackerState.Hash(command.State!), userId, cancellationToken).ConfigureAwait(false);
        if (lookup is ConsentLookup.WrongUser)
        {
            return Result.Failure<PendingConsent>(Error.Forbidden("that consent was started by another user"));
        }

        if (lookup is not ConsentLookup.Taken { Consent: var consent })
        {
            return Result.Failure<PendingConsent>(TrackerErrors.ConsentInvalid);
        }

        if (consent.Provider != command.Provider || consent.OrgId != scopes.Require().OrgId)
        {
            return Result.Failure<PendingConsent>(TrackerErrors.ConsentInvalid);
        }

        if (AuthoringRules.Project(consent.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<PendingConsent>(scopeError);
        }

        audit.Describe(new AuditObject(
            "tracker_connection",
            consent.ProjectId.ToString("D", CultureInfo.InvariantCulture),
            Metadata: new Dictionary<string, string>
            {
                ["provider"] = TrackerProviders.ToWire(consent.Provider),
                ["outcome"] = "consent_taken",
            },
            ProjectId: consent.ProjectId));

        return Result.Success(consent);
    }
}

/// <summary>Handler of <see cref="CompleteTrackerConnectionCommand"/>.</summary>
/// <param name="db">The tenant-scoped database, for the project check.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller.</param>
/// <param name="tenant">The request's tenant context, for the project scope check.</param>
/// <param name="store">The connection store, which protects the tokens.</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("tracker.connection.connect")]
[Permission(Permissions.Project.Write)]
internal sealed class CompleteTrackerConnectionCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    ITrackerConnectionStore store,
    IAuditTrail audit) : ICommandHandler<CompleteTrackerConnectionCommand, Result<TrackerConnectionDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<TrackerConnectionDto>> Handle(CompleteTrackerConnectionCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(CompleteTrackerConnectionCommand));

        ArgumentNullException.ThrowIfNull(command);

        var consent = command.Consent;
        var orgId = scopes.Require().OrgId;

        // Unit one proved all of this; a second look costs nothing and keeps this unit safe on its own.
        if (principals.Current?.UserId != consent.UserId || consent.OrgId != orgId)
        {
            return Result.Failure<TrackerConnectionDto>(Error.Forbidden("that consent was started by another user"));
        }

        if (AuthoringRules.Project(consent.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<TrackerConnectionDto>(scopeError);
        }

        if (await AuthoringProjectProbe.MissingAsync(db, orgId, consent.ProjectId, cancellationToken).ConfigureAwait(false) is { } missing)
        {
            return Result.Failure<TrackerConnectionDto>(missing);
        }

        // The caller's own membership, through the module's one actor lookup, so the store holds no membership SQL.
        var membershipId = await AuthoringRequestScope.ActorMembershipAsync(db, orgId, principals, cancellationToken).ConfigureAwait(false);

        // Null means the consent was cancelled by a disconnect while the exchange ran, or another consent connected the
        // project first; either way nothing was stored for this one.
        var view = await store.CreateAsync(
            new NewTrackerConnection(
                consent.ProjectId,
                consent.Provider,
                consent.UserId,
                command.Token.AccessToken,
                command.Token.RefreshToken,
                command.Token.ExpiresAt,
                command.Token.GrantedScopes,
                membershipId,
                consent.ConsentId),
            cancellationToken).ConfigureAwait(false);
        if (view is null)
        {
            return Result.Failure<TrackerConnectionDto>(Error.Conflict(
                ApiErrorCode.TrackerConnectionExists,
                "the connection was disconnected while it was being made, or the project already has a connection to that tracker; start again"));
        }

        audit.Describe(new AuditObject(
            "tracker_connection",
            view.Id.ToString("D", CultureInfo.InvariantCulture),
            Metadata: new Dictionary<string, string>
            {
                ["provider"] = TrackerProviders.ToWire(view.Provider),
                ["outcome"] = "connected",
            },
            ProjectId: view.ProjectId));

        return Result.Success(TrackerConnectionMapping.ToDto(view));
    }
}
