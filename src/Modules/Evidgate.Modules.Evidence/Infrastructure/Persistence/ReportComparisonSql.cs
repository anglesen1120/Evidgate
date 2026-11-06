namespace Evidgate.Modules.Evidence.Infrastructure.Persistence;

/// <summary>
/// The statements of the report comparison read: the stored report joined to its artifact for the scope check, and one
/// keyset page over the terminal events that report's parse wrote, ordered by the producer's sequence.
/// </summary>
/// <remarks>
/// <c>org_id</c> is left to row level security (forced on both tables), so the run and report predicates are the only
/// extra filters. The case page names the artifact-scoped synthetic producer id instead of the run's events, so
/// another artifact of the same run contributes no row. The page groups by <c>execution_key</c> and pages on its smallest
/// sequence, because the contract counts the events of a case rather than assuming exactly one.
/// </remarks>
internal static class ReportComparisonSql
{
    /// <summary>
    /// The report row the route names, with the project its artifact belongs to. The three-column join repeats the
    /// report's own run, so a row whose artifact names another run cannot answer for this route.
    /// </summary>
    internal const string Report = """
        select rr.id,
               rr.run_id,
               rr.artifact_id,
               rr.producer_kind,
               rr.parser_id,
               rr.parser_version,
               rr.outcome,
               rr.reason,
               rr.declared_tests,
               rr.declared_failures,
               rr.declared_errors,
               rr.declared_skipped,
               rr.observed_tests,
               rr.observed_failures,
               rr.observed_errors,
               rr.observed_skipped,
               rr.discrepancies::text as discrepancies,
               a.project_id
          from public.raw_report rr
          join public.artifact a
            on a.org_id = rr.org_id
           and a.id = rr.artifact_id
           and a.run_id = rr.run_id
         where rr.run_id = @run_id
           and rr.id = @report_id
        """;

    /// <summary>The page's projection: one row per execution key, with its first event's producer status.</summary>
    private const string CaseColumns = """
        e.execution_key,
               (array_agg(e.raw_status order by e.sequence))[1] as raw_status,
               min(e.sequence) as sequence,
               count(*)::int as observed_events
        """;

    /// <summary>The first page of the report's terminal events, oldest sequence first.</summary>
    internal const string FirstCasePage = $"""
        select {CaseColumns}
          from public.event e
         where e.run_id = @run_id
           and e.producer_id = @producer_id
           and e.type = 'test.finished'
         group by e.execution_key
         order by min(e.sequence)
         limit @limit
        """;

    /// <summary>
    /// The next page: the same rows above the decoded cursor. The bound is on the group's smallest sequence, never on
    /// a single event's, so a key whose first event an earlier page returned is never repeated.
    /// </summary>
    internal const string NextCasePage = $"""
        select {CaseColumns}
          from public.event e
         where e.run_id = @run_id
           and e.producer_id = @producer_id
           and e.type = 'test.finished'
         group by e.execution_key
        having min(e.sequence) > @cursor_sequence
         order by min(e.sequence)
         limit @limit
        """;

    /// <summary>The parameters of a first page: the run, the report's producer and one more row than the page size.</summary>
    internal static object FirstPageParameters(Guid runId, string producerId, int limit) =>
        new { run_id = runId, producer_id = producerId, limit };

    /// <summary>The parameters of a next page, which bind the decoded sequence rather than the client's cursor text.</summary>
    internal static object NextPageParameters(Guid runId, string producerId, long cursorSequence, int limit) =>
        new { run_id = runId, producer_id = producerId, cursor_sequence = cursorSequence, limit };
}
