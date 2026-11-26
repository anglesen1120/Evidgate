using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

namespace Evidgate.Modules.Orchestrator.Application.Releases;

/// <summary>
/// <see cref="IReleaseAggregationReader"/> over the caller's tenant transaction. It writes nothing and opens no
/// transaction, so an aggregation task reads exactly what the bind committed.
/// </summary>
internal sealed class ReleaseAggregationReader(ITenantDb db, ITenantScopeAccessor scopes) : IReleaseAggregationReader
{
    /// <inheritdoc />
    public async ValueTask<Guid?> GetProjectIdAsync(Guid releaseId, CancellationToken cancellationToken) =>
        await db.QuerySingleOrDefaultAsync<Guid?>(
            ReleaseAggregationSql.ProjectOfRelease,
            new { release_id = releaseId, org_id = scopes.Require().OrgId },
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<ReleaseAggregationInputs?> GetInputsAsync(Guid releaseId, CancellationToken cancellationToken)
    {
        var orgId = scopes.Require().OrgId;
        var projectId = await GetProjectIdAsync(releaseId, cancellationToken).ConfigureAwait(false);
        if (projectId is null)
        {
            return null;
        }

        var components = await db.QueryAsync<ComponentRow>(
            ReleaseSql.ComponentsByRelease,
            new { org_id = orgId, release_id = releaseId },
            cancellationToken).ConfigureAwait(false);
        var bindings = await db.QueryAsync<BindingRow>(
            ReleaseAggregationSql.Bindings,
            new { org_id = orgId, release_id = releaseId, limit = IReleaseAggregationReader.MaxBindings + 1 },
            cancellationToken).ConfigureAwait(false);

        if (bindings.Count > IReleaseAggregationReader.MaxBindings)
        {
            throw new InvalidOperationException(
                $"release {releaseId:D} has more than {IReleaseAggregationReader.MaxBindings} bindings; refusing to aggregate a truncated set");
        }

        return new ReleaseAggregationInputs(
            releaseId,
            projectId.Value,
            [.. components.Select(c => new ReleaseAggregationComponent(c.ComponentKey, c.Digest))],
            [.. bindings.Select(b => new ReleaseAggregationBinding(b.RunId, b.ComponentKey, b.Purpose, b.RunDigest))]);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<Guid>> GetReleaseIdsBoundToRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var ids = await db.QueryAsync<Guid>(
            ReleaseAggregationSql.ReleasesOfRun,
            new { org_id = scopes.Require().OrgId, run_id = runId, limit = IReleaseAggregationReader.MaxReleasesPerRun },
            cancellationToken).ConfigureAwait(false);
        return [.. ids];
    }

    private sealed class ComponentRow
    {
        public string ComponentKey { get; set; } = string.Empty;

        public string Digest { get; set; } = string.Empty;
    }

    private sealed class BindingRow
    {
        public Guid RunId { get; set; }

        public string ComponentKey { get; set; } = string.Empty;

        public string Purpose { get; set; } = string.Empty;

        public string? RunDigest { get; set; }
    }
}
