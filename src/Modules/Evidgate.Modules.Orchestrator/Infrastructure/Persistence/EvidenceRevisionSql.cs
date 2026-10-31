namespace Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

/// <summary>
/// The SQL of 05.3.2-T2.3/T2.4's requirement resolution and evidence freeze (plan §5, migration <c>0049</c>): every
/// statement runs over the ambient tenant transaction (00 §5.5 C3), filters <c>org_id</c> explicitly as well as relying
/// on row level security, and answers one bounded result — no statement per execution, per requirement or per artifact.
/// <para>
/// Lock order (<c>0049</c> header, DECISION D-S115-Q1): the caller already holds the run row <c>FOR NO KEY UPDATE</c>
/// through <see cref="ReconcileSql.LockRun"/>, so the insert below fires <c>0049</c>'s guard on a lock this transaction
/// owns, then the pointer moves, then the state changes. Nothing here locks an <c>evidence_revision</c> row first and
/// nothing takes <c>FOR SHARE</c>.
/// </para>
/// </summary>
internal static class EvidenceRevisionSql
{
    /// <summary>
    /// The policy revision the run pinned (<c>run.policy_revision_id</c>, FR-07-2), read once per reconcile. Null is
    /// legitimate — a project with no active policy leaves the pin null and the run requires nothing — and an invisible
    /// run answers no row at all.
    /// </summary>
    internal const string SelectRunPolicyRevision = """
        select r.policy_revision_id as policy_revision_id
          from public.run r
         where r.org_id = @org_id and r.id = @run_id
        """;

    /// <summary>
    /// The run's planned execution keys, bounded by <c>@limit</c>: one read for the whole plan (00 §5.7 P3), served by
    /// <c>planned_execution_run_execution_key</c>'s <c>(org_id, run_id)</c> prefix (0031). The statement deliberately
    /// does <b>not</b> sort: the keys only index a requirement set whose order the freeze sorts itself, so an
    /// <c>order by … collate "C"</c> over up to <see cref="FreezeRequest.MaxRequired"/> + 1 rows is cost without a
    /// determinate reader (PerfRev P2).
    /// </summary>
    internal const string ListPlannedExecutionKeys = """
        select p.execution_key as execution_key
          from public.planned_execution p
         where p.org_id = @org_id and p.run_id = @run_id
         limit @limit
        """;

    /// <summary>
    /// The manifest hash the revision records: the hash of the manifest row the run was created from (<c>run.manifest_id</c>,
    /// 0031), joined tenant-first exactly like <see cref="OrchestratorSql.ReadRunForFinalize"/> so a cross-tenant manifest
    /// is never read. <c>0049</c> requires 1..200 characters and the create path stores the <c>sha256:&lt;hex&gt;</c> address.
    /// </summary>
    internal const string SelectManifestHash = """
        select m.manifest_hash as manifest_hash
          from public.run r
          join public.manifest m on m.org_id = r.org_id and m.id = r.manifest_id
         where r.org_id = @org_id and r.id = @run_id
        """;

    /// <summary>
    /// The one <c>evidence_revision</c> row of this attempt. <c>revision_no</c> is allocated in the statement itself
    /// (<c>coalesce(max) + 1</c>, 1 for the first) and <c>returning</c> answers it, so the caller reports the number it
    /// actually wrote; <c>0049</c>'s <c>guard_evidence_revision_insert</c> re-checks it against the same rule under the
    /// run lock this transaction already holds, which is what keeps the sequence gap-free. <c>outcome</c> stays null
    /// (<c>0049</c> allows it and nothing in T2.3/T2.4 derives it); <c>missing</c> is a one-dimensional <c>text[]</c> of the
    /// 00 §8.5 codes in the caller's stable order.
    /// <para>
    /// <c>@missing</c> is one delimited string, never a <c>string[]</c>: the tenant Dapper path registers
    /// <c>StringArrayTypeHandler</c>, whose write side refuses a client array by design (writes are EF Core's, 00 §5.5
    /// C4/C5), while the codes themselves are <c>[a-z0-9_]</c> and can never contain the delimiter. The empty set is an
    /// explicit <c>array[]::text[]</c> because <c>string_to_array('', ',')</c> is not portable to rely on.
    /// </para>
    /// </summary>
    internal const string InsertRevision = """
        insert into public.evidence_revision
               (id, org_id, run_id, revision_no, completeness, missing, outcome, artifact_set_hash, event_watermark, manifest_hash)
        values (@id, @org_id, @run_id,
                (select coalesce(max(er.revision_no), 0) + 1
                   from public.evidence_revision er
                  where er.org_id = @org_id and er.run_id = @run_id),
                @completeness,
                case when @missing = '' then array[]::text[] else string_to_array(@missing, ',') end,
                null, @artifact_set_hash, @event_watermark, @manifest_hash)
        returning revision_no
        """;

    /// <summary>
    /// The latest revision of the run, read before a late append decides whether it has anything new to record: the
    /// watermark is the run's stored event count, so an equal one means no new event arrived; the frozen artifact set
    /// is read alongside it because a re-freeze can resolve an artifact without any event moving (a parse finished in
    /// between), and that is different evidence at the same watermark. A late reconcile runs after the consumer committed the previous
    /// attempt's transaction and may be re-claimed when that process died before stamping the row <c>done</c>
    /// (<c>TaskConsumer</c> commits the scope, then completes the row), so this read is what keeps one attempt one
    /// revision.
    /// </summary>
    internal const string SelectLatestRevision = """
        select er.id, er.revision_no, er.completeness, er.event_watermark, er.artifact_set_hash
          from public.evidence_revision er
         where er.org_id = @org_id and er.run_id = @run_id
         order by er.revision_no desc
         limit 1
        """;

    /// <summary>
    /// The pointer move 0036/0049 grant the app role: exactly the column <c>current_evidence_revision_id</c>, and
    /// <c>0049</c>'s <c>guard_run_evidence_pointer</c> refuses any value that is not the latest revision of this run. It
    /// runs after the insert in the same transaction, so the pointer can never be ahead of or behind the rows.
    /// </summary>
    internal const string UpdateRunPointer = """
        update public.run r
           set current_evidence_revision_id = @revision_id
         where r.org_id = @org_id and r.id = @run_id
        """;
}
