using System.Net;
using System.Reflection;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Domain;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The row facts one completion needs (06.1.2 §5, T86 step 1), read as one bounded statement (P3): the artifact's
/// state-machine facts, the declared size/checksum/media type, the final object key, the project's status and the
/// kind policy the mint used. A missing policy row leaves the three policy members null.
/// </summary>
/// <param name="State">One of <see cref="ArtifactState.All"/>.</param>
/// <param name="Bytes">The declared size, persisted at mint (T1 decision 8).</param>
/// <param name="Checksum">The declared content address, <c>sha256:&lt;64 lowercase hex&gt;</c>.</param>
/// <param name="MediaType">The declared media type.</param>
/// <param name="ObjectKey">The final object key the row already holds (T1 decision 7).</param>
/// <param name="ProjectId">The project the artifact belongs to.</param>
/// <param name="FailureCode">The reason code of a rejected artifact; null otherwise.</param>
/// <param name="ProjectStatus">The project's status (migration 0005); <c>deleted</c> refuses the completion.</param>
/// <param name="MaxBytes">The kind policy's size cap, or null when the project has no policy row for the kind.</param>
/// <param name="MediaTypes">The kind policy's accepted media types, or null with no policy row.</param>
/// <param name="CaptureEnabled">The kind policy's capture switch, or null with no policy row.</param>
internal sealed record ArtifactFacts(
    string? State,
    long? Bytes,
    string Checksum,
    string MediaType,
    string ObjectKey,
    Guid ProjectId,
    string? FailureCode,
    string ProjectStatus,
    long? MaxBytes,
    string[]? MediaTypes,
    bool? CaptureEnabled);

/// <summary>
/// The endpoint-side verification of one completion (06.1.2 §5, T86 steps 1-4), run BEFORE the writer command
/// dispatches: it takes the C2 <c>artifact:write</c> decision the pipeline would reach (an unauthorized caller —
/// a viewer, a non-member, a token scoped to another project — is refused here, so it can never reach the storage
/// calls), reads the row facts in a read-only tenant scope, checks the scope and the state, then <c>HEAD</c>s the
/// staging object, verifies size/cap/checksum and promotes it to the final key. Every storage call runs with NO
/// transaction open — the read scope is disposed first and the command's tenant transaction opens only after this
/// returns (00 §5.7 P5, AC4) — and the verdict it derives travels into the command, which re-reads the row and
/// guards the state again under its own transaction.
/// <para>
/// The verdict is deterministic for a given artifact, which is what lets the idempotency store replay a retry: a
/// row already <c>ready</c> or <c>rejected</c> yields the stored outcome without touching the store, so the same
/// <c>Idempotency-Key</c> hashes to the same command.
/// </para>
/// </summary>
internal sealed class ArtifactCompletionVerifier(
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
               a.object_key,
               a.project_id,
               a.failure_code,
               p.status as project_status,
               kp.max_bytes,
               kp.media_types,
               kp.capture_enabled
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
    /// does not exist in that project, 409 for a project being deleted or a state that allows no completion,
    /// and otherwise the verdict the writer commits — a verified length, or a rejection carrying its reason.
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

        if (string.Equals(facts.ProjectStatus, ProjectDeleted, StringComparison.Ordinal))
        {
            return Result.Failure<ArtifactCompletionVerdict>(EvidenceErrors.ProjectDeleting);
        }

        // A stored outcome is returned as it stands (AC5): no storage call, and the verdict mirrors the row so a
        // replay of the same key derives the identical command.
        if (string.Equals(facts.State, ArtifactState.Ready, StringComparison.Ordinal))
        {
            return Result.Success(ArtifactCompletionVerdict.Accept(facts.Bytes));
        }

        if (string.Equals(facts.State, ArtifactState.Rejected, StringComparison.Ordinal))
        {
            return Result.Success(ArtifactCompletionVerdict.Reject(facts.FailureCode));
        }

        // Only the 06.1.2 §6 transitions are completable; pending_delete/deleted are the 409 the contract names.
        if (!IsCompletable(facts.State))
        {
            return Result.Failure<ArtifactCompletionVerdict>(EvidenceErrors.ArtifactStateConflict);
        }

        // The kind policy is the authority on the cap and the accepted media types, and a missing or switched-off
        // row fails closed exactly as it does at mint (T1 decision 3).
        if (facts.MaxBytes is not { } maxBytes || facts.CaptureEnabled != true)
        {
            return Result.Failure<ArtifactCompletionVerdict>(EvidenceErrors.KindDisabled);
        }

        if (!MediaTypeAllowed(facts.MediaTypes, facts.MediaType))
        {
            return Result.Success(ArtifactCompletionVerdict.Reject(ArtifactFailureCode.MediaTypeMismatch));
        }

        // T86 step 2/3: the staging key is derived, never read from the row (T1 decision 7).
        var stagingKey = ArtifactKeyMinter.StagingKey(context.OrgId, artifactId);
        var staging = await storage.HeadAsync(stagingKey, cancellationToken).ConfigureAwait(false);

        if (staging is not null)
        {
            if (Mismatch(staging, facts, maxBytes) is { } staged)
            {
                return Result.Success(ArtifactCompletionVerdict.Reject(staged));
            }

            // T86 step 4: promote the verified bytes. A store that reports the source gone between the HEAD and
            // the copy (the 24 h lifecycle) falls through to the retry path instead of failing the request.
            if (await TryPromoteAsync(stagingKey, facts.ObjectKey, cancellationToken).ConfigureAwait(false))
            {
                return Result.Success(ArtifactCompletionVerdict.Accept(staging.ContentLength));
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
            : Result.Success(ArtifactCompletionVerdict.Accept(final.ContentLength));
    }

    /// <summary>True when the state may still be completed (06.1.2 §6: <c>pending_upload</c> or <c>uploaded</c>).</summary>
    private static bool IsCompletable(string? state) =>
        string.Equals(state, ArtifactState.PendingUpload, StringComparison.Ordinal)
        || string.Equals(state, ArtifactState.Uploaded, StringComparison.Ordinal);

    /// <summary>
    /// The T86 step-3 verification of one stored object against the declared facts (AC3): the stored length must
    /// equal the declared <c>bytes</c> and stay within the kind policy's cap, and the SHA-256 the store recorded
    /// must equal the declared one. A store that records no checksum is not verified and fails closed. Null means
    /// the object is verified.
    /// </summary>
    /// <param name="head">What the store reported about the object.</param>
    /// <param name="facts">The declared facts of the row.</param>
    /// <param name="maxBytes">The kind policy's size cap.</param>
    private static string? Mismatch(ObjectHead head, ArtifactFacts facts, long maxBytes)
    {
        if (facts.Bytes != head.ContentLength)
        {
            return ArtifactFailureCode.SizeMismatch;
        }

        if (head.ContentLength > maxBytes)
        {
            return ArtifactFailureCode.TooLarge;
        }

        // The declared checksum is the 00 §7 content address the mint validated; anything else is not verifiable
        // and is refused rather than compared loosely.
        var declared = facts.Checksum.StartsWith(ChecksumPrefix, StringComparison.Ordinal)
            ? facts.Checksum[ChecksumPrefix.Length..]
            : null;

        return declared is not null && string.Equals(head.ChecksumSha256Hex, declared, StringComparison.OrdinalIgnoreCase)
            ? null
            : ArtifactFailureCode.ChecksumMismatch;
    }

    /// <summary>
    /// Copies the verified staging object to the final key (T86 step 4, idempotent on the destination). False when
    /// the store reports the source missing: the caller then checks the final key, exactly like the retry path,
    /// instead of turning a lifecycle race into a 500.
    /// </summary>
    /// <param name="stagingKey">The derived staging key.</param>
    /// <param name="objectKey">The final key the row holds.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    private async ValueTask<bool> TryPromoteAsync(string stagingKey, string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            await storage.CopyAsync(stagingKey, objectKey, cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (ObjectStorageException exception) when (IsAbsent(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// True when the store reported the source object absent: the adapter's own 404 rule (<c>HeadAsync</c> answers
    /// null for the same three answers, so the copy path reads the race the same way).
    /// </summary>
    /// <param name="exception">The failed storage call.</param>
    private static bool IsAbsent(ObjectStorageException exception) =>
        exception.HttpStatus == HttpStatusCode.NotFound
        || string.Equals(exception.S3ErrorCode, "NoSuchKey", StringComparison.Ordinal)
        || string.Equals(exception.S3ErrorCode, "NotFound", StringComparison.Ordinal);

    /// <summary>
    /// True when <paramref name="mediaType"/> is one of the policy's accepted types (06.1.2 §5, AC3): the bare
    /// type (parameters after <c>;</c> stripped) is compared case-insensitively and <em>verbatim</em> — surrounding
    /// whitespace and control characters are not trimmed away — and a control character anywhere in the type is
    /// refused. The same comparison the mint applies (06.1.2-T1), so both ends of the upload agree on what the
    /// policy accepts.
    /// </summary>
    /// <param name="allowed">The policy's accepted types, or null with no policy row.</param>
    /// <param name="mediaType">The declared media type of the artifact.</param>
    private static bool MediaTypeAllowed(string[]? allowed, string? mediaType)
    {
        if (allowed is null || mediaType is null || !ArtifactShape.HasNoControlCharacter(mediaType))
        {
            return false;
        }

        var bare = mediaType.Split(';', 2)[0];

        return allowed.Any(candidate => string.Equals(candidate, bare, StringComparison.OrdinalIgnoreCase));
    }
}
