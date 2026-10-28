namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// Read SQL of the security-events API (11.5.1-T5.2): one statement per page (P3), keyset on
/// <c>(created_at, id)</c> descending. Two indexes cover it (P4): an unfiltered read, or one narrowed
/// only by <c>from</c>/<c>to</c>, is served by <c>security_event_org_created_idx</c> on
/// <c>(org_id, created_at desc, id desc)</c>; a read that binds all three of <c>project_id</c>,
/// <c>type</c> and <c>severity</c> is narrowed by <c>security_event_list_idx</c> on
/// <c>(org_id, project_id, type, severity, created_at desc, id desc)</c>, whose equalities come first and
/// whose key ends in the cursor tuple, so an index scan returns the page already ordered. A bitmap scan
/// (cheap for a small selective page, keeps no order) and a partial bind on the three filters (an unbound
/// column precedes <c>created_at</c>) still sort, legitimately.
/// </summary>
internal static class SecurityEventReadSql
{
    /// <summary>
    /// One page. Optional filters are <c>null</c>-tolerant; the cursor predicate is the tuple comparison
    /// both indexes serve, in their <c>(created_at desc, id desc)</c> order. <c>@limit</c> is page size + 1.
    /// Only the 8 DTO columns are read: <c>org_id</c> and the internals (<c>updated_at</c>, <c>version</c>)
    /// never leave the tenant boundary.
    /// </summary>
    internal const string Page =
        """
        select id as Id, project_id as ProjectId, type as Type, severity as Severity,
               actor::text as ActorJson, details::text as DetailsJson,
               request_id as RequestId, created_at as CreatedAt
          from public.security_event
         where org_id = @org_id
           and (@project_id::uuid is null or project_id = @project_id)
           and (@type::text is null or type = @type)
           and (@severity::text is null or severity = @severity)
           and (@from::timestamptz is null or created_at >= @from)
           and (@to::timestamptz is null or created_at < @to)
           and (@cursor_created_at::timestamptz is null or (created_at, id) < (@cursor_created_at, @cursor_id::uuid))
         order by created_at desc, id desc
         limit @limit
        """;
}

/// <summary>Dapper shape of <see cref="SecurityEventReadSql.Page"/>.</summary>
internal sealed class SecurityEventPageRow
{
    public Guid Id { get; set; }

    public Guid? ProjectId { get; set; }

    public string Type { get; set; } = string.Empty;

    public string Severity { get; set; } = string.Empty;

    public string ActorJson { get; set; } = "{}";

    public string DetailsJson { get; set; } = "{}";

    public string? RequestId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
