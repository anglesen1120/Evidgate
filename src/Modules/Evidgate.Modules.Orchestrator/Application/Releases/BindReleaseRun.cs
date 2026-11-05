using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Application.Manifests;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Evidgate.Modules.Policy.Contracts;
using Mediator;
using CreateRunHandler = Evidgate.Modules.Orchestrator.Application.Runs.CreateRunCommandHandler;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Orchestrator.Application.Releases;

/// <summary>Bind one run to a release (<c>POST /v1/releases/{id}/runs</c>).</summary>
/// <param name="ReleaseId">The release from the route.</param>
/// <param name="Body">The raw UTF-8 body (≤ 64 KiB), parsed by the handler after authorization.</param>
/// <param name="BodySha256">Hex SHA-256 of <paramref name="Body"/>: what the idempotency store compares.</param>
/// <param name="IdempotencyKey">The caller's <c>Idempotency-Key</c>.</param>
internal sealed record BindReleaseRunCommand(
    Guid ReleaseId,
    [property: JsonIgnore] ReadOnlyMemory<byte> Body,
    string BodySha256,
    string IdempotencyKey)
    : ICommand<Result<ReleaseBindResult>>, IIdempotentCommand
{
    /// <summary>The dedupe operation the key is claimed for, stable across a rename.</summary>
    string IIdempotentCommand.Operation => "release:bind";
}

/// <summary>
/// The explicit bind: load the release and the run, refuse a fork or replay run and a draft release, refuse a run that
/// already has a live verdict, then write one <c>release_run</c> row per component whose digests agree, with its audit
/// row and its outbox event, in the caller's one tenant transaction. Mismatches are never stored — the answer reports
/// them, and a later read derives them again from the same two rows.
/// </summary>
/// <remarks>
/// A re-bind of an existing pair inserts nothing (<c>on conflict do nothing</c>) and answers every row the pair holds,
/// so a different <c>Idempotency-Key</c> for the same pair is a no-op rather than a conflict. The run row is locked
/// <c>FOR UPDATE</c> before the live guard runs: an <c>evaluation</c> row's composite foreign key takes
/// <c>FOR KEY SHARE</c> on its run, which conflicts with <c>FOR UPDATE</c>, so a concurrent live verdict is either
/// committed before the guard reads it or waits for this bind to commit. That orders the two events without a migration
/// or a second write path.
/// </remarks>
[AuditedAction("release.bind")]
[Permission(Permissions.Run.Create)]
internal sealed class BindReleaseRunHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    ILiveEvaluationReader liveEvaluations,
    IOutboxWriter outbox,
    IAuditTrail audit)
    : ICommandHandler<BindReleaseRunCommand, Result<ReleaseBindResult>>
{
    /// <summary>The outbox task type an applied bind enqueues: no handler consumes it yet, and a row no handler claims stays queued.</summary>
    internal const string BoundTaskType = "orchestrator.release.bound";

    /// <inheritdoc />
    public async ValueTask<Result<ReleaseBindResult>> Handle(BindReleaseRunCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Orchestrator, nameof(BindReleaseRunCommand));

        var (release, releaseError) = await LoadReleaseAsync(command.ReleaseId, cancellationToken).ConfigureAwait(false);
        if (releaseError is not null)
        {
            return Result.Failure<ReleaseBindResult>(releaseError);
        }

        var (runId, bodyError) = BindReleaseBody.Parse(command.Body);
        if (bodyError is not null)
        {
            return Result.Failure<ReleaseBindResult>(bodyError);
        }

        var orgId = scopes.Require().OrgId;
        var (run, runError) = await LoadRunAsync(runId, release!.ProjectId, orgId, cancellationToken).ConfigureAwait(false);
        if (runError is not null)
        {
            return Result.Failure<ReleaseBindResult>(runError);
        }

        if (Refusal(release, run!) is { } refusal)
        {
            return Result.Failure<ReleaseBindResult>(refusal);
        }

        var closed = await TakeRunLockAsync(runId, orgId, cancellationToken).ConfigureAwait(false);
        if (closed is not null)
        {
            return Result.Failure<ReleaseBindResult>(closed);
        }

        return await BindAsync(command.ReleaseId, runId, orgId, run!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The release, answered as one 404 for every release the caller may not bind: unknown, another tenant's, and a
    /// sibling project's. A distinct 403 would tell a project-narrowed credential that the id exists elsewhere in its org.
    /// </summary>
    private async ValueTask<(ReleaseRow? Release, Error? Error)> LoadReleaseAsync(
        Guid releaseId,
        CancellationToken cancellationToken)
    {
        var release = await db.QuerySingleOrDefaultAsync<ReleaseRow>(
            ReleaseSql.ReleaseById,
            new { release_id = releaseId, org_id = scopes.Require().OrgId },
            cancellationToken).ConfigureAwait(false);

        return release is null || OrchestratorScope.Project(release.ProjectId, principals, tenant) is not null
            ? (null, OrchestratorErrors.BindTargetNotFound)
            : (release, null);
    }

    /// <summary>The run under the release's project, or the same 404 an unknown id gets.</summary>
    private async ValueTask<(RunRow? Run, Error? Error)> LoadRunAsync(
        Guid runId,
        Guid projectId,
        Guid orgId,
        CancellationToken cancellationToken)
    {
        var run = await db.QuerySingleOrDefaultAsync<RunRow>(
            ReleaseSql.RunComponents,
            new { run_id = runId, org_id = orgId, project_id = projectId },
            cancellationToken).ConfigureAwait(false);

        return run is null ? (null, OrchestratorErrors.BindTargetNotFound) : (run, null);
    }

    /// <summary>
    /// The refusals that need no lock, in the order a caller can act on them: a fork run's identity is advisory-only, a
    /// replay run re-executes a stored run's inputs rather than this release's, and a <c>draft</c> release has no final
    /// component set yet.
    /// </summary>
    private static Error? Refusal(ReleaseRow release, RunRow run)
    {
        if (run.IsFork)
        {
            return OrchestratorErrors.ForkRunNotBindable;
        }

        if (run.Purpose == CreateRunHandler.ReplayPurpose)
        {
            return OrchestratorErrors.ReplayReleaseNotAllowed;
        }

        return release.State == ReleaseStates.Draft ? OrchestratorErrors.ReleaseNotActive : null;
    }

    /// <summary>
    /// Locks the run for the rest of the transaction, then asks whether a live verdict already exists: the lock is what
    /// makes the answer stable, because an evaluation row's composite foreign key needs the key-share lock this one
    /// conflicts with.
    /// </summary>
    private async ValueTask<Error?> TakeRunLockAsync(Guid runId, Guid orgId, CancellationToken cancellationToken)
    {
        await db.ExecuteAsync(
            ReleaseSql.LockRun,
            new { run_id = runId, org_id = orgId },
            cancellationToken).ConfigureAwait(false);

        return await liveEvaluations.HasLiveAsync(runId, cancellationToken).ConfigureAwait(false)
            ? OrchestratorErrors.ReleaseBindClosed
            : null;
    }

    /// <summary>The match and its write: components, insert, the rows the pair now holds, then the event and the audit row.</summary>
    private async ValueTask<Result<ReleaseBindResult>> BindAsync(
        Guid releaseId,
        Guid runId,
        Guid orgId,
        RunRow run,
        CancellationToken cancellationToken)
    {
        var components = await db.QueryAsync<ComponentRow>(
            ReleaseSql.ComponentsByRelease,
            new { org_id = orgId, release_id = releaseId },
            cancellationToken).ConfigureAwait(false);
        var match = ReleaseMatch.Match(
            components.Select(component => new ReleaseComponentDigest(component.ComponentKey, component.Digest)),
            ReleaseMatch.ParseRunComponents(run.Components));

        if (match.Bound.Count == 0)
        {
            return Result.Failure<ReleaseBindResult>(NoComponentMatches(match));
        }

        var inserted = await db.ExecuteAsync(
            ReleaseSql.InsertBindings,
            new { org_id = orgId, release_id = releaseId, run_id = runId, rows = BindingRows(match.Bound) },
            cancellationToken).ConfigureAwait(false);
        var bound = await db.QueryAsync<BoundRow>(
            ReleaseSql.BoundComponentsByPair,
            new { org_id = orgId, release_id = releaseId, run_id = runId },
            cancellationToken).ConfigureAwait(false);

        if (inserted == 0)
        {
            // The pair already held every key: nothing new committed, so no second audit row and no second event. The
            // answer still lists what the pair holds, which is what makes the call a no-op rather than a refusal.
            audit.Suppress();
            return Result.Success(BindResult(releaseId, runId, bound, match), StatusCodes.Status201Created);
        }

        // A bind that wrote nothing is not an applied bind: its mismatches were counted when the rows were inserted.
        EvidgateTelemetry.RecordReleaseBindMismatch(match.Mismatched.Count);

        await EnqueueBound(releaseId, runId, match, cancellationToken).ConfigureAwait(false);
        audit.Describe(AppliedAudit(releaseId, runId, bound.Count, match));

        return Result.Success(BindResult(releaseId, runId, bound, match), StatusCodes.Status201Created);
    }

    /// <summary>One outbox event for the applied bind, deduped on the matched key set (see <see cref="OutboxKey"/>).</summary>
    private async ValueTask EnqueueBound(
        Guid releaseId,
        Guid runId,
        ReleaseMatchOutcome match,
        CancellationToken cancellationToken) =>
        await outbox.EnqueueAsync(
            BoundTaskType,
            JsonSerializer.Serialize(new
            {
                release_id = releaseId,
                run_id = runId,
                bound = match.Bound.Count,
                mismatched = match.Mismatched.Count,
            }),
            "release",
            releaseId,
            OutboxKey(releaseId, runId, match.Bound),
            cancellationToken).ConfigureAwait(false);

    /// <summary>The audit object of an applied bind: allowlisted keys only, and no component key or digest.</summary>
    /// <param name="releaseId">The release the row is about.</param>
    /// <param name="runId">The run the binding is for.</param>
    /// <param name="boundCount">The rows the pair holds after the call.</param>
    /// <param name="match">The match that was applied: its mismatch count decides the outcome word.</param>
    private static AuditObject AppliedAudit(Guid releaseId, Guid runId, int boundCount, ReleaseMatchOutcome match) =>
        new(
            "release",
            releaseId.ToString("D"),
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["count"] = boundCount.ToString(CultureInfo.InvariantCulture),
                ["outcome"] = match.Mismatched.Count == 0 ? "complete" : "partial",
                ["run_id"] = runId.ToString("D"),
            });

    /// <summary>The zero-match refusal: one detail per component, naming the run's pointer and the reason.</summary>
    private static Error NoComponentMatches(ReleaseMatchOutcome match) =>
        OrchestratorErrors.ReleaseComponentMismatch(
            [.. match.Mismatched.Select(mismatched => new ErrorDetail(
                $"/run/components/{mismatched.ComponentKey}",
                mismatched.Reason))]);

    /// <summary>The answer of a call that held its rows: what the pair holds now, and the keys it never will.</summary>
    private static ReleaseBindResult BindResult(
        Guid releaseId,
        Guid runId,
        IReadOnlyList<BoundRow> bound,
        ReleaseMatchOutcome match) =>
        new(
            releaseId,
            runId,
            [.. bound.Select(row => new ReleaseBoundComponent(row.ComponentKey, row.Digest))],
            [.. match.Mismatched.Select(mismatched => new ReleaseBindMismatch(mismatched.ComponentKey, mismatched.Reason))]);

    /// <summary>The one <c>jsonb</c> batch parameter of <see cref="ReleaseSql.InsertBindings"/>: ids server-chosen, keys ordered.</summary>
    private static string BindingRows(IReadOnlyList<ReleaseBoundComponent> bound) =>
        JsonSerializer.Serialize(bound.Select(component => new BindingRow(
            Guid.CreateVersion7(),
            component.ComponentKey)));

    /// <summary>
    /// The outbox dedupe key of this call's matched set: <c>orchestrator:release:&lt;id&gt;:run:&lt;id&gt;:&lt;digest&gt;</c>,
    /// where the digest covers the ordered matched keys. It stays inside the column's 256-character bound.
    /// </summary>
    private static string OutboxKey(Guid releaseId, Guid runId, IReadOnlyList<ReleaseBoundComponent> bound)
    {
        var keys = string.Join('\u001f', bound.Select(component => component.ComponentKey));
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(keys)))[..32];
        return $"orchestrator:release:{releaseId:D}:run:{runId:D}:{digest}";
    }

    /// <summary>One binding as the <c>jsonb_to_recordset</c> batch reads it.</summary>
    private sealed record BindingRow(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("component_key")] string ComponentKey);

    /// <summary>The release header the bind needs: its project and whether its components are still open.</summary>
    private sealed class ReleaseRow
    {
        public Guid ReleaseId { get; set; }

        public Guid OrgId { get; set; }

        public Guid ProjectId { get; set; }

        public string State { get; set; } = string.Empty;
    }

    /// <summary>The run's recorded components and the two identity facts that decide whether it may bind at all.</summary>
    private sealed class RunRow
    {
        public string? Components { get; set; }

        public bool IsFork { get; set; }

        public string Purpose { get; set; } = string.Empty;
    }

    /// <summary>One release component the match compares.</summary>
    private sealed class ComponentRow
    {
        public string ComponentKey { get; set; } = string.Empty;

        public string Digest { get; set; } = string.Empty;
    }

    /// <summary>One stored binding with the digest its component declares.</summary>
    private sealed class BoundRow
    {
        public string ComponentKey { get; set; } = string.Empty;

        public string Digest { get; set; } = string.Empty;
    }
}
