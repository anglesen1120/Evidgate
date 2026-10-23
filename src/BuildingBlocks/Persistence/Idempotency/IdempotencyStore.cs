using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.BuildingBlocks.Persistence.Idempotency;

/// <summary>
/// Dapper over the ambient tenant scope (01.5.1-T6.1). Stateless: the scope is
/// resolved per call and every statement runs on the scope's connection and
/// transaction, so the begin row, the handler's writes and the stored response
/// commit together (00 §5.5 C2) — a committed record without a response cannot be
/// observed from outside that transaction.
/// <para>
/// The statements project snake_case columns into positional records, which Dapper
/// binds only with <c>DefaultTypeMap.MatchNamesWithUnderscores</c> — set once by
/// <c>AddEvidgateTenancy</c>, which this registration sits on top of.
/// </para>
/// </summary>
public sealed class IdempotencyStore : IIdempotencyStore
{
    /// <summary>
    /// The retention floor of 00 §7 / T11, written into <c>expires_at</c> and
    /// enforced again by <c>idempotency_record_expiry_check</c> (0007), so a caller
    /// cannot shorten it. An expired record is removed by the scheduled purge
    /// (01.5.1-T6.2); until then it still dedupes, which is what "unique for at
    /// least 24 hours" means.
    /// </summary>
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>
    /// Inserts the begin row and returns its id when this command won the key.
    /// <c>on conflict … do nothing</c> is what makes two concurrent commands with
    /// one key produce exactly one record: the loser waits on the unique index
    /// (<c>idempotency_record_uq</c>, <c>nulls not distinct</c>, T94) until the
    /// winner commits or rolls back. `org_id` is the scope's tenant, never a
    /// caller's argument, so a row can only be written for the tenant the command
    /// runs as.
    /// </summary>
    private const string BeginSql = """
        insert into public.idempotency_record
          (id, org_id, project_id, operation, key, request_hash, expires_at)
        values
          (@Id, @OrgId, @ProjectId, @Operation, @Key, @RequestHash, now() + @Retention)
        on conflict (org_id, project_id, operation, key) do nothing
        returning id
        """;

    /// <summary>
    /// The record the key already belongs to, read after the insert was refused.
    /// `project_id is not distinct from @ProjectId` is the NULL-safe comparison the
    /// T94 key needs: an org-scoped operation has no project, and NULL must match
    /// NULL — `= @ProjectId` would never find its own record.
    /// </summary>
    private const string ExistingSql = """
        select r.request_hash, (r.response_status is not null) as has_response
          from public.idempotency_record r
         where r.org_id = @OrgId
           and r.project_id is not distinct from @ProjectId
           and r.operation = @Operation
           and r.key = @Key
        """;

    /// <summary>The stored response of a completed record, for a replayed retry.</summary>
    private const string ReplaySql = """
        select r.response_status, r.response_body
          from public.idempotency_record r
         where r.org_id = @OrgId
           and r.project_id is not distinct from @ProjectId
           and r.operation = @Operation
           and r.key = @Key
           and r.response_status is not null
        """;

    /// <summary>
    /// Stamps the response of the command that owns the record. RLS already scopes
    /// the UPDATE to the ambient org, and <c>org_id = @OrgId</c> repeats it in the
    /// statement as defence in depth (00 §5.4): a foreign record matches no row
    /// either way, and the guard below fails loudly instead of storing a response
    /// into another tenant's record.
    /// </summary>
    private const string CompleteSql = """
        update public.idempotency_record
           set response_status = @Status,
               response_body = @Body::jsonb
         where id = @Id
           and org_id = @OrgId
        """;

    private readonly ITenantDb _db;
    private readonly ITenantScopeAccessor _scopes;

    /// <summary>Creates the store over the Dapper path and the ambient scope.</summary>
    public IdempotencyStore(ITenantDb db, ITenantScopeAccessor scopes)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scopes);
        _db = db;
        _scopes = scopes;
    }

    /// <inheritdoc />
    public async Task<IdempotencyBeginResult> TryBeginAsync(
        string operation,
        string key,
        string requestJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(requestJson);

        // The digest is taken over the RFC 8785 canonical form (00 §7), so the same
        // request with a different member order or whitespace still replays instead
        // of conflicting.
        var requestHash = CanonicalHash.Compute(requestJson);

        var scope = _scopes.Require();
        var id = Guid.CreateVersion7();
        var parameters = new
        {
            Id = id,
            OrgId = scope.OrgId,
            ProjectId = scope.ProjectId,
            Operation = operation,
            Key = key,
            RequestHash = requestHash,
            Retention,
        };

        var inserted = await _db
            .ScalarAsync<Guid?>(BeginSql, parameters, cancellationToken)
            .ConfigureAwait(false);

        if (inserted is not null)
        {
            return new IdempotencyBeginResult(IdempotencyOutcome.Proceed, id);
        }

        var existing = await _db
            .QuerySingleOrDefaultAsync<ExistingRecord>(ExistingSql, parameters, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"the idempotency key '{key}' for operation '{operation}' was refused as a duplicate but no "
                + "record exists for it: the arbiter of Idempotency-Key is public.idempotency_record (TL T11).");

        // A record exists and its hash differs: the key was reused for another
        // request (409, 00 §9.1). A record without a stored response is reported as
        // a conflict as well — it is unreachable from outside (begin, handler and
        // response commit together), and a caller that sees it has already tried to
        // begin the same key twice inside one transaction.
        return existing.RequestHash == requestHash && existing.HasResponse
            ? new IdempotencyBeginResult(IdempotencyOutcome.Replay, null)
            : new IdempotencyBeginResult(IdempotencyOutcome.Conflict, null);
    }

    /// <inheritdoc />
    public async Task CompleteAsync(
        Guid recordId,
        int status,
        string responseJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(responseJson);

        var scope = _scopes.Require();

        var affected = await _db.ExecuteAsync(
            CompleteSql,
            new { Id = recordId, OrgId = scope.OrgId, Status = status, Body = responseJson },
            cancellationToken).ConfigureAwait(false);

        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"completing idempotency record {recordId:D} affected {affected} rows instead of one: the record "
                + "was not begun by this tenant scope.");
        }
    }

    /// <inheritdoc />
    public Task<IdempotencyReplay?> ReplayAsync(
        string operation,
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var scope = _scopes.Require();
        return _db.QuerySingleOrDefaultAsync<IdempotencyReplay>(
            ReplaySql,
            new { OrgId = scope.OrgId, ProjectId = scope.ProjectId, Operation = operation, Key = key },
            cancellationToken);
    }

    /// <summary>
    /// The key's existing record, as far as a replay decision needs it:
    /// <see cref="HasResponse"/> is read as a boolean instead of projecting a
    /// nullable <c>response_status</c> so the projection stays two non-nullable
    /// columns, which is what Dapper's positional-record binding requires.
    /// </summary>
    private sealed record ExistingRecord(string RequestHash, bool HasResponse);
}
