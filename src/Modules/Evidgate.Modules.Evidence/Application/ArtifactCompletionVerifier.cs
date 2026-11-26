using System.Net;
using System.Reflection;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The row facts one completion needs (06.1.2 §5, T86 step 1), read as one bounded statement (P3): the artifact's
/// state-machine facts, the kind, the declared size/checksum/media type, the final object key, the project's
/// status and the kind policy the mint used. A missing policy row leaves the four policy members null, and
/// <see cref="ArtifactKindDefaults.Resolve"/> then decides from the artifact's <see cref="Kind"/> whether the
/// static default applies or the project has disabled the kind (06.1.2-T3.1).
/// </summary>
/// <param name="State">One of <see cref="ArtifactState.All"/>.</param>
/// <param name="Bytes">The declared size, persisted at mint (T1 decision 8).</param>
/// <param name="Checksum">The declared content address, <c>sha256:&lt;64 lowercase hex&gt;</c>.</param>
/// <param name="MediaType">The declared media type.</param>
/// <param name="Kind">One of the 00 §7 kinds; the kind the static-default lookup is keyed by.</param>
/// <param name="ObjectKey">The final object key the row already holds (T1 decision 7).</param>
/// <param name="ProjectId">The project the artifact belongs to.</param>
/// <param name="FailureCode">The reason code of a rejected artifact; null otherwise.</param>
/// <param name="ProjectStatus">The project's status (migration 0005); <c>deleted</c> refuses the completion.</param>
/// <param name="MaxBytes">The kind policy row's size cap, or null when the project has no row for the kind (the static default then caps it).</param>
/// <param name="MediaTypes">The kind policy row's accepted media types, or null with no row (the static default's list then applies).</param>
/// <param name="CaptureEnabled">The kind policy row's capture switch, or null with no row.</param>
/// <param name="DefaultSensitivity">
/// The kind policy row's sensitivity, or null with no row. The completion itself reads only the cap, the accepted
/// types and the capture switch; the row is passed to the shared resolution whole, so the mint's and the
/// completion's resolution of one artifact cannot differ.
/// </param>
internal sealed record ArtifactFacts(
    string? State,
    long? Bytes,
    string Checksum,
    string MediaType,
    string Kind,
    string ObjectKey,
    Guid ProjectId,
    string? FailureCode,
    string ProjectStatus,
    long? MaxBytes,
    string[]? MediaTypes,
    bool? CaptureEnabled,
    string? DefaultSensitivity);

/// <summary>
/// The endpoint-side verification of one completion (06.1.2 §5, T86 steps 1-4), run BEFORE the writer command
/// dispatches: it takes the C2 <c>artifact:write</c> decision the pipeline would reach (an unauthorized caller —
/// a viewer, a non-member, a token scoped to another project — is refused here, so it can never reach the storage
/// calls), reads the row facts in a read-only tenant scope, checks the scope and the state, re-resolves the kind
/// policy (the project's row against the static per-kind defaults, 06.1.2-T3.1), then <c>HEAD</c>s the
/// staging object, verifies size/cap/checksum, sniffs its leading bytes against the declared media type before the
/// promotion (so a mismatch is never copied to the final key), promotes it to the final key, re-verifies the
/// promoted object and sniffs it again (06.3.1 AC5). Every storage call runs with NO
/// transaction open — the read scope is disposed first and the command's tenant transaction opens only after this
/// returns (00 §5.7 P5, AC4) — and the verdict it derives travels into the command, which re-reads the row and
/// guards the state again under its own transaction.
/// <para>
/// The verdict is deterministic for a given artifact, which is what lets the idempotency store replay a retry: a
/// row already <c>ready</c> or <c>rejected</c> yields the stored outcome without touching the store, so the same
/// <c>Idempotency-Key</c> hashes to the same command.
/// </para>
/// </summary>
internal sealed partial class ArtifactCompletionVerifier(
    ITenantScopeFactory scopes,
    ITenantDb db,
    IRequestTenantContextAccessor tenant,
    IPermissionEvaluator permissions,
    IObjectStorage storage)
{
    /// <summary>The <c>project.status</c> that refuses a completion (migration 0005, 06.4.4 AC2).</summary>
    private const string ProjectDeleted = "deleted";

    /// <summary>
    /// The permission the C2 authorization stage enforces for <see cref="CompleteArtifactUpload"/>: read from
    /// the handler's own <see cref="PermissionAttribute"/> so this endpoint-side gate and the stage can never
    /// disagree — a gate weaker than the stage would let an unauthorized caller reach the storage calls below.
    /// </summary>
    private static readonly string RequiredPermission =
        typeof(CompleteArtifactUploadHandler).GetCustomAttribute<PermissionAttribute>()?.Permission
        ?? throw new InvalidOperationException(
            "CompleteArtifactUploadHandler must declare [Permission] (00 §5.7 S2)");

    /// <summary>The declared-checksum prefix of the 00 §7 content address.</summary>
    private const string ChecksumPrefix = "sha256:";

    /// <summary>
    /// The row facts of one artifact, the project status and the kind policy as one statement: the artifact is
    /// visible to the scope's org through RLS, the project join also fails for another tenant, and the policy is
    /// left-joined so a project without one is still a row (and fails closed later).
    /// </summary>
    private const string RowFactsSql = """
        select a.state,
               a.bytes,
               a.checksum,
               a.media_type,
               a.kind,
               a.object_key,
               a.project_id,
               a.failure_code,
               p.status as project_status,
               kp.max_bytes,
               kp.media_types,
               kp.capture_enabled,
               kp.default_sensitivity
          from public.artifact a
          join public.project p on p.id = a.project_id
          left join public.artifact_kind_policy kp
            on kp.org_id = a.org_id
           and kp.project_id = a.project_id
           and kp.kind = a.kind
         where a.id = @artifact_id
        """;

    /// <summary>
    /// Verifies the object of one artifact (06.1.2 §5, T86): 403 when the caller is refused (no project in the
    /// credential, or the C2 <c>artifact:write</c> decision the pipeline would reach), 404 for an artifact that
    /// does not exist in that project, 409 for a
    /// project being deleted or a state that allows no completion, and otherwise the verdict the writer commits —
    /// a verified length, or a rejection carrying its reason.
    /// </summary>
    /// <param name="artifactId">The artifact the route addressed.</param>
    /// <param name="principal">The caller's token identity, as the C2 authorization stage would evaluate it.</param>
    /// <param name="cancellationToken">Cancels the reads and the storage calls.</param>
    /// <returns>The verdict, or the named refusal the endpoint renders as the 00 §9.2 envelope.</returns>
    public async ValueTask<Result<ArtifactCompletionVerdict>> VerifyAsync(
        Guid artifactId,
        IRequestPrincipal principal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var context = tenant.Require();

        // The route names no project, so the project comes from the credential (T1 decision 10): an org-scoped
        // principal is refused before any read or storage call, so it can never probe another project's artifacts.
        if (context.ProjectId is not { } projectId)
        {
            return Result.Failure<ArtifactCompletionVerdict>(EvidenceErrors.ProjectRequired);
        }

        ArtifactFacts? facts;
        PermissionDecision decision;

        // One read-only scope, disposed before the first storage call: nothing below runs inside a transaction
        // (00 §5.7 P5), and the writer command opens its own scope when it dispatches.
        await using (var scope = await scopes
            .BeginAsync(context.OrgId, projectId, readOnlyTransaction: true, cancellationToken)
            .ConfigureAwait(false))
        {
            // The C2 authorization stage (stage 4) runs only after this method returns, so without this gate the
            // HEAD and the COPY below would run for a caller the pipeline is about to refuse (a viewer, a
            // non-member, a token scoped to another project). The stage's own evaluator is asked here first, in
            // the same tenant scope — the membership rows the member path reads are RLS-protected — and a denial
            // returns the identical 403 the stage would produce, before any read or storage call.
            decision = await permissions
                .EvaluateAsync(RequiredPermission, principal, context, cancellationToken)
                .ConfigureAwait(false);

            facts = decision.IsGranted
                ? await db
                    .QuerySingleOrDefaultAsync<ArtifactFacts>(RowFactsSql, new { artifact_id = artifactId }, cancellationToken)
                    .ConfigureAwait(false)
                : null;
        }

        if (!decision.IsGranted)
        {
            return Result.Failure<ArtifactCompletionVerdict>(decision.ToError());
        }

        // RLS already hides another tenant's row; the project comparison hides another project's row of the same
        // org, so an unknown, a foreign and a foreign-project id are one answer and never an oracle.
        if (facts is null || facts.ProjectId != projectId)
        {
            return Result.Failure<ArtifactCompletionVerdict>(EvidenceErrors.ArtifactNotFound);
        }

        // A stored outcome is returned as it stands (AC5): no storage call, and the verdict mirrors the row so a
        // replay of the same key derives the identical command. It is returned before the project-deleting guard
        // (C-F4): a replay of an already committed artifact answers the stored 200 whatever the project's status
        // since, and the guard below therefore only ever refuses a row that could still be completed.
        if (string.Equals(facts.State, ArtifactState.Ready, StringComparison.Ordinal))
        {
            return Result.Success(ArtifactCompletionVerdict.Accept(facts.Bytes));
        }

        if (string.Equals(facts.State, ArtifactState.Rejected, StringComparison.Ordinal))
        {
            return Result.Success(ArtifactCompletionVerdict.Reject(facts.FailureCode));
        }

        if (string.Equals(facts.ProjectStatus, ProjectDeleted, StringComparison.Ordinal))
        {
            return Result.Failure<ArtifactCompletionVerdict>(EvidenceErrors.ProjectDeleting);
        }

        // Only the 06.1.2 §6 transitions are completable; pending_delete/deleted are the 409 the contract names.
        if (!IsCompletable(facts.State))
        {
            return Result.Failure<ArtifactCompletionVerdict>(EvidenceErrors.ArtifactStateConflict);
        }

        // The effective policy is the project's row resolved against the static per-kind defaults (06.1.2-T3.1),
        // by the same rule the mint applied. Only a kind with neither a row nor a default, and a row whose capture
        // switch is off, fail closed — exactly as at mint (T1 decision 3).
        var policy = ArtifactKindDefaults.Resolve(
            facts.Kind,
            facts.MaxBytes is { } rowMaxBytes
                ? new KindPolicyRow(rowMaxBytes, facts.MediaTypes ?? [], facts.CaptureEnabled == true, facts.DefaultSensitivity ?? ArtifactSensitivity.ForKind(facts.Kind))
                : null);
        if (policy is null || !policy.CaptureEnabled)
        {
            return Result.Failure<ArtifactCompletionVerdict>(EvidenceErrors.KindDisabled);
        }

        if (!MediaTypeAllowed(policy.MediaTypes, facts.MediaType))
        {
            return Result.Success(ArtifactCompletionVerdict.Reject(ArtifactFailureCode.MediaTypeMismatch));
        }

        // The effective cap the stored object is checked against: the resolved policy's, never the raw row's, so a
        // row above a kind's default cannot widen the cap at completion either (D-A9).
        var maxBytes = policy.MaxBytes;

        // T86 step 2/3: the staging key is derived, never read from the row (T1 decision 7).
        var stagingKey = ArtifactKeyMinter.StagingKey(context.OrgId, artifactId);
        var staging = await storage.HeadAsync(stagingKey, cancellationToken).ConfigureAwait(false);

        if (staging is not null)
        {
            if (Mismatch(staging, facts, maxBytes) is { } staged)
            {
                return Result.Success(ArtifactCompletionVerdict.Reject(staged));
            }

            // 06.3.1 AC5 / plan §7: the pending object is sniffed BEFORE the promotion, so a mismatching
            // artifact is never copied to the final key. Every declared type is sniffed now (06.3.1-T2.2: a
            // type outside the table is a mismatch, fail closed), and the read is pinned to the ETag the HEAD
            // above reported, so the bytes are the object that HEAD verified. Only a failure refuses here; a
            // prefix read that finds the staging object gone (the 24 h lifecycle race) falls through to the
            // copy, which reports the missing source the retry path below handles.
            if (await StagingSniffFailureAsync(stagingKey, facts, staging.ETag, cancellationToken).ConfigureAwait(false)
                is { } stagingFailure)
            {
                return Result.Success(ArtifactCompletionVerdict.Reject(stagingFailure));
            }

            // T86 step 4: promote the verified bytes. A store that reports the source gone between the HEAD and
            // the copy (the 24 h lifecycle) falls through to the retry path instead of failing the request.
            if (await TryPromoteAsync(stagingKey, facts.ObjectKey, cancellationToken).ConfigureAwait(false))
            {
                // S-F2: the copy is not a verification. The source could have been replaced between the HEAD above
                // and the COPY, so the object the commit would mark ready is re-verified at the FINAL key with the
                // same size/cap/checksum rule the staging check applied — a mismatch is rejected with the same
                // reason codes, never committed ready. The final key is then sniffed again (the bytes could also
                // have been swapped between the staging read and the copy); this is the happy path's one extra
                // HEAD, while the retry path below already HEADs the final key.
                var promoted = await storage.HeadAsync(facts.ObjectKey, cancellationToken).ConfigureAwait(false);

                if (promoted is null)
                {
                    return Result.Success(ArtifactCompletionVerdict.Reject(ArtifactFailureCode.ObjectMissing));
                }

                return Mismatch(promoted, facts, maxBytes) is { } afterCopy
                    ? Result.Success(ArtifactCompletionVerdict.Reject(afterCopy))
                    : Result.Success(await SniffAsync(facts, promoted.ContentLength, promoted.ETag, cancellationToken).ConfigureAwait(false));
            }
        }

        // The retry path (T86, AC5): a previous attempt may have copied the bytes before its commit failed, and
        // the lifecycle may have collected the staging object since, so the final key is the only candidate left.
        var final = await storage.HeadAsync(facts.ObjectKey, cancellationToken).ConfigureAwait(false);

        if (final is null)
        {
            return Result.Success(ArtifactCompletionVerdict.Reject(ArtifactFailureCode.ObjectMissing));
        }

        return Mismatch(final, facts, maxBytes) is { } mismatched
            ? Result.Success(ArtifactCompletionVerdict.Reject(mismatched))
            : Result.Success(await SniffAsync(facts, final.ContentLength, final.ETag, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>True when the state may still be completed (06.1.2 §6: <c>pending_upload</c> or <c>uploaded</c>).</summary>
    private static bool IsCompletable(string? state) =>
        string.Equals(state, ArtifactState.PendingUpload, StringComparison.Ordinal)
        || string.Equals(state, ArtifactState.Uploaded, StringComparison.Ordinal);
}
