using System.Text.Json;
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
    public async ValueTask<IReadOnlyList<RunReleaseRef>> BoundReleasesAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<RunReleaseRow>(
            ReleaseSql.BoundReleases,
            new { org_id = scopes.Require().OrgId, run_id = runId },
            cancellationToken).ConfigureAwait(false);

        return [.. rows.Select(row => new RunReleaseRef(row.ReleaseId, row.ReleaseName, row.ComponentKey))];
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ReleaseMismatch>> MismatchesAsync(
        Guid releaseId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var source = await db.QuerySingleOrDefaultAsync<MismatchSourceRow>(
            ReleaseSql.MismatchSource,
            new { org_id = scopes.Require().OrgId, release_id = releaseId, run_id = runId },
            cancellationToken).ConfigureAwait(false);

        // No row means the release is unknown or invisible, or the run is (or belongs to another project): "nothing to
        // compare" is the same empty answer a release with no component produces.
        if (source is null)
        {
            return [];
        }

        var outcome = ReleaseMatch.Match(
            Components(source.ReleaseComponents),
            ReleaseMatch.ParseRunComponents(source.RunComponents));

        return outcome.Mismatched;
    }

    /// <summary>The aggregate document the one row carries: release components in <c>component_key</c> order.</summary>
    private static List<ReleaseComponentDigest> Components(string? releaseComponents)
    {
        if (string.IsNullOrWhiteSpace(releaseComponents))
        {
            return [];
        }

        var components = new List<ReleaseComponentDigest>();
        using var document = JsonDocument.Parse(releaseComponents);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            components.Add(new ReleaseComponentDigest(
                element.GetProperty("component_key").GetString()!,
                element.GetProperty("digest").GetString()!));
        }

        return components;
    }

    /// <summary>The one row of the mismatch source: the run's recorded components and the release's, aggregated.</summary>
    private sealed class MismatchSourceRow
    {
        public string? RunComponents { get; set; }

        public string? ReleaseComponents { get; set; }
    }

    /// <summary>One row of <see cref="ReleaseSql.BoundReleases"/>: a stored binding with its release's name.</summary>
    private sealed class RunReleaseRow
    {
        public Guid ReleaseId { get; set; }

        public string ReleaseName { get; set; } = string.Empty;

        public string ComponentKey { get; set; } = string.Empty;
    }
}
