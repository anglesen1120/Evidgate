using System.Globalization;
using System.Text.Json;
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
using Evidgate.BuildingBlocks.SharedKernel.Security;
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
/// <param name="Platform">Taxonomy of the suite (03.1.4); null means <c>web</c>.</param>
/// <param name="Level">Its level; null means the platform's default level.</param>
/// <param name="Purpose">Its purpose; null means <c>regression</c>.</param>
internal sealed record CreateSuiteCommand(
    Guid ProjectId,
    string Name,
    string Slug,
    string IdempotencyKey,
    string? Platform = null,
    string? Level = null,
    string? Purpose = null)
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
            .Must(slug => slug is not null && AsciiShape.IsMatch(slug, 2, 63, AsciiShape.LowerAlnum, AsciiShape.LowerAlnumDash, AsciiShape.LowerAlnum))
            .WithErrorCode("invalid_format");
        RuleFor(command => command.Platform)
            .Must(platform => Taxonomy.Canonical(platform, Taxonomy.Platforms) is not null)
            .WithErrorCode("invalid_value")
            .When(command => command.Platform is not null);
        RuleFor(command => command.Level)
            .Must(level => Taxonomy.Canonical(level, Taxonomy.Levels) is not null)
            .WithErrorCode("invalid_value")
            .When(command => command.Level is not null);
        RuleFor(command => command.Purpose)
            .Must(purpose => Taxonomy.Canonical(purpose, Taxonomy.Purposes) is not null)
            .WithErrorCode("invalid_value")
            .When(command => command.Purpose is not null);
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
        var platform = command.Platform is null ? Taxonomy.Default.Platform : Taxonomy.Canonical(command.Platform, Taxonomy.Platforms);
        if (platform is null)
        {
            return Result.Failure<SuiteDto>(SuiteErrors.Invalid(
                "platform must be one of web, api, unit or mobile", [new ErrorDetail("/platform", "invalid_value")]));
        }

        // A new suite has no members, so it carries no variant that could name a native mobile platform: `mobile` here
        // would be a promise no runner can keep. The rule is the revision rule (AC4), decided on evidence this request
        // does not have, so the answer and the reason are the same.
        if (platform == "mobile")
        {
            return Result.Failure<SuiteDto>(SuiteErrors.Invalid(
                "a mobile suite needs a member whose variant names a native mobile platform (android, ios); a browser variant is device emulation and belongs to a web suite",
                [new ErrorDetail("/platform", "mobile_capability_required")]));
        }

        var level = command.Level is null ? Taxonomy.DefaultLevelFor(platform) : Taxonomy.Canonical(command.Level, Taxonomy.Levels);
        if (level is null)
        {
            return Result.Failure<SuiteDto>(SuiteErrors.Invalid(
                "level must be one of unit, integration, e2e or smoke", [new ErrorDetail("/level", "invalid_value")]));
        }

        var purpose = command.Purpose is null ? Taxonomy.Default.Purpose : Taxonomy.Canonical(command.Purpose, Taxonomy.Purposes);
        if (purpose is null)
        {
            return Result.Failure<SuiteDto>(SuiteErrors.Invalid(
                "purpose must be one of regression, smoke, release_gate or exploratory", [new ErrorDetail("/purpose", "invalid_value")]));
        }

        try
        {
            await db.ExecuteAsync(
                SuiteReadSql.InsertSuite,
                new
                {
                    id,
                    org_id = scopes.Require().OrgId,
                    project_id = command.ProjectId,
                    name = command.Name.Trim(),
                    slug = command.Slug,
                    platform,
                    level,
                    purpose,
                },
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
    IFixtureRegistry fixtures,
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

        // Everything that only reads — validation, the membership check, normalisation, the hash and the serialised
        // payloads — happens before the suite row is locked: a project, a case revision and a test revision never change,
        // so the answer stays true, and the lock is held for statements only. The suite is re-read under the lock below.
        var peek = await db.QuerySingleOrDefaultAsync<SuiteRow>(SuiteReadSql.SuiteForRead, new { suite_id = command.SuiteId }, cancellationToken).ConfigureAwait(false);
        if (peek is null || CatalogScope.Project(peek.ProjectId, principals, tenant) is not null)
        {
            return Result.Failure<SuiteRevisionHead>(SuiteErrors.SuiteNotFound);
        }

        // The taxonomy the suite held at the peek. A request that omits a member inherits it, so it must inherit from
        // the row the write commits against, not from a row a concurrent revision may have replaced (see the re-plan).
        var peekedTaxonomy = peek.AsTaxonomy();
        var planned = await planner.PlanAsync(db, fixtures, peek.ProjectId, command.Body, peekedTaxonomy, cancellationToken).ConfigureAwait(false);
        if (!planned.IsSuccess)
        {
            return Result.Failure<SuiteRevisionHead>(planned.Error!);
        }

        var plan = planned.Value;
        var payloads = BuildPayloads(plan);

        // ---- under the lock: numbering, dedupe, the inserts, the head.
        // A bounded wait: a stuck holder surfaces as 503 and a retry, not as a pile of waiting requests.
        await db.ExecuteAsync("select set_config('lock_timeout', '5000', true)", null, cancellationToken).ConfigureAwait(false);
        SuiteRow? suite;
        try
        {
            suite = await db.QuerySingleOrDefaultAsync<SuiteRow>(SuiteReadSql.SuiteForUpdate, new { suite_id = command.SuiteId }, cancellationToken).ConfigureAwait(false);
        }
        catch (Npgsql.PostgresException exception) when (exception.SqlState == "55P03")
        {
            return Result.Failure<SuiteRevisionHead>(new Error(ApiErrorCode.Unavailable, "the suite is being changed by another request; retry shortly", 503));
        }

        if (suite is null)
        {
            return Result.Failure<SuiteRevisionHead>(SuiteErrors.SuiteNotFound);
        }

        if (suite.Status != "active")
        {
            return Result.Failure<SuiteRevisionHead>(SuiteErrors.Archived);
        }

        // A concurrent revision may have moved the head (and the taxonomy with it) between the peek and this lock, so
        // the inherited values planned above can be stale: re-plan against the row that is now locked, or an omitted
        // `purpose` would silently revert the newer value and the hash would pin the reverted taxonomy.
        if (suite.AsTaxonomy() != peekedTaxonomy)
        {
            var replanned = await planner.PlanAsync(db, fixtures, suite.ProjectId, command.Body, suite.AsTaxonomy(), cancellationToken).ConfigureAwait(false);
            if (!replanned.IsSuccess)
            {
                return Result.Failure<SuiteRevisionHead>(replanned.Error!);
            }

            plan = replanned.Value;
            payloads = BuildPayloads(plan);
        }

        var existing = await db.QuerySingleOrDefaultAsync<ExistingRevision>(
            SuiteReadSql.ExistingByHash,
            new { org_id = orgId, suite_id = command.SuiteId, revision_hash = plan.RevisionHash },
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // Re-submitting the content of an older revision makes it the current definition again: the head follows the
            // content, so a revert is visible as a head move and not silently ignored. The suite's taxonomy follows the
            // head in the same statement, because the hash pins the taxonomy the revision was written under.
            var moved = suite.HeadRevisionId != existing.Id || suite.AsTaxonomy() != plan.Taxonomy;
            if (moved)
            {
                await db.ExecuteAsync(
                    SuiteReadSql.SetHeadAndTaxonomy,
                    new { suite_id = command.SuiteId, revision_id = existing.Id, platform = plan.Taxonomy.Platform, level = plan.Taxonomy.Level, purpose = plan.Taxonomy.Purpose },
                    cancellationToken).ConfigureAwait(false);
            }

            audit.Describe(new AuditObject("suite_revision", existing.Id.ToString("D")));

            return Result.Success(new SuiteRevisionHead(
                existing.Id, existing.RevisionNo, plan.RevisionHash, existing.Status, false, plan.Warnings, moved,
                plan.Taxonomy.Platform, plan.Taxonomy.Level, plan.Taxonomy.Purpose));
        }

        var revisionNo = await db.ScalarAsync<int>(SuiteReadSql.NextRevisionNo, new { org_id = orgId, suite_id = command.SuiteId }, cancellationToken).ConfigureAwait(false);
        var revisionId = Guid.CreateVersion7();

        if (payloads.Variants is not null)
        {
            await db.ExecuteAsync(SuiteReadSql.InsertVariants, new { org_id = orgId, project_id = suite.ProjectId, rows = payloads.Variants }, cancellationToken).ConfigureAwait(false);
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
                platform = plan.Taxonomy.Platform,
                level = plan.Taxonomy.Level,
                purpose = plan.Taxonomy.Purpose,
                shard = plan.ShardJson,
                retry = plan.RetryJson,
                environment = plan.EnvironmentJson,
                created_by = principals.Current?.UserId,
            },
            cancellationToken).ConfigureAwait(false);

        if (payloads.Members is not null)
        {
            var inserted = await db.ExecuteAsync(
                SuiteReadSql.InsertMembers,
                new { org_id = orgId, revision_id = revisionId, project_id = suite.ProjectId, rows = payloads.Members },
                cancellationToken).ConfigureAwait(false);
            if (inserted != plan.Members.Count)
            {
                throw new InvalidOperationException("suite members were not all inserted; the transaction is rolled back");
            }
        }

        if (payloads.Edges is not null)
        {
            await db.ExecuteAsync(SuiteReadSql.InsertEdges, new { org_id = orgId, revision_id = revisionId, rows = payloads.Edges }, cancellationToken).ConfigureAwait(false);
        }

        await db.ExecuteAsync(
            SuiteReadSql.SetHeadAndTaxonomy,
            new { suite_id = command.SuiteId, revision_id = revisionId, platform = plan.Taxonomy.Platform, level = plan.Taxonomy.Level, purpose = plan.Taxonomy.Purpose },
            cancellationToken).ConfigureAwait(false);

        audit.Describe(new AuditObject("suite_revision", revisionId.ToString("D")));

        return Result.Success(new SuiteRevisionHead(
            revisionId, revisionNo, plan.RevisionHash, "draft", true, plan.Warnings, true,
            plan.Taxonomy.Platform, plan.Taxonomy.Level, plan.Taxonomy.Purpose), 201);
    }

    private static string Json<T>(IEnumerable<T> rows) => JsonSerializer.Serialize(rows, Snake);

    /// <summary>The set-based payloads of one plan; rebuilt when the plan is re-made under the lock.</summary>
    /// <param name="plan">The plan whose members and edges are written.</param>
    private static Payloads BuildPayloads(SuiteRevisionPlan plan)
    {
        var variants = plan.Members.Count == 0
            ? null
            : Json(plan.Members.Select(member => member.Variant).DistinctBy(variant => variant.Hash)
                .OrderBy(variant => variant.Hash, StringComparer.Ordinal) // one global order, or two suites inserting shared variants could deadlock
                .Select(variant => new VariantRow(Guid.CreateVersion7(), variant.Hash, JsonSerializer.Deserialize<JsonElement>(variant.ParamsJson))));
        var memberIds = plan.Members.Select(_ => Guid.CreateVersion7()).ToArray();
        var members = plan.Members.Count == 0
            ? null
            : Json(plan.Members.Select((member, ordinal) => new MemberRow(memberIds[ordinal], member.CaseRevisionId, member.Variant.Hash, ordinal, member.Required)));
        var edges = plan.Edges.Count == 0
            ? null
            : Json(plan.Edges.Select(edge => new EdgeRow(Guid.CreateVersion7(), memberIds[edge.Parent], memberIds[edge.Child])));

        return new Payloads(variants, members, edges);
    }

    private sealed record Payloads(string? Variants, string? Members, string? Edges);

    private sealed record VariantRow(Guid Id, string VariantHash, JsonElement Params);

    private sealed record MemberRow(Guid Id, Guid CaseRevisionId, string VariantHash, int Ordinal, bool Required);

    private sealed record EdgeRow(Guid Id, Guid Parent, Guid Child);

    private sealed class SuiteRow
    {
        public Guid Id { get; set; }

        public Guid ProjectId { get; set; }

        public string Status { get; set; } = string.Empty;

        public Guid? HeadRevisionId { get; set; }

        public string Platform { get; set; } = "web";

        public string Level { get; set; } = "e2e";

        public string Purpose { get; set; } = "regression";

        /// <summary>The three columns as one value; named as a method so it never shadows the <see cref="Domain.Taxonomy"/> catalogue.</summary>
        public SuiteTaxonomy AsTaxonomy() => new(Platform, Level, Purpose);
    }

    private sealed class ExistingRevision
    {
        public Guid Id { get; set; }

        public int RevisionNo { get; set; }

        public string Status { get; set; } = string.Empty;
    }
}
