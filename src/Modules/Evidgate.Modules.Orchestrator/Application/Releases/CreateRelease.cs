using System.Text.Json;
using System.Text.Json.Serialization;
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
using Evidgate.Modules.Orchestrator.Application.Manifests;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Evidgate.Modules.Orchestrator.Application.Releases;

/// <summary>Declare a release (<c>POST /v1/projects/{p}/releases</c>, 05.6.1 §5).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Body">The raw UTF-8 body (≤ 64 KiB), parsed by the handler after authorization.</param>
/// <param name="BodySha256">Hex SHA-256 of <paramref name="Body"/>: what the idempotency store compares.</param>
/// <param name="IdempotencyKey">The caller's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record CreateReleaseCommand(
    Guid ProjectId,
    [property: JsonIgnore] ReadOnlyMemory<byte> Body,
    string BodySha256,
    string IdempotencyKey)
    : ICommand<Result<ReleaseCreated>>, IIdempotentCommand
{
    /// <summary>The dedupe operation the key is claimed for, beside <c>run:create</c> (00 §9.1).</summary>
    string IIdempotentCommand.Operation => "release:create";
}

/// <summary>
/// The one write path of a release (05.6.1 AC1): validate the closed body, refuse an environment revision that does not
/// resolve inside the project, then insert the release and its components in the command's own tenant transaction and
/// move the release to <c>active</c> when the request asked for it. Authorization is the C2 pipeline's <c>run:create</c>
/// check, the audit row is the same unit of work (00 §5.5 N14), and no outbox row is written: a release declares
/// digests, it triggers no work.
/// </summary>
[AuditedAction("release.create")]
[Permission(Permissions.Run.Create)]
internal sealed class CreateReleaseCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit)
    : ICommandHandler<CreateReleaseCommand, Result<ReleaseCreated>>
{
    /// <inheritdoc />
    public async ValueTask<Result<ReleaseCreated>> Handle(CreateReleaseCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Orchestrator, nameof(CreateReleaseCommand));

        if (OrchestratorScope.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<ReleaseCreated>(mismatch);
        }

        var orgId = scopes.Require().OrgId;

        // The 404 comes first, before the body is even parsed: an unknown, soft-deleted, other-tenant or sibling
        // project must not learn from a `release_invalid` or a `request_invalid` that its id was recognised. It is one
        // statement (`ReleaseSql.ProjectVisible`), and the insert below repeats the predicate, so a project that
        // vanishes between the two still inserts nothing.
        if (await db.QuerySingleOrDefaultAsync<int>(
                ReleaseSql.ProjectVisible,
                new { project_id = command.ProjectId, org_id = orgId },
                cancellationToken).ConfigureAwait(false) is not 1)
        {
            return Result.Failure<ReleaseCreated>(OrchestratorErrors.ProjectNotFound);
        }

        var (request, bodyError) = ReleaseBody.Parse(command.Body);
        if (bodyError is not null)
        {
            return Result.Failure<ReleaseCreated>(bodyError);
        }

        if (await UnresolvedAsync(command.ProjectId, request.Components, cancellationToken).ConfigureAwait(false) is { Count: > 0 } unresolved)
        {
            return Result.Failure<ReleaseCreated>(OrchestratorErrors.ReleaseInvalid(
                "every component names an environment revision of this project",
                unresolved));
        }

        var releaseId = Guid.CreateVersion7();
        var actor = AuditActorResolver.From(principals.Current);
        if (await InsertReleaseAsync(command.ProjectId, orgId, releaseId, request, actor, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return Result.Failure<ReleaseCreated>(refusal);
        }

        await db.ExecuteAsync(
            ReleaseSql.InsertComponents,
            new { org_id = orgId, release_id = releaseId, rows = ComponentRows(request.Components) },
            cancellationToken).ConfigureAwait(false);

        if (request.State == ReleaseStates.Active)
        {
            await db.ExecuteAsync(ReleaseSql.Activate, new { release_id = releaseId }, cancellationToken).ConfigureAwait(false);
        }

        // The metadata carries the two bounded facts of the declaration — how many components it has and the state it
        // was stored in. The component keys themselves stay on the append-only rows: joined they can exceed the audit
        // metadata's value bound, and a truncated list would claim something the release does not say.
        audit.Describe(new AuditObject(
            "release",
            releaseId.ToString("D"),
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["count"] = request.Components.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["status"] = request.State,
            }));

        return Result.Success(new ReleaseCreated(releaseId), StatusCodes.Status201Created);
    }

    /// <summary>The release row itself, or the refusal its insert produced: 404 for a project the tenant cannot see, 409 for a name already taken.</summary>
    private async ValueTask<Error?> InsertReleaseAsync(
        Guid projectId,
        Guid orgId,
        Guid releaseId,
        ReleaseRequest request,
        AuditActor actor,
        CancellationToken cancellationToken)
    {
        try
        {
            var inserted = await db.ExecuteAsync(
                ReleaseSql.InsertRelease,
                new
                {
                    id = releaseId,
                    org_id = orgId,
                    project_id = projectId,
                    name = request.Name,
                    created_by = $"{actor.Type}:{actor.Id}",
                },
                cancellationToken).ConfigureAwait(false);

            return inserted == 0 ? OrchestratorErrors.ProjectNotFound : null;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation
                                                  && exception.ConstraintName == "release_name_key")
        {
            return OrchestratorErrors.ReleaseNameConflict;
        }
    }

    /// <summary>The 422 details of the components whose environment revision does not resolve in this project, by index.</summary>
    private async ValueTask<List<ErrorDetail>> UnresolvedAsync(
        Guid projectId,
        IReadOnlyList<ReleaseComponentRequest> components,
        CancellationToken cancellationToken)
    {
        var rows = JsonSerializer.Serialize(components.Select((component, index) => new RevisionProbe(index, component.EnvironmentRevisionId)));
        var unresolved = await db.QueryAsync<int>(
            ReleaseSql.UnresolvedRevisions,
            new { project_id = projectId, rows },
            cancellationToken).ConfigureAwait(false);

        return [.. unresolved.Select(index => new ErrorDetail($"/components/{index}/environment_revision_id", "unknown_reference", index))];
    }

    /// <summary>The one <c>jsonb</c> batch parameter of the component insert: the column names are the recordset's names.</summary>
    private static string ComponentRows(IReadOnlyList<ReleaseComponentRequest> components) =>
        JsonSerializer.Serialize(components.Select(component => new ComponentRow(
            Guid.CreateVersion7(),
            component.ComponentKey,
            component.Digest,
            component.EnvironmentRevisionId)));

    /// <summary>One component as the <c>jsonb_to_recordset</c> batch reads it.</summary>
    private sealed record ComponentRow(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("component_key")] string ComponentKey,
        [property: JsonPropertyName("digest")] string Digest,
        [property: JsonPropertyName("environment_revision_id")] Guid EnvironmentRevisionId);

    /// <summary>One component as the environment-revision probe reads it: its request index and the revision it names.</summary>
    private sealed record RevisionProbe(
        [property: JsonPropertyName("idx")] int Index,
        [property: JsonPropertyName("environment_revision_id")] Guid EnvironmentRevisionId);
}
