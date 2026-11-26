namespace Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

/// <summary>
/// The run-id chunks behind the decision counts: the project's runs created at or after <c>@since</c>, newest first,
/// keyset-paged on <c>(created_at, id)</c>. The order is exactly <c>run_project_created_idx</c>
/// (<c>org_id, project_id, created_at desc, id</c>), so every chunk is an index range scan that stops at the limit
/// and needs no Sort. The statements are <c>internal</c> so the plan tests can <c>EXPLAIN</c> what the handler runs.
/// </summary>
internal static class RunDecisionCountsSql
{
    /// <summary>The first chunk: the newest runs from <c>@since</c> on. Parameters: org_id, project_id, since, limit.</summary>
    internal const string FirstChunk = """
        select r.id as run_id,
               r.created_at
          from public.run r
         where r.org_id = @org_id
           and r.project_id = @project_id
           and r.created_at >= @since
         order by r.created_at desc,
                  r.id
         limit @limit
        """;

    /// <summary>
    /// The chunk after a position. The leading <c>created_at &lt;=</c> bound is stated apart because a row comparison
    /// cannot mix directions and a bare <c>or</c> would not be an index condition; the second line only decides the
    /// rows tied on <c>created_at</c>. Parameters: those of <see cref="FirstChunk"/> plus cursor_created_at, cursor_id.
    /// </summary>
    internal const string NextChunk = """
        select r.id as run_id,
               r.created_at
          from public.run r
         where r.org_id = @org_id
           and r.project_id = @project_id
           and r.created_at >= @since
           and r.created_at <= @cursor_created_at::timestamptz
           and (r.created_at < @cursor_created_at::timestamptz or r.id > @cursor_id::uuid)
         order by r.created_at desc,
                  r.id
         limit @limit
        """;
}
