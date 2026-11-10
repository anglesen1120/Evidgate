namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// The mechanism's statements (01.5.1 §4/§7 high-level plan §7). They live in one
/// place because more than one component issues them and because the index proofs
/// of 01.5.1 §9 run <c>EXPLAIN</c> on the very text production runs
/// (<c>Claim_UsesTaskClaimIndex</c>), rather than on a copy that could drift.
/// </summary>
/// <remarks>
/// Column aliases are quoted PascalCase so Dapper binds the positional records of
/// <see cref="TaskClaim"/> without relying on the process-wide
/// <c>MatchNamesWithUnderscores</c> switch, and <c>payload::text</c> hands the
/// handler a JSON string rather than a provider-specific jsonb value.
/// </remarks>
public static class TaskQueueSql
{
    /// <summary>
    /// The queue-state snapshot behind <c>task_queue_oldest_age_seconds</c> and <c>task_dead_letter_depth</c>:
    /// one aggregate pass over the claim index for the due queue and the dead-letter partial index for the
    /// depth, so a scrape of the two gauges costs one bounded query per refresh interval, never one per row.
    /// </summary>
    internal const string QueueStateSnapshot = """
        select
          coalesce(
              extract(epoch from now() - min(available_at))
                  filter (where state in ('queued', 'retry') and available_at <= now()),
              0)::double precision as "OldestAgeSeconds",
          count(*) filter (where state = 'dead_letter')::bigint as "DeadLetters"
        from public.task
        """;

    /// <summary>
    /// The visibility timeout of 01.5.1 §6: a lease that ran out without a
    /// completion is flipped back to <c>queued</c> so the next claim pass takes it
    /// again. This is the crashed-worker path, and it is why a claim never needs a
    /// heartbeat. The predicate is one queue and one row state, so the scan is
    /// bounded by the queue's in-flight concurrency; the claim query itself always
    /// filters on <c>task_claim_idx</c>.
    ///
    /// <para>
    /// Rows that already used up their attempts are excluded here and moved to
    /// <c>dead_letter</c> by <see cref="DeadLetterExpiredLeases"/> instead: a
    /// handler that kills the process (rather than throwing) never reaches the
    /// consumer's own failure transition, so without this branch a poison task would
    /// be reclaimed and re-run for ever — exactly the loop the retry cap exists to
    /// stop (§6).
    /// </para>
    /// </summary>
    public const string ReclaimExpiredLeases = """
        update public.task
           set state = 'queued',
               locked_until = null,
               available_at = now()
         where id in (
                 select id
                   from public.task
                  where queue = @queue
                    and state = 'active'
                    and locked_until <= now()
                    and attempts < max_attempts
                  order by locked_until
                  limit @batch_size
                    for update skip locked)
        """;

    /// <summary>
    /// The other half of the reclaim: an expired lease whose attempts are used up
    /// gives up instead of coming back. Its <c>last_error</c> names the lease as
    /// the cause — overwritten even when an earlier attempt already left an error,
    /// because a handler that crashed the consumer is exactly what an expired lease
    /// at the cap looks like — and the row becomes visible to the operator path
    /// (01.5.1-T4.2) rather than looping.
    /// </summary>
    /// <remarks>
    /// The <c>RETURNING</c> clause exists for the alert hook of §7: the transition
    /// commits first, then <see cref="ITaskAlertSink"/> is told about each row by
    /// the caller — an UPDATE over a bounded set is how every dead-lettered row
    /// gets its own alert instead of the batch count.
    /// </remarks>
    public const string DeadLetterExpiredLeases = """
        update public.task
           set state = 'dead_letter',
               locked_until = null,
               last_error = 'lease expired: the consumer holding this row did not finish attempt ' || attempts
         where id in (
                 select id
                   from public.task
                  where queue = @queue
                    and state = 'active'
                    and locked_until <= now()
                    and attempts >= max_attempts
                  order by locked_until
                  limit @batch_size
                    for update skip locked)
        returning id as "Id",
                  org_id as "OrgId",
                  queue as "Queue",
                  task_type as "TaskType",
                  attempts as "Attempts",
                  max_attempts as "MaxAttempts",
                  last_error as "LastError"
        """;

    /// <summary>
    /// Claims up to <c>@batch_size</c> due rows of one queue and takes the lease in
    /// the same statement (so a row is never `active` without a lease, and a row
    /// between "selected" and "updated" cannot be seen by anyone but the locker).
    ///
    /// <para>
    /// `for update skip locked` is the mechanism's concurrency contract (F6,
    /// AC7): two claimers never hand the same row to two consumers — the second
    /// walks past the locked rows instead of waiting. The sub-select walks
    /// <c>task_claim_idx</c> (partial on `state in ('queued','retry')`, in
    /// `(queue, available_at)` order) so the batch is the queue's oldest due work.
    /// </para>
    ///
    /// <para>
    /// `task_type = any(@task_types)` is what keeps an undeployed task type
    /// `queued` (01.5.1 §11): a consumer only claims the work it has a handler for,
    /// so a payload a later release introduces is not burning attempts — and then
    /// landing in `dead_letter` — while nobody can run it.
    /// </para>
    ///
    /// <para>
    /// `attempts &lt; max_attempts` is the cap of §6 as a claim predicate: a row that
    /// used up its attempts is not claimable again, whatever put it back in
    /// `queued`/`retry` (the reclaim above, or an operator replay that resets
    /// `attempts` on purpose).
    /// </para>
    ///
    /// <para>
    /// The batch is returned in <em>due order</em> (`order by available_at, id`) rather
    /// than in whatever order the UPDATE happened to return rows: an `UPDATE …
    /// RETURNING` has no defined output order (PostgreSQL returns them in the order it
    /// processed the sub-select's hash output), so the wrapper CTE is what makes "the
    /// queue's oldest due work first" a contract the consumer can rely on and assert.
    /// The `id` tiebreak gives a dispatcher's batch — many rows sharing one timestamp —
    /// the FIFO order its UUIDv7 ids already encode.
    /// </para>
    ///
    /// <para>
    /// `version` comes back with the claim and is the lease token every transition is
    /// conditional on (<see cref="TaskLeaseWriter"/>): the claim's own UPDATE bumps it
    /// through the shared trigger, and so does every later write to the row. A consumer
    /// whose lease expired therefore cannot apply its outcome on top of the newer
    /// attempt's — not even when a later claim happens to take a lease with the same
    /// `locked_until`. Relying on the lease value alone would rest on a timestamp
    /// comparison that only holds as long as the reclaim/claim timing rules do; the
    /// version holds whatever the timing is.
    /// </para>
    /// </summary>
    public const string ClaimBatch = """
        with claimed as (
          update public.task
             set state = 'active',
                 locked_until = now() + make_interval(secs => @visibility_timeout),
                 attempts = attempts + 1
           where id in (
                   select id
                     from public.task
                    where queue = @queue
                      and task_type = any(@task_types)
                      and state in ('queued', 'retry')
                      and attempts < max_attempts
                      and available_at <= now()
                    order by available_at
                    limit @batch_size
                      for update skip locked)
           returning id,
                     org_id,
                     queue,
                     task_type,
                     payload::text as payload,
                     idempotency_key,
                     schedule_name,
                     fire_at,
                     state,
                     attempts,
                     max_attempts,
                     available_at,
                     locked_until,
                     version)
        select id as "Id",
               org_id as "OrgId",
               queue as "Queue",
               task_type as "TaskType",
               payload as "Payload",
               idempotency_key as "IdempotencyKey",
               schedule_name as "ScheduleName",
               fire_at as "FireAt",
               state as "State",
               attempts as "Attempts",
               max_attempts as "MaxAttempts",
               available_at as "AvailableAt",
               locked_until as "LockedUntil",
               version as "Version"
          from claimed
         order by available_at, id
        """;

    /// <summary>
    /// The first page of <c>GET /internal/operator/tasks/dead-letter</c>
    /// (01.5.1 §5/§8 P4): keyset over <c>task_dead_idx</c> in its own
    /// <c>(created_at, id)</c> order, so the partial index serves both the
    /// <c>state = 'dead_letter'</c> predicate and the ordering — the limit the
    /// caller passes is fetch size + 1 (the one extra row is how "there is a next
    /// page" is answered without a count). The queue filter is an index filter on
    /// top of the key columns, never part of the ordering.
    /// </summary>
    internal const string DeadLetterFirstPage = """
        select id as "Id",
               org_id as "OrgId",
               queue as "Queue",
               task_type as "TaskType",
               idempotency_key as "IdempotencyKey",
               attempts as "Attempts",
               max_attempts as "MaxAttempts",
               last_error as "LastError",
               created_at as "CreatedAt"
          from public.task
         where state = 'dead_letter'
           and (@queue is null or queue = @queue)
         order by created_at, id
         limit @limit
        """;

    /// <summary>
    /// The continuation of <see cref="DeadLetterFirstPage"/>: the row comparison
    /// <c>(created_at, id) &gt; (@cursor_created_at, @cursor_id)</c> of 00 §9.1,
    /// with explicit casts so the comparison types against the index columns
    /// instead of relying on parameter inference. There is no NULL-sentinel
    /// branch (that would defeat the index): a missing cursor goes through the
    /// first-page constant above.
    /// </summary>
    internal const string DeadLetterNextPage = """
        select id as "Id",
               org_id as "OrgId",
               queue as "Queue",
               task_type as "TaskType",
               idempotency_key as "IdempotencyKey",
               attempts as "Attempts",
               max_attempts as "MaxAttempts",
               last_error as "LastError",
               created_at as "CreatedAt"
          from public.task
         where state = 'dead_letter'
           and (@queue is null or queue = @queue)
           and (created_at, id) > (@cursor_created_at::timestamptz, @cursor_id::uuid)
         order by created_at, id
         limit @limit
        """;

    /// <summary>
    /// The operator replay of 01.5.1 §5/§6 (`dead_letter → queued`): the guard
    /// is the <c>state = 'dead_letter'</c> predicate, which makes the POST
    /// idempotent — a second replay of the same row no-ops into the 409 the
    /// endpoint answers. The row's error is kept: a replay is a re-queue, not an
    /// acquittal, and the operator's audit of why it dead-lettered stays visible
    /// on the row until the terminal sweep.
    /// </summary>
    internal const string ReplayDeadLetter = """
        update public.task
           set state = 'queued',
               attempts = 0,
               available_at = now(),
               locked_until = null
         where id = @id
           and state = 'dead_letter'
        returning available_at as "AvailableAt"
        """;

    /// <summary>
    /// The 404/409 discriminator of <see cref="ReplayDeadLetter"/> (01.5.1 §5):
    /// a replay that matched no dead-letter row reads the row's actual state —
    /// no row means `task_not_found`, a row in any other state means
    /// `task_not_dead_letter`. Same connection as the update, so no TOCTOU can
    /// name a state that was already replaced.
    /// </summary>
    internal const string TaskStateById = """
        select state
          from public.task
         where id = @id
        """;
}
