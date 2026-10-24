using Evidgate.Modules.Tenancy.Contracts;

namespace Evidgate.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// The Dapper read SQL of the org/project registry (02.1.1-T1.3, 00 §5.5 C5).
/// Every statement is a single bounded query over the covering index of
/// migration 0005, uses named parameters only (00 §5.7 S1), and projects the
/// exact column set of <see cref="OrganizationDto"/> / <see cref="ProjectDto"/>,
/// so the query handlers of 02.1.1-T2 hold no SQL of their own.
/// </summary>
/// <remarks>
/// <para>
/// The statements return snake_case column names; the read path maps them to the
/// DTO records through Dapper's underscore matching
/// (<c>DefaultTypeMap.MatchNamesWithUnderscores</c>, switched on by
/// <c>AddEvidgateTenancy</c> — 01.4.2). Queries run inside
/// <c>ITenantScope.BeginAsync(orgId)</c> with <c>SET TRANSACTION READ ONLY</c>
/// (00 §5.5 C5), so RLS supplies the tenant filter and the statements never
/// write.
/// </para>
/// <para>
/// Both DTOs are positional records, so the <b>column order below is
/// load-bearing</b>: Dapper matches a constructor parameter to the reader column
/// at the same position, by name only. A column reordered here is a DTO field
/// silently left at its default (M1W3-02), which is what
/// <c>ReadSqlContractTests</c> pins. Every <c>timestamptz</c> column on this path
/// is read through the registered <c>DateTimeOffset</c> type handler, because
/// Npgsql reports it as a <c>DateTime</c>.
/// </para>
/// </remarks>
internal static class TenancyReadSql
{
    /// <summary>One organization by id (<c>GET /v1/orgs/{o}</c>).</summary>
    internal const string OrganizationById = """
        select id,
               name,
               slug,
               status,
               retention_default_days,
               allowed_email_domains,
               created_at,
               updated_at,
               version,
               max_projects,
               max_seats
          from public.organization
         where id = @org_id
        """;

    /// <summary>
    /// One project by id (<c>GET /v1/projects/{p}</c>). The tenant filter comes
    /// from RLS, which is also what makes another org's id indistinguishable from
    /// a missing one (AC3): the query cannot add an <c>org_id</c> predicate the
    /// caller could forget.
    /// </summary>
    internal const string ProjectById = """
        select id,
               org_id,
               name,
               slug,
               status,
               default_environment_id,
               created_at,
               updated_at,
               version,
               retention_days,
               monthly_run_quota,
               case_prefix
          from public.project
         where id = @project_id
        """;

    /// <summary>
    /// The first page of projects of an org (00 §9.1, 00 §5.5 C5): keyset-ordered
    /// on <c>(created_at, id)</c> and covered by
    /// <c>project_org_status_created_id_idx</c> (00 §5.7 P4). There is no cursor
    /// predicate at all, so the first page never carries a branch that could stop
    /// the planner from using the index condition.
    /// </summary>
    internal const string ProjectsFirstPage = """
        select id,
               org_id,
               name,
               slug,
               status,
               default_environment_id,
               created_at,
               updated_at,
               version,
               retention_days,
               monthly_run_quota,
               case_prefix
          from public.project
         where org_id = @org_id
           and status = @status
         order by created_at,
                  id
         limit @limit
        """;

    /// <summary>
    /// The next page of projects of an org: the same projection and order with the
    /// row comparison cursor <c>(created_at, id) &gt; (@cursor_created_at,
    /// @cursor_id)</c> (00 §9.1). The explicit casts keep the comparison typed
    /// against the index columns instead of relying on parameter type inference.
    /// The cursor's two values are required parameters here — the first page is
    /// <see cref="ProjectsFirstPage"/>, so there is no NULL sentinel and no
    /// <c>is null or …</c> branch to defeat the index.
    /// </summary>
    internal const string ProjectsNextPage = """
        select id,
               org_id,
               name,
               slug,
               status,
               default_environment_id,
               created_at,
               updated_at,
               version,
               retention_days,
               monthly_run_quota,
               case_prefix
          from public.project
         where org_id = @org_id
           and status = @status
           and (created_at, id) > (@cursor_created_at::timestamptz, @cursor_id::uuid)
         order by created_at,
                  id
         limit @limit
        """;

    /// <summary>
    /// First half of the project-create gate of 02.1.1-T3.2: the org's status and
    /// <c>max_projects</c>, taking the org row lock. Two concurrent creates in one org are
    /// serialised on this lock; creates in different orgs never contend. The lock is
    /// <c>FOR NO KEY UPDATE</c>, not <c>FOR UPDATE</c>: the latter conflicts with the
    /// <c>FOR KEY SHARE</c> every FK check on <c>organization</c> takes, so it blocked
    /// membership and project inserts in the same org for the whole create (measured in
    /// the 02.1.1 performance review); <c>NO KEY UPDATE</c> still conflicts with itself.
    /// </summary>
    /// <remarks>
    /// The count is deliberately <b>not</b> part of this statement. Under READ COMMITTED a
    /// statement reads from the snapshot taken when it started — before it waited for the
    /// lock — so a count in the same statement misses the project the lock holder just
    /// committed, and N concurrent creates all pass a limit (measured: 8 of 8 passed
    /// <c>max_projects = 3</c>). <see cref="ActiveProjectCount"/> runs after the lock is
    /// held and so gets a snapshot that includes every committed create.
    /// </remarks>
    internal const string ProjectCreateGate = """
        select status,
               max_projects
          from public.organization
         where id = @org_id
           for no key update
        """;

    /// <summary>
    /// Second half of the gate: the org's projects that are not <c>deleted</c>, run only
    /// when a limit is set and only after <see cref="ProjectCreateGate"/> holds the lock.
    /// Bounded by <c>@max_projects</c>: the gate only needs "is the count at the limit",
    /// so the cost is O(limit) rather than O(projects) while the lock is held (an
    /// unbounded count seq-scanned 45 000 rows, ~10 ms, in the performance review).
    /// </summary>
    internal const string ActiveProjectCount = """
        select count(*)
          from (select 1
                  from public.project
                 where org_id = @org_id
                   and status <> 'deleted'
                 limit @max_projects) as counted
        """;

    /// <summary>The status of the request's org, for the mutation guard of 02.1.1 §6 (<c>org_suspended</c>).</summary>
    internal const string OrganizationStatus = """
        select status
          from public.organization
         where id = @org_id
        """;

    /// <summary>The limit surface of <c>ITenantLimits</c> for the request's org (02.1.1-T3.1).</summary>
    internal const string OrganizationLimits = """
        select max_projects,
               max_seats,
               retention_default_days
          from public.organization
         where id = @org_id
        """;

    /// <summary>
    /// The limit surface of <c>ITenantLimits</c> for one project: the project override
    /// wins over the org default for retention (02.1.1 §4).
    /// </summary>
    internal const string ProjectLimits = """
        select p.monthly_run_quota,
               coalesce(p.retention_days, o.retention_default_days) as effective_retention_days
          from public.project p
          join public.organization o
            on o.id = p.org_id
         where p.id = @project_id
        """;
}
