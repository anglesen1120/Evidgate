using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Application.Manifests;
using Evidgate.Modules.Orchestrator.Application.Releases;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// The run create's release binding, kept beside <see cref="CreateRunCommandHandler"/> rather than inside it: the run
/// create only names a release, and the two steps — resolve it before any write, bind the matched keys after the run,
/// its executions and its slots exist — are the whole of this story's hook into that transaction.
/// </summary>
internal static class RunReleaseBinding
{
    /// <summary>
    /// Resolves a body's <c>release_id</c> inside the run's project before anything is written. A null id means the
    /// body named none; an error is the refusal the run create answers and then writes nothing: <c>404 not_found</c> for
    /// an unknown, another tenant's or a sibling project's release, <c>422 release_not_active</c> for a <c>draft</c> one.
    /// </summary>
    /// <param name="releaseId">The body's <c>release_id</c>, or null.</param>
    /// <param name="projectId">The project from the run route.</param>
    /// <param name="orgId">The ambient scope's org.</param>
    /// <param name="db">The caller's tenant transaction.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static async ValueTask<(Guid? ReleaseId, Error? Error)> ResolveAsync(
        Guid? releaseId,
        Guid projectId,
        Guid orgId,
        ITenantDb db,
        CancellationToken cancellationToken)
    {
        if (releaseId is not { } id)
        {
            return (null, null);
        }

        var target = await db.QuerySingleOrDefaultAsync<ReleaseTargetRow>(
            ReleaseSql.RunReleaseTarget,
            new { release_id = id, org_id = orgId, project_id = projectId },
            cancellationToken).ConfigureAwait(false);

        if (target is null)
        {
            return (null, OrchestratorErrors.ReleaseNotFound);
        }

        return target.State == ReleaseStates.Draft
            ? (null, OrchestratorErrors.ReleaseNotActive)
            : (id, null);
    }

    /// <summary>
    /// Writes one <c>release_run</c> row per component whose digests agree, inside the run-create transaction. A
    /// component with another digest, or no digest at all, writes nothing and is left to
    /// <see cref="IReleaseBindingReader"/>: a partial or empty match never fails a run create, because the run
    /// itself is still evidence of what it exercised.
    /// </summary>
    /// <param name="releaseId">The resolved release.</param>
    /// <param name="runId">The run just inserted.</param>
    /// <param name="orgId">The ambient scope's org.</param>
    /// <param name="components">The body's <c>components</c> member, or null when it carried none.</param>
    /// <param name="db">The caller's tenant transaction.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static async ValueTask BindAsync(
        Guid releaseId,
        Guid runId,
        Guid orgId,
        JsonElement? components,
        ITenantDb db,
        CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<ReleaseComponentRow>(
            ReleaseSql.ComponentsByRelease,
            new { org_id = orgId, release_id = releaseId },
            cancellationToken).ConfigureAwait(false);

        var match = ReleaseMatch.Match(
            rows.Select(row => new ReleaseComponentDigest(row.ComponentKey, row.Digest)),
            ReleaseMatch.ParseRunComponents(components));

        if (match.Bound.Count == 0)
        {
            return;
        }

        var bindings = JsonSerializer.Serialize(match.Bound.Select(component => new
        {
            id = Guid.CreateVersion7(),
            component_key = component.ComponentKey,
        }));

        await db.ExecuteAsync(
            ReleaseSql.InsertBindings,
            new { org_id = orgId, release_id = releaseId, run_id = runId, rows = bindings },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The release a run create targets: the state that decides whether it may be bound at all.</summary>
    private sealed class ReleaseTargetRow
    {
        public string State { get; set; } = string.Empty;
    }

    /// <summary>One release component the match compares.</summary>
    private sealed class ReleaseComponentRow
    {
        public string ComponentKey { get; set; } = string.Empty;

        public string Digest { get; set; } = string.Empty;
    }
}
