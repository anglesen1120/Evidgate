using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Evidgate.Modules.Policy.Contracts;
using Mediator;

namespace Evidgate.Modules.Orchestrator.Application.Queries;

/// <summary>Per-decision counts of a project's runs created at or after <paramref name="Since"/>.</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Since">Inclusive lower bound on <c>run.created_at</c>; null means now minus the default window.</param>
internal sealed record ListRunDecisionCountsQuery(Guid ProjectId, DateTimeOffset? Since)
    : IQuery<Result<RunDecisionCounts>>;

/// <summary>
/// The most runs one decision-count request reads. A verdict lives in Policy, so the runs are read in keyset chunks
/// of run ids and each chunk is counted by one Policy aggregate; this cap bounds the chunks (at most
/// <c>cap / 200 + 1</c> of each statement) so a project with a huge history cannot turn the dashboard read into a
/// full scan. Beyond it the answer covers the newest runs and says <c>truncated</c>. It is a registered value so a
/// test can pin the truncated path with a small one; it is not configuration.
/// </summary>
/// <param name="Rows">Runs read at most; positive.</param>
internal sealed record RunDecisionCountsScanCap(int Rows)
{
    /// <summary>The cap production runs with.</summary>
    internal const int DefaultRows = 5000;
}

/// <summary>
/// Handler of <see cref="ListRunDecisionCountsQuery"/>. The project check and the second permission follow the run
/// list exactly (<see cref="RunReadAccess"/>): an unknown or foreign project is the one <c>404 not_found</c> before
/// anything else is read, and a caller without <c>decision:read</c> gets <c>403 forbidden</c> because a count by
/// verdict would disclose the protected value the run list withholds from them. The runs are then read newest first in
/// chunks of at most <see cref="ChunkSize"/> ids (one extra row tells whether more follow, so a scan that ends exactly
/// on a chunk costs no empty read), each chunk is counted by one Policy call, and a run Policy does not count has no
/// live evaluation. No statement joins across modules.
/// </summary>
[Permission(Permissions.Run.Read)]
internal sealed class ListRunDecisionCountsQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IPermissionEvaluator permissions,
    IPolicyContracts policy,
    TimeProvider clock,
    RunDecisionCountsScanCap cap)
    : IQueryHandler<ListRunDecisionCountsQuery, Result<RunDecisionCounts>>
{
    /// <summary>The ids per chunk, equal to the Policy aggregate's own bound.</summary>
    internal const int ChunkSize = 200;

    /// <summary>The window when <c>since</c> is absent, counted back from now.</summary>
    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(30);

    /// <inheritdoc />
    public async ValueTask<Result<RunDecisionCounts>> Handle(
        ListRunDecisionCountsQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(
            ModuleName.Orchestrator,
            nameof(ListRunDecisionCountsQuery));

        if (await RunReadAccess.CheckProjectAsync(query.ProjectId, db, principals, tenant, cancellationToken)
                .ConfigureAwait(false) is { } projectError)
        {
            return Result.Failure<RunDecisionCounts>(projectError);
        }

        if (!await RunReadAccess.CanReadDecisionsAsync(principals, tenant, permissions, cancellationToken)
                .ConfigureAwait(false))
        {
            return Result.Failure<RunDecisionCounts>(Error.Forbidden(
                "counting runs by decision needs decision:read"));
        }

        var since = (query.Since ?? clock.GetUtcNow() - DefaultWindow).ToUniversalTime();
        return Result.Success(await ScanAsync(query.ProjectId, since, cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<RunDecisionCounts> ScanAsync(Guid projectId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var orgId = scopes.Require().OrgId;
        int pass = 0, fail = 0, inconclusive = 0, scanned = 0;
        var truncated = false;
        RunChunkRow? last = null;

        while (true)
        {
            var take = Math.Min(ChunkSize, cap.Rows - scanned);
            var rows = await ReadChunkAsync(orgId, projectId, since, last, take + 1, cancellationToken)
                .ConfigureAwait(false);
            var more = rows.Count > take;
            IReadOnlyList<RunChunkRow> chunk = more ? [.. rows.Take(take)] : rows;
            if (chunk.Count == 0)
            {
                break;
            }

            var counts = await policy
                .CountLatestLiveDecisionsAsync([.. chunk.Select(row => row.RunId)], cancellationToken)
                .ConfigureAwait(false);
            pass += counts.Pass;
            fail += counts.Fail;
            inconclusive += counts.Inconclusive;
            scanned += chunk.Count;
            last = chunk[^1];

            if (!more)
            {
                break;
            }

            if (scanned >= cap.Rows)
            {
                truncated = true;
                break;
            }
        }

        return new RunDecisionCounts(since, pass, fail, inconclusive, scanned - pass - fail - inconclusive, scanned, truncated);
    }

    private Task<IReadOnlyList<RunChunkRow>> ReadChunkAsync(
        Guid orgId,
        Guid projectId,
        DateTimeOffset since,
        RunChunkRow? after,
        int limit,
        CancellationToken cancellationToken) =>
        db.QueryAsync<RunChunkRow>(
            after is null ? RunDecisionCountsSql.FirstChunk : RunDecisionCountsSql.NextChunk,
            after is null
                ? new { org_id = orgId, project_id = projectId, since, limit }
                : new
                {
                    org_id = orgId,
                    project_id = projectId,
                    since,
                    limit,
                    cursor_created_at = after.CreatedAt,
                    cursor_id = after.RunId,
                },
            cancellationToken);

    /// <summary>One run of a chunk: its id for Policy and its position for the next keyset step.</summary>
    private sealed class RunChunkRow
    {
        public Guid RunId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
    }
}
