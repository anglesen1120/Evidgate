namespace Evidgate.Modules.Evidence.Infrastructure.Persistence;

/// <summary>
/// The read of <c>IFrozenEvidence</c>. It runs on the caller's tenant scope and passes the trusted <c>org_id</c>
/// explicitly as well as relying on row level security, so another tenant's run or revision yields no row whatever the
/// caller asks for.
/// </summary>
internal static class FrozenEvidenceSql
{
    /// <summary>
    /// The frozen revision, keyed by <c>(org_id, run_id, revision_no)</c> (the table's own unique key, so at most one
    /// header stands for a revision), left-joined to one row per execution key of the run: the status of its highest
    /// attempt at or before the revision's <c>created_at</c>. The header is repeated on every row and the attempt
    /// columns are null when no attempt is admitted, so a revision without attempts still answers its real header.
    /// <c>distinct on</c> with <c>attempt_no desc</c> is the set-based form of the per-execution "latest attempt"
    /// lateral, and the key is unique per <c>(org_id, run_id, execution_key, attempt_no)</c>, so the winner is
    /// unambiguous. The bound is inclusive and read from the revision row: without it a retry of an older revision would
    /// judge evidence the frozen row never saw.
    /// </summary>
    internal const string LoadFrozenInput = """
        select er.revision_no as "RevisionNo",
               er.completeness as "Completeness",
               er.missing as "Missing",
               er.manifest_hash as "ManifestHash",
               er.created_at as "CreatedAt",
               latest.execution_key as "ExecutionKey",
               latest.normalized_status as "Status"
          from public.evidence_revision er
          left join lateral (
                select distinct on (a.execution_key)
                       a.execution_key,
                       a.normalized_status
                  from public.case_attempt a
                 where a.org_id = er.org_id
                   and a.run_id = er.run_id
                   and a.created_at <= er.created_at
                 order by a.execution_key,
                          a.attempt_no desc) latest on true
         where er.org_id = @org_id
           and er.run_id = @run_id
           and er.revision_no = @revision_no
         order by latest.execution_key nulls first
        """;
}
