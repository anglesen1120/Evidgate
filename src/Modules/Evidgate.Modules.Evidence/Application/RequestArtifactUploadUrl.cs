using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Endpoints;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Orchestrator.Contracts;
using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// Request a presigned <c>PUT</c> for one planned artifact (06.1.2 §5, <c>POST /v1/artifacts/upload-url</c>): the
/// run scope, the caller's bound producer slot, the manifest's planned <c>(execution_key, attempt_no)</c> and the
/// kind policy are checked, then the row is minted and its staging key presigned. <c>org_id</c> and
/// <c>project_id</c> are never command members — they come from the C2 tenant transaction (00 §5.4.1, T3).
/// <para>
/// <paramref name="CredentialId"/> and <paramref name="CiRunRef"/> are the caller's token identity, resolved by the
/// endpoint from <c>IRequestPrincipalAccessor</c> and passed in so the handler can bind the producer slot (T9/T85)
/// without a second principal lookup. They are server-derived, never request input, and are part of the idempotency
/// hash: a replay of the same <c>Idempotency-Key</c> by a different token identity is a 409
/// <c>idempotency_conflict</c>, so one principal can never receive another's stored presigned URL without the
/// producer-slot check. They are never serialized into a response or the request DTO.
/// </para>
/// </summary>
/// <param name="RunId">The run the artifact was captured in.</param>
/// <param name="ProducerId">The manifest shard's deterministic producer id (T54).</param>
/// <param name="ExecutionKey">The attempt's execution key, sha256 hex (00 §7).</param>
/// <param name="AttemptNo">The attempt number within the execution key, at least 1.</param>
/// <param name="Kind">One of the 00 §7 artifact kinds.</param>
/// <param name="MediaType">The declared media type, 1–255 characters.</param>
/// <param name="DeclaredBytes">The declared size in bytes, greater than zero.</param>
/// <param name="Checksum">The declared content address, <c>sha256:&lt;64 lowercase hex&gt;</c>.</param>
/// <param name="CredentialId">The caller's token id (<c>jti</c>), or null when it has none.</param>
/// <param name="CiRunRef">The caller's GitHub run reference, or null for a non-CI caller.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1, T92).</param>
internal sealed record RequestArtifactUploadUrl(
    Guid RunId,
    string ProducerId,
    string ExecutionKey,
    int AttemptNo,
    string Kind,
    string MediaType,
    long DeclaredBytes,
    string Checksum,
    string? CredentialId,
    string? CiRunRef,
    string IdempotencyKey)
    : ICommand<Result<UploadUrlResponse>>, IIdempotentCommand;

/// <summary>
/// Shape validation of <see cref="RequestArtifactUploadUrl"/> (C2 stage 2, before the tenant transaction): the
/// execution key, the attempt number, the media type and the producer id, all of which the database also constrains
/// (<c>artifact_execution_key_check</c>, <c>artifact_attempt_no_check</c>, <c>artifact_media_type_check</c>,
/// <c>producer_slot_producer_id_check</c>), plus generous caps on the <c>kind</c> and <c>checksum</c> length so a
/// multi-megabyte string is refused as <c>request_invalid</c> before it reaches the handler. Reasons are the
/// lower_snake vocabulary the other validators use (00 §9.2); the named 422 codes are produced by the handler
/// through <see cref="EvidenceErrors"/>, because a validator failure can only ever surface as
/// <c>request_invalid</c> with the code as a detail reason (the <c>Error.Unprocessable</c> contract).
/// </summary>
internal sealed class RequestArtifactUploadUrlValidator : AbstractValidator<RequestArtifactUploadUrl>
{
    /// <summary>The generous cap on the <c>kind</c> length in code points; a longer value is a client error.</summary>
    private const int MaxKindLength = 64;

    /// <summary>The generous cap on the <c>checksum</c> length in code points; a longer value is a client error.</summary>
    private const int MaxChecksumLength = 128;

    /// <summary>Builds the rules.</summary>
    public RequestArtifactUploadUrlValidator()
    {
        RuleFor(command => command.ExecutionKey)
            .Must(ArtifactShape.IsExecutionKey)
            .WithErrorCode("invalid_format");

        RuleFor(command => command.AttemptNo)
            .GreaterThanOrEqualTo(1)
            .WithErrorCode("out_of_range");

        RuleFor(command => command.MediaType)
            .Must(ArtifactShape.IsMediaType)
            .WithErrorCode("invalid_length");

        RuleFor(command => command.ProducerId)
            .Must(producerId => producerId is not null
                && producerId.Length is >= 1 and <= ProducerSlotLimits.MaxProducerIdLength)
            .WithErrorCode("invalid_length");

        // Only the length is bounded here: a null or in-range malformed value still reaches the handler and gets
        // its named code (kind_unknown / checksum_invalid), while an over-long one is a request_invalid.
        RuleFor(command => command.Kind)
            .Must(kind => kind is null || kind.EnumerateRunes().Count() <= MaxKindLength)
            .WithErrorCode("invalid_length");

        RuleFor(command => command.Checksum)
            .Must(checksum => checksum is null || checksum.EnumerateRunes().Count() <= MaxChecksumLength)
            .WithErrorCode("invalid_length");
    }
}

/// <summary>
/// Handler of <see cref="RequestArtifactUploadUrl"/> (06.1.2 §5): the pure in-memory shape refusals first (kind,
/// checksum, size, token identity), then inside ONE tenant transaction (1) the run scope, (2) the kind policy,
/// (3) the planned <c>(execution_key, attempt_no)</c> pair, (4) the producer-slot binding, (5) the mint and
/// (6) the presign. Every refusal is a named 00 §9.2 code; the presigned URL is a bearer credential and is never
/// logged, thrown or audited (S3).
/// </summary>
/// <remarks>
/// The audit action is <c>artifact.mint</c>: this command mints the same <c>artifact</c> row as
/// <see cref="CreateArtifactRecord"/>, and <see cref="ArtifactMinter"/> never describes an object itself, so exactly
/// one audit entry is written per request.
/// </remarks>
[AuditedAction("artifact.mint")]
[Permission(Permissions.Artifact.Write)]
internal sealed class RequestArtifactUploadUrlHandler(
    IRunPlanReader runs,
    IProducerRegistry producers,
    ArtifactMinter minter,
    IObjectStorage storage,
    ITenantDbContextFactory<EvidenceDbContext> contexts,
    ITenantScopeAccessor scopes,
    IAuditTrail audit)
    : ICommandHandler<RequestArtifactUploadUrl, Result<UploadUrlResponse>>
{
    /// <summary>The <c>project.state</c> that refuses a new upload (migration 0005, 06.4.4 AC2).</summary>
    private const string ProjectDeleted = "deleted";

    /// <inheritdoc />
    public async ValueTask<Result<UploadUrlResponse>> Handle(RequestArtifactUploadUrl command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Evidence, nameof(RequestArtifactUploadUrl));

        // The pure in-memory refusals run before any statement is built (F1): the kind, the checksum and the size,
        // in the same relative order as CreateArtifactRecord (06.1.1 §5). A
        // malformed body therefore costs no database work, kind_unknown is reachable, and the answer no longer
        // depends on the run's or the slot's state.
        if (!ArtifactKind.IsKnown(command.Kind))
        {
            return Result.Failure<UploadUrlResponse>(EvidenceErrors.KindUnknown);
        }

        if (!ArtifactShape.IsChecksum(command.Checksum))
        {
            return Result.Failure<UploadUrlResponse>(EvidenceErrors.ChecksumInvalid);
        }

        if (command.DeclaredBytes <= 0)
        {
            return Result.Failure<UploadUrlResponse>(EvidenceErrors.SizeOutOfRange);
        }

        var scope = scopes.Require();
        if (scope.ProjectId is not { } projectId)
        {
            return Result.Failure<UploadUrlResponse>(EvidenceErrors.ProjectRequired);
        }

        // (1) Run scope: the run must be visible to the caller's project — RLS makes another tenant's run
        // invisible, so an unknown id and a foreign id are the same null — and the project must not be deleting.
        var run = await runs.FindAsync(command.RunId, command.ExecutionKey, cancellationToken).ConfigureAwait(false);
        if (run is null || run.ProjectId != projectId)
        {
            return Result.Failure<UploadUrlResponse>(EvidenceErrors.RunNotFound);
        }

        if (string.Equals(run.ProjectState, ProjectDeleted, StringComparison.Ordinal))
        {
            return Result.Failure<UploadUrlResponse>(EvidenceErrors.ProjectDeleting);
        }

        // (2) Kind policy, one statement. A missing row fails closed: the policy is the only authority on what a
        // project accepts, and 06.1.2-T3.1's static defaults may later relax this to a permissive fallback.
        var policy = await ReadPolicyAsync(contexts, scope.OrgId, projectId, command.Kind, cancellationToken).ConfigureAwait(false);
        if (policy is null || !policy.CaptureEnabled)
        {
            return Result.Failure<UploadUrlResponse>(EvidenceErrors.KindDisabled);
        }

        if (command.DeclaredBytes > policy.MaxBytes)
        {
            return Result.Failure<UploadUrlResponse>(EvidenceErrors.PayloadTooLarge);
        }

        if (!MediaTypeAllowed(policy.MediaTypes, command.MediaType))
        {
            return Result.Failure<UploadUrlResponse>(EvidenceErrors.MediaTypeNotAllowed);
        }

        // (3) The (execution_key, attempt_no) pair must be planned. planned_execution has no attempt_no column, so
        // only the key's membership is checked here; the attempt number is validated for shape (>= 1) by the
        // command validator (T10/T17).
        if (!run.Planned)
        {
            return Result.Failure<UploadUrlResponse>(EvidenceErrors.UnknownExecutionKey);
        }

        // A caller with no token identity can never bind a slot: refuse it before the registry is called. It sits
        // after the run and policy checks so an unknown run stays the 404 every principal gets, never a 403 oracle.
        if (command.CredentialId is null)
        {
            return Result.Failure<UploadUrlResponse>(EvidenceErrors.NoTokenIdentity);
        }

        // (4) Bind the producer slot to the caller's token identity on the first accepted write (T9/T85). The
        // identity is the credential's jti plus the CI run reference. The registry's own error is returned
        // verbatim (unknown_producer 422, producer_slot_taken 403), and a failure rolls the tenant transaction back.
        var binding = await producers.BindOrCheckAsync(
            command.RunId,
            command.ProducerId,
            command.CredentialId,
            command.CiRunRef,
            cancellationToken).ConfigureAwait(false);
        if (!binding.IsSuccess)
        {
            return Result.Failure<UploadUrlResponse>(binding.Error!);
        }

        // (5) The mint. The upload-url body carries no client object key, so object_key_not_allowed is
        // structurally unreachable on this path. The policy row read above already carries the sensitivity, so the
        // minter is spared a second read of it (F2).
        var record = await minter.MintAsync(
            scope.OrgId,
            projectId,
            command.RunId,
            command.ExecutionKey,
            command.AttemptNo,
            command.Kind,
            command.MediaType,
            command.DeclaredBytes,
            command.Checksum,
            cancellationToken,
            policy.DefaultSensitivity).ConfigureAwait(false);

        // (6) Presign the staging key. The URL is a bearer credential: it is never logged, thrown or audited (S3).
        // The idempotency store keeps this response body — including the URL — for the key's lifetime, which is why
        // the URL's expiry is at most 15 minutes (T18).
        var stagingKey = ArtifactKeyMinter.StagingKey(scope.OrgId, record.ArtifactId);
        var presigned = storage.PresignPut(
            new PresignPutRequest(stagingKey, command.DeclaredBytes, ChecksumHex(command.Checksum)));

        audit.Describe(new AuditObject("artifact", record.ArtifactId.ToString("D")));

        return Result.Success(new UploadUrlResponse(
            record.ArtifactId,
            stagingKey,
            presigned.Url,
            presigned.RequiredHeaders,
            presigned.ExpiresAt));
    }

    /// <summary>The project's kind policy, or null when it has none for the kind (one statement, P3).</summary>
    private static async Task<ArtifactKindPolicy?> ReadPolicyAsync(
        ITenantDbContextFactory<EvidenceDbContext> contexts,
        Guid orgId,
        Guid projectId,
        string kind,
        CancellationToken cancellationToken)
    {
        await using var context = contexts.Create();

        return await context.KindPolicies
            .Where(policy => policy.OrgId == orgId && policy.ProjectId == projectId && policy.Kind == kind)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// True when <paramref name="mediaType"/> is one of the policy's accepted types (06.1.2 §5): the bare type
    /// (parameters after <c>;</c> stripped) is compared case-insensitively and <em>verbatim</em> — surrounding
    /// whitespace and control characters are not trimmed away, so the string the policy accepts is exactly the
    /// string that is persisted. A control character anywhere in the type is refused.
    /// </summary>
    private static bool MediaTypeAllowed(string[] allowed, string? mediaType)
    {
        if (mediaType is null || !ArtifactShape.HasNoControlCharacter(mediaType))
        {
            return false;
        }

        var bare = mediaType.Split(';', 2)[0];
        return allowed.Any(candidate => string.Equals(candidate, bare, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The 64 lowercase hex characters of a validated <c>sha256:&lt;hex&gt;</c> checksum (the presign form).</summary>
    private static string ChecksumHex(string checksum) => checksum["sha256:".Length..];
}
