using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

namespace Evidgate.Modules.Orchestrator.Application.Releases;

/// <summary>
/// <see cref="IReleaseBindingReader"/> over the caller's tenant transaction: the stored bindings of a release/run pair,
/// and the components the release requires but the run does not match. It writes nothing and opens no transaction of its
/// own, so a caller inside the evaluation path reads exactly what the bind committed.
/// </summary>
/// <param name="db">The Dapper path over the ambient scope.</param>
/// <param name="scopes">The ambient tenant scope the org predicate comes from.</param>
internal sealed class ReleaseBindingReader(ITenantDb db, ITenantScopeAccessor scopes) : IReleaseBindingReader
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> BoundKeysAsync(
        Guid releaseId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var keys = await db.QueryAsync<string>(
            ReleaseSql.BoundKeys,
            new { org_id = scopes.Require().OrgId, release_id = releaseId, run_id = runId },
            cancellationToken).ConfigureAwait(false);

        return [.. keys];
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ReleaseMismatch>> MismatchesAsync(
        Guid releaseId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<MismatchRow>(
            ReleaseSql.MismatchSource,
            new { org_id = scopes.Require().OrgId, release_id = releaseId, run_id = runId },
            cancellationToken).ConfigureAwait(false);

        // The header repeats on every row; no row means the release is unknown or invisible, or the run is, and
        // "nothing to compare" is the same empty answer the bind would have computed for a pair with no components.
        if (rows.Count == 0)
        {
            return [];
        }

        var outcome = ReleaseMatch.Match(
            rows.Select(row => new ReleaseComponentDigest(row.ComponentKey, row.ReleaseDigest)),
            ReleaseMatch.ParseRunComponents(rows[0].RunComponents));

        return outcome.Mismatched;
    }

    /// <summary>One release component joined with the run's recorded <c>components</c>.</summary>
    private sealed class MismatchRow
    {
        public string ComponentKey { get; set; } = string.Empty;

        public string ReleaseDigest { get; set; } = string.Empty;

        public string? RunComponents { get; set; }
    }
}
