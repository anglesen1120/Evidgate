namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// The Dapper read SQL of the permission catalogue (02.2.1-T1.2, 00 §5.5 C5). Two
/// bounded statements, named parameters only (00 §5.7 S1), projecting exactly the
/// columns of <c>PermissionView</c> / <c>RoleView</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both statements run inside <c>ITenantScope.BeginAsync</c> with
/// <c>SET TRANSACTION READ ONLY</c> (C1/T95), so RLS supplies the row filter: the
/// catalogue rows (<c>org_id is null</c>) plus the caller's own org rows, never
/// another org's (02 §4.5). The read path maps snake_case columns to the DTOs
/// through <c>DefaultTypeMap.MatchNamesWithUnderscores</c> (switched on by
/// <c>AddEvidgateTenancy</c>) and every <c>timestamptz</c> through the registered
/// <c>DateTimeOffset</c> type handler.
/// </para>
/// <para>
/// Each statement takes the cursor as two nullable parameters and guards the
/// keyset predicate with <c>is null or …</c> rather than splitting into a
/// first-page/next-page pair the way <c>TenancyReadSql</c> does. That split exists
/// there because a project list is hot and a NULL sentinel can keep the planner off
/// the index condition; these are platform-catalogue lists of tens of rows whose
/// whole point is the small page, so one statement per query is the cheaper
/// reading. The casts (<c>::text</c>, <c>::timestamptz</c>, <c>::uuid</c>) keep the
/// comparison typed against the indexed columns instead of relying on parameter
/// type inference for a NULL.
/// </para>
/// </remarks>
internal static class IdentityReadSql
{
    /// <summary>
    /// One page of the permission catalogue (<c>GET /v1/permissions</c>), ordered by
    /// <c>code</c> — the table's primary key, so the order is total and the cursor
    /// is simply the last code of the previous page.
    /// </summary>
    internal const string PermissionsPage = """
        select code,
               resource,
               action,
               description,
               sensitive,
               reserved,
               tenant_assignable
          from public.permission
         where (@cursor_code::text is null or code > @cursor_code::text)
         order by code
         limit @limit
        """;

    /// <summary>
    /// One page of roles (<c>GET /v1/roles</c>), keyset-ordered on
    /// <c>(created_at, id)</c> per 00 §9.1 and covered by
    /// <c>role_created_id_idx</c> (00 §5.7 P4). RLS decides which roles those are:
    /// the built-in catalogue plus the caller's own org rows.
    /// </summary>
    internal const string RolesPage = """
        select id,
               org_id,
               code,
               name,
               is_builtin,
               created_at,
               version
          from public.role
         where (@cursor_created_at::timestamptz is null
                or (created_at, id) > (@cursor_created_at::timestamptz, @cursor_id::uuid))
         order by created_at,
                  id
         limit @limit
        """;
}
