using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// Mint one artifact row for a planned <c>(execution_key, attempt_no)</c> before the <c>case_attempt</c> exists
/// (06.1.1 §5, T17). The object key is chosen by the server only: the command carries no key, and a
/// <paramref name="ClientObjectKey"/> is refused outright (AC1). <c>org_id</c> and <c>project_id</c> are never
/// command members — they come from the C2 tenant transaction (00 §5.4.1, T3).
/// </summary>
/// <param name="RunId">The run the artifact was captured in (no FK yet; 05.2.1-T1 creates <c>run</c>).</param>
/// <param name="ExecutionKey">The attempt's execution key, sha256 hex (00 §7).</param>
/// <param name="AttemptNo">The attempt number within the execution key, at least 1.</param>
/// <param name="Kind">One of the 00 §7 artifact kinds.</param>
/// <param name="MediaType">The declared media type, 1–255 characters.</param>
/// <param name="DeclaredBytes">The declared size in bytes, greater than zero.</param>
/// <param name="Checksum">The declared content address, <c>sha256:&lt;64 lowercase hex&gt;</c>.</param>
/// <param name="ClientObjectKey">A client-supplied object key, if any; always refused (AC1).</param>
internal sealed record CreateArtifactRecord(
    Guid RunId,
    string ExecutionKey,
    int AttemptNo,
    string Kind,
    string MediaType,
    long DeclaredBytes,
    string Checksum,
    string? ClientObjectKey)
    : ICommand<Result<ArtifactRecord>>;

/// <summary>The mint result (06.1.1 §5): the row's id, its server-chosen key and its state.</summary>
/// <param name="ArtifactId">The minted artifact's UUIDv7 id.</param>
/// <param name="ObjectKey">The server-chosen object key (FR-06-1).</param>
/// <param name="State">The stored state, <c>pending_upload</c> (00 §8.4).</param>
internal sealed record ArtifactRecord(Guid ArtifactId, string ObjectKey, string State);

/// <summary>
/// Shape validation of <see cref="CreateArtifactRecord"/> (C2 stage 2, before the tenant transaction): the
/// execution key, the attempt number and the media type, all of which the database also constrains
/// (<c>artifact_execution_key_check</c>, <c>artifact_attempt_no_check</c>, <c>artifact_media_type_check</c>).
/// Reasons are the lower_snake vocabulary the other validators use (00 §9.2); the four named 422 codes of
/// 06.1.1 §5 are produced by the handler through <see cref="EvidenceErrors"/>, because a validator failure can
/// only ever surface as <c>request_invalid</c> with the code as a detail reason (the <c>Error.Unprocessable</c>
/// contract).
/// </summary>
internal sealed partial class CreateArtifactRecordValidator : AbstractValidator<CreateArtifactRecord>
{
    /// <summary>The 00 §7 execution-key shape: sha256 hex, lowercase (the 0028 CHECK).</summary>
    [GeneratedRegex("\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExecutionKeyShape();

    /// <summary>Builds the rules.</summary>
    public CreateArtifactRecordValidator()
    {
        RuleFor(command => command.ExecutionKey)
            .Must(key => key is not null && ExecutionKeyShape().IsMatch(key))
            .WithErrorCode("invalid_format")
            .When(command => command.ClientObjectKey is null);

        RuleFor(command => command.AttemptNo)
            .GreaterThanOrEqualTo(1)
            .WithErrorCode("out_of_range")
            .When(command => command.ClientObjectKey is null);

        RuleFor(command => command.MediaType)
            // Postgres char_length counts code points, not UTF-16 units (0028 artifact_media_type_check).
            .Must(mediaType => mediaType is not null && mediaType.EnumerateRunes().Count() is >= 1 and <= 255)
            .WithErrorCode("invalid_length")
            .When(command => command.ClientObjectKey is null);
    }
}

/// <summary>
/// Handler of <see cref="CreateArtifactRecord"/>: the four 06.1.1 §5 refusals in the C2 order (client key, kind,
/// checksum, size) return before any statement is built, then the row is minted — <c>default_sensitivity</c>
/// resolved server-side from <c>artifact_kind_policy</c> with the <see cref="ArtifactSensitivity.ForKind"/>
/// fallback, the key minted by <see cref="ArtifactKeyMinter"/>, and the row inserted in state
/// <c>pending_upload</c> with <c>scan_state</c> <c>pending</c> and no attempt (AC2, T17, T19).
/// </summary>
[AuditedAction("artifact.mint")]
[Permission(Permissions.Artifact.Write)]
internal sealed partial class CreateArtifactRecordHandler(
    ITenantDbContextFactory<EvidenceDbContext> contexts,
    ITenantScopeAccessor scopes,
    IAuditTrail audit)
    : ICommandHandler<CreateArtifactRecord, Result<ArtifactRecord>>
{
    /// <summary>The 00 §7 checksum shape: <c>sha256:</c> and 64 lowercase hex characters, anchored with <c>\z</c>.</summary>
    [GeneratedRegex("\\Asha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumShape();

    /// <inheritdoc />
    public async ValueTask<Result<ArtifactRecord>> Handle(CreateArtifactRecord command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Evidence, nameof(CreateArtifactRecord));

        // The four 06.1.1 §5 refusals, in the C2 order, before anything touches the database: a client key is
        // rejected before any value is looked at, then the kind, the checksum and the size. Each returns its own
        // 00 §9.2 code (AC1), so the caller never sees a generic request_invalid for a named refusal.
        if (command.ClientObjectKey is not null)
        {
            return Result.Failure<ArtifactRecord>(EvidenceErrors.ObjectKeyNotAllowed);
        }

        if (!ArtifactKind.IsKnown(command.Kind))
        {
            return Result.Failure<ArtifactRecord>(EvidenceErrors.KindUnknown);
        }

        if (command.Checksum is null || !ChecksumShape().IsMatch(command.Checksum))
        {
            return Result.Failure<ArtifactRecord>(EvidenceErrors.ChecksumInvalid);
        }

        if (command.DeclaredBytes <= 0)
        {
            return Result.Failure<ArtifactRecord>(EvidenceErrors.SizeOutOfRange);
        }

        // org_id and project_id come from the tenant transaction, never from the command (00 §5.4.1, T3).
        var scope = scopes.Require();
        if (scope.ProjectId is not { } projectId)
        {
            return Result.Failure<ArtifactRecord>(EvidenceErrors.ProjectRequired);
        }

        var orgId = scope.OrgId;

        await using var context = contexts.Create();

        var sensitivity = await context.KindPolicies
            .Where(policy => policy.OrgId == orgId && policy.ProjectId == projectId && policy.Kind == command.Kind)
            .Select(policy => policy.DefaultSensitivity)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? ArtifactSensitivity.ForKind(command.Kind);

        var artifactId = Guid.CreateVersion7();
        var objectKey = ArtifactKeyMinter.Mint(orgId, projectId, command.RunId, command.ExecutionKey, command.AttemptNo, artifactId);

        await context.Artifacts.AddAsync(
            new Artifact(
                artifactId,
                orgId,
                projectId,
                command.RunId,
                command.ExecutionKey,
                command.AttemptNo,
                command.Kind,
                objectKey,
                command.MediaType,
                command.Checksum,
                sensitivity,
                scanState: ArtifactScanState.Pending,
                state: ArtifactState.PendingUpload),
            cancellationToken).ConfigureAwait(false);

        // A UUIDv7 collision would repeat the id and therefore the key: a bug, not a client conflict, so the
        // unique violation is left to surface as an infrastructure fault rather than masked as a 409.
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        audit.Describe(new AuditObject("artifact", artifactId.ToString("D")));

        return Result.Success(new ArtifactRecord(artifactId, objectKey, ArtifactState.PendingUpload), 201);
    }
}
