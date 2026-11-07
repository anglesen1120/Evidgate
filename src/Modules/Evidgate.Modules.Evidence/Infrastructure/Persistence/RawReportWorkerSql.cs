namespace Evidgate.Modules.Evidence.Infrastructure.Persistence;

/// <summary>
/// Parse and reprocess write the same event, slot and report shapes. Producer identities and report ids remain
/// parameters so each caller keeps its own replay identity and transaction.
/// </summary>
internal static class RawReportWorkerSql
{
    internal const string NextShardIndex = """
        select coalesce(max(shard_index), 0) + 1
          from public.producer_slot
         where org_id = @org_id
           and run_id = @run_id
        """;

    // Synthetic producers never finalize, so the slot must be closed and declared together.
    internal const string InsertSlot = """
        insert into public.producer_slot (
            id, org_id, run_id, shard_index, producer_id,
            bound_token_jti, last_sequence, closed_at, declared_last_sequence, declared_count)
        values (
            @id, @org_id, @run_id, @shard_index, @producer_id,
            @bound_token_jti, @last_sequence, now(), @declared_last_sequence, @declared_count)
        on conflict do nothing
        """;

    // Returning only new identities lets the caller distinguish inserts from replay conflicts.
    internal const string InsertEvents = """
        insert into public.event (
            id, org_id, run_id, event_id, producer_id, sequence, job_id, epoch, execution_key,
            attempt_no, type, raw_status, normalized_status, duration_ms, occurred_at, payload_hash,
            payload, artifact_ids, after_cancel, late)
        select
            x.id, @org_id, @run_id, x.event_id, x.producer_id, x.sequence, null, null, x.execution_key,
            1, 'test.finished', x.raw_status, x.normalized_status, x.duration_ms, null, x.payload_hash,
            x.payload::jsonb, x.artifact_ids, @after_cancel, @late
        from jsonb_to_recordset(@events::jsonb) as x (
            id uuid, event_id uuid, producer_id text, sequence bigint, execution_key text, raw_status text,
            normalized_status text, duration_ms bigint, payload_hash text, payload text, artifact_ids uuid[])
        on conflict do nothing
        returning event_id
        """;

    // Conflicting identities are replays only when their stored payload hashes agree.
    internal const string StoredEventHashes = """
        select e.event_id as "EventId",
               e.payload_hash as "PayloadHash"
          from jsonb_to_recordset(@events::jsonb) as x (event_id uuid)
          join public.event e
            on e.org_id = @org_id
           and e.run_id = @run_id
           and e.event_id = x.event_id
        """;

    internal const string UpdateSlot = """
        update public.producer_slot
           set last_sequence = greatest(last_sequence, @last_sequence)
         where org_id = @org_id
           and run_id = @run_id
           and producer_id = @producer_id
        """;

    // A report's verdict is immutable; conflicts leave the earlier row for the caller to inspect.
    internal const string InsertRawReport = """
        insert into public.raw_report (
            id, org_id, run_id, artifact_id, producer_kind, parser_id, parser_version,
            outcome, reason, declared_tests, declared_failures, declared_errors, declared_skipped,
            observed_tests, observed_failures, observed_errors, observed_skipped, discrepancies)
        values (
            @id, @org_id, @run_id, @artifact_id, @producer_kind, @parser_id::text, @parser_version::text,
            @outcome, @reason::text, @declared_tests::integer, @declared_failures::integer,
            @declared_errors::integer, @declared_skipped::integer,
            @observed_tests, @observed_failures, @observed_errors, @observed_skipped, @discrepancies::jsonb)
        on conflict do nothing
        """;

    // The initial parse detects an earlier verdict by artifact and parser release, not its newly minted id.
    internal const string StoredOutcome = """
        select outcome
        from public.raw_report
        where org_id = @org_id
          and artifact_id = @artifact_id
          and parser_id = @parser_id::text
          and parser_version = @parser_version::text
        """;

    // Reprocess uses the request id as its report identity, including when the insert conflicts.
    internal const string StoredOutcomeById = """
        select outcome
        from public.raw_report
        where org_id = @org_id
          and id = @id
        """;
}
