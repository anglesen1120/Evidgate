using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Domain;
using Npgsql;

namespace Evidgate.Modules.Policy.Application;

/// <summary>One append of a policy revision: the row's id and number, its hash, and whether this call created it.</summary>
/// <param name="Id">The revision id (the existing row's id when the hash was already on file).</param>
/// <param name="RevisionNo">The revision number.</param>
/// <param name="Hash">The content address.</param>
/// <param name="Created">True when this call wrote the row.</param>
internal sealed record PolicyRevisionAppend(Guid Id, int RevisionNo, string Hash, bool Created);

/// <summary>
/// The append-only store of policy revisions (07.1.1-T1): one statement per append, the project row locked so the
/// per-project numbering is serialised (the 05.1.2 environment-revision precedent), and a duplicate hash returns
/// the existing row without writing anything or consuming a number (ruling T25). The hash is passed in by the
/// caller — the hasher service is 07.1.1-T2 — and only its shape is checked here.
/// <para>
/// The outcome is a <see cref="Result{T}"/> (00 §5.5 C8), the shape the 05.1.2 environment-revision precedent
/// returns: a project that is missing or soft-deleted is a typed 404
/// (<see cref="PolicyRevisionErrors.ProjectNotFound"/>), and a project row another append holds past
/// <c>lock_timeout</c> is a typed 503 (<see cref="PolicyRevisionErrors.Busy"/>). Both are decided before the slot
/// query and the insert, so a refused append writes no row.
/// </para>
/// </summary>
internal sealed class PolicyRevisionStore(ITenantDb db, ITenantScopeAccessor scopes)
{
    /// <summary>Appends one revision, or returns the existing row when the hash is already on file.</summary>
    public async ValueTask<Result<PolicyRevisionAppend>> AppendAsync(
        Guid projectId,
        string body,
        string hash,
        Guid createdBy,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(AppendAsync));

        // Both shapes are checked before the scope is demanded, so a malformed append never reaches a statement.
        if (!PolicyRevisionRules.IsValidHash(hash) || !PolicyRevisionRules.IsJsonObject(body))
        {
            throw new ArgumentException(
                "the revision hash must be sha256:<64 hex> and the body a JSON object",
                nameof(hash));
        }

        var orgId = scopes.Require().OrgId;

        // A bounded wait for the lock below, the unique-key wait of a concurrent append included (05.1.2 precedent).
        await db.ExecuteAsync(PolicyRevisionSql.LockTimeout, null, cancellationToken).ConfigureAwait(false);

        // The project row is locked first: it serialises the revision numbering of its policy revisions, so two
        // concurrent appends cannot both compute the same max(revision_no) + 1 (05.1.2 precedent). A missing or
        // soft-deleted project is not-found; a lock wait past lock_timeout is busy.
        ProjectRow? project;
        try
        {
            project = await db.QuerySingleOrDefaultAsync<ProjectRow>(
                PolicyRevisionSql.ProjectForUpdate,
                new { project_id = projectId },
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return Result.Failure<PolicyRevisionAppend>(PolicyRevisionErrors.Busy);
        }

        if (project is null)
        {
            return Result.Failure<PolicyRevisionAppend>(PolicyRevisionErrors.ProjectNotFound);
        }

        var slot = await db.QuerySingleOrDefaultAsync<SlotRow>(
            PolicyRevisionSql.RevisionSlot,
            new { org_id = orgId, project_id = projectId, hash },
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("the revision slot query returned no row");

        if (slot.ExistingId is { } existingId)
        {
            return Result.Success(new PolicyRevisionAppend(existingId, slot.ExistingNo!.Value, hash, false));
        }

        var id = Guid.CreateVersion7();
        var inserted = await db.QuerySingleOrDefaultAsync<AppendRow>(
            PolicyRevisionSql.InsertRevision,
            new
            {
                id,
                org_id = orgId,
                project_id = projectId,
                revision_no = slot.NextNo,
                hash,
                body,
                state = PolicyRevisionState.Draft,
                created_by = createdBy,
            },
            cancellationToken).ConfigureAwait(false);

        if (inserted is not null)
        {
            return Result.Success(new PolicyRevisionAppend(inserted.Id, inserted.RevisionNo, hash, true));
        }

        // A concurrent append of the same hash that slipped past the lock: read the winner's row (the same second
        // statement TestRevisionStore uses, because under READ COMMITTED the loser cannot see it in the insert).
        var existing = await db.QuerySingleOrDefaultAsync<AppendRow>(
            PolicyRevisionSql.ReadByHash,
            new { org_id = orgId, project_id = projectId, hash },
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("the revision insert returned neither a new nor an existing row");

        return Result.Success(new PolicyRevisionAppend(existing.Id, existing.RevisionNo, hash, false));
    }

    private sealed class ProjectRow
    {
        public Guid Id { get; set; }
    }

    private sealed class SlotRow
    {
        public Guid? ExistingId { get; set; }

        public int? ExistingNo { get; set; }

        public int NextNo { get; set; }
    }

    private sealed class AppendRow
    {
        public Guid Id { get; set; }

        public int RevisionNo { get; set; }
    }
}

/// <summary>
/// Errors of the policy-revision store (07.1.1-T1), the shape the 05.1.2 environment-revision precedent returns
/// (00 §5.5 C8): a value, not an exception, so 07.1.1-T3's command maps it straight to the 00 §9.2 envelope.
/// </summary>
internal static class PolicyRevisionErrors
{
    /// <summary>404: no project with that id is visible to this tenant, or it is soft-deleted.</summary>
    internal static Error ProjectNotFound => Error.NotFound("no project with that id is visible to this tenant");

    /// <summary>503: another append holds the project row past <c>lock_timeout</c>; the request is retryable.</summary>
    internal static Error Busy => new(
        ApiErrorCode.Unavailable,
        "the project's policy revisions are being changed by another request; retry shortly",
        503);
}

/// <summary>The statements of the policy-revision store; every one runs in the request's tenant transaction.</summary>
internal static class PolicyRevisionSql
{
    /// <summary>A bounded wait for every lock the append takes (05.1.2 precedent).</summary>
    internal const string LockTimeout = "select set_config('lock_timeout', '5000', true)";

    /// <summary>The project row, locked: it serialises the revision numbering of its policy revisions.</summary>
    internal const string ProjectForUpdate = """
        select p.id
          from public.project p
         where p.id = @project_id and p.status <> 'deleted'
           for no key update of p
        """;

    /// <summary>Dedupe and numbering in one round trip.</summary>
    internal const string RevisionSlot = """
        select same.id as existing_id, same.revision_no as existing_no,
               (select coalesce(max(revision_no), 0) + 1 from public.policy_revision
                 where org_id = @org_id and project_id = @project_id) as next_no
          from (select 1) as one
          left join lateral (
                select id, revision_no from public.policy_revision
                 where org_id = @org_id and project_id = @project_id and hash = @hash) as same on true
        """;

    /// <summary>One append; a hash already on file is skipped by the unique key and returns no row.</summary>
    internal const string InsertRevision = """
        insert into public.policy_revision
               (id, org_id, project_id, revision_no, hash, body, state, created_by)
        values (@id, @org_id, @project_id, @revision_no, @hash, @body::jsonb, @state, @created_by)
        on conflict (org_id, project_id, hash) do nothing
        returning id, revision_no
        """;

    /// <summary>The row a lost race left behind.</summary>
    internal const string ReadByHash = """
        select id, revision_no
          from public.policy_revision
         where org_id = @org_id and project_id = @project_id and hash = @hash
        """;
}
