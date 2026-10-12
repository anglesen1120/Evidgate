using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Ingestion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Evidgate.Modules.Ingestion.Application;

/// <summary>
/// The producer registry of 04.1.2-T4.2 (00 §9.4, TL T9/T54): it registers one slot per manifest
/// shard inside the caller's tenant transaction and answers the binding question on the first
/// accepted write. Every statement runs on the ambient tenant scope - the Dapper path for the
/// binding and the read, the module's EF context for the bulk insert - so it joins the caller's
/// transaction and RLS scopes every row (00 §5.5 C3/C4). A call outside a tenant scope fails loudly
/// through <see cref="ITenantScopeAccessor.Require"/>.
/// </summary>
internal sealed class ProducerRegistry(
    ITenantDbContextFactory<IngestionDbContext> contexts,
    ITenantDb db,
    ITenantScopeAccessor scopes) : IProducerRegistry
{
    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<ProducerSlotRef>>> RegisterSlotsAsync(
        Guid runId,
        int shardCount,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Ingestion, nameof(RegisterSlotsAsync));

        // The manifest bound (contracts/manifest/manifest.v1.json shard_plan.shards.maxItems) is checked
        // before any SQL, so an out-of-range count never reaches the database (00 §5.5 C6).
        if (shardCount < 1 || shardCount > ProducerSlotLimits.MaxShards)
        {
            return Result.Failure<IReadOnlyList<ProducerSlotRef>>(ProducerSlotErrors.ShardCountOutOfRange);
        }

        var orgId = scopes.Require().OrgId;

        // Idempotency (T13): a run whose slots are already committed returns them unchanged when the
        // count matches, so a retried create is a no-op; a different count is a conflict, never a
        // silent partial insert.
        var existing = await db.QueryAsync<SlotRow>(
            ProducerSlotSql.ReadRunSlots,
            new { org_id = orgId, run_id = runId },
            cancellationToken).ConfigureAwait(false);

        if (existing.Count > 0)
        {
            return existing.Count == shardCount
                ? Result.Success<IReadOnlyList<ProducerSlotRef>>([.. existing.Select(ToRef)])
                : Result.Failure<IReadOnlyList<ProducerSlotRef>>(ProducerSlotErrors.SlotsAlreadyRegistered);
        }

        var ids = new Guid[shardCount];
        for (var index = 0; index < shardCount; index++)
        {
            ids[index] = Guid.CreateVersion7();
        }

        // One statement for every shard (P2): the ids travel as a uuid[] and the shard index is the
        // array's 1-based ordinal, so there is no per-shard round trip. The insert runs on the module's
        // EF context, which the tenant factory bound to the caller's connection and transaction.
        //
        // `on conflict do nothing` is what keeps a concurrent second registration from aborting the
        // caller's transaction: a bare unique violation would poison it (a caught 23505 leaves the
        // transaction unusable), so the statement itself absorbs the race and the re-read below decides.
        await using var context = contexts.Create();
        var inserted = await context.Database.ExecuteSqlRawAsync(
            ProducerSlotSql.InsertSlots,
            [
                new NpgsqlParameter("org_id", NpgsqlDbType.Uuid) { Value = orgId },
                new NpgsqlParameter("run_id", NpgsqlDbType.Uuid) { Value = runId },
                new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids },
            ],
            cancellationToken).ConfigureAwait(false);

        if (inserted == shardCount)
        {
            var refs = new ProducerSlotRef[shardCount];
            for (var index = 0; index < shardCount; index++)
            {
                refs[index] = new ProducerSlotRef(ids[index], index + 1, ProducerId(index + 1));
            }

            return Result.Success<IReadOnlyList<ProducerSlotRef>>(refs);
        }

        // A partial insert (0 < inserted < shardCount) means a concurrent registration wrote a
        // different shard set into this run: the run's slot set is now mixed, so this call is a
        // conflict and never a success. The caller rolls back, which removes the rows this call added.
        if (inserted > 0)
        {
            return Result.Failure<IReadOnlyList<ProducerSlotRef>>(ProducerSlotErrors.SlotsAlreadyRegistered);
        }

        // Nothing was inserted: the run already carried a full slot set (a concurrent same-count
        // registration won, or a retried create). Re-read and answer from what is stored - the same
        // count is the idempotent no-op, anything else is the conflict.
        var stored = await db.QueryAsync<SlotRow>(
            ProducerSlotSql.ReadRunSlots,
            new { org_id = orgId, run_id = runId },
            cancellationToken).ConfigureAwait(false);

        return stored.Count == shardCount
            ? Result.Success<IReadOnlyList<ProducerSlotRef>>([.. stored.Select(ToRef)])
            : Result.Failure<IReadOnlyList<ProducerSlotRef>>(ProducerSlotErrors.SlotsAlreadyRegistered);
    }

    /// <inheritdoc />
    public async ValueTask<Result<ProducerSlotBinding>> BindOrCheckAsync(
        Guid runId,
        string producerId,
        string tokenJti,
        string? ciRunRef,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Ingestion, nameof(BindOrCheckAsync));

        // The column bounds are checked before any SQL (00 §5.5 C6): an empty or over-long identity
        // would otherwise reach the table and surface as an unhandled 23514 from the CHECK.
        if (!ProducerSlotArguments.IsValidProducerId(producerId)
            || !ProducerSlotArguments.IsWithin(tokenJti, ProducerSlotLimits.MaxTokenJtiLength)
            || !ProducerSlotArguments.IsWithinOrNull(ciRunRef, ProducerSlotLimits.MaxCiRunRefLength))
        {
            return Result.Failure<ProducerSlotBinding>(ProducerSlotErrors.ArgumentOutOfRange);
        }

        var orgId = scopes.Require().OrgId;

        // Read first (P3): after the first write every call is a bound slot, so the common path is one
        // SELECT and no UPDATE. The decision table is unchanged - unknown, idempotent, taken, or bind.
        var existing = await db.QuerySingleOrDefaultAsync<SlotRow>(
            ProducerSlotSql.ReadByProducer,
            new { org_id = orgId, run_id = runId, producer_id = producerId },
            cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            return Result.Failure<ProducerSlotBinding>(ProducerSlotErrors.UnknownProducer);
        }

        // T88: when the run carries a CI reference (set at run create from the caller's OIDC claims), the
        // caller's claims must equal it exactly - a different CI run is a different identity even for an
        // already-bound slot. A run created without one has nothing to compare, and the slot's own binding
        // below is then the whole identity. Both answer the same 403 producer_slot_taken, so a foreign CI
        // run learns nothing about the slot beyond that code (T9).
        if (RunReferenceMismatch(existing, ciRunRef))
        {
            return Result.Failure<ProducerSlotBinding>(ProducerSlotErrors.SlotTaken);
        }

        if (existing.BoundTokenJti is not null)
        {
            return string.Equals(existing.BoundTokenJti, tokenJti, StringComparison.Ordinal)
                && string.Equals(existing.BoundCiRunRef, ciRunRef, StringComparison.Ordinal)
                ? Result.Success(new ProducerSlotBinding(existing.Id, existing.ShardIndex, existing.ProducerId, false))
                : Result.Failure<ProducerSlotBinding>(ProducerSlotErrors.SlotTaken);
        }

        // Unbound: the bind is one conditional UPDATE (T9) whose WHERE keeps the guard, so two racing
        // first writers cannot both win - the loser's WHERE matches no row and falls through below.
        var bound = await db.QuerySingleOrDefaultAsync<SlotRow>(
            ProducerSlotSql.BindIfUnbound,
            new { org_id = orgId, run_id = runId, producer_id = producerId, jti = tokenJti, ci_run_ref = ciRunRef },
            cancellationToken).ConfigureAwait(false);

        if (bound is not null)
        {
            return Result.Success(new ProducerSlotBinding(bound.Id, bound.ShardIndex, bound.ProducerId, true));
        }

        // The race was lost: one re-read decides idempotent (the winner bound this same identity) or taken.
        var after = await db.QuerySingleOrDefaultAsync<SlotRow>(
            ProducerSlotSql.ReadByProducer,
            new { org_id = orgId, run_id = runId, producer_id = producerId },
            cancellationToken).ConfigureAwait(false);

        if (after is null)
        {
            return Result.Failure<ProducerSlotBinding>(ProducerSlotErrors.UnknownProducer);
        }

        // The same T88 comparison as the read path above: the race loser decides against the row the
        // winner wrote, so a mismatching CI run is refused here too.
        if (RunReferenceMismatch(after, ciRunRef))
        {
            return Result.Failure<ProducerSlotBinding>(ProducerSlotErrors.SlotTaken);
        }

        return string.Equals(after.BoundTokenJti, tokenJti, StringComparison.Ordinal)
            && string.Equals(after.BoundCiRunRef, ciRunRef, StringComparison.Ordinal)
            ? Result.Success(new ProducerSlotBinding(after.Id, after.ShardIndex, after.ProducerId, false))
            : Result.Failure<ProducerSlotBinding>(ProducerSlotErrors.SlotTaken);
    }

    /// <inheritdoc />
    public async ValueTask<Result<ProducerSlotClosure>> CloseSlotAsync(
        Guid runId,
        string producerId,
        string tokenJti,
        string? ciRunRef,
        long declaredLastSequence,
        int declaredCount,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Ingestion, nameof(CloseSlotAsync));

        // The column bounds are checked before any SQL (00 §5.5 C6), exactly like the bind's identity
        // arguments: a negative declared value would otherwise surface as an unhandled 23514.
        if (!ProducerSlotArguments.IsValidProducerId(producerId)
            || !ProducerSlotArguments.IsWithin(tokenJti, ProducerSlotLimits.MaxTokenJtiLength)
            || !ProducerSlotArguments.IsWithinOrNull(ciRunRef, ProducerSlotLimits.MaxCiRunRefLength))
        {
            return Result.Failure<ProducerSlotClosure>(ProducerSlotErrors.ArgumentOutOfRange);
        }

        // The declared values carry their own 422: the identity message above names producerId/tokenJti/
        // ciRunRef, so reusing it for a negative declared value would misdescribe the failure.
        if (declaredLastSequence < 0 || declaredCount < 0)
        {
            return Result.Failure<ProducerSlotClosure>(ProducerSlotErrors.DeclaredOutOfRange);
        }

        var orgId = scopes.Require().OrgId;

        // Read first: the decision table is unknown (422), foreign (403), already closed (the idempotent
        // no-op that returns the STORED values), or close. The same indexed lookup the bind uses (P3/P4).
        var existing = await db.QuerySingleOrDefaultAsync<SlotRow>(
            ProducerSlotSql.ReadByProducer,
            new { org_id = orgId, run_id = runId, producer_id = producerId },
            cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            return Result.Failure<ProducerSlotClosure>(ProducerSlotErrors.UnknownProducer);
        }

        // T9/T88: only the bound identity may finalize the slot. An unbound slot has no bound identity,
        // so nobody may close it - 05.3.2-T1's handler binds through BindOrCheckAsync first, so a finalize
        // arriving before the events still closes the slot (05.3.2 AC7).
        if (existing.BoundTokenJti is null
            || RunReferenceMismatch(existing, ciRunRef)
            || !string.Equals(existing.BoundTokenJti, tokenJti, StringComparison.Ordinal)
            || !string.Equals(existing.BoundCiRunRef, ciRunRef, StringComparison.Ordinal))
        {
            return Result.Failure<ProducerSlotClosure>(ProducerSlotErrors.SlotTaken);
        }

        // Already closed: idempotent on closed_at (T68, AC9) - the stored values are returned, nothing is
        // written and no version bump happens, so a duplicate finalize is a no-op (05.3.2 AC6/D3).
        if (existing.ClosedAt is { } alreadyClosed)
        {
            return Result.Success(new ProducerSlotClosure(
                existing.Id,
                existing.ShardIndex,
                existing.ProducerId,
                alreadyClosed,
                existing.DeclaredLastSequence ?? 0,
                existing.DeclaredCount ?? 0,
                Closed: false));
        }

        var closed = await db.QuerySingleOrDefaultAsync<SlotRow>(
            ProducerSlotSql.CloseIfOpen,
            new
            {
                org_id = orgId,
                run_id = runId,
                producer_id = producerId,
                jti = tokenJti,
                ci_run_ref = ciRunRef,
                declared_last_sequence = declaredLastSequence,
                declared_count = declaredCount,
            },
            cancellationToken).ConfigureAwait(false);

        if (closed is not null)
        {
            return Result.Success(new ProducerSlotClosure(
                closed.Id,
                closed.ShardIndex,
                closed.ProducerId,
                closed.ClosedAt!.Value,
                closed.DeclaredLastSequence ?? declaredLastSequence,
                closed.DeclaredCount ?? declaredCount,
                Closed: true));
        }

        // The race was lost: a concurrent close won, so this call is the idempotent no-op and answers with
        // what that winner stored. The row cannot have vanished (a slot is never deleted), but the read is
        // still guarded so the method cannot return an uninitialised closure.
        var after = await db.QuerySingleOrDefaultAsync<SlotRow>(
            ProducerSlotSql.ReadByProducer,
            new { org_id = orgId, run_id = runId, producer_id = producerId },
            cancellationToken).ConfigureAwait(false);

        return after?.ClosedAt is { } racedClosed
            ? Result.Success(new ProducerSlotClosure(
                after.Id,
                after.ShardIndex,
                after.ProducerId,
                racedClosed,
                after.DeclaredLastSequence ?? 0,
                after.DeclaredCount ?? 0,
                Closed: false))
            : Result.Failure<ProducerSlotClosure>(ProducerSlotErrors.UnknownProducer);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ProducerSlotView>> GetRunProducerSlotsAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Ingestion, nameof(GetRunProducerSlotsAsync));

        var orgId = scopes.Require().OrgId;
        var rows = await db.QueryAsync<SlotRow>(
            ProducerSlotSql.ReadRunSlots,
            new { org_id = orgId, run_id = runId },
            cancellationToken).ConfigureAwait(false);

        return [.. rows.Select(row => new ProducerSlotView(
            row.Id,
            row.ShardIndex,
            row.ProducerId,
            row.BoundTokenJti is not null,
            row.ClosedAt,
            row.LastSequence,
            row.DeclaredLastSequence,
            row.DeclaredCount))];
    }

    /// <summary>The deterministic producer id of a 1-based shard index (T54).</summary>
    internal static string ProducerId(int shardIndex) => $"shard-{shardIndex}";

    private static ProducerSlotRef ToRef(SlotRow row) => new(row.Id, row.ShardIndex, row.ProducerId);

    /// <summary>
    /// True when the run carries a CI reference (T88) that is not exactly the caller's. Only the run's
    /// side is compared: a run created without a CI reference has nothing to compare, and the slot's own
    /// binding is then the whole identity. Every mismatch is another identity and answers 403
    /// <c>producer_slot_taken</c>, never a distinct code that would leak whether a slot exists (T9).
    /// </summary>
    private static bool RunReferenceMismatch(SlotRow row, string? ciRunRef) =>
        row.RunCiRunRef is not null && !string.Equals(row.RunCiRunRef, ciRunRef, StringComparison.Ordinal);

    /// <summary>One slot row as the registry's statements return it.</summary>
    private sealed class SlotRow
    {
        public Guid Id { get; set; }

        public int ShardIndex { get; set; }

        public string ProducerId { get; set; } = string.Empty;

        public string? BoundTokenJti { get; set; }

        public string? BoundCiRunRef { get; set; }

        public long LastSequence { get; set; }

        public DateTimeOffset? ClosedAt { get; set; }

        public long? DeclaredLastSequence { get; set; }

        public int? DeclaredCount { get; set; }

        /// <summary>The run's CI reference as <c>repository_id/run_id/run_attempt</c> (T88), or null; only <c>ReadByProducer</c> selects it.</summary>
        public string? RunCiRunRef { get; set; }
    }
}

/// <summary>
/// Errors of the producer registry (04.1.2-T4.2), the shape the 04.7.1-T2 capability store returns
/// (00 §5.5 C8): a value, not an exception, so the caller maps it straight to the 00 §9.2 envelope.
/// </summary>
internal static class ProducerSlotErrors
{
    /// <summary>422: the manifest declares more shards than the 256-shard bound, or fewer than one.</summary>
    internal static Error ShardCountOutOfRange => Error.Unprocessable(
        ApiErrorCode.RequestInvalid,
        $"shardCount must be between 1 and {ProducerSlotLimits.MaxShards}");

    /// <summary>409: the run already carries a different number of slots, so a second registration cannot be a no-op.</summary>
    internal static Error SlotsAlreadyRegistered => Error.Conflict(
        ApiErrorCode.ProducerSlotsConflict,
        "the run already has a different number of producer slots");

    /// <summary>422: no slot of the run carries this producer id (T9).</summary>
    internal static Error UnknownProducer => Error.Unprocessable(
        ApiErrorCode.UnknownProducer,
        "no producer slot of this run carries that producer id");

    /// <summary>
    /// 422: an argument is outside the column bounds the migration's CHECKs enforce, so it is refused
    /// before any SQL instead of surfacing as an unhandled 23514.
    /// </summary>
    internal static Error ArgumentOutOfRange => Error.Unprocessable(
        ApiErrorCode.RequestInvalid,
        $"producerId must match {ProducerSlotLimits.ProducerIdPattern}, tokenJti 1..{ProducerSlotLimits.MaxTokenJtiLength} "
        + $"and ciRunRef null or 1..{ProducerSlotLimits.MaxCiRunRefLength}");

    /// <summary>403: the slot is bound to another token identity or CI reference (T9).</summary>
    internal static Error SlotTaken => Error.Denied(
        ApiErrorCode.ProducerSlotTaken,
        "the producer slot is bound to another token identity");

    /// <summary>422: a declared value <c>CloseSlotAsync</c> would store is outside its column bound (T68).</summary>
    internal static Error DeclaredOutOfRange => Error.Unprocessable(
        ApiErrorCode.RequestInvalid,
        "declaredLastSequence and declaredCount must be non-negative");
}

/// <summary>
/// The argument bounds of the registry, shared with the migration's CHECKs through
/// <see cref="ProducerSlotLimits"/> so the two cannot drift (00 §5.5 C6).
/// </summary>
internal static class ProducerSlotArguments
{
    /// <summary>True when <paramref name="producerId"/> matches the column's charset and length bound.</summary>
    internal static bool IsValidProducerId(string? producerId) =>
        producerId is not null && ProducerIdRegex.IsMatch(producerId);

    /// <summary>
    /// True when <paramref name="value"/> is 1..<paramref name="maxLength"/> characters and carries no
    /// control character or lone surrogate: PostgreSQL rejects U+0000 in text (22021) and a lone
    /// surrogate cannot be encoded, so either would surface as an unhandled exception instead of a
    /// result (00 §5.5 C6).
    /// </summary>
    internal static bool IsWithin(string? value, int maxLength) =>
        value is not null
        && value.Length >= 1
        && value.Length <= maxLength
        && IsStorableText(value);

    /// <summary>True when <paramref name="value"/> is null or 1..<paramref name="maxLength"/> characters.</summary>
    internal static bool IsWithinOrNull(string? value, int maxLength) =>
        value is null || IsWithin(value, maxLength);

    /// <summary>True when every character is storable: no control character and no lone surrogate.</summary>
    private static bool IsStorableText(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];

            if (char.IsControl(character))
            {
                return false;
            }

            if (char.IsSurrogate(character))
            {
                // A surrogate pair is fine; a lone surrogate is not.
                if (!char.IsHighSurrogate(character)
                    || index + 1 >= value.Length
                    || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                index++;
            }
        }

        return true;
    }

    /// <summary>
    /// The migration's <c>producer_slot_producer_id_check</c> charset, built from the single shared
    /// constant: the trailing <c>$</c> of <see cref="ProducerSlotLimits.ProducerIdPattern"/> becomes
    /// <c>\z</c>, because .NET's <c>$</c> also matches before a trailing newline while PostgreSQL's
    /// <c>~</c> does not. Built at runtime (not <c>[GeneratedRegex]</c>) precisely so the shared
    /// constant stays the one source of the charset.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ProducerIdRegex = new(
        ProducerSlotLimits.ProducerIdPattern[..^1] + @"\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
}

/// <summary>The statements of the producer registry; every one runs in the caller's tenant transaction.</summary>
internal static class ProducerSlotSql
{
    /// <summary>
    /// The per-shard insert of one run (AC2, P2): the ids arrive as a <c>uuid[]</c> and the shard
    /// index is the array's 1-based ordinal, so every slot is written in one statement.
    /// <c>on conflict do nothing</c> absorbs a concurrent registration on either run-scoped unique
    /// (<c>producer_slot_run_producer_key</c>/<c>producer_slot_run_shard_key</c>) instead of raising a
    /// 23505 that would abort the caller's transaction; the caller re-reads and decides.
    /// </summary>
    internal const string InsertSlots = """
        insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id)
        select u.id, @org_id, @run_id, u.ord, 'shard-' || u.ord
          from unnest(@ids::uuid[]) with ordinality as u(id, ord)
        on conflict do nothing
        """;

    /// <summary>The slots of one run, in shard order (P4, served by the <c>producer_slot_run</c> index).</summary>
    internal const string ReadRunSlots = """
        select id, shard_index, producer_id, bound_token_jti, bound_ci_run_ref, last_sequence, closed_at,
               declared_last_sequence, declared_count
          from public.producer_slot
         where org_id = @org_id
           and run_id = @run_id
         order by shard_index
        """;

    /// <summary>
    /// One slot by its producer id, with the run's CI reference (T88) and the declared values (T68): the
    /// single indexed lookup of <see cref="IProducerRegistry.BindOrCheckAsync"/> (P3) and the read
    /// <see cref="IProducerRegistry.CloseSlotAsync"/> decides on. The run join is what makes the T88
    /// comparison part of the same statement, so the enforcement costs no extra round trip; it joins on
    /// the slot's own <c>(org_id, run_id)</c>, so RLS scopes the run exactly like the slot row, and
    /// <c>ci_run_ref</c> is written once at run create and never updated, so the join needs no lock.
    /// </summary>
    internal const string ReadByProducer = """
        select s.id, s.shard_index, s.producer_id, s.bound_token_jti, s.bound_ci_run_ref,
               s.last_sequence, s.closed_at, s.declared_last_sequence, s.declared_count,
               (r.ci_run_ref->>'repository_id') || '/' || (r.ci_run_ref->>'run_id') || '/' || (r.ci_run_ref->>'run_attempt') as run_ci_run_ref
          from public.producer_slot s
          join public.run r on r.org_id = s.org_id and r.id = s.run_id
         where s.org_id = @org_id
           and s.run_id = @run_id
           and s.producer_id = @producer_id
        """;

    /// <summary>
    /// The first-write bind (T9): a conditional UPDATE that writes only an unbound slot, so the
    /// statement itself is the race arbiter - a second first writer matches no row and returns none.
    /// </summary>
    internal const string BindIfUnbound = """
        update public.producer_slot
           set bound_token_jti = @jti,
               bound_ci_run_ref = @ci_run_ref
         where org_id = @org_id
           and run_id = @run_id
           and producer_id = @producer_id
           and bound_token_jti is null
        returning id, shard_index, producer_id, bound_token_jti, bound_ci_run_ref, last_sequence, closed_at
        """;

    /// <summary>
    /// The one-shot close (T68, AC9): the whole identity guard sits in the <c>WHERE</c> - an unclosed
    /// slot bound to exactly this jti and CI reference - so the statement itself is the race arbiter and
    /// a second finalize matches no row. <c>closed_at</c> and the two declared values are written
    /// together exactly once; <c>is not distinct from</c> is the null-safe equality the binding needs
    /// (<c>bound_ci_run_ref</c> is null for a non-CI producer), which the read path expresses in C#.
    /// </summary>
    internal const string CloseIfOpen = """
        update public.producer_slot
           set closed_at = now(),
               declared_last_sequence = @declared_last_sequence,
               declared_count = @declared_count
         where org_id = @org_id
           and run_id = @run_id
           and producer_id = @producer_id
           and closed_at is null
           and bound_token_jti = @jti
           and bound_ci_run_ref is not distinct from @ci_run_ref
        returning id, shard_index, producer_id, closed_at, declared_last_sequence, declared_count
        """;
}
