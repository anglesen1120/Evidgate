namespace Evidgate.Modules.Evidence.Infrastructure.Persistence;

/// <summary>
/// The statements of the reprocess command: the report the caller named, the release it may parse with, the two
/// stored rows that already answer for a (report artifact, release) pair, and the request row it inserts.
/// </summary>
/// <remarks>
/// <c>org_id</c> is left to row level security and re-stated where a row is written, so no statement depends on the
/// policy alone; the run predicate is what keeps a report of a sibling run out of every read.
/// </remarks>
internal static class ReprocessRawReportSql
{
    /// <summary>
    /// The report the caller named, restricted to the routed run: <c>org_id</c> comes back with it because the request
    /// row is written with the same value, and every later statement re-states it so no read depends on RLS alone.
    /// </summary>
    internal const string Source = """
        select r.org_id, r.run_id, r.artifact_id, r.parser_id, r.parser_version, r.outcome, a.project_id
          from public.raw_report r
          join public.artifact a
            on a.org_id = r.org_id
           and a.id = r.artifact_id
           and a.run_id = r.run_id
         where r.id = @raw_report_id
           and r.run_id = @run_id
        """;

    /// <summary>
    /// Whether the requested release is one this org may parse with: a built-in release carries a null org, a
    /// project-owned one only exists for its own org.
    /// </summary>
    internal const string ParserVisible = """
        select exists (
            select 1
              from public.report_parser
             where parser_id = @parser_id
               and version = @parser_version
               and (org_id is null or org_id = @org_id))
        """;

    /// <summary>The report already parsed with this release: the id it was written under is the answer.</summary>
    internal const string StoredReport = """
        select id
          from public.raw_report
         where org_id = @org_id
           and artifact_id = @artifact_id
           and parser_id = @parser_id
           and parser_version = @parser_version
        """;

    /// <summary>The request already recorded for this pair: its id is the answer, and its task is already queued.</summary>
    internal const string ExistingRequest = """
        select id
          from public.raw_report_reprocess
         where org_id = @org_id
           and artifact_id = @artifact_id
           and parser_id = @parser_id
           and parser_version = @parser_version
        """;

    /// <summary>
    /// Records the request. The conflict target is the natural key, so a concurrent identical request inserts nothing
    /// and reads nothing back: it answers the winner's id and enqueues no second task.
    /// </summary>
    internal const string Insert = """
        insert into public.raw_report_reprocess
            (id, org_id, run_id, artifact_id, source_raw_report_id, parser_id, parser_version)
        values
            (@id, @org_id, @run_id, @artifact_id, @source_raw_report_id, @parser_id, @parser_version)
        on conflict (org_id, artifact_id, parser_id, parser_version) do nothing
        returning id
        """;
}
