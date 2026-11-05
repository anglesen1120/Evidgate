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
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;
using Evidgate.Modules.Catalog.Infrastructure.Persistence;
using FluentValidation;
using Mediator;
using Npgsql;

namespace Evidgate.Modules.Catalog.Application.Commands;

/// <summary>Create a fixture with its first revision (<c>POST /v1/projects/{p}/fixtures</c>, 03.3.1 §5).</summary>
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

        var fixtureId = Guid.CreateVersion7();
        try
        {
            await db.ExecuteAsync(
                FixtureReadSql.InsertFixture,
                new
                {
                    id = fixtureId,
                    org_id = scopes.Require().OrgId,
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

        var revision = RevisionInput.From(command.SeedVersion, command.BodyRef, command.TimeoutS, command.CleanupPolicy);
        var revisionHash = FixtureHasher.Hash(command.FixtureKey, revision.SeedVersion, revision.TimeoutS, revision.CleanupPolicy, revision.BodyRef);
        var revisionId = Guid.CreateVersion7();

        await db.ExecuteAsync(
            FixtureReadSql.InsertRevision,
            new
            {
                id = revisionId,
                org_id = scopes.Require().OrgId,
                fixture_id = fixtureId,
                project_id = command.ProjectId,
                fixture_key = command.FixtureKey,
                revision_no = 1,
                revision_hash = revisionHash,
                seed_version = revision.SeedVersion,
                timeout_s = revision.TimeoutS,
                cleanup_policy = revision.CleanupPolicy,
                resource_prefix = FixtureHasher.ResourcePrefix(fixtureId, revisionHash),
                body_ref = revision.BodyRef,
                created_by = principals.Current?.UserId,
            },
            cancellationToken).ConfigureAwait(false);

        audit.Describe(new AuditObject("fixture", fixtureId.ToString("D")));

        return Result.Success(new FixtureRevisionHead(fixtureId, command.FixtureKey, revisionId, 1, revisionHash, true), 201);
    }
}

/// <summary>Append a revision to an existing fixture (<c>POST /v1/fixtures/{f}/revisions</c>, 03.3.1 §5).</summary>
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
/// Handler of <see cref="CreateFixtureRevisionCommand"/>. The per-fixture advisory lock serialises the allocation of
/// <c>revision_no</c> and the hash dedupe: two concurrent appends get n and n+1, and identical content answers the
/// existing revision instead of allocating another. Everything read-only — validation, the hash, the resource prefix —
/// happens before the lock, so the lock covers statements only.
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

        var orgId = scopes.Require().OrgId;
        var peek = await db.QuerySingleOrDefaultAsync<FixtureRow>(FixtureReadSql.FixtureForRead, new { fixture_id = command.FixtureId }, cancellationToken).ConfigureAwait(false);
        if (peek is null || CatalogScope.Project(peek.ProjectId, principals, tenant) is not null)
        {
            return Result.Failure<FixtureRevisionHead>(FixtureErrors.NotFound);
        }

        var revision = RevisionInput.From(command.Body.SeedVersion, command.Body.BodyRef, command.Body.TimeoutS, command.Body.CleanupPolicy);
        var revisionHash = FixtureHasher.Hash(peek.FixtureKey, revision.SeedVersion, revision.TimeoutS, revision.CleanupPolicy, revision.BodyRef);

        // ---- under the lock: numbering, dedupe, the insert. A bounded wait turns a stuck holder into 503 + retry.
        await db.ExecuteAsync("select set_config('lock_timeout', '5000', true)", null, cancellationToken).ConfigureAwait(false);
        FixtureRow? fixture;
        try
        {
            await db.ExecuteAsync(FixtureReadSql.AcquireFixtureLock, new { fixture_id = command.FixtureId }, cancellationToken).ConfigureAwait(false);
            fixture = await db.QuerySingleOrDefaultAsync<FixtureRow>(FixtureReadSql.FixtureForRead, new { fixture_id = command.FixtureId }, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == "55P03")
        {
            return Result.Failure<FixtureRevisionHead>(new Error(ApiErrorCode.Unavailable, "the fixture is being changed by another request; retry shortly", 503));
        }

        if (fixture is null)
        {
            return Result.Failure<FixtureRevisionHead>(FixtureErrors.NotFound);
        }

        var existing = await db.QuerySingleOrDefaultAsync<ExistingRevision>(
            FixtureReadSql.ExistingByHash,
            new { org_id = orgId, fixture_id = command.FixtureId, revision_hash = revisionHash },
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            audit.Describe(new AuditObject("fixture_revision", existing.Id.ToString("D")));

            return Result.Success(new FixtureRevisionHead(fixture.Id, fixture.FixtureKey, existing.Id, existing.RevisionNo, revisionHash, false));
        }

        var revisionNo = await db.ScalarAsync<int>(FixtureReadSql.NextRevisionNo, new { org_id = orgId, fixture_id = command.FixtureId }, cancellationToken).ConfigureAwait(false);
        var revisionId = Guid.CreateVersion7();

        await db.ExecuteAsync(
            FixtureReadSql.InsertRevision,
            new
            {
                id = revisionId,
                org_id = orgId,
                fixture_id = command.FixtureId,
                project_id = fixture.ProjectId,
                fixture_key = fixture.FixtureKey,
                revision_no = revisionNo,
                revision_hash = revisionHash,
                seed_version = revision.SeedVersion,
                timeout_s = revision.TimeoutS,
                cleanup_policy = revision.CleanupPolicy,
                resource_prefix = FixtureHasher.ResourcePrefix(fixture.Id, revisionHash),
                body_ref = revision.BodyRef,
                created_by = principals.Current?.UserId,
            },
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
