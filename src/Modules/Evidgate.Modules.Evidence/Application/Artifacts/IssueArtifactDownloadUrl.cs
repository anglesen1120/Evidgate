using System.Reflection;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Endpoints;
using Mediator;

namespace Evidgate.Modules.Evidence.Application.Artifacts;

/// <summary>
/// Issues one short-lived presigned <c>GET</c> for an artifact's content. It is a command rather than a query
/// because it appends an audit row on a grant and, through <see cref="ArtifactDownloadGate"/>, on a denial — a
/// read that writes is a command.
/// </summary>
/// <remarks>
/// The command carries the id and nothing else: the handler resolves the key, the lifecycle state and the
/// classification itself, so no caller can hand it an object key it was never shown or claim a sensitive row is
/// ordinary.
/// </remarks>
/// <param name="ArtifactId">The artifact the route addressed.</param>
internal sealed record IssueArtifactDownloadUrl(Guid ArtifactId) : ICommand<Result<DownloadUrlResponse>>;

/// <summary>
/// The endpoint-side decision of one download issuance: the <c>artifact:read_sensitive</c> answer for the caller, the
/// row as the shared read model projects it for that caller, and the audit row a denial needs. It runs before the
/// command dispatches, in its own scope, so a denial is written and committed instead of being rolled back with the
/// transaction the pipeline would open for a failing command.
/// </summary>
/// <remarks>
/// The base <c>artifact:read</c> stays the pipeline's decision; this gate only answers the data-dependent half. The
/// route names no project, so the row's own project decides the scope: a member's roles are org-level and read any
/// project of the org, while a request narrowed to another project gets the same 404 as an unknown id. <c>artifact:read</c>
/// is outside the service ceiling, so no project-bound credential reaches this route today. A row that is served as
/// sensitive and a caller without the permission is the 403 <c>sensitivity_forbidden</c> with one audit row, and no
/// key, checksum or URL appears in that answer.
/// </remarks>
internal sealed class ArtifactDownloadGate(
    ITenantScopeFactory scopes,
    ITenantDb db,
    IRequestTenantContextAccessor tenant,
    IPermissionEvaluator permissions,
    IAuditTrail audit)
{
    /// <summary>The audit action of a refused sensitive download.</summary>
    internal const string DeniedAction = "artifact.download_denied";

    /// <summary>
    /// The base permission the C2 authorization stage enforces for the download command, read from the handler's own
    /// <see cref="PermissionAttribute"/> so this gate and the stage can never disagree. It is evaluated here FIRST:
    /// a caller without <c>artifact:read</c> must be refused before the row is read, so it can neither tell an
    /// existing artifact from a missing one (403/409 versus 404) nor generate a denial audit for a row it may not
    /// even see.
    /// </summary>
    private static readonly string RequiredPermission =
        typeof(IssueArtifactDownloadUrlHandler).GetCustomAttribute<PermissionAttribute>()?.Permission
        ?? throw new InvalidOperationException(
            "IssueArtifactDownloadUrlHandler must declare [Permission] (00 §5.7 S2)");

    /// <summary>The one lifecycle state that has bytes at its final key.</summary>
    private const string Ready = "ready";

    /// <summary>
    /// Resolves the row for <paramref name="artifactId"/> as <paramref name="principal"/> may see it, and audits the
    /// denial when the row is served as sensitive and the caller lacks <c>artifact:read_sensitive</c>.
    /// </summary>
    /// <param name="artifactId">The addressed artifact.</param>
    /// <param name="principal">The caller's token identity, as the pipeline evaluates it.</param>
    /// <param name="cancellationToken">Cancels the reads and the audit append.</param>
    /// <returns>The row to issue a URL for, or the named refusal to render.</returns>
    public async ValueTask<Result<ArtifactReadRow>> AuthorizeAsync(
        Guid artifactId,
        IRequestPrincipal principal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var context = tenant.Require();

        ArtifactReadRow? row;
        bool granted;
        PermissionDecision baseDecision;

        await using (var scope = await scopes
            .BeginAsync(context.OrgId, context.ProjectId, readOnlyTransaction: true, cancellationToken)
            .ConfigureAwait(false))
        {
            // The base permission first, before any row is read: the same decision the C2 stage would reach, asked in
            // the same scope. A caller the stage is about to refuse therefore never learns whether the artifact
            // exists (no 404/403/409 distinction) and never causes a denial audit row.
            baseDecision = await permissions
                .EvaluateAsync(RequiredPermission, principal, context, cancellationToken)
                .ConfigureAwait(false);

            if (!baseDecision.IsGranted)
            {
                return Result.Failure<ArtifactReadRow>(baseDecision.ToError());
            }

            granted = (await permissions
                .EvaluateAsync(Permissions.Artifact.ReadSensitive, principal, context, cancellationToken)
                .ConfigureAwait(false)).IsGranted;

            row = await ArtifactReadModel
                .ReadAsync(db, artifactId, granted, cancellationToken)
                .ConfigureAwait(false);
        }

        // RLS already hides another tenant's row; the project scope hides another project's row of the same org, so
        // unknown, foreign and foreign-project ids answer the one 404 and never an oracle.
        if (row is null
            || EvidenceProjectScope.Project(row.ProjectId, principal.ProjectScope, context.ProjectId) is not null)
        {
            return Result.Failure<ArtifactReadRow>(EvidenceErrors.ArtifactNotFound);
        }

        // A deleting or deleted row is gated by the read model whatever permission the caller holds, so the
        // lifecycle answer is asked first: 409, not the 403 a masked sensitive row earns.
        if (row.State is ArtifactState.PendingDelete or ArtifactState.Deleted)
        {
            return Result.Failure<ArtifactReadRow>(EvidenceErrors.ArtifactStateConflict);
        }

        if (row.ContentGated)
        {
            await AuditDenialAsync(context.OrgId, row.ProjectId, row, principal, cancellationToken).ConfigureAwait(false);

            return Result.Failure<ArtifactReadRow>(EvidenceErrors.SensitivityForbidden);
        }

        // Only a ready row has bytes at its final key: a pending or rejected upload would otherwise presign a key
        // that holds nothing or holds a staging object nobody verified.
        return string.Equals(row.State, Ready, StringComparison.Ordinal)
            ? Result.Success(row)
            : Result.Failure<ArtifactReadRow>(EvidenceErrors.ArtifactStateConflict);
    }

    /// <summary>
    /// Appends the one audit row a denied sensitive read needs, in its own scope and unit of work, and commits it:
    /// the pipeline writes no audit entry for a failing command and rolls the tenant transaction back, so a denial
    /// recorded there would never survive. Nothing of the artifact's content is recorded — only its id, its run,
    /// the actor and the outcome.
    /// </summary>
    private async Task AuditDenialAsync(
        Guid orgId,
        Guid projectId,
        ArtifactReadRow row,
        IRequestPrincipal principal,
        CancellationToken cancellationToken)
    {
        using var unit = audit.BeginUnit();
        await using var scope = await scopes
            .BeginAsync(orgId, projectId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        audit.Describe(new AuditObject(
            "artifact",
            row.Id.ToString("D"),
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["outcome"] = "sensitive_forbidden",
                ["run_id"] = row.RunId.ToString("D"),
            }));
        audit.Record(DeniedAction, AuditActorResolver.From(principal));

        await audit.FlushAsync(orgId, db, cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Presigns the <c>GET</c> of one artifact the gate already authorized, and audits the issuance of a sensitive
/// artifact (the grant half of the audited pairs). The URL is a bearer credential: it is never logged, thrown or
/// recorded — the audit row names the artifact, its run and the outcome, never the URL.
/// </summary>
/// <remarks>
/// The handler re-reads the row inside the pipeline's tenant transaction instead of receiving the key: the command
/// carries only the id, so the key, the state and the classification this answer is built from are the database's,
/// never a caller's. The gate has already refused an unknown, foreign or gated row, so the re-read only re-proves it
/// for the moment of issuance — a row that became unready or deleted in between is a conflict, not a URL.
/// </remarks>
/// <param name="storage">Presigner of the object store's <c>GET</c>.</param>
/// <param name="db">The scoped data access of the open tenant transaction.</param>
/// <param name="audit">The audit trail of the successful command.</param>
[AuditedAction("artifact.download")]
[Permission(Permissions.Artifact.Read)]
internal sealed class IssueArtifactDownloadUrlHandler(IObjectStorage storage, ITenantDb db, IAuditTrail audit)
    : ICommandHandler<IssueArtifactDownloadUrl, Result<DownloadUrlResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<DownloadUrlResponse>> Handle(
        IssueArtifactDownloadUrl command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Evidence, nameof(IssueArtifactDownloadUrl));
        cancellationToken.ThrowIfCancellationRequested();

        // `true` asks for the row as the store holds it: the permission was the gate's question, and a masked read
        // here would withhold the very key this command exists to presign.
        var row = await ArtifactReadModel
            .ReadAsync(db, command.ArtifactId, canReadSensitive: true, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return Result.Failure<DownloadUrlResponse>(EvidenceErrors.ArtifactNotFound);
        }

        // Covers the deleting and deleted rows: with the permission asked for, content_gated is true for those alone.
        if (row.ContentGated || !string.Equals(row.State, ArtifactState.Ready, StringComparison.Ordinal))
        {
            return Result.Failure<DownloadUrlResponse>(EvidenceErrors.ArtifactStateConflict);
        }

        var sensitive = string.Equals(
            row.EffectiveSensitivity, ArtifactSensitivity.Sensitive, StringComparison.Ordinal);

        // A normal artifact's issuance changes nothing worth the chain, so only the sensitive grant is recorded: the
        // stage would otherwise append a row for every ordinary view.
        if (!sensitive)
        {
            audit.Suppress();
        }
        else
        {
            audit.Describe(new AuditObject(
                "artifact",
                row.Id.ToString("D"),
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["outcome"] = "sensitive_granted",
                    ["run_id"] = row.RunId.ToString("D"),
                }));
        }

        var presigned = storage.PresignGet(row.ObjectKey ?? throw new InvalidOperationException(
            "a ready, ungated row always carries its object key"));

        return Result.Success(new DownloadUrlResponse(row.Id, presigned.Url, presigned.ExpiresAt));
    }
}
