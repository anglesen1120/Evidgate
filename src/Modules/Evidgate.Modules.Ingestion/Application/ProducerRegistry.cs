using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
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
    /// <remarks>
    /// Lock order (T91): a caller that reads the run takes the run row <c>FOR SHARE</c> first (the events
    /// handler does, in its scope read); this method then reads the slot (joined to the run, unlocked) and at most
    /// binds it with one conditional UPDATE, and never locks the run. No path takes the slot row before the run, so there is no cycle.
    /// </remarks>
    public async ValueTask<Result<ProducerSlotBinding>> BindOrCheckAsync(
        Guid runId,
        string producerId,
        string ownerId,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Ingestion, nameof(BindOrCheckAsync));

        // The column bounds are checked before any SQL (00 §5.5 C6): an empty or over-long identity
        // would otherwise reach the table and surface as an unhandled 23514 from the CHECK.
        if (!ProducerSlotArguments.IsValidProducerId(producerId)
            || !ProducerSlotArguments.IsWithin(ownerId, ProducerSlotLimits.MaxOwnerIdLength))
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

        if (existing.BoundTokenJti is not null)
        {
            return string.Equals(existing.BoundTokenJti, ownerId, StringComparison.Ordinal)
                ? Result.Success(new ProducerSlotBinding(existing.Id, existing.ShardIndex, existing.ProducerId, false))
                : Result.Failure<ProducerSlotBinding>(ProducerSlotErrors.SlotTaken);
        }

        // Unbound: the bind is one conditional UPDATE (T9) whose WHERE keeps the guard, so two racing
        // first writers cannot both win - the loser's WHERE matches no row and falls through below.
        var bound = await db.QuerySingleOrDefaultAsync<SlotRow>(
            ProducerSlotSql.BindIfUnbound,
            new { org_id = orgId, run_id = runId, producer_id = producerId, owner_id = ownerId },
            cancellationToken).ConfigureAwait(false);

        if (bound is not null)
        {
            return Result.Success(new ProducerSlotBinding(bound.Id, bound.ShardIndex, bound.ProducerId, true));
        }

        // The race was lost: one re-read decides idempotent (the winner bound this same owner) or taken.
        var after = await db.QuerySingleOrDefaultAsync<SlotRow>(
            ProducerSlotSql.ReadByProducer,
            new { org_id = orgId, run_id = runId, producer_id = producerId },
            cancellationToken).ConfigureAwait(false);

        if (after is null)
        {
            return Result.Failure<ProducerSlotBinding>(ProducerSlotErrors.UnknownProducer);
        }

        return string.Equals(after.BoundTokenJti, ownerId, StringComparison.Ordinal)
            ? Result.Success(new ProducerSlotBinding(after.Id, after.ShardIndex, after.ProducerId, false))
            : Result.Failure<ProducerSlotBinding>(ProducerSlotErrors.SlotTaken);
    }

    /// <inheritdoc />
    public async ValueTask<Result<ProducerSlotClosure>> CloseSlotAsync(
        Guid runId,
        string producerId,
        string ownerId,
        long declaredLastSequence,
        int declaredCount,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Ingestion, nameof(CloseSlotAsync));

        // The column bounds are checked before any SQL (00 §5.5 C6), exactly like the bind's identity
        // arguments: a negative declared value would otherwise surface as an unhandled 23514.
        if (!ProducerSlotArguments.IsValidProducerId(producerId)
            || !ProducerSlotArguments.IsWithin(ownerId, ProducerSlotLimits.MaxOwnerIdLength))
        {
            return Result.Failure<ProducerSlotClosure>(ProducerSlotErrors.ArgumentOutOfRange);
        }

        // The declared values carry their own 422: the identity message above names producerId/ownerId,
        // so reusing it for a negative declared value would misdescribe the failure.
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

        // T9: only the bound owner may finalize the slot. An unbound slot has no owner, so nobody may
        // close it - the finalize handler binds through BindOrCheckAsync first, so a finalize arriving
        // before the events still closes the slot (05.3.2 AC7).
        if (existing.BoundTokenJti is null
            || !string.Equals(existing.BoundTokenJti, ownerId, StringComparison.Ordinal))
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
                owner_id = ownerId,
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

    /// <summary>One slot row as the registry's statements return it.</summary>
    private sealed class SlotRow
    {
        public Guid Id { get; set; }

        public int ShardIndex { get; set; }

        public string ProducerId { get; set; } = string.Empty;

        /// <summary>The bound owner; the column keeps its historical name <c>bound_token_jti</c>.</summary>
        public string? BoundTokenJti { get; set; }

        public long LastSequence { get; set; }

        public DateTimeOffset? ClosedAt { get; set; }

        public long? DeclaredLastSequence { get; set; }

        public int? DeclaredCount { get; set; }
    }
}
