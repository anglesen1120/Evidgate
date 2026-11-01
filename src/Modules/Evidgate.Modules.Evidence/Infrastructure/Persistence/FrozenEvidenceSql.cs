namespace Evidgate.Modules.Evidence.Infrastructure.Persistence;

/// <summary>
/// The reads of <c>IFrozenEvidence</c>. Both run on the caller's tenant scope and pass the trusted <c>org_id</c>
/// explicitly as well as relying on row level security, so another tenant's run or revision yields no row whatever the
/// caller asks for.
/// </summary>
internal static class FrozenEvidenceSql
{
    /// <summary>
    /// The frozen revision, keyed by <c>(org_id, run_id, revision_no)</c> — the table's own unique key, so at most one row
    /// stands for a revision and the freeze stamp is read from the row rather than computed by the caller.
    /// </summary>
    internal const string LoadRevision = """
        select er.revision_no as "RevisionNo",
               er.completeness as "Completeness",
               er.missing as "Missing",
               er.manifest_hash as "ManifestHash",
               er.created_at as "CreatedAt"
          from public.evidence_revision er
         where er.org_id = @org_id
           and er.run_id = @run_id
           and er.revision_no = @revision_no
        """;

    /// <summary>
    /// One row per execution key of the run: the status of its highest attempt at or before the revision's
    /// <c>created_at</c>. <c>distinct on</c> with <c>attempt_no desc</c> is the set-based form of the per-execution
    /// "latest attempt" lateral, and the key is unique per <c>(org_id, run_id, execution_key, attempt_no)</c>, so the
    /// winner is unambiguous. The bound is inclusive and read from the joined revision row: without it a retry of an
    /// older revision would judge evidence the frozen row never saw. Only keys that have an admitted attempt appear.
    /// </summary>
    internal const string LoadLatestAttemptStatuses = """
        select distinct on (a.execution_key)
               a.execution_key as "ExecutionKey",
               a.normalized_status as "Status"
          from public.case_attempt a
          join public.evidence_revision er
            on er.org_id = a.org_id
           and er.run_id = a.run_id
           and er.revision_no = @revision_no
         where a.org_id = @org_id
           and a.run_id = @run_id
           and a.created_at <= er.created_at
         order by a.execution_key,
                  a.attempt_no desc
        """;
}
