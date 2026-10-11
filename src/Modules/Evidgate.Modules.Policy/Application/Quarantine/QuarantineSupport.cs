using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Policy.Application.Quarantine;

/// <summary>Wire shape of a quarantine entry (07.3.1 §5).</summary>
/// <param name="Id">Entry id (UUIDv7).</param>
/// <param name="ProjectId">The project the entry belongs to.</param>
/// <param name="CaseId">The catalog case the entry excludes from gating.</param>
/// <param name="Owner">The member accountable for the quarantine.</param>
/// <param name="Reason">The free-text justification.</param>
/// <param name="State">The stored state, returned as is (07.3.1 §6).</param>
/// <param name="ExpiresAt">When the entry stops applying.</param>
/// <param name="CreatedAt">Creation, UTC.</param>
/// <param name="UpdatedAt">Last write, UTC.</param>
internal sealed record QuarantineEntry(
    Guid Id,
    Guid ProjectId,
    Guid CaseId,
    Guid Owner,
    string Reason,
    string State,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Cursor page of <c>GET /v1/projects/{p}/quarantine</c> (07.3.1 §5).</summary>
/// <param name="Items">Entries in creation order.</param>
/// <param name="NextCursor">Opaque cursor, or null on the last page.</param>
internal sealed record QuarantineEntryPage(IReadOnlyList<QuarantineEntry> Items, string? NextCursor);

/// <summary>Body of <c>POST /v1/quarantine</c> (07.3.1 §5).</summary>
/// <param name="ProjectId">The project the entry belongs to.</param>
/// <param name="CaseId">The catalog case the entry excludes from gating.</param>
/// <param name="Owner">The member accountable for the quarantine.</param>
/// <param name="Reason">Justification, 1–2000 characters, no NUL.</param>
/// <param name="ExpiresAt">A date-time strictly in the future.</param>
internal sealed record QuarantineEntryCreateRequest(
    Guid ProjectId,
    Guid CaseId,
    Guid Owner,
    string? Reason,
    DateTimeOffset? ExpiresAt);

/// <summary>Errors of the quarantine routes (00 §9.2, 07.3.1 §5).</summary>
internal static class QuarantineErrors
{
    /// <summary>404: no entry with that id is visible to this tenant, or it is not <c>active</c> (AC6).</summary>
    internal static Error NotFound => Error.NotFound("no active quarantine entry with that id is visible to this tenant");

    /// <summary>404: no live project with that id is visible to this tenant (unknown, other tenant, soft-deleted).</summary>
    internal static Error ProjectNotFound => Error.NotFound("no project with that id is visible to this tenant");

    /// <summary>404: no case with that id is visible in that project (unknown, other tenant, other project).</summary>
    internal static Error CaseNotFound => Error.NotFound("no case with that id is visible in that project");

    /// <summary>403: the credential is narrowed to another project (00 §9.1, 02 §4.3).</summary>
    internal static Error ProjectScopeMismatch => Error.Denied(
        ApiErrorCode.ProjectScopeMismatch,
        "the credential is scoped to a different project");

    /// <summary>422: the named owner is not a membership of this organization (the FK backs it; never a 500).</summary>
    internal static Error UnknownOwner => Error.Invalid(
        "the owner is not a member of this organization",
        [new ErrorDetail("owner", "unknown")]);

    /// <summary>422: the case is on the active policy revision's critical list and can never be quarantined (AC4, T23).</summary>
    internal static Error CriticalCaseNotQuarantinable => Error.Unprocessable(
        ApiErrorCode.CriticalCaseNotQuarantinable,
        "a case on the active policy revision's critical list can never be quarantined",
        [new ErrorDetail("case_id", "critical")]);

    /// <summary>409: an active quarantine entry for that case already exists in the project (07.3.1 §5).</summary>
    internal static Error Conflict => Error.Conflict(
        ApiErrorCode.QuarantineConflict,
        "an active quarantine entry for that case already exists in this project");
}

/// <summary>
/// The project half of the C2 scope rule for the quarantine routes: a credential narrowed to one project may touch
/// only that project, and the request's own project (header) must agree (copy of <c>WaiverScopeGuard.Project</c>).
/// </summary>
internal static class QuarantineScopeGuard
{
    /// <summary>Null when <paramref name="projectId"/> is inside every scope the request carries, else 403 <c>project_scope_mismatch</c>.</summary>
    internal static Error? Project(
        Guid projectId,
        IRequestPrincipalAccessor principals,
        IRequestTenantContextAccessor tenant)
    {
        if (principals.Current?.ProjectScope is { } tokenScope && tokenScope != projectId)
        {
            return QuarantineErrors.ProjectScopeMismatch;
        }

        return tenant.Current?.ProjectId is { } requestProject && requestProject != projectId
            ? QuarantineErrors.ProjectScopeMismatch
            : null;
    }
}

/// <summary>
/// The T23 critical-list lookup (07.3.1 §8, D4): the <c>public.project</c> row is locked <c>FOR SHARE</c> inside the
/// same tenant transaction as the insert (<see cref="QuarantineSql.LockProjectForShare"/>), and only then is the
/// project's single active <c>policy_revision</c> row read, so a concurrent 07.1.1-T4 activation that retires it
/// must wait for this transaction to commit — there is no TOCTOU window between the check and the write. The lock
/// is taken on <c>project</c>, not on <c>policy_revision</c>: <c>evidgate_app</c> holds table-level <c>UPDATE</c> on
/// <c>project</c> (0005) but only <c>SELECT, INSERT</c> on <c>policy_revision</c> (0025), and a row lock needs
/// <c>UPDATE</c> — a <c>FOR SHARE</c> on <c>policy_revision</c> is denied 42501. 07.1.1-T4's activation must contend
/// on the same lock: the append path already takes the project row <c>FOR NO KEY UPDATE</c>
/// (<c>PolicyRevisionStore.AppendAsync</c>), which conflicts with this <c>FOR SHARE</c>.
/// The list is the revision body's <c>critical_cases</c> array (<c>contracts/schemas/policy-revision.v1.json</c>); an
/// item that is not UUID-shaped (a fixture path such as <c>checkout/guest</c>) simply never matches the request's
/// canonical lower-case UUID text.
/// </summary>
internal static class QuarantineCriticalCases
{
    /// <summary>
    /// The active revision's <c>critical_cases</c> array as <c>jsonb</c> text, or SQL NULL when the project has no
    /// active revision (no active policy ⇒ no critical list ⇒ the write is allowed). The caller has already locked
    /// the project row <c>FOR SHARE</c> (<see cref="QuarantineSql.LockProjectForShare"/>), so this read is serialised
    /// against a concurrent activation.
    /// </summary>
    internal const string ActiveRevisionCriticalCases = """
        select body -> 'critical_cases' as critical_cases
          from public.policy_revision
         where org_id = @org_id
           and project_id = @project_id
           and state = 'active'
        """;

    /// <summary>True when <paramref name="caseId"/> is on the list (case-insensitive on the canonical UUID text).</summary>
    internal static bool Contains(string? criticalCasesJson, Guid caseId)
    {
        if (string.IsNullOrEmpty(criticalCasesJson))
        {
            return false;
        }

        var canonical = caseId.ToString("D");
        using var document = JsonDocument.Parse(criticalCasesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String
                && string.Equals(item.GetString(), canonical, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The Dapper statements of the quarantine routes (00 §5.5 C5, S1): every one is a single parameterised statement,
/// the list is keyset-paged on <c>(created_at, id)</c> over <c>quarantine_entry_project_created_idx</c>, and the
/// insert names only the columns the app role holds <c>INSERT</c> on (07.3.1 §4).
/// </summary>
internal static class QuarantineSql
{
    private const string Columns = """
               id, project_id, case_id, owner, reason, state, expires_at, created_at, updated_at
        """;

    /// <summary>The project's liveness, for the project-existence check of the list route.</summary>
    internal const string ProjectExists = "select 1 from public.project where id = @project_id and status <> 'deleted'";

    /// <summary>
    /// The project row, locked <c>FOR SHARE</c> (D4): the create's T23 critical-list read and its insert run under
    /// this lock, so a concurrent 07.1.1-T4 activation — which takes the same project row <c>FOR NO KEY UPDATE</c>,
    /// the lock <c>PolicyRevisionStore.AppendAsync</c> uses — cannot retire the active revision between the check
    /// and the write. <c>evidgate_app</c> holds table-level <c>UPDATE</c> on <c>public.project</c> (0005), so the
    /// lock is permitted; the same lock on <c>policy_revision</c> would be denied (0025 grants only
    /// <c>SELECT, INSERT</c>). A missing or soft-deleted project reads no row, so the create still answers 404.
    /// </summary>
    internal const string LockProjectForShare = """
        select 1
          from public.project
         where id = @project_id
           and status <> 'deleted'
           for share
        """;

    /// <summary>The case's existence inside the project; RLS scopes it to the request's tenant, so another tenant's
    /// or another project's case reads no row (D5).</summary>
    internal const string CaseExists = """
        select 1
          from public.test_case
         where org_id = @org_id
           and project_id = @project_id
           and id = @case_id
        """;

    /// <summary>The owner's membership in the org; the FK backs it, so a missing owner is a 422 here, never a 500.</summary>
    internal const string OwnerExists = "select 1 from public.membership where org_id = @org_id and id = @owner";

    /// <summary>
    /// The create: only the granted columns are named, so the app role can never insert a removed/expired entry nor
    /// rewrite an identity column; <c>state</c> takes its <c>active</c> default (07.3.1 §4).
    /// </summary>
    internal const string Insert = $"""
        insert into public.quarantine_entry (id, org_id, project_id, case_id, owner, reason, expires_at)
        values (@id, @org_id, @project_id, @case_id, @owner, @reason, @expires_at)
        returning {Columns}
        """;

    /// <summary>
    /// The remove: one conditional statement, so two concurrent removes race on the row and exactly one matches
    /// <c>state = 'active'</c> (the other gets 404, AC6). The project scope is folded into the predicate, so the
    /// transition is never a read-then-write and an out-of-scope row matches nothing (404, never a leak).
    /// </summary>
    internal const string Remove = $"""
        update public.quarantine_entry
           set state = 'removed'
         where id = @id
           and state = 'active'
           and (@token_scope::uuid is null or project_id = @token_scope::uuid)
           and (@header_project::uuid is null or project_id = @header_project::uuid)
        returning {Columns}
        """;

    /// <summary>First page of a project's quarantine entries, oldest first.</summary>
    internal const string FirstPage = $"""
        select {Columns}
          from public.quarantine_entry
         where org_id = @org_id
           and project_id = @project_id
         order by created_at,
                  id
         limit @limit
        """;

    /// <summary>The next page: the same projection and order with the row-comparison cursor (00 §9.1).</summary>
    internal const string NextPage = $"""
        select {Columns}
          from public.quarantine_entry
         where org_id = @org_id
           and project_id = @project_id
           and (created_at, id) > (@cursor_created_at::timestamptz, @cursor_id::uuid)
         order by created_at,
                  id
         limit @limit
        """;
}
