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

        // A concurrent registration won the race (or a partial set already existed): re-read the run's
        // slots and answer from what is stored - the same count is the idempotent no-op, anything else
        // is the conflict. Never a partial insert.
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

        // The bind is one conditional UPDATE (T9): only an unbound slot is written, so two racing first
        // writers cannot both win - the loser's WHERE matches no row and it falls through to the check.
        var bound = await db.QuerySingleOrDefaultAsync<SlotRow>(
            ProducerSlotSql.BindIfUnbound,
            new { org_id = orgId, run_id = runId, producer_id = producerId, jti = tokenJti, ci_run_ref = ciRunRef },
            cancellationToken).ConfigureAwait(false);

        if (bound is not null)
        {
            return Result.Success(new ProducerSlotBinding(bound.Id, bound.ShardIndex, bound.ProducerId, true));
        }

        // Nothing was bound: the slot is unknown, already bound to this identity (idempotent), or taken.
        var existing = await db.QuerySingleOrDefaultAsync<SlotRow>(
            ProducerSlotSql.ReadByProducer,
            new { org_id = orgId, run_id = runId, producer_id = producerId },
            cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            return Result.Failure<ProducerSlotBinding>(ProducerSlotErrors.UnknownProducer);
        }

        if (!string.Equals(existing.BoundTokenJti, tokenJti, StringComparison.Ordinal)
            || !string.Equals(existing.BoundCiRunRef, ciRunRef, StringComparison.Ordinal))
        {
            return Result.Failure<ProducerSlotBinding>(ProducerSlotErrors.SlotTaken);
        }

        return Result.Success(new ProducerSlotBinding(existing.Id, existing.ShardIndex, existing.ProducerId, false));
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
            row.LastSequence))];
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

        public string? BoundTokenJti { get; set; }

        public string? BoundCiRunRef { get; set; }

        public long LastSequence { get; set; }

        public DateTimeOffset? ClosedAt { get; set; }
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
        ApiErrorCode.InvalidTransition,
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
}

/// <summary>
/// The argument bounds of the registry, shared with the migration's CHECKs through
/// <see cref="ProducerSlotLimits"/> so the two cannot drift (00 §5.5 C6).
/// </summary>
internal static partial class ProducerSlotArguments
{
    /// <summary>True when <paramref name="producerId"/> matches the column's charset and length bound.</summary>
    internal static bool IsValidProducerId(string? producerId) =>
        producerId is not null && ProducerIdRegex().IsMatch(producerId);

    /// <summary>True when <paramref name="value"/> is 1..<paramref name="maxLength"/> characters.</summary>
    internal static bool IsWithin(string? value, int maxLength) =>
        value is not null && value.Length >= 1 && value.Length <= maxLength;

    /// <summary>True when <paramref name="value"/> is null or 1..<paramref name="maxLength"/> characters.</summary>
    internal static bool IsWithinOrNull(string? value, int maxLength) =>
        value is null || IsWithin(value, maxLength);

    /// <summary>The migration's <c>producer_slot_producer_id_check</c> charset.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(
        ProducerSlotLimits.ProducerIdPattern,
        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial System.Text.RegularExpressions.Regex ProducerIdRegex();
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
        select id, shard_index, producer_id, bound_token_jti, bound_ci_run_ref, last_sequence, closed_at
          from public.producer_slot
         where org_id = @org_id
           and run_id = @run_id
         order by shard_index
        """;

    /// <summary>One slot by its producer id; the re-read that decides idempotent versus taken (T9).</summary>
    internal const string ReadByProducer = """
        select id, shard_index, producer_id, bound_token_jti, bound_ci_run_ref, last_sequence, closed_at
          from public.producer_slot
         where org_id = @org_id
           and run_id = @run_id
           and producer_id = @producer_id
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
}
