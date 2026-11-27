using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application.Import;

/// <summary>
/// Route 25's import half (13.1.1 AC13/AC15, §5): one file of rows is validated as a whole, resolved against the project,
/// then applied <b>atomically</b> — one transaction, one <c>batch_id</c>, <c>source = import</c> — or refused as a whole.
/// </summary>
/// <remarks>
/// <para>
/// <b>The five phases, in this order.</b> (1) <see cref="ImportValidator"/>: everything the file alone decides — the key
/// graph, the kind ladder, the §4 value rules, the steps schema — with no database read. (2) <see cref="ImportPlanner"/>:
/// three reads of the stored tree and one of the stored hashes resolve every row to a create, update, restore or
/// unchanged outcome. (3) A dry run stops here: the same report, no allocation, no write. (4) The two counter calls:
/// <b>one</b> <c>ReserveAtLeastAsync</c> past the highest code the file brought — before the block is handed out, so a
/// fresh code can never land on an explicit one — and <b>one</b> <c>AllocateManyAsync</c> for every code-less case row.
/// (5) The apply: a fixed number of set-based statements (see <see cref="ImportApplySql"/>), so 50 rows and 5 000 rows
/// cost the same round trips (plan P2).
/// </para>
/// <para>
/// <b>Nothing is written until everything is known.</b> The whole file is validated and resolved before the first
/// statement of the apply runs, so "one bad row at the end" refuses 199 good ones (AC13). The allocation that phase 4
/// performs is burned when the apply then fails — deliberate, §4's [DECISION] on non-gap-free numbering, exactly as a
/// create that rolls back burns one number.
/// </para>
/// <para>
/// <b>Refusals never echo a cell.</b> Every <c>details[]</c> entry is <c>(path, reason, index)</c> — a fixed column
/// pointer, one of the AC13 reasons, and the row's file position (S3). A key, a title, a step text or a code the caller
/// sent can therefore never reach a log or another tenant through an error.
/// </para>
/// <para>
/// <b>It runs in the caller's scope.</b> Every statement runs on the ambient tenant transaction of the Mediator handler
/// (<see cref="ITenantDb"/>), which owns <c>commit</c>/<c>rollback</c>; the two counter calls deliberately open their own
/// short-lived transaction (the allocator's own design, §4).
/// </para>
/// </remarks>
internal sealed partial class AuthoringImporter(
    ITenantDb db,
    AuthoringCodeAllocator allocator,
    ManualCaseStepsValidator steps,
    RequirementWriter requirements)
{
    /// <summary>The JSON shape of the payloads the set-based statements consume (snake_case, nulls kept).</summary>
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly ImportValidator validator = new(steps);

    private readonly RequirementImporter requirementImporter = new(db, requirements);

    /// <summary>Imports one file's rows (route 25), or reports what it would do.</summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="projectId">The project to import into.</param>
    /// <param name="actorMembershipId">The authoring member, or null.</param>
    /// <param name="rows">The rows a reader produced, in file order.</param>
    /// <param name="dryRun">True to return the report without allocating or writing anything (AC13).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The report, or the refusal of the whole file.</returns>
    internal async ValueTask<Result<ImportReport>> ImportAsync(
        Guid orgId,
        Guid projectId,
        Guid? actorMembershipId,
        IReadOnlyList<ImportRow> rows,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ImportCoreAsync(orgId, projectId, actorMembershipId, rows, dryRun, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (AuthoringLockGuard.IsContention(exception))
        {
            // The writer-wide 5 s lock_timeout (55P03) and a deadlock (40P01) answer the retryable 503 of §5.
            return AuthoringErrors.Fail<ImportReport>(AuthoringErrors.Unavailable());
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation)
        {
            // Only the constraints a concurrent writer can take — a sibling position, a case code, a project counter or a
            // parent row — are retryable: the plan was right when it read, and a second attempt sees the new state. Any
            // other unique or foreign-key violation is this file's own content, which no retry can fix, so it answers
            // deterministically instead of inviting a client to spin (finding H).
            return AuthoringErrors.Fail<ImportReport>(
                IsContentionConstraint(exception.ConstraintName)
                    ? AuthoringErrors.Unavailable()
                    : AuthoringErrors.Invalid(
                        "/rows",
                        "value_invalid",
                        "the file's rows collide with the project's stored rows"));
        }
    }

    /// <summary>
    /// The constraints a concurrent writer can take between the resolution read and the apply: the ones this import
    /// answers with the retryable <c>503</c>. Everything else is the file's own content (finding H).
    /// </summary>
    private static readonly HashSet<string> ContentionConstraints = new(StringComparer.Ordinal)
    {
        "project_org_case_prefix_uq",
        "feature_node_sibling_position_uq",
        "manual_test_case_code_key",
        "feature_node_project_fk",
        "feature_node_parent_fk",
        "checklist_item_feature_node_fk",
        "manual_test_case_checklist_item_fk",
        "authoring_code_counter_project_fk",
    };

    /// <summary>True when a unique or foreign-key violation is one a concurrent writer can cause.</summary>
    /// <param name="constraint">The violated constraint.</param>
    private static bool IsContentionConstraint(string? constraint) =>
        constraint is not null && ContentionConstraints.Contains(constraint);

    /// <summary>The five phases of one import.</summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="actorMembershipId">The authoring member, or null.</param>
    /// <param name="rows">The rows a reader produced, in file order.</param>
    /// <param name="dryRun">True for a report only.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask<Result<ImportReport>> ImportCoreAsync(
        Guid orgId,
        Guid projectId,
        Guid? actorMembershipId,
        IReadOnlyList<ImportRow> rows,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (projectId == Guid.Empty)
        {
            return AuthoringErrors.Fail<ImportReport>(AuthoringErrors.Invalid(
                "/project_id",
                "required",
                "the project is required"));
        }

        if (rows.Count == 0)
        {
            return AuthoringErrors.Fail<ImportReport>(AuthoringErrors.Invalid(
                "/rows",
                "required",
                "an import carries at least one data row"));
        }

        if (rows.Count > ImportLimits.MaxDataRows)
        {
            // A reader refuses an over-long file with 413 already; this guards a caller that bypassed it, so the apply's
            // statements stay bounded whatever the caller does.
            return AuthoringErrors.Fail<ImportReport>(TooLarge());
        }

        var casePrefix = await db.ScalarAsync<string>(
            AuthoringSql.ReadCasePrefix,
            new { org_id = orgId, project_id = projectId },
            cancellationToken).ConfigureAwait(false);

        if (casePrefix is null)
        {
            return AuthoringErrors.Fail<ImportReport>(AuthoringErrors.NotFound(
                "no project with that id is visible in this tenant"));
        }

        if (RequirementImportValidator.MixedFamily(rows) is { } mixed)
        {
            return Refusal([mixed]);
        }

        if (rows[0].Kind == ImportRowKind.Requirement)
        {
            return await requirementImporter
                .ImportAsync(orgId, projectId, actorMembershipId, rows, dryRun, cancellationToken)
                .ConfigureAwait(false);
        }

        var validation = validator.Validate(rows, casePrefix);

        if (!validation.IsAccepted)
        {
            return Refusal(validation.Issues);
        }

        var plan = await new ImportPlanner(db)
            .PlanAsync(orgId, projectId, validation.Rows, cancellationToken)
            .ConfigureAwait(false);

        if (!plan.IsAccepted)
        {
            return Refusal(plan.Issues);
        }

        if (dryRun)
        {
            return Result.Success(Report(null, plan.Rows));
        }

        var coded = await CodesAsync(orgId, projectId, plan, cancellationToken).ConfigureAwait(false);

        if (!coded.IsSuccess)
        {
            return AuthoringErrors.Fail<ImportReport>(coded.Error!);
        }

        var final = coded.Value!;

        if (final.All(row => row.Outcome == ImportOutcome.Unchanged))
        {
            // AC15: a file that describes exactly what is stored appends nothing, so there is no batch to name.
            return Result.Success(Report(null, final));
        }

        var batchId = Guid.CreateVersion7();
        var applied = await ApplyAsync(
            orgId,
            projectId,
            batchId,
            actorMembershipId,
            final,
            plan.Groups,
            cancellationToken).ConfigureAwait(false);

        return applied.IsSuccess
            ? Result.Success(Report(batchId, final))
            : AuthoringErrors.Fail<ImportReport>(applied.Error!);
    }

    /// <summary>Phase 4: the reservation past the file's highest code, then the one block of fresh codes.</summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="plan">The accepted plan.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask<Result<IReadOnlyList<ImportPlannedRow>>> CodesAsync(
        Guid orgId,
        Guid projectId,
        ImportPlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.ExplicitCodeFloor is { } floor)
        {
            // Before the block: the counter must already be past every code the file brought, or a fresh number could
            // land on one of them and the whole transaction would die on manual_test_case_code_key.
            var reserved = await allocator.ReserveAtLeastAsync(orgId, projectId, floor, cancellationToken)
                .ConfigureAwait(false);

            if (!reserved.IsSuccess)
            {
                return AuthoringErrors.Fail<IReadOnlyList<ImportPlannedRow>>(reserved.Error!);
            }
        }

        if (plan.Allocations == 0)
        {
            return Result.Success(plan.Rows);
        }

        var allocated = await allocator.AllocateManyAsync(orgId, projectId, plan.Allocations, cancellationToken)
            .ConfigureAwait(false);

        if (!allocated.IsSuccess)
        {
            return AuthoringErrors.Fail<IReadOnlyList<ImportPlannedRow>>(allocated.Error!);
        }

        var codes = allocated.Value!;
        var next = 0;
        var filled = new ImportPlannedRow[plan.Rows.Count];

        for (var index = 0; index < plan.Rows.Count; index++)
        {
            var row = plan.Rows[index];

            if (row.Outcome != ImportOutcome.Create || row.Row.Kind != ImportRowKind.Case || row.Code is not null)
            {
                filled[index] = row;
                continue;
            }

            if (row.Content is not ManualTestCaseSnapshot manualCase)
            {
                throw new InvalidOperationException("a case create carries a manual test case snapshot");
            }

            var code = codes[next++].Value;
            filled[index] = row with
            {
                Code = code,
                Content = manualCase with { Code = code },
            };
        }

        if (next != codes.Count)
        {
            throw new InvalidOperationException("the allocation block does not match the planned case creates");
        }

        if (filled.Any(row => row.Outcome == ImportOutcome.Create
            && row.Row.Kind == ImportRowKind.Case
            && row.Code is null))
        {
            throw new InvalidOperationException("a case create left phase 4 without a code");
        }

        return Result.Success<IReadOnlyList<ImportPlannedRow>>(filled);
    }
}
