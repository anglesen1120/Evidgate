using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Policy.Application.Waivers;

/// <summary>Wire shape of a waiver (07.3.1 §5).</summary>
/// <param name="Id">Waiver id (UUIDv7).</param>
/// <param name="ProjectId">The project the waiver belongs to.</param>
/// <param name="Scope"><c>case</c> or <c>suite</c>.</param>
/// <param name="TargetId">The case or suite the waiver covers.</param>
/// <param name="Reason">The requester's free-text justification.</param>
/// <param name="RequestedBy">The member that requested it.</param>
/// <param name="ApprovedBy">The member that approved it, or null while <c>requested</c>.</param>
/// <param name="State">The stored state, returned as is (07.3.1 §6).</param>
/// <param name="ExpiresAt">When the waiver stops applying.</param>
/// <param name="CreatedAt">Creation, UTC.</param>
/// <param name="UpdatedAt">Last write, UTC.</param>
internal sealed record WaiverDto(
    Guid Id,
    Guid ProjectId,
    string Scope,
    Guid TargetId,
    string Reason,
    Guid RequestedBy,
    Guid? ApprovedBy,
    string State,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Cursor page of <c>GET /v1/projects/{p}/waivers</c> (07.3.1 §5).</summary>
/// <param name="Items">Waivers in creation order.</param>
/// <param name="NextCursor">Opaque cursor, or null on the last page.</param>
internal sealed record WaiverPage(IReadOnlyList<WaiverDto> Items, string? NextCursor);

/// <summary>Body of <c>POST /v1/waivers</c> (07.3.1 §5).</summary>
/// <param name="ProjectId">The project the waiver belongs to.</param>
/// <param name="Scope"><c>case</c> or <c>suite</c>.</param>
/// <param name="TargetId">The case or suite the waiver covers.</param>
/// <param name="Reason">Justification, 1–2000 characters, no NUL.</param>
/// <param name="ExpiresAt">A date-time strictly in the future.</param>
internal sealed record WaiverCreateRequest(
    Guid ProjectId,
    string? Scope,
    Guid TargetId,
    string? Reason,
    DateTimeOffset? ExpiresAt);

/// <summary>Errors of the waiver routes (00 §9.2, 07.3.1 §5).</summary>
internal static class WaiverErrors
{
    /// <summary>404: no waiver with that id is visible to this tenant, or its project is outside the token scope.</summary>
    internal static Error NotFound => Error.NotFound("no waiver with that id is visible to this tenant");

    /// <summary>404: no live project with that id is visible to this tenant (unknown, other tenant, soft-deleted).</summary>
    internal static Error ProjectNotFound => Error.NotFound("no project with that id is visible to this tenant");

    /// <summary>403: the credential is narrowed to another project (00 §9.1, 02 §4.3).</summary>
    internal static Error ProjectScopeMismatch => Error.Denied(
        ApiErrorCode.ProjectScopeMismatch,
        "the credential is scoped to a different project");

    /// <summary>403: the approver is the requester (AC1, FR-07-8).</summary>
    internal static Error SelfApproval => Error.Denied(
        ApiErrorCode.WaiverSelfApproval,
        "a waiver may not be approved by the member who requested it");

    /// <summary>409: the conditional update matched no row — the state moved, or the waiver expired (07.3.1 §6).</summary>
    internal static Error StateConflict => Error.Conflict(
        ApiErrorCode.WaiverStateConflict,
        "the waiver is not in a state that allows this transition");
}

/// <summary>
/// The project half of the C2 scope rule for the waiver routes: a credential narrowed to one project may touch only
/// that project, and the request's own project (header) must agree (copy of <c>OrchestratorScope.Project</c>).
/// </summary>
internal static class WaiverScopeGuard
{
    /// <summary>Null when <paramref name="projectId"/> is inside every scope the request carries, else 403 <c>project_scope_mismatch</c>.</summary>
    internal static Error? Project(
        Guid projectId,
        IRequestPrincipalAccessor principals,
        IRequestTenantContextAccessor tenant)
    {
        if (principals.Current?.ProjectScope is { } tokenScope && tokenScope != projectId)
        {
            return WaiverErrors.ProjectScopeMismatch;
        }

        return tenant.Current?.ProjectId is { } requestProject && requestProject != projectId
            ? WaiverErrors.ProjectScopeMismatch
            : null;
    }
}

/// <summary>
/// The Dapper statements of the waiver routes (00 §5.5 C5, S1): every one is a single parameterised statement, the
/// list is keyset-paged on <c>(created_at, id)</c> over <c>waiver_project_created_idx</c>, and the insert names only
/// the columns the app role holds <c>INSERT</c> on (07.3.1 §4).
/// </summary>
internal static class WaiverSql
{
    private const string Columns = """
               id, project_id, scope, target_id, reason, requested_by, approved_by, state, expires_at, created_at, updated_at
        """;

    /// <summary>One waiver by id; RLS scopes it to the request's tenant (00 §5.4.2), so another tenant reads no row.</summary>
    internal const string ById = $"""
        select {Columns}
          from public.waiver
         where id = @id
        """;

    /// <summary>The project's liveness, for the project-existence check of the create and list routes.</summary>
    internal const string ProjectExists = "select 1 from public.project where id = @project_id and status <> 'deleted'";

    /// <summary>
    /// The create: only the granted columns are named, so the app role can never insert an already-approved waiver
    /// nor rewrite an identity column; <c>state</c> and <c>approved_by</c> take their defaults (07.3.1 §4).
    /// </summary>
    internal const string Insert = $"""
        insert into public.waiver (id, org_id, project_id, scope, target_id, reason, requested_by, expires_at)
        values (@id, @org_id, @project_id, @scope, @target_id, @reason, @requested_by, @expires_at)
        returning {Columns}
        """;

    /// <summary>
    /// The approve: one conditional statement, so two concurrent approvals race on the row and exactly one matches
    /// <c>state = 'requested'</c> (the other gets 409 <c>waiver_state_conflict</c>).
    /// </summary>
    internal const string Approve = $"""
        update public.waiver
           set state = 'approved',
               approved_by = @approved_by
         where id = @id
           and state = 'requested'
           and expires_at > now()
        returning {Columns}
        """;

    /// <summary>The revoke: one conditional statement over the two non-terminal states (07.3.1 §6).</summary>
    internal const string Revoke = $"""
        update public.waiver
           set state = 'revoked'
         where id = @id
           and state in ('requested', 'approved')
           and expires_at > now()
        returning {Columns}
        """;

    /// <summary>First page of a project's waivers, oldest first.</summary>
    internal const string FirstPage = $"""
        select {Columns}
          from public.waiver
         where org_id = @org_id
           and project_id = @project_id
         order by created_at,
                  id
         limit @limit
        """;

    /// <summary>The next page: the same projection and order with the row-comparison cursor (00 §9.1).</summary>
    internal const string NextPage = $"""
        select {Columns}
          from public.waiver
         where org_id = @org_id
           and project_id = @project_id
           and (created_at, id) > (@cursor_created_at::timestamptz, @cursor_id::uuid)
         order by created_at,
                  id
         limit @limit
        """;
}
