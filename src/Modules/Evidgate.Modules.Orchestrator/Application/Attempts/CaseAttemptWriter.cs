using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

namespace Evidgate.Modules.Orchestrator.Application.Attempts;

/// <summary>
/// <see cref="ICaseAttemptWriter"/> over the caller's tenant transaction (05.3.3-T2.1, FR-05-15, ruling T50). It
/// never opens a connection or a transaction, never commits and never rolls back: every statement runs on the
/// ambient scope's connection and transaction through <see cref="ITenantDb"/>, so a rollback of the caller leaves no
/// attempt row (AC5).
/// </summary>
/// <param name="db">The Dapper path over the ambient scope; it demands an active scope before it touches the database.</param>
/// <param name="scopes">The tenant-scope accessor; <c>Require()</c> is the guard that throws before any statement.</param>
internal sealed class CaseAttemptWriter(ITenantDb db, ITenantScopeAccessor scopes) : ICaseAttemptWriter
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CaseAttemptWriteOutcome>> WriteAttemptsAsync(
        Guid runId,
        IReadOnlyList<CaseAttemptInput> attempts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        // Argument problems are the caller's mistake and are reported before the scope guard, so a batch of the
        // wrong size never depends on whether a scope happens to be open.
        if (attempts.Count == 0 || attempts.Count > ICaseAttemptWriter.MaxBatchSize)
        {
            throw new ArgumentException(
                $"a case-attempt batch holds between 1 and {ICaseAttemptWriter.MaxBatchSize} attempts, not {attempts.Count}.",
                nameof(attempts));
        }

        // A null element is a caller mistake too, so it is reported with the batch arguments and before the scope
        // guard, never as a NullReferenceException inside the pre-pass or the SQL.
        for (var index = 0; index < attempts.Count; index++)
        {
            if (attempts[index] is null)
            {
                throw new ArgumentNullException(
                    nameof(attempts),
                    $"attempts[{index}] is null; a case-attempt batch holds no null element.");
            }
        }

        // The tenant-scope guard runs before any statement (AC1): with no open scope this throws and nothing is sent.
        var scope = scopes.Require();

        var outcomes = new CaseAttemptWriteOutcome[attempts.Count];
        var candidates = new List<Candidate>(attempts.Count);
        var firstOccurrence = new Dictionary<SlotKey, int>(attempts.Count);
        var refusal = false;

        for (var index = 0; index < attempts.Count; index++)
        {
            var attempt = attempts[index];
            if (CaseAttemptRules.Validate(attempt) is { } reason)
            {
                outcomes[index] = new CaseAttemptWriteOutcome(CaseAttemptWriteStatus.Invalid, reason);
                refusal = true;
                continue;
            }

            var payload = CaseAttemptRules.PayloadOf(attempt);
            var slot = new SlotKey(attempt.ExecutionKey, attempt.AttemptNo);
            if (firstOccurrence.TryGetValue(slot, out var first))
            {
                // The slot repeats inside the batch: the first occurrence is the candidate, so a later one with the
                // same payload is an idempotent replay and a different payload is a refusal (T50). A same-payload
                // replay remembers its index: if the first occurrence turns out a refusal in statement 2, this entry
                // reports the same refusal instead of Duplicate (C2).
                var status = candidates[first].Payload == payload
                    ? CaseAttemptWriteStatus.Duplicate
                    : CaseAttemptWriteStatus.DuplicateDifferentPayload;
                outcomes[index] = new CaseAttemptWriteOutcome(status);
                refusal |= status == CaseAttemptWriteStatus.DuplicateDifferentPayload;
                if (status == CaseAttemptWriteStatus.Duplicate)
                {
                    candidates[first].DuplicateIndexes.Add(index);
                }

                continue;
            }

            firstOccurrence[slot] = candidates.Count;
            candidates.Add(new Candidate(index, slot, Guid.CreateVersion7(), payload));
            outcomes[index] = new CaseAttemptWriteOutcome(CaseAttemptWriteStatus.Accepted);
        }

        if (refusal)
        {
            // Nothing was written, so no attempt is a persisted fact: every entry that was not decided is the advisory
            // Accepted the pre-pass set and the caller MUST roll back because at least one outcome IsRefusal
            // (05.3.3-T2.1 step 3). The refusing attempts keep their Invalid / DuplicateDifferentPayload outcome, and an
            // in-batch same-payload replay keeps its Duplicate (C1) — its payload equality was decided without any
            // statement, so a refusal elsewhere in the batch does not turn it into an advisory Accepted.
            for (var index = 0; index < outcomes.Length; index++)
            {
                if (outcomes[index].IsRefusal || outcomes[index].Status == CaseAttemptWriteStatus.Duplicate)
                {
                    continue;
                }

                outcomes[index] = new CaseAttemptWriteOutcome(CaseAttemptWriteStatus.Accepted);
            }

            return outcomes;
        }

        // Statement 1: ONE batch insert for the distinct candidates. The inserted id set is the duplicate detection —
        // a slot that already holds a row is simply not returned, never a caught unique violation.
        var insertedIds = await db.QueryAsync<Guid>(
            CaseAttemptSql.InsertAttempts,
            new
            {
                org_id = scope.OrgId,
                run_id = runId,
                project_id = scope.ProjectId,
                rows = SerializeInsertRows(candidates),
            },
            cancellationToken).ConfigureAwait(false);

        var inserted = insertedIds.ToHashSet();

        // Telemetry counts the rows this call wrote inside the caller's transaction. A later rollback of that
        // transaction is not subtracted, so the counter is a write count, not a committed-row count.
        foreach (var group in candidates
                     .Where(candidate => inserted.Contains(candidate.Id))
                     .GroupBy(candidate => candidate.Payload.NormalizedStatus, StringComparer.Ordinal))
        {
            EvidgateTelemetry.RecordCaseAttemptWritten(group.Key, group.LongCount());
        }

        var unwritten = candidates.Where(candidate => !inserted.Contains(candidate.Id)).ToList();
        if (unwritten.Count == 0)
        {
            return outcomes;
        }

        // Statement 2: only the slots statement 1 did not insert, to tell a same-payload replay from a different
        // payload or an execution key the run does not plan for this project.
        var existing = await db.QueryAsync<SlotRow>(
            CaseAttemptSql.ClassifyUnwrittenSlots,
            new
            {
                org_id = scope.OrgId,
                run_id = runId,
                project_id = scope.ProjectId,
                slots = SerializeSlotRows(unwritten),
            },
            cancellationToken).ConfigureAwait(false);

        var bySlot = new Dictionary<SlotKey, Candidate>(unwritten.Count);
        foreach (var candidate in unwritten)
        {
            bySlot[candidate.Slot] = candidate;
        }

        foreach (var row in existing)
        {
            if (!bySlot.TryGetValue(new SlotKey(row.ExecutionKey, row.AttemptNo), out var candidate))
            {
                // The query returns exactly the requested slots; a row for any other slot is unreachable.
                continue;
            }

            if (!row.Planned)
            {
                // Not planned, or planned for a run the scope's project cannot see: the caller answers 422 (T90).
                outcomes[candidate.Index] = new CaseAttemptWriteOutcome(CaseAttemptWriteStatus.UnknownExecution);
                continue;
            }

            if (!row.HasAttempt)
            {
                // Planned, visible, and yet the insert wrote no row and none exists: an invariant violation the
                // caller's transaction must abort on rather than report as a duplicate.
                throw new InvalidOperationException(
                    $"planned execution {row.ExecutionKey} of run {runId} has no case_attempt after the insert wrote nothing.");
            }

            var stored = new CaseAttemptPayload(
                row.RawStatus ?? string.Empty,
                row.NormalizedStatus ?? string.Empty,
                CaseAttemptRules.Truncate(row.StartedAt),
                CaseAttemptRules.Truncate(row.FinishedAt),
                row.DurationMs,
                row.JobId);

            outcomes[candidate.Index] = new CaseAttemptWriteOutcome(
                stored == candidate.Payload
                    ? CaseAttemptWriteStatus.Duplicate
                    : CaseAttemptWriteStatus.DuplicateDifferentPayload);
        }

        // C2: a later in-batch occurrence of a slot ends with the slot's FINAL outcome. When the first occurrence is a
        // refusal after statement 2 — UnknownExecution, or DuplicateDifferentPayload against a stored row that differs —
        // every in-batch same-payload replay of that slot reports the same refusal instead of Duplicate. An in-batch
        // different-payload replay is already DuplicateDifferentPayload and never reaches here (it refuses the batch in
        // the pre-pass), and a written first occurrence leaves its replays as Duplicate.
        foreach (var candidate in unwritten)
        {
            if (!outcomes[candidate.Index].IsRefusal)
            {
                continue;
            }

            foreach (var replayIndex in candidate.DuplicateIndexes)
            {
                outcomes[replayIndex] = outcomes[candidate.Index];
            }
        }

        return outcomes;
    }

    /// <summary>The one <c>jsonb</c> batch parameter of statement 1, in the recordset's column names.</summary>
    private static string SerializeInsertRows(List<Candidate> candidates)
    {
        var rows = new InsertRow[candidates.Count];
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            rows[index] = new InsertRow(
                candidate.Id,
                candidate.Slot.ExecutionKey,
                candidate.Slot.AttemptNo,
                candidate.Payload.RawStatus,
                candidate.Payload.NormalizedStatus,
                Format(candidate.Payload.StartedAt),
                Format(candidate.Payload.FinishedAt),
                candidate.Payload.DurationMs,
                candidate.Payload.JobId);
        }

        return JsonSerializer.Serialize(rows);
    }

    /// <summary>The one <c>jsonb</c> batch parameter of statement 2: the slots to classify.</summary>
    private static string SerializeSlotRows(List<Candidate> candidates)
    {
        var rows = new SlotRowInput[candidates.Count];
        for (var index = 0; index < candidates.Count; index++)
        {
            rows[index] = new SlotRowInput(candidates[index].Slot.ExecutionKey, candidates[index].Slot.AttemptNo);
        }

        return JsonSerializer.Serialize(rows);
    }

    /// <summary>The instant as an ISO-8601 round-trip UTC string, or null; PostgreSQL parses it to the stored microsecond.</summary>
    private static string? Format(DateTimeOffset? instant) =>
        instant?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>The identity of one attempt slot, <c>(execution_key, attempt_no)</c> — the 0032 unique key.</summary>
    private readonly record struct SlotKey(string ExecutionKey, int AttemptNo);

    /// <summary>One distinct slot of the batch: the first occurrence, its index in the caller's list, its new id and payload.</summary>
    private sealed record Candidate(int Index, SlotKey Slot, Guid Id, CaseAttemptPayload Payload)
    {
        /// <summary>
        /// The indexes of the later in-batch occurrences that repeated this slot's first payload (C2). When statement 2
        /// classifies the first occurrence as a refusal they report the same refusal instead of <c>Duplicate</c>.
        /// </summary>
        internal List<int> DuplicateIndexes { get; } = [];
    }

    /// <summary>One row of statement 1 as the <c>jsonb_to_recordset</c> batch reads it (the wire names are the column names).</summary>
    private sealed record InsertRow(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("execution_key")] string ExecutionKey,
        [property: JsonPropertyName("attempt_no")] int AttemptNo,
        [property: JsonPropertyName("raw_status")] string RawStatus,
        [property: JsonPropertyName("normalized_status")] string NormalizedStatus,
        [property: JsonPropertyName("started_at")] string? StartedAt,
        [property: JsonPropertyName("finished_at")] string? FinishedAt,
        [property: JsonPropertyName("duration_ms")] int? DurationMs,
        [property: JsonPropertyName("job_id")] Guid? JobId);

    /// <summary>One slot of statement 2 as the <c>jsonb_to_recordset</c> batch reads it.</summary>
    private sealed record SlotRowInput(
        [property: JsonPropertyName("execution_key")] string ExecutionKey,
        [property: JsonPropertyName("attempt_no")] int AttemptNo);

    /// <summary>The classification of one unwritten slot (statement 2), snake_case columns mapped by Dapper.</summary>
    private sealed class SlotRow
    {
        public string ExecutionKey { get; set; } = string.Empty;

        public int AttemptNo { get; set; }

        public bool Planned { get; set; }

        public bool HasAttempt { get; set; }

        public string? RawStatus { get; set; }

        public string? NormalizedStatus { get; set; }

        public DateTimeOffset? StartedAt { get; set; }

        public DateTimeOffset? FinishedAt { get; set; }

        public int? DurationMs { get; set; }

        public Guid? JobId { get; set; }
    }
}
