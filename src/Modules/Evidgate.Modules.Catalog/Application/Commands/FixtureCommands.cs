using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;
using Evidgate.Modules.Catalog.Infrastructure.Persistence;
using FluentValidation;
using Mediator;
using Npgsql;

namespace Evidgate.Modules.Catalog.Application.Commands;

/// <summary>Create a fixture with its first revision (<c>POST /v1/projects/{p}/fixtures</c>).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="FixtureKey">Key, unique in the project.</param>
/// <param name="SeedVersion">Version of the data the body seeds.</param>
/// <param name="BodyRef">A repository path or artifact id; never the body.</param>
/// <param name="TimeoutS">Executor timeout in seconds; null means the default.</param>
/// <param name="CleanupPolicy">Cleanup policy; null means the default.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record CreateFixtureCommand(
    Guid ProjectId,
    string FixtureKey,
    string SeedVersion,
    string BodyRef,
    string IdempotencyKey,
    int? TimeoutS = null,
    string? CleanupPolicy = null)
    : ICommand<Result<FixtureRevisionHead>>, IIdempotentCommand;

/// <summary>Shape validation of <see cref="CreateFixtureCommand"/>; every rule is the column's CHECK, so a valid request is an insertable row.</summary>
internal sealed class CreateFixtureCommandValidator : AbstractValidator<CreateFixtureCommand>
{
    /// <summary>Builds the rules.</summary>
    public CreateFixtureCommandValidator()
    {
        RuleFor(command => command.FixtureKey).Must(FixtureRules.IsValidKey).WithErrorCode("invalid_format");
        RuleFor(command => command.SeedVersion).Must(FixtureRules.IsValidSeedVersion).WithErrorCode("invalid_length");
        RuleFor(command => command.BodyRef).Must(FixtureRules.IsValidBodyRef).WithErrorCode("invalid_value");
        RuleFor(command => command.TimeoutS)
            .Must(timeout => timeout is >= 1 and <= FixtureRules.MaxTimeoutSeconds)
            .WithErrorCode("out_of_range")
            .When(command => command.TimeoutS is not null);
        RuleFor(command => command.CleanupPolicy)
            .Must(FixtureRules.IsValidCleanupPolicy)
            .WithErrorCode("invalid_value")
            .When(command => command.CleanupPolicy is not null);
    }
}

/// <summary>Handler of <see cref="CreateFixtureCommand"/>: 404 for a project the tenant cannot see, 409 <c>fixture_key_taken</c>.</summary>
[AuditedAction("fixture.create")]
[Permission(Permissions.Fixture.Write)]
internal sealed class CreateFixtureCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit)
    : ICommandHandler<CreateFixtureCommand, Result<FixtureRevisionHead>>
{
    /// <inheritdoc />
    public async ValueTask<Result<FixtureRevisionHead>> Handle(CreateFixtureCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(CreateFixtureCommand));

        if (CatalogScope.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<FixtureRevisionHead>(mismatch);
        }

        var exists = await db.ScalarAsync<int?>(CatalogReadSql.ProjectExists, new { project_id = command.ProjectId }, cancellationToken).ConfigureAwait(false);
        if (exists is null)
        {
            return Result.Failure<FixtureRevisionHead>(CatalogErrors.ProjectNotFound);
        }

        var revision = RevisionInput.From(command.SeedVersion, command.BodyRef, command.TimeoutS, command.CleanupPolicy);
        var revisionHash = FixtureHasher.Hash(command.FixtureKey, revision.SeedVersion, revision.TimeoutS, revision.CleanupPolicy, revision.BodyRef);

        return await CreateFirstRevisionAsync(command, revision, revisionHash, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Inserts the fixture and its first revision; a taken key is the caller's 409, never a retried insert.</summary>
    private async Task<Result<FixtureRevisionHead>> CreateFirstRevisionAsync(
        CreateFixtureCommand command,
        RevisionInput revision,
        string revisionHash,
        CancellationToken cancellationToken)
    {
        var fixtureId = Guid.CreateVersion7();
        var orgId = scopes.Require().OrgId;
        try
        {
            await db.ExecuteAsync(
                FixtureReadSql.InsertFixture,
                new
                {
                    id = fixtureId,
                    org_id = orgId,
                    project_id = command.ProjectId,
                    fixture_key = command.FixtureKey,
                    created_by = principals.Current?.UserId,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation && exception.ConstraintName == "fixture_project_key")
        {
            // A key names a fixture for good: versions of it are appended to the existing fixture, never created beside it.
            return Result.Failure<FixtureRevisionHead>(FixtureErrors.KeyTaken);
        }

        var revisionId = Guid.CreateVersion7();
        await FixtureRevisionWriter.InsertAsync(
            db,
            new RevisionRow(revisionId, orgId, fixtureId, command.ProjectId, command.FixtureKey, 1, revision, revisionHash, principals.Current?.UserId),
            cancellationToken).ConfigureAwait(false);

        audit.Describe(new AuditObject("fixture", fixtureId.ToString("D")));

        return Result.Success(new FixtureRevisionHead(fixtureId, command.FixtureKey, revisionId, 1, revisionHash, true), 201);
    }
}

/// <summary>Append a revision to an existing fixture (<c>POST /v1/fixtures/{f}/revisions</c>).</summary>
/// <param name="FixtureId">The fixture from the route.</param>
/// <param name="Body">The revision to append.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record CreateFixtureRevisionCommand(Guid FixtureId, CreateFixtureRevisionRequest Body, string IdempotencyKey)
    : ICommand<Result<FixtureRevisionHead>>, IIdempotentCommand;

/// <summary>Shape validation of <see cref="CreateFixtureRevisionCommand"/>: the same rules as create, minus the key the fixture already holds.</summary>
internal sealed class CreateFixtureRevisionCommandValidator : AbstractValidator<CreateFixtureRevisionCommand>
{
    /// <summary>Builds the rules.</summary>
    public CreateFixtureRevisionCommandValidator()
    {
        RuleFor(command => command.Body.SeedVersion).Must(FixtureRules.IsValidSeedVersion).WithErrorCode("invalid_length");
        RuleFor(command => command.Body.BodyRef).Must(FixtureRules.IsValidBodyRef).WithErrorCode("invalid_value");
        RuleFor(command => command.Body.TimeoutS)
            .Must(timeout => timeout is >= 1 and <= FixtureRules.MaxTimeoutSeconds)
            .WithErrorCode("out_of_range")
            .When(command => command.Body.TimeoutS is not null);
        RuleFor(command => command.Body.CleanupPolicy)
            .Must(FixtureRules.IsValidCleanupPolicy)
            .WithErrorCode("invalid_value")
            .When(command => command.Body.CleanupPolicy is not null);
    }
}

/// <summary>
/// Handler of <see cref="CreateFixtureRevisionCommand"/>: the per-fixture advisory lock serialises the number
/// allocation and the hash dedupe, and everything read-only happens before it so the lock covers statements only.
/// </summary>
[AuditedAction("fixture_revision.create")]
[Permission(Permissions.Fixture.Write)]
internal sealed class CreateFixtureRevisionCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit)
    : ICommandHandler<CreateFixtureRevisionCommand, Result<FixtureRevisionHead>>
{
    /// <inheritdoc />
    public async ValueTask<Result<FixtureRevisionHead>> Handle(CreateFixtureRevisionCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(CreateFixtureRevisionCommand));

        var peek = await db.QuerySingleOrDefaultAsync<FixtureRow>(FixtureReadSql.FixtureForRead, new { fixture_id = command.FixtureId }, cancellationToken).ConfigureAwait(false);
        if (peek is null || CatalogScope.Project(peek.ProjectId, principals, tenant) is not null)
        {
            return Result.Failure<FixtureRevisionHead>(FixtureErrors.NotFound);
        }

        var revision = RevisionInput.From(command.Body.SeedVersion, command.Body.BodyRef, command.Body.TimeoutS, command.Body.CleanupPolicy);
        var revisionHash = FixtureHasher.Hash(peek.FixtureKey, revision.SeedVersion, revision.TimeoutS, revision.CleanupPolicy, revision.BodyRef);

        var locked = await LockAndReadAsync(command.FixtureId, cancellationToken).ConfigureAwait(false);
        if (locked.Error is { } lockError)
        {
            return Result.Failure<FixtureRevisionHead>(lockError);
        }

        return await AppendAsync(locked.Value, scopes.Require().OrgId, revision, revisionHash, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes the per-fixture lock and re-reads the row under it. The bounded wait makes a holder that never finishes a
    /// 503 the client may retry, instead of a request parked for as long as the holder lives.
    /// </summary>
    private async Task<Result<FixtureRow>> LockAndReadAsync(Guid fixtureId, CancellationToken cancellationToken)
    {
        FixtureRow? fixture;
        try
        {
            await db.ExecuteAsync(FixtureReadSql.LockTimeout, null, cancellationToken).ConfigureAwait(false);
            await db.ExecuteAsync(FixtureReadSql.AcquireFixtureLock, new { fixture_id = fixtureId }, cancellationToken).ConfigureAwait(false);
            fixture = await db.QuerySingleOrDefaultAsync<FixtureRow>(FixtureReadSql.FixtureForRead, new { fixture_id = fixtureId }, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return Result.Failure<FixtureRow>(FixtureErrors.Contended);
        }

        if (fixture is null)
        {
            return Result.Failure<FixtureRow>(FixtureErrors.NotFound);
        }

        return Result.Success(fixture);
    }

    /// <summary>The dedupe, the number and the insert, all inside the lock the caller holds.</summary>
    private async Task<Result<FixtureRevisionHead>> AppendAsync(
        FixtureRow fixture,
        Guid orgId,
        RevisionInput revision,
        string revisionHash,
        CancellationToken cancellationToken)
    {
        var existing = await db.QuerySingleOrDefaultAsync<ExistingRevision>(
            FixtureReadSql.ExistingByHash,
            new { org_id = orgId, fixture_id = fixture.Id, revision_hash = revisionHash },
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            audit.Describe(new AuditObject("fixture_revision", existing.Id.ToString("D")));

            return Result.Success(new FixtureRevisionHead(fixture.Id, fixture.FixtureKey, existing.Id, existing.RevisionNo, revisionHash, false));
        }

        var revisionNo = await db.ScalarAsync<int>(FixtureReadSql.NextRevisionNo, new { org_id = orgId, fixture_id = fixture.Id }, cancellationToken).ConfigureAwait(false);
        var revisionId = Guid.CreateVersion7();
        await FixtureRevisionWriter.InsertAsync(
            db,
            new RevisionRow(revisionId, orgId, fixture.Id, fixture.ProjectId, fixture.FixtureKey, revisionNo, revision, revisionHash, principals.Current?.UserId),
            cancellationToken).ConfigureAwait(false);

        audit.Describe(new AuditObject("fixture_revision", revisionId.ToString("D")));

        return Result.Success(new FixtureRevisionHead(fixture.Id, fixture.FixtureKey, revisionId, revisionNo, revisionHash, true), 201);
    }

    private sealed class FixtureRow
    {
        public Guid Id { get; set; }

        public Guid ProjectId { get; set; }

        public string FixtureKey { get; set; } = string.Empty;
    }

    private sealed class ExistingRevision
    {
        public Guid Id { get; set; }

        public int RevisionNo { get; set; }
    }
}

/// <summary>One normalised revision request: the defaults applied and the seed version trimmed, hashed as it is stored.</summary>
/// <param name="SeedVersion">The seed version, trimmed.</param>
/// <param name="BodyRef">The body pointer.</param>
/// <param name="TimeoutS">The effective timeout.</param>
/// <param name="CleanupPolicy">The effective cleanup policy.</param>
internal sealed record RevisionInput(string SeedVersion, string BodyRef, int TimeoutS, string CleanupPolicy)
{
    /// <summary>Applies the defaults, so create and append cannot disagree about what a missing member means.</summary>
    /// <param name="seedVersion">The authored seed version.</param>
    /// <param name="bodyRef">The authored body pointer.</param>
    /// <param name="timeoutS">The authored timeout, or null.</param>
    /// <param name="cleanupPolicy">The authored cleanup policy, or null.</param>
    public static RevisionInput From(string seedVersion, string bodyRef, int? timeoutS, string? cleanupPolicy) =>
        new(
            seedVersion.Trim(),
            bodyRef,
            timeoutS ?? FixtureRules.DefaultTimeoutSeconds,
            cleanupPolicy ?? FixtureRules.DefaultCleanupPolicy);
}

/// <summary>One revision row exactly as it is stored; the row both create paths build, so their columns cannot drift.</summary>
/// <param name="Id">Revision id.</param>
/// <param name="OrgId">Owning organization.</param>
/// <param name="FixtureId">The fixture the revision belongs to.</param>
/// <param name="ProjectId">The fixture's project.</param>
/// <param name="FixtureKey">The fixture's key, denormalised onto the revision.</param>
/// <param name="RevisionNo">The number allocated under the per-fixture lock.</param>
/// <param name="Revision">The normalised request.</param>
/// <param name="RevisionHash">The content identity.</param>
/// <param name="CreatedBy">The caller's user id, when known.</param>
internal sealed record RevisionRow(
    Guid Id,
    Guid OrgId,
    Guid FixtureId,
    Guid ProjectId,
    string FixtureKey,
    int RevisionNo,
    RevisionInput Revision,
    string RevisionHash,
    Guid? CreatedBy);

/// <summary>The single revision insert, so a column added to the table cannot be missed by one of the two create paths.</summary>
internal static class FixtureRevisionWriter
{
    public static Task<int> InsertAsync(ITenantDb db, RevisionRow row, CancellationToken cancellationToken) =>
        db.ExecuteAsync(
            FixtureReadSql.InsertRevision,
            new
            {
                id = row.Id,
                org_id = row.OrgId,
                fixture_id = row.FixtureId,
                project_id = row.ProjectId,
                fixture_key = row.FixtureKey,
                revision_no = row.RevisionNo,
                revision_hash = row.RevisionHash,
                seed_version = row.Revision.SeedVersion,
                timeout_s = row.Revision.TimeoutS,
                cleanup_policy = row.Revision.CleanupPolicy,
                resource_prefix = FixtureHasher.ResourcePrefix(row.FixtureId, row.RevisionHash),
                body_ref = row.Revision.BodyRef,
                created_by = row.CreatedBy,
            },
            cancellationToken);
}
