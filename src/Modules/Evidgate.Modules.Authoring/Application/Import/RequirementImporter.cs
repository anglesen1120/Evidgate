using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;

namespace Evidgate.Modules.Authoring.Application.Import;

/// <summary>
/// Imports a file of requirement rows (user stories, acceptance criteria, epics, spec sections) as requirements with
/// immutable versions, source <c>import</c>.
/// </summary>
/// <remarks>
/// <para>
/// The whole file is validated and resolved before the first write, so a refused file leaves nothing behind; the
/// writes then go through <see cref="RequirementWriter"/>, the one path that checks identity shape, references and the
/// body cap and appends a version. Only a changed row is written: re-importing a file appends no version and writes no
/// audit row (the report carries no batch id then).
/// </para>
/// <para>
/// Applying imports of one project run one after the other: the project's advisory lock is taken before the plan is read
/// and held until the scope commits, so a second file resolves against what the first wrote. A dry run takes no lock.
/// </para>
/// <para>
/// The writer costs a few statements per row. That is accepted for the 5 000-row cap rather than a second write path
/// that would have to repeat the writer's invariants; the reads of the plan stay at two statements per file.
/// </para>
/// </remarks>
/// <param name="db">The scoped tenant database.</param>
/// <param name="writer">The requirement writer.</param>
internal sealed class RequirementImporter(ITenantDb db, RequirementWriter writer)
{
    private const string ImportSource = "import";

    private readonly RequirementImportPlanner planner = new(db);

    /// <summary>Imports one requirement file, or reports what it would do.</summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="projectId">The project the file imports into (already known to exist).</param>
    /// <param name="actorMembershipId">The author recorded on every version, or null.</param>
    /// <param name="rows">The rows, all of kind requirement, at least one.</param>
    /// <param name="dryRun">True to plan without writing.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<Result<ImportReport>> ImportAsync(
        Guid orgId,
        Guid projectId,
        Guid? actorMembershipId,
        IReadOnlyList<ImportRow> rows,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var (accepted, issues) = RequirementImportValidator.Validate(rows);
        if (issues.Count != 0)
        {
            return AuthoringImporter.Refusal(issues);
        }

        if (!dryRun)
        {
            await LockProjectAsync(orgId, projectId, cancellationToken).ConfigureAwait(false);
        }

        var plan = await planner.PlanAsync(orgId, projectId, accepted, cancellationToken).ConfigureAwait(false);
        if (!plan.IsAccepted)
        {
            return AuthoringImporter.Refusal(plan.Issues);
        }

        if (dryRun || plan.Rows.All(row => row.Outcome == ImportOutcome.Unchanged))
        {
            return Result.Success(Report(null, plan.Rows));
        }

        var failure = await ApplyAsync(orgId, projectId, actorMembershipId, plan.Rows, cancellationToken).ConfigureAwait(false);

        return failure is null
            ? Result.Success(Report(Guid.CreateVersion7(), plan.Rows))
            : AuthoringErrors.Fail<ImportReport>(failure);
    }

    /// <summary>Bounds the wait, then takes the project's import lock; a timeout answers the retryable 503 of the caller.</summary>
    private async Task LockProjectAsync(Guid orgId, Guid projectId, CancellationToken cancellationToken)
    {
        await AuthoringLockGuard.SetLockTimeoutAsync(db, cancellationToken).ConfigureAwait(false);
        await db.ExecuteAsync(
            RequirementImportSql.LockProject,
            new { lock_key = $"requirement_import:{orgId:D}:{projectId:D}" },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the changed rows in file order, so a parent exists before its children; the first failure stops the batch.</summary>
    /// <returns>The writer's refusal, or null when every write succeeded.</returns>
    private async Task<Error?> ApplyAsync(
        Guid orgId,
        Guid projectId,
        Guid? actorMembershipId,
        IReadOnlyList<RequirementPlannedRow> rows,
        CancellationToken cancellationToken)
    {
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);

        foreach (var planned in rows)
        {
            if (planned.Outcome == ImportOutcome.Unchanged)
            {
                ids[planned.Row.Source.Key] = planned.Existing!.Id;

                continue;
            }

            var identity = planned.ParentIsNew
                ? planned.Identity with { ParentRequirementId = ids[planned.Row.ParentKey!] }
                : planned.Identity;

            var result = planned.Outcome == ImportOutcome.Create
                ? await CreateAsync(orgId, projectId, actorMembershipId, planned, identity, cancellationToken).ConfigureAwait(false)
                : await UpdateAsync(orgId, projectId, actorMembershipId, planned, identity, cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                return result.Error;
            }

            ids[planned.Row.Source.Key] = result.Value.RequirementId;
        }

        return null;
    }

    private ValueTask<Result<RequirementHead>> CreateAsync(
        Guid orgId,
        Guid projectId,
        Guid? actorMembershipId,
        RequirementPlannedRow planned,
        RequirementIdentity identity,
        CancellationToken cancellationToken) => writer.CreateAsync(
            orgId,
            new RequirementCreate(
                projectId,
                identity.Kind,
                identity.Title,
                identity.ExternalRef,
                identity.FeatureNodeId,
                identity.ParentRequirementId,
                planned.Row.Body,
                ImportSource,
                actorMembershipId,
                null),
            cancellationToken);

    private ValueTask<Result<RequirementHead>> UpdateAsync(
        Guid orgId,
        Guid projectId,
        Guid? actorMembershipId,
        RequirementPlannedRow planned,
        RequirementIdentity identity,
        CancellationToken cancellationToken)
    {
        var stored = planned.Existing!;

        return writer.UpdateAsync(
            orgId,
            projectId,
            stored.Id,
            stored.RowVersion,
            identity,
            new RequirementIdentity(stored.Kind, stored.Title, stored.ExternalRef, stored.FeatureNodeId, stored.ParentRequirementId),
            planned.Row.Body,
            ImportSource,
            actorMembershipId,
            null,
            cancellationToken);
    }

    private static ImportReport Report(Guid? batchId, IReadOnlyList<RequirementPlannedRow> rows) => new(
        batchId,
        new ImportCounts(
            rows.Count(row => row.Outcome == ImportOutcome.Create),
            rows.Count(row => row.Outcome == ImportOutcome.Update),
            0,
            rows.Count(row => row.Outcome == ImportOutcome.Unchanged)),
        [.. rows.Select(row => new ImportRowReport(
            row.Row.Source.Index,
            row.Row.Source.Key,
            ImportRowKind.Requirement,
            AuthoringImporter.OutcomeName(row.Outcome),
            null))]);
}
