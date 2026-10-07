using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Domain;
using Npgsql;

namespace Evidgate.Modules.Policy.Application;

/// <summary>One append of a policy revision: the stored row's columns and whether this call created it.</summary>
/// <param name="Id">The revision id (the existing row's id when the hash was already on file).</param>
/// <param name="RevisionNo">The revision number.</param>
/// <param name="Hash">The content address.</param>
/// <param name="Body">The stored <c>jsonb</c> as text (the database's rendering, finding CR5).</param>
/// <param name="State">The row's lifecycle state.</param>
/// <param name="CreatedBy">The member that wrote the row.</param>
/// <param name="CreatedAt">When the row was stored, UTC.</param>
/// <param name="Version">The row's optimistic-concurrency token.</param>
/// <param name="Created">True when this call wrote the row.</param>
internal sealed record PolicyRevisionAppend(
    Guid Id,
    int RevisionNo,
    string Hash,
    string Body,
    string State,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    int Version,
    bool Created);

/// <summary>
/// The append-only store of policy revisions (07.1.1-T1): one statement per append, the project row locked so the
/// per-project numbering is serialised (the 05.1.2 environment-revision precedent), and a duplicate hash returns
/// the existing row without writing anything or consuming a number (ruling T25). The hash is computed here from
/// the body by the injected <see cref="IPolicyHasher"/> (07.1.1-T2, finding T1-SEC-S2), so a stored body can never
/// sit under another content's address.
/// <para>
/// The outcome is a <see cref="Result{T}"/> (00 §5.5 C8), the shape the 05.1.2 environment-revision precedent
/// returns: a project that is missing or soft-deleted is a typed 404
/// (<see cref="PolicyRevisionErrors.ProjectNotFound"/>), and a project row another append holds past
/// <c>lock_timeout</c> is a typed 503 (<see cref="PolicyRevisionErrors.Busy"/>). Both are decided before the slot
/// query and the insert, so a refused append writes no row.
/// </para>
/// <para>
/// An optional <c>expectedHeadId</c> (07.1.1-T3, AC5) is compared against the project's current head revision
/// <em>after</em> the project row lock and before the slot query, so the check and the append are serialised by the
/// same lock: a stale <c>If-Match</c> is a typed 412
/// (<see cref="PolicyErrors.Stale"/>) and writes nothing.
/// </para>
/// </summary>
internal sealed class PolicyRevisionStore(ITenantDb db, ITenantScopeAccessor scopes, IPolicyHasher hasher)
{
    /// <summary>Appends one revision, or returns the existing row when the hash is already on file.</summary>
    /// <param name="projectId">The project the revision belongs to.</param>
    /// <param name="body">The raw policy body.</param>
    /// <param name="createdBy">The member writing the revision.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="expectedHeadId">
    /// The head revision id the caller's <c>If-Match</c> named, or null for no precondition. Checked under the
    /// project row lock, so a concurrent append cannot slip between the check and the insert.
    /// </param>
    public async ValueTask<Result<PolicyRevisionAppend>> AppendAsync(
        Guid projectId,
        string body,
        Guid createdBy,
        CancellationToken cancellationToken,
        Guid? expectedHeadId = null)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(AppendAsync));

        // Every shape is checked before the scope is demanded, so a malformed append never reaches a statement.
        if (!PolicyRevisionRules.IsJsonObject(body))
        {
            throw new ArgumentException("the body must be a storable JSON object", nameof(body));
        }

        // A number that only exists beyond double precision would be rounded by RFC 8785, so two bodies could
        // share one content address (I-JSON, RFC 7493 §2.2). Refused before the hash and before any statement.
        if (!PolicyRevisionRules.HasOnlyExactNumbers(body))
        {
            throw new ArgumentException("the body must carry only numbers exactly representable as IEEE 754 doubles", nameof(body));
        }

        string hash;
        try
        {
            hash = hasher.Hash(body);
        }
        catch (JsonCanonicalizationException)
        {
            // A duplicate name, a lone surrogate or a number outside the IEEE 754 double range: not a policy body.
            // The cause is not chained: its message carries property names and number text from the body (review S2).
            throw new ArgumentException("the body cannot be canonicalised", nameof(body));
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

        // The If-Match precondition (AC5) is decided here, under the project row lock: a concurrent append cannot
        // move the head between this read and the insert below, so a caller whose view is stale writes nothing.
        if (expectedHeadId is { } expected)
        {
            var head = await db.ScalarAsync<Guid?>(
                PolicyRevisionSql.HeadRevision,
                new { org_id = orgId, project_id = projectId },
                cancellationToken).ConfigureAwait(false);

            if (head != expected)
            {
                return Result.Failure<PolicyRevisionAppend>(PolicyErrors.Stale(head));
            }
        }

        var slot = await db.QuerySingleOrDefaultAsync<SlotRow>(
            PolicyRevisionSql.RevisionSlot,
            new { org_id = orgId, project_id = projectId, hash },
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("the revision slot query returned no row");

        if (slot.ExistingId is { } existingId)
        {
            return Result.Success(new PolicyRevisionAppend(
                existingId,
                slot.ExistingNo!.Value,
                hash,
                slot.ExistingBody!,
                slot.ExistingState!,
                slot.ExistingCreatedBy!.Value,
                slot.ExistingCreatedAt!.Value,
                slot.ExistingVersion!.Value,
                false));
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
            return Result.Success(new PolicyRevisionAppend(
                inserted.Id,
                inserted.RevisionNo,
                hash,
                inserted.Body,
                inserted.State,
                inserted.CreatedBy,
                inserted.CreatedAt,
                inserted.Version,
                true));
        }

        // A concurrent append of the same hash that slipped past the lock: read the winner's row (the same second
        // statement TestRevisionStore uses, because under READ COMMITTED the loser cannot see it in the insert).
        var existing = await db.QuerySingleOrDefaultAsync<AppendRow>(
            PolicyRevisionSql.ReadByHash,
            new { org_id = orgId, project_id = projectId, hash },
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("the revision insert returned neither a new nor an existing row");

        return Result.Success(new PolicyRevisionAppend(
            existing.Id,
            existing.RevisionNo,
            hash,
            existing.Body,
            existing.State,
            existing.CreatedBy,
            existing.CreatedAt,
            existing.Version,
            false));
    }

    private sealed class ProjectRow
    {
        public Guid Id { get; set; }
    }

    private sealed class SlotRow
    {
        public Guid? ExistingId { get; set; }

        public int? ExistingNo { get; set; }

        public string? ExistingBody { get; set; }

        public string? ExistingState { get; set; }

        public Guid? ExistingCreatedBy { get; set; }

        public DateTimeOffset? ExistingCreatedAt { get; set; }

        public int? ExistingVersion { get; set; }

        public int NextNo { get; set; }
    }

    private sealed class AppendRow
    {
        public Guid Id { get; set; }

        public int RevisionNo { get; set; }

        public string Body { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public Guid CreatedBy { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public int Version { get; set; }
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

    /// <summary>The project's current head revision (the highest <c>revision_no</c>), or no row before the first append.</summary>
    internal const string HeadRevision = """
        select id
          from public.policy_revision
         where org_id = @org_id and project_id = @project_id
         order by revision_no desc
         limit 1
        """;

    /// <summary>Dedupe and numbering in one round trip.</summary>
    internal const string RevisionSlot = """
        select same.id as existing_id, same.revision_no as existing_no,
               same.body::text as existing_body,
               same.state as existing_state, same.created_by as existing_created_by,
               same.created_at as existing_created_at, same.version as existing_version,
               (select coalesce(max(revision_no), 0) + 1 from public.policy_revision
                 where org_id = @org_id and project_id = @project_id) as next_no
          from (select 1) as one
          left join lateral (
                select id, revision_no, body::text as body, state, created_by, created_at, version from public.policy_revision
                 where org_id = @org_id and project_id = @project_id and hash = @hash) as same on true
        """;

    /// <summary>One append; a hash already on file is skipped by the unique key and returns no row.</summary>
    internal const string InsertRevision = """
        insert into public.policy_revision
               (id, org_id, project_id, revision_no, hash, body, state, created_by)
        values (@id, @org_id, @project_id, @revision_no, @hash, @body::jsonb, @state, @created_by)
        on conflict (org_id, project_id, hash) do nothing
        returning id, revision_no, state, created_by, created_at, version, body::text as body
        """;

    /// <summary>The row a lost race left behind.</summary>
    internal const string ReadByHash = """
        select id, revision_no, state, created_by, created_at, version, body::text as body
          from public.policy_revision
         where org_id = @org_id and project_id = @project_id and hash = @hash
        """;
}
