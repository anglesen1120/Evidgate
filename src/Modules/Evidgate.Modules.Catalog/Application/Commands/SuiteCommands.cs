using System.Globalization;
using System.Text.Json;
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

/// <summary>Create a suite (<c>POST /v1/projects/{p}/suites</c>, 03.2.1 §5).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Name">Display name.</param>
/// <param name="Slug">Slug, unique in the project.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record CreateSuiteCommand(Guid ProjectId, string Name, string Slug, string IdempotencyKey)
    : ICommand<Result<SuiteDto>>, IIdempotentCommand;

/// <summary>Shape validation of <see cref="CreateSuiteCommand"/>.</summary>
internal sealed class CreateSuiteCommandValidator : AbstractValidator<CreateSuiteCommand>
{
    /// <summary>Builds the rules.</summary>
    public CreateSuiteCommandValidator()
    {
        RuleFor(command => command.Name)
            .Must(name => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 128 && TextRules.IsClean(name))
            .WithErrorCode("invalid_length");
        RuleFor(command => command.Slug)
            .Must(slug => slug is not null && System.Text.RegularExpressions.Regex.IsMatch(slug, "^[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            .WithErrorCode("invalid_format");
    }
}

/// <summary>Handler of <see cref="CreateSuiteCommand"/>: 404 for a project the tenant cannot see, 409 <c>suite_slug_taken</c>.</summary>
[AuditedAction("suite.create")]
[Permission(Permissions.Suite.Write)]
internal sealed class CreateSuiteCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit)
    : ICommandHandler<CreateSuiteCommand, Result<SuiteDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<SuiteDto>> Handle(CreateSuiteCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(CreateSuiteCommand));

        if (CatalogScope.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<SuiteDto>(mismatch);
        }

        var exists = await db.ScalarAsync<int?>(CatalogReadSql.ProjectExists, new { project_id = command.ProjectId }, cancellationToken).ConfigureAwait(false);
        if (exists is null)
        {
            return Result.Failure<SuiteDto>(CatalogErrors.ProjectNotFound);
        }

        var id = Guid.CreateVersion7();
        try
        {
            await db.ExecuteAsync(
                SuiteReadSql.InsertSuite,
                new { id, org_id = scopes.Require().OrgId, project_id = command.ProjectId, name = command.Name.Trim(), slug = command.Slug },
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation && exception.ConstraintName == "suite_project_slug_key")
        {
            return Result.Failure<SuiteDto>(SuiteErrors.SlugTaken);
        }

        audit.Describe(new AuditObject("suite", id.ToString("D")));

        var created = await db.QuerySingleOrDefaultAsync<SuiteDto>(SuiteReadSql.SuiteById, new { suite_id = id }, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("the suite was inserted in this transaction yet cannot be read back");

        return Result.Success(created, 201);
    }
}

/// <summary>Create a suite revision (<c>POST /v1/suites/{s}/revisions</c>, 03.2.1 AC1–AC5).</summary>
/// <param name="SuiteId">The suite from the route.</param>
/// <param name="Body">The authored revision.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record CreateSuiteRevisionCommand(Guid SuiteId, CreateSuiteRevisionRequest Body, string IdempotencyKey)
    : ICommand<Result<SuiteRevisionHead>>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="CreateSuiteRevisionCommand"/>. The suite row is locked first, which serialises the allocation of
/// <c>revision_no</c> (AC5): two concurrent creates get n and n+1, never a lost revision. Identical content (same hash in
/// the same suite) returns the existing revision and allocates nothing. Everything is written in one transaction with
/// a handful of set-based statements, whatever the size of the revision.
/// </summary>
[AuditedAction("suite_revision.create")]
[Permission(Permissions.Suite.Write)]
internal sealed class CreateSuiteRevisionCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    SuiteRevisionPlanner planner,
    IAuditTrail audit)
    : ICommandHandler<CreateSuiteRevisionCommand, Result<SuiteRevisionHead>>
{
    private static readonly System.Text.Json.JsonSerializerOptions Snake = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <inheritdoc />
    public async ValueTask<Result<SuiteRevisionHead>> Handle(CreateSuiteRevisionCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(CreateSuiteRevisionCommand));

        var orgId = scopes.Require().OrgId;
        var suite = await db.QuerySingleOrDefaultAsync<SuiteRow>(SuiteReadSql.SuiteForUpdate, new { suite_id = command.SuiteId }, cancellationToken).ConfigureAwait(false);

        if (suite is null || CatalogScope.Project(suite.ProjectId, principals, tenant) is not null)
        {
            return Result.Failure<SuiteRevisionHead>(SuiteErrors.SuiteNotFound);
        }

        if (suite.Status != "active")
        {
            return Result.Failure<SuiteRevisionHead>(SuiteErrors.Archived);
        }

        var planned = await planner.PlanAsync(db, suite.ProjectId, command.Body, cancellationToken).ConfigureAwait(false);
        if (!planned.IsSuccess)
        {
            return Result.Failure<SuiteRevisionHead>(planned.Error!);
        }

        var plan = planned.Value;

        var existing = await db.QuerySingleOrDefaultAsync<ExistingRevision>(
            SuiteReadSql.ExistingByHash,
            new { org_id = orgId, suite_id = command.SuiteId, revision_hash = plan.RevisionHash },
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            audit.Describe(new AuditObject("suite_revision", existing.Id.ToString("D")));

            return Result.Success(new SuiteRevisionHead(existing.Id, existing.RevisionNo, plan.RevisionHash, existing.Status, false, plan.Warnings));
        }

        var revisionNo = await db.ScalarAsync<int>(SuiteReadSql.NextRevisionNo, new { org_id = orgId, suite_id = command.SuiteId }, cancellationToken).ConfigureAwait(false);
        var revisionId = Guid.CreateVersion7();

        var variants = plan.Members.Select(member => member.Variant).DistinctBy(variant => variant.Hash)
            .Select(variant => new VariantRow(Guid.CreateVersion7(), variant.Hash, JsonSerializer.Deserialize<JsonElement>(variant.ParamsJson)));
        if (plan.Members.Count > 0)
        {
            await db.ExecuteAsync(SuiteReadSql.InsertVariants, new { org_id = orgId, project_id = suite.ProjectId, rows = Json(variants) }, cancellationToken).ConfigureAwait(false);
        }

        await db.ExecuteAsync(
            SuiteReadSql.InsertRevision,
            new
            {
                id = revisionId,
                org_id = orgId,
                suite_id = command.SuiteId,
                revision_no = revisionNo,
                revision_hash = plan.RevisionHash,
                shard = plan.ShardJson,
                retry = plan.RetryJson,
                environment = plan.EnvironmentJson,
                created_by = principals.Current?.UserId,
            },
            cancellationToken).ConfigureAwait(false);

        var memberIds = plan.Members.Select(_ => Guid.CreateVersion7()).ToArray();
        if (plan.Members.Count > 0)
        {
            var rows = plan.Members.Select((member, ordinal) => new MemberRow(memberIds[ordinal], member.CaseRevisionId, member.Variant.Hash, ordinal, member.Required));
            await db.ExecuteAsync(
                SuiteReadSql.InsertMembers,
                new { org_id = orgId, revision_id = revisionId, project_id = suite.ProjectId, rows = Json(rows) },
                cancellationToken).ConfigureAwait(false);
        }

        if (plan.Edges.Count > 0)
        {
            var rows = plan.Edges.Select(edge => new EdgeRow(Guid.CreateVersion7(), memberIds[edge.Parent], memberIds[edge.Child]));
            await db.ExecuteAsync(SuiteReadSql.InsertEdges, new { org_id = orgId, revision_id = revisionId, rows = Json(rows) }, cancellationToken).ConfigureAwait(false);
        }

        await db.ExecuteAsync(SuiteReadSql.SetHead, new { suite_id = command.SuiteId, revision_id = revisionId }, cancellationToken).ConfigureAwait(false);

        audit.Describe(new AuditObject("suite_revision", revisionId.ToString("D")));

        return Result.Success(new SuiteRevisionHead(revisionId, revisionNo, plan.RevisionHash, "draft", true, plan.Warnings), 201);
    }

    private static string Json<T>(IEnumerable<T> rows) => JsonSerializer.Serialize(rows, Snake);

    private sealed record VariantRow(Guid Id, string VariantHash, JsonElement Params);

    private sealed record MemberRow(Guid Id, Guid CaseRevisionId, string VariantHash, int Ordinal, bool Required);

    private sealed record EdgeRow(Guid Id, Guid Parent, Guid Child);

    private sealed class SuiteRow
    {
        public Guid Id { get; set; }

        public Guid ProjectId { get; set; }

        public string Status { get; set; } = string.Empty;
    }

    private sealed class ExistingRevision
    {
        public Guid Id { get; set; }

        public int RevisionNo { get; set; }

        public string Status { get; set; } = string.Empty;
    }
}
