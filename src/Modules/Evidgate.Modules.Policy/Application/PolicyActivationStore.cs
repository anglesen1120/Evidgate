using System.Text.Json;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Domain;
using Npgsql;

namespace Evidgate.Modules.Policy.Application;

/// <summary>One activation: the pointer's new target, its hash and the outcome the wire DTO reports.</summary>
/// <param name="PolicyRevisionId">The revision that is now the project's active policy.</param>
/// <param name="Hash">That revision's content address (the audit row carries it, 07.1.1 §4).</param>
/// <param name="Mode">The stored mode (<c>advisory</c>; <c>enforcing</c> is refused before the write).</param>
/// <param name="ActivatedBy">The member that activated it.</param>
/// <param name="ActivatedAt">When the pointer was written, UTC (the database's clock).</param>
/// <param name="PreviousPolicyRevisionId">The revision this call retired, or null when nothing changed.</param>
/// <param name="Changed">True when this call moved the pointer; false when the target was already active.</param>
internal sealed record PolicyActivationOutcome(
    Guid PolicyRevisionId,
    string Hash,
    string Mode,
    Guid ActivatedBy,
    DateTimeOffset ActivatedAt,
    Guid? PreviousPolicyRevisionId,
    bool Changed);

/// <summary>
/// The activation store (07.1.1-T4.1, 07.1.1 §6): one tenant transaction that locks the project row
/// <c>FOR NO KEY UPDATE</c> (the same lock <see cref="PolicyRevisionStore.AppendAsync"/> takes, and the one
/// quarantine creation contends on with <c>FOR SHARE</c>), reads the target revision, refuses the
/// critical-case rules, retires the previous active revision and activates the target, then upserts the
/// project's <c>policy_activation</c> pointer. The caller writes the audit row in the same transaction.
/// <para>
/// The outcome is a <see cref="Result{T}"/> (00 §5.5 C8), the shape the policy-revision store returns: a
/// missing or soft-deleted project is 404, an unknown / another project's / another tenant's revision is
/// the same 404 (RLS scopes the read), a retired revision is 409 <c>invalid_transition</c> (07.1.1 §6),
/// and a project row another writer holds past <c>lock_timeout</c> is 503 <c>unavailable</c>.
/// </para>
/// <para>
/// Ordering is load-bearing for the partial unique index <c>policy_revision_active_key</c>
/// (<c>(org_id, project_id) where state = 'active'</c>, migration 0033): the previous active revision is
/// retired before the target is activated, so at most one row is active at every statement boundary.
/// </para>
/// </summary>
internal sealed class PolicyActivationStore(ITenantDb db, ITenantScopeAccessor scopes)
{
    /// <summary>Activates <paramref name="revisionId"/> as the project's policy, or returns the typed refusal.</summary>
    /// <param name="projectId">The project from the route.</param>
    /// <param name="revisionId">The revision the body named.</param>
    /// <param name="mode">The validated mode (<c>advisory</c>).</param>
    /// <param name="activatedBy">The authenticated member.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<Result<PolicyActivationOutcome>> ActivateAsync(
        Guid projectId,
        Guid revisionId,
        string mode,
        Guid activatedBy,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(ActivateAsync));

        var orgId = scopes.Require().OrgId;

        // A bounded wait for the project row lock below, the same 5 s the append path allows (05.1.2 precedent).
        await db.ExecuteAsync(PolicyActivationSql.LockTimeout, null, cancellationToken).ConfigureAwait(false);

        // The project row is locked first, in the same statement/helper shape the append path uses: it
        // serialises this activation against a concurrent append, a concurrent activation and a concurrent
        // quarantine create (which takes the same row FOR SHARE, 07.3.1 D4). A missing or soft-deleted
        // project reads no row (404); a lock wait past lock_timeout is busy (503).
        int? live;
        try
        {
            live = await db.ScalarAsync<int?>(
                PolicyActivationSql.ProjectForUpdate,
                new { project_id = projectId },
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return Result.Failure<PolicyActivationOutcome>(PolicyActivationErrors.Busy);
        }

        if (live is null)
        {
            return Result.Failure<PolicyActivationOutcome>(PolicyActivationErrors.ProjectNotFound);
        }

        // RLS scopes this read to the request's tenant, so another tenant's revision is the same 404 as an
        // unknown id; the project predicate makes another project's revision a 404 too.
        var revision = await db.QuerySingleOrDefaultAsync<RevisionRow>(
            PolicyActivationSql.RevisionById,
            new { org_id = orgId, project_id = projectId, revision_id = revisionId },
            cancellationToken).ConfigureAwait(false);
        if (revision is null)
        {
            return Result.Failure<PolicyActivationOutcome>(PolicyActivationErrors.RevisionNotFound);
        }

        // `retired` is terminal (07.1.1 §6): a retired revision is never activated again.
        if (revision.State == PolicyRevisionState.Retired)
        {
            return Result.Failure<PolicyActivationOutcome>(PolicyActivationErrors.Retired);
        }

        // Q3: every critical_cases entry must be a case UUID (the 07.3.1-T2 Guid.TryParse-after-trim rule).
        // A non-UUID entry is refused with a pointer to it, before any write.
        if (CriticalCasesOf(revision.CriticalCases) is { } malformed)
        {
            return Result.Failure<PolicyActivationOutcome>(malformed);
        }

        // Q2: a critical case of the revision must not have an active quarantine entry in this project.
        // The project row is already locked, so this read cannot race a quarantine create (which takes the
        // same row FOR SHARE) nor a quarantine remove.
        var caseIds = CriticalCaseIds(revision.CriticalCases);
        if (caseIds.Count > 0)
        {
            var quarantined = (await db.QueryAsync<Guid>(
                PolicyActivationSql.QuarantinedCriticalCases,
                new { org_id = orgId, project_id = projectId, case_ids = caseIds.ToArray() },
                cancellationToken).ConfigureAwait(false)).ToHashSet();

            if (quarantined.Count > 0)
            {
                return Result.Failure<PolicyActivationOutcome>(PolicyActivationErrors.CriticalCaseQuarantined(
                    [.. caseIds.Where(quarantined.Contains).Select(id => new ErrorDetail($"critical_cases/{id:D}", "quarantined"))]));
            }
        }

        var previousActiveId = await db.ScalarAsync<Guid?>(
            PolicyActivationSql.ActiveRevision,
            new { org_id = orgId, project_id = projectId },
            cancellationToken).ConfigureAwait(false);

        // Activating the already-active revision changes nothing (200, changed = false); the pointer is
        // still upserted so the mode is re-stamped and exactly one audit row is written per call.
        var changed = previousActiveId != revisionId;
        if (changed)
        {
            if (previousActiveId is not null)
            {
                await db.ExecuteAsync(
                    PolicyActivationSql.RetireActive,
                    new { org_id = orgId, project_id = projectId },
                    cancellationToken).ConfigureAwait(false);
            }

            var activated = await db.ExecuteAsync(
                PolicyActivationSql.Activate,
                new { org_id = orgId, project_id = projectId, revision_id = revisionId },
                cancellationToken).ConfigureAwait(false);
            if (activated != 1)
            {
                throw new InvalidOperationException("the policy activation matched no draft revision");
            }
        }

        var row = await db.QuerySingleOrDefaultAsync<ActivationRow>(
            PolicyActivationSql.UpsertActivation,
            new
            {
                id = Guid.CreateVersion7(),
                org_id = orgId,
                project_id = projectId,
                policy_revision_id = revisionId,
                mode,
                activated_by = activatedBy,
            },
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("the policy activation upsert returned no row");

        return Result.Success(new PolicyActivationOutcome(
            row.PolicyRevisionId,
            revision.Hash,
            row.Mode,
            row.ActivatedBy,
            row.ActivatedAt,
            changed ? previousActiveId : null,
            changed));
    }

    /// <summary>
    /// Null when every <c>critical_cases</c> entry is a UUID, else the 422 naming each entry that is not
    /// (Q3, the 07.3.1-T2 <c>Guid.TryParse</c>-after-trim rule). A missing or non-array value carries no
    /// entries, so it is not refused here — the closed <c>policy-revision.v1</c> schema already owns that.
    /// </summary>
    private static Error? CriticalCasesOf(string? criticalCasesJson)
    {
        if (string.IsNullOrEmpty(criticalCasesJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(criticalCasesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var details = new List<ErrorDetail>();
        var index = 0;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || !Guid.TryParse(item.GetString()?.Trim(), out _))
            {
                details.Add(new ErrorDetail($"critical_cases/{index}", "invalid_value"));
            }

            index++;
        }

        return details.Count == 0 ? null : PolicyActivationErrors.CriticalCasesNotUuids(details);
    }

    /// <summary>The revision's <c>critical_cases</c> entries that are UUIDs, in body order (Q2).</summary>
    private static List<Guid> CriticalCaseIds(string? criticalCasesJson)
    {
        var ids = new List<Guid>();
        if (string.IsNullOrEmpty(criticalCasesJson))
        {
            return ids;
        }

        using var document = JsonDocument.Parse(criticalCasesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return ids;
        }

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString()?.Trim(), out var parsed))
            {
                ids.Add(parsed);
            }
        }

        return ids;
    }

    private sealed class RevisionRow
    {
        public Guid Id { get; set; }

        public string State { get; set; } = string.Empty;

        public string Hash { get; set; } = string.Empty;

        public string? CriticalCases { get; set; }
    }

    private sealed class ActivationRow
    {
        public Guid PolicyRevisionId { get; set; }

        public string Mode { get; set; } = string.Empty;

        public Guid ActivatedBy { get; set; }

        public DateTimeOffset ActivatedAt { get; set; }
    }
}

/// <summary>Errors of the activation store (00 §9.2, 07.1.1 §5/§6, owner decisions Q2/Q3).</summary>
internal static class PolicyActivationErrors
{
    /// <summary>404: no live project with that id is visible to this tenant (unknown, other tenant, soft-deleted).</summary>
    internal static Error ProjectNotFound => Error.NotFound("no project with that id is visible to this tenant");

    /// <summary>404: no revision with that id is visible in that project (unknown, other project, other tenant).</summary>
    internal static Error RevisionNotFound => Error.NotFound("no policy revision with that id is visible in this project");

    /// <summary>409: the revision is retired, and `retired` is terminal (07.1.1 §6).</summary>
    internal static Error Retired => Error.Conflict(
        ApiErrorCode.InvalidTransition,
        "a retired policy revision can never be activated again");

    /// <summary>503: another writer holds the project row past <c>lock_timeout</c>; the request is retryable.</summary>
    internal static Error Busy => new(
        ApiErrorCode.Unavailable,
        "the project's policy is being changed by another request; retry shortly",
        503);

    /// <summary>422: a critical case of the revision has an active quarantine entry in the project (Q2).</summary>
    internal static Error CriticalCaseQuarantined(IReadOnlyList<ErrorDetail> details) => Error.Unprocessable(
        ApiErrorCode.CriticalCaseQuarantined,
        "a critical case of this revision has an active quarantine entry in the project",
        details);

    /// <summary>422: a <c>critical_cases</c> entry is not a case UUID (Q3); the pointer names it.</summary>
    internal static Error CriticalCasesNotUuids(IReadOnlyList<ErrorDetail> details) => Error.Invalid(
        "the revision's critical_cases must be case UUIDs",
        details);
}

/// <summary>The statements of the activation store; every one runs in the request's tenant transaction.</summary>
internal static class PolicyActivationSql
{
    /// <summary>A bounded wait for every lock the activation takes (05.1.2/PolicyRevisionStore precedent).</summary>
    internal const string LockTimeout = "select set_config('lock_timeout', '5000', true)";

    /// <summary>
    /// The project row, locked <c>FOR NO KEY UPDATE</c> — the lock <c>PolicyRevisionStore.AppendAsync</c>
    /// uses and the one quarantine creation takes <c>FOR SHARE</c>, so activation, append and quarantine
    /// create are serialised. A missing or soft-deleted project reads no row.
    /// </summary>
    internal const string ProjectForUpdate = """
        select 1
          from public.project p
         where p.id = @project_id and p.status <> 'deleted'
           for no key update of p
        """;

    /// <summary>The target revision's lifecycle state, hash and critical list (the tenant scope is the org GUC).</summary>
    internal const string RevisionById = """
        select id, state, hash, body -> 'critical_cases' as critical_cases
          from public.policy_revision
         where org_id = @org_id
           and project_id = @project_id
           and id = @revision_id
        """;

    /// <summary>The project's currently active revision, or no row when it has none.</summary>
    internal const string ActiveRevision = """
        select id
          from public.policy_revision
         where org_id = @org_id
           and project_id = @project_id
           and state = 'active'
        """;

    /// <summary>Retires the previous active revision (before the target is activated: the partial unique index).</summary>
    internal const string RetireActive = """
        update public.policy_revision
           set state = 'retired'
         where org_id = @org_id
           and project_id = @project_id
           and state = 'active'
        """;

    /// <summary>Activates the target; the state predicate makes a lost race match nothing.</summary>
    internal const string Activate = """
        update public.policy_revision
           set state = 'active'
         where org_id = @org_id
           and project_id = @project_id
           and id = @revision_id
           and state = 'draft'
        """;

    /// <summary>
    /// The pointer UPSERT: one row per project (<c>policy_activation_project_key</c>), re-stamped on every
    /// activation. Only the granted columns are named (migration 0033); <c>activated_at</c> and the
    /// bookkeeping columns are the database's.
    /// </summary>
    internal const string UpsertActivation = """
        insert into public.policy_activation (id, org_id, project_id, policy_revision_id, mode, activated_by)
        values (@id, @org_id, @project_id, @policy_revision_id, @mode, @activated_by)
        on conflict (org_id, project_id) do update
           set policy_revision_id = excluded.policy_revision_id,
               mode = excluded.mode,
               activated_by = excluded.activated_by,
               activated_at = now()
        returning policy_revision_id, mode, activated_by, activated_at
        """;

    /// <summary>The project's active quarantine entries that cover any of the revision's critical cases (Q2).</summary>
    internal const string QuarantinedCriticalCases = """
        select case_id
          from public.quarantine_entry
         where org_id = @org_id
           and project_id = @project_id
           and state = 'active'
           and case_id = any(@case_ids::uuid[])
        """;
}
