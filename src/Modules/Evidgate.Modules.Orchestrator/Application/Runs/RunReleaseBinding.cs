using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
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
    /// The body's <c>release_id</c> member, shape-checked and nothing more: a uuid string that is not the empty guid.
    /// Whether that release exists, belongs to this project or is bindable at all is a database answer
    /// (<see cref="ResolveAsync"/>); keep the two apart so a malformed body is refused before any read.
    /// </summary>
    /// <param name="body">The run-create body, already known to be a JSON object.</param>
    internal static (Guid? ReleaseId, Error? Error) ParseReleaseId(JsonElement body)
    {
        if (!body.TryGetProperty("release_id", out var element))
        {
            return (null, null);
        }

        return element.ValueKind == JsonValueKind.String
            && element.TryGetGuid(out var releaseId)
            && releaseId != Guid.Empty
            ? (releaseId, null)
            : (null, Error.Invalid(
                "release_id must be a UUID",
                [new ErrorDetail("/release_id", "invalid_value")]));
    }

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
    /// <param name="outbox">The same transaction's outbox writer, for the binding's event.</param>
    /// <param name="audit">The run create's audit buffer, for the binding's row.</param>
    /// <param name="actor">The principal the binding is attributed to.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static async ValueTask BindAsync(
        Guid releaseId,
        Guid runId,
        Guid orgId,
        JsonElement? components,
        ITenantDb db,
        IOutboxWriter outbox,
        IAuditTrail audit,
        AuditActor actor,
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

        var inserted = await db.ExecuteAsync(
            ReleaseSql.InsertBindings,
            new { org_id = orgId, release_id = releaseId, run_id = runId, rows = bindings },
            cancellationToken).ConfigureAwait(false);

        // The binding is a change of its own even though a run create carried it: the same event and the same audit row
        // as an explicit bind, written in this same transaction, and only when a row actually landed.
        if (inserted == 0)
        {
            return;
        }

        var boundKeys = match.Bound.Select(component => component.ComponentKey).ToArray();
        await ReleaseBindWriter
            .EnqueueAsync(outbox, releaseId, runId, boundKeys, match.Mismatched.Count, cancellationToken)
            .ConfigureAwait(false);
        ReleaseBindWriter.Describe(audit, releaseId, runId, boundKeys.Length, match.Mismatched.Count);
        ReleaseBindWriter.Record(audit, actor);
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
