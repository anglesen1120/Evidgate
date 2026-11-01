namespace Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

/// <summary>
/// The statements of the run's evidence-revision page: the run's visibility read and one keyset page over
/// <c>evidence_revision</c> (<c>0049</c>), newest first. <c>org_id</c> is left to row level security (forced on both
/// tables), so the run predicate is the only extra filter and another tenant's rows are unreachable.
/// </summary>
/// <remarks>
/// The order is <c>revision_no descending</c>, and the index <c>evidence_revision_run_idx</c>
/// (<c>org_id, run_id, revision_no desc</c>) serves it without a sort. <c>revision_no</c> is unique per run
/// (<c>evidence_revision_run_revision_key</c>), so the cursor is that single number: a row written between two pages
/// takes the next number and therefore sorts <b>before</b> the cursor, where a client that pages forward has already
/// passed it — it is neither repeated nor skipped, and nothing an older page returned changes.
/// </remarks>
internal static class EvidenceRevisionListSql
{
    /// <summary>The run's visibility row, for the existence check (unknown, other tenant → no row → 404).</summary>
    internal const string RunScope = "select r.project_id from public.run r where r.id = @run_id";

    /// <summary>The page's projection: the frozen row's own number, stamp, verdict inputs and reason codes.</summary>
    private const string Columns = """
        er.revision_no,
               er.created_at,
               er.completeness,
               er.missing
        """;

    /// <summary>First page of a run's revisions, newest first.</summary>
    internal const string FirstPage = $"""
        select {Columns}
          from public.evidence_revision er
         where er.run_id = @run_id
         order by er.revision_no desc
         limit @limit
        """;

    /// <summary>The next page: the same projection and order below the decoded cursor revision.</summary>
    internal const string NextPage = $"""
        select {Columns}
          from public.evidence_revision er
         where er.run_id = @run_id
           and er.revision_no < @cursor_revision_no::int
         order by er.revision_no desc
         limit @limit
        """;

    /// <summary>
    /// The parameters a page statement declares: the run, the limit and — for a next page — the decoded cursor
    /// number, never the client string. The first page's statement has no cursor parameter at all, and the extended
    /// query protocol refuses a bind that supplies one it omits.
    /// </summary>
    internal static object Parameters(Guid runId, int? cursorRevisionNo, int limit) =>
        cursorRevisionNo is { } cursor
            ? new { run_id = runId, cursor_revision_no = cursor, limit }
            : new { run_id = runId, limit };
}
