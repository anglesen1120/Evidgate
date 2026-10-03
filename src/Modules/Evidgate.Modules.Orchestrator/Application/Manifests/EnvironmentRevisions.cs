using System.Text.Json;
using System.Text.RegularExpressions;
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
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Mediator;
using Npgsql;

namespace Evidgate.Modules.Orchestrator.Application.Manifests;

/// <summary>Append an environment revision (<c>POST /v1/projects/{p}/environments/{e}/revisions</c>, 05.1.2 §5).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="EnvironmentKey">The environment key from the route (<c>{e}</c>).</param>
/// <param name="Body">The body, already valid against <c>environment-revision.v1.json</c>.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record CreateEnvironmentRevisionCommand(Guid ProjectId, string EnvironmentKey, CreateEnvironmentRevisionRequest Body, string IdempotencyKey)
    : ICommand<Result<EnvironmentRevisionCreated>>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="CreateEnvironmentRevisionCommand"/>. The environment is created on its first revision; its row is
/// then locked, which serialises numbering (AC1). Identical content returns the stored revision with 200 and makes it
/// current again; new content gets the next number with 201. The revision row itself is never updated (AC5).
/// </summary>
[AuditedAction("environment_revision.create")]
[Permission(Permissions.Environment.Write)]
internal sealed partial class CreateEnvironmentRevisionCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit)
    : ICommandHandler<CreateEnvironmentRevisionCommand, Result<EnvironmentRevisionCreated>>
{
    /// <inheritdoc />
    public async ValueTask<Result<EnvironmentRevisionCreated>> Handle(CreateEnvironmentRevisionCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Orchestrator, nameof(CreateEnvironmentRevisionCommand));

        if (OrchestratorScope.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<EnvironmentRevisionCreated>(mismatch);
        }

        if (!EnvironmentKey().IsMatch(command.EnvironmentKey))
        {
            return Result.Failure<EnvironmentRevisionCreated>(OrchestratorErrors.EnvironmentRevisionInvalid(
                "the environment key must be lower-case letters, digits, '_' or '-' (at most 64)",
                [new ErrorDetail("e", "invalid_format")]));
        }

        var name = command.Body.Name?.Trim();
        if (name is { Length: 0 } || (name is not null && name.Any(char.IsControl)))
        {
            return Result.Failure<EnvironmentRevisionCreated>(OrchestratorErrors.EnvironmentRevisionInvalid(
                "the name must be printable text",
                [new ErrorDetail("/name", "invalid_value")]));
        }

        var (configuration, details) = EnvironmentRevisionRules.Check(command.Body);
        if (configuration is null)
        {
            return Result.Failure<EnvironmentRevisionCreated>(OrchestratorErrors.EnvironmentRevisionInvalid(
                "the environment revision must be secret-free configuration",
                details));
        }

        var orgId = scopes.Require().OrgId;
        await db.ExecuteAsync(
            OrchestratorSql.EnsureEnvironment,
            new { id = Guid.CreateVersion7(), org_id = orgId, project_id = command.ProjectId, environment_key = command.EnvironmentKey, name = name ?? command.EnvironmentKey },
            cancellationToken).ConfigureAwait(false);

        await db.ExecuteAsync(OrchestratorSql.LockTimeout, null, cancellationToken).ConfigureAwait(false);
        EnvironmentRow? environment;
        try
        {
            environment = await db.QuerySingleOrDefaultAsync<EnvironmentRow>(
                OrchestratorSql.EnvironmentForUpdate,
                new { project_id = command.ProjectId, environment_key = command.EnvironmentKey },
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return Result.Failure<EnvironmentRevisionCreated>(OrchestratorErrors.Busy);
        }

        if (environment is null)
        {
            return Result.Failure<EnvironmentRevisionCreated>(OrchestratorErrors.ProjectNotFound);
        }

        var slot = await db.QuerySingleOrDefaultAsync<SlotRow>(
            OrchestratorSql.RevisionSlot,
            new { org_id = orgId, environment_id = environment.Id, revision_hash = configuration.RevisionHash },
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("the revision slot query returned no row");

        if (slot.ExistingId is { } existingId)
        {
            // Identical content is the same revision: it becomes the current one again (a revert is visible), nothing is stored.
            await db.ExecuteAsync(
                OrchestratorSql.MovePointer,
                new { environment_id = environment.Id, revision_id = existingId, name },
                cancellationToken).ConfigureAwait(false);
            audit.Describe(new AuditObject("environment_revision", existingId.ToString("D")));

            return Result.Success(new EnvironmentRevisionCreated(existingId, slot.ExistingNo!.Value, configuration.RevisionHash, false));
        }

        if (command.Body.RevisionNo is { } expected && expected != slot.NextNo)
        {
            return Result.Failure<EnvironmentRevisionCreated>(OrchestratorErrors.RevisionConflict(slot.NextNo));
        }

        var revisionId = Guid.CreateVersion7();
        var actor = AuditActorResolver.From(principals.Current);
        await db.ExecuteAsync(
            OrchestratorSql.InsertRevision,
            new
            {
                id = revisionId,
                org_id = orgId,
                project_id = command.ProjectId,
                environment_id = environment.Id,
                revision_no = slot.NextNo,
                base_urls = configuration.BaseUrlsJson,
                dependency_versions = configuration.DependencyVersionsJson,
                secret_refs = JsonSerializer.Serialize(configuration.SecretRefs),
                revision_hash = configuration.RevisionHash,
                created_by = $"{actor.Type}:{actor.Id}",
            },
            cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync(
            OrchestratorSql.MovePointer,
            new { environment_id = environment.Id, revision_id = revisionId, name },
            cancellationToken).ConfigureAwait(false);

        audit.Describe(new AuditObject("environment_revision", revisionId.ToString("D")));

        return Result.Success(new EnvironmentRevisionCreated(revisionId, slot.NextNo, configuration.RevisionHash, true), 201);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex EnvironmentKey();

    private sealed class EnvironmentRow
    {
        public Guid Id { get; set; }

        public Guid? CurrentRevisionId { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class SlotRow
    {
        public Guid? ExistingId { get; set; }

        public int? ExistingNo { get; set; }

        public int NextNo { get; set; }
    }
}

/// <summary>Read one environment revision (<c>GET /v1/environment-revisions/{id}</c>, 05.1.2 AC4).</summary>
/// <param name="RevisionId">The revision.</param>
internal sealed record GetEnvironmentRevisionQuery(Guid RevisionId) : IQuery<Result<EnvironmentRevisionDto>>;

/// <summary>Handler of <see cref="GetEnvironmentRevisionQuery"/>; another tenant's or another project's revision is 404.</summary>
[Permission(Permissions.Run.Read)]
internal sealed class GetEnvironmentRevisionQueryHandler(
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetEnvironmentRevisionQuery, Result<EnvironmentRevisionDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<EnvironmentRevisionDto>> Handle(GetEnvironmentRevisionQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Orchestrator, nameof(GetEnvironmentRevisionQuery));

        var row = await db.QuerySingleOrDefaultAsync<RevisionRow>(OrchestratorSql.RevisionById, new { revision_id = query.RevisionId }, cancellationToken)
            .ConfigureAwait(false);
        if (row is null || OrchestratorScope.Project(row.ProjectId, principals, tenant) is not null)
        {
            return Result.Failure<EnvironmentRevisionDto>(OrchestratorErrors.EnvironmentRevisionNotFound);
        }

        return Result.Success(new EnvironmentRevisionDto(
            row.RevisionId,
            row.EnvironmentId,
            row.EnvironmentKey,
            row.ProjectId,
            row.RevisionNo,
            row.RevisionHash,
            JsonSerializer.Deserialize<Dictionary<string, string>>(row.BaseUrls)!,
            JsonSerializer.Deserialize<Dictionary<string, string>>(row.DependencyVersions)!,
            JsonSerializer.Deserialize<string[]>(row.SecretRefs) ?? [],
            row.CreatedAt));
    }

    private sealed class RevisionRow
    {
        public Guid RevisionId { get; set; }

        public Guid EnvironmentId { get; set; }

        public string EnvironmentKey { get; set; } = string.Empty;

        public Guid ProjectId { get; set; }

        public int RevisionNo { get; set; }

        public string RevisionHash { get; set; } = string.Empty;

        public string BaseUrls { get; set; } = "{}";

        public string DependencyVersions { get; set; } = "{}";

        public string SecretRefs { get; set; } = "[]";

        public DateTimeOffset CreatedAt { get; set; }
    }
}
