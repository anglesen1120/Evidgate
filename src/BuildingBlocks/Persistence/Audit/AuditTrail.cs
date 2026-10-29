using System.Diagnostics;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.BuildingBlocks.Persistence.Audit;

/// <summary>
/// The per-unit-of-work audit buffer (02.7.1-T1.2/T2.1). The pipeline opens a unit with the
/// tenant transaction, the authorization stage records one entry per successful audited
/// command, and the tenant stage appends the whole buffer to the org's chain just before it
/// commits — so the audit row and the business change are one transaction (AC1) and the
/// advisory lock is held only for the final inserts (AC3).
/// </summary>
public interface IAuditTrail
{
    /// <summary>Opens a unit for the current async flow; a nested call joins the open unit.</summary>
    IDisposable BeginUnit();

    /// <summary>
    /// Describes the object the running audited command changed. Called by a handler; the next
    /// <see cref="Record"/> picks it up. Metadata is validated against the allowlist.
    /// </summary>
    /// <param name="auditObject">The object detail.</param>
    void Describe(AuditObject auditObject);

    /// <summary>Queues one entry for the unit (called by the authorization stage after success).</summary>
    /// <param name="action">The <c>[AuditedAction]</c> action string.</param>
    /// <param name="actor">The caller.</param>
    void Record(string action, AuditActor actor);

    /// <summary>Drops the next <see cref="Record"/>: an idempotent replay changed nothing.</summary>
    void Suppress();

    /// <summary>Appends the unit's entries to the org's chain inside <paramref name="db"/>'s transaction.</summary>
    /// <param name="orgId">The tenant of the open transaction.</param>
    /// <param name="db">The scoped data access.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The number of rows appended.</returns>
    Task<int> FlushAsync(Guid orgId, ITenantDb db, CancellationToken cancellationToken);
}

/// <summary>Default <see cref="IAuditTrail"/>: an ambient buffer per async flow, written under the org's advisory lock.</summary>
public sealed class AuditTrail : IAuditTrail
{
    /// <summary>Baggage item the host's request-id middleware sets so audit rows carry the 00 §9.2 request id.</summary>
    public const string RequestIdBaggage = "evidgate.request_id";

    private readonly AsyncLocal<Unit?> _unit = new();

    /// <inheritdoc />
    public IDisposable BeginUnit()
    {
        if (_unit.Value is not null)
        {
            return NoopScope.Instance;
        }

        var unit = new Unit();
        _unit.Value = unit;

        return new UnitScope(this, unit);
    }

    /// <inheritdoc />
    public void Describe(AuditObject auditObject)
    {
        ArgumentNullException.ThrowIfNull(auditObject);

        var unit = _unit.Value
            ?? throw new InvalidOperationException("audit entries can only be described inside a pipeline unit of work");
        var metadata = AuditMetadata.Validate(auditObject.Metadata);

        unit.Pending = auditObject with { Metadata = metadata };
    }

    /// <inheritdoc />
    public void Record(string action, AuditActor actor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(actor);

        var unit = _unit.Value
            ?? throw new InvalidOperationException("audit entries can only be recorded inside a pipeline unit of work");

        var pending = unit.Pending;
        unit.Pending = null;

        if (unit.Suppressed)
        {
            unit.Suppressed = false;

            return;
        }

        var activity = Activity.Current;
        unit.Entries.Add(new AuditEntry(
            action,
            actor,
            pending,
            activity?.GetBaggageItem(RequestIdBaggage),
            activity?.TraceId.ToString()));
    }

    /// <inheritdoc />
    public void Suppress()
    {
        if (_unit.Value is { } unit)
        {
            unit.Suppressed = true;
        }
    }

    /// <inheritdoc />
    public async Task<int> FlushAsync(Guid orgId, ITenantDb db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var unit = _unit.Value;
        if (unit is null || unit.Entries.Count == 0)
        {
            return 0;
        }

        // 11.9.2-T2.2 (AC10): the last gate before the chain. A payload naming a personal-data key is
        // refused here too, so no hash is computed, no advisory lock is taken and no row is inserted
        // for it — a programming error fails the unit instead of entering the permanent record.
        foreach (var entry in unit.Entries)
        {
            AuditMetadata.EnsureNoForbiddenKeys(entry.Subject, "audit entry");
        }

        // The lock comes after every business statement and before the chain read: it is held
        // from here to the commit, i.e. for about the length of these inserts (T8, P5). A bounded
        // wait keeps a burst on one org from parking every pooled connection behind the lock.
        await db.ExecuteAsync(AuditSql.LockTimeout, null, cancellationToken).ConfigureAwait(false);

        try
        {
            await db.ExecuteAsync(AuditSql.AdvisoryLock, new { org = orgId.ToString("D") }, cancellationToken).ConfigureAwait(false);
        }
        catch (Npgsql.PostgresException exception) when (exception.SqlState == Npgsql.PostgresErrorCodes.LockNotAvailable)
        {
            throw new AuditBusyException(exception);
        }

        var tip = await db.QuerySingleOrDefaultAsync<ChainTip>(AuditSql.ChainTip, new { org_id = orgId }, cancellationToken)
            .ConfigureAwait(false);

        var seq = tip?.Seq ?? 0;
        var prev = tip?.EntryHash ?? AuditHashing.Genesis;

        // created_at never runs backwards against seq, even across API nodes with skewed clocks.
        var floor = tip?.CreatedAt ?? DateTimeOffset.MinValue;

        foreach (var entry in unit.Entries)
        {
            seq++;
            var createdAt = AuditHashing.Truncate(DateTimeOffset.UtcNow);
            if (createdAt < floor)
            {
                createdAt = floor;
            }

            floor = createdAt;
            var row = new AuditRow(
                Guid.CreateVersion7(),
                orgId,
                seq,
                entry.Actor.Type,
                entry.Actor.Id,
                entry.Action,
                entry.Subject?.ObjectType,
                entry.Subject?.ObjectId,
                entry.Subject?.BeforeHash,
                entry.Subject?.AfterHash,
                entry.RequestId,
                entry.TraceId,
                entry.Subject?.Metadata,
                prev,
                [],
                createdAt);
            var entryHash = AuditHashing.EntryHash(prev, row);

            await db.ExecuteAsync(
                AuditSql.Insert,
                new
                {
                    id = row.Id,
                    org_id = orgId,
                    seq = row.Seq,
                    actor_type = row.ActorType,
                    actor_id = row.ActorId,
                    action = row.Action,
                    object_type = row.ObjectType,
                    object_id = row.ObjectId,
                    before_hash = row.BeforeHash,
                    after_hash = row.AfterHash,
                    request_id = row.RequestId,
                    trace_id = row.TraceId,
                    metadata = row.Metadata is null ? null : JsonSerializer.Serialize(row.Metadata),
                    prev_hash = prev,
                    entry_hash = entryHash,
                    created_at = row.CreatedAt,
                },
                cancellationToken).ConfigureAwait(false);

            prev = entryHash;
        }

        var written = unit.Entries.Count;
        unit.Entries.Clear();

        return written;
    }

    private sealed record ChainTip(long Seq, byte[] EntryHash, DateTimeOffset CreatedAt);

    private sealed class Unit
    {
        public List<AuditEntry> Entries { get; } = [];

        public AuditObject? Pending { get; set; }

        public bool Suppressed { get; set; }
    }

    private sealed class UnitScope(AuditTrail owner, Unit unit) : IDisposable
    {
        public void Dispose()
        {
            if (ReferenceEquals(owner._unit.Value, unit))
            {
                owner._unit.Value = null;
            }
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static NoopScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>SQL of the audit writer, verifier and read API. Parameters only; no dynamic SQL (00 §5.7 S1).</summary>
internal static class AuditSql
{
    internal const string LockTimeout = "select set_config('lock_timeout', '2000', true)";

    internal const string AdvisoryLock = "select pg_advisory_xact_lock(hashtext('audit:' || @org))";

    internal const string ChainTip =
        "select seq as Seq, entry_hash as EntryHash, created_at as CreatedAt from public.audit_log where org_id = @org_id order by seq desc limit 1";

    internal const string Insert =
        """
        insert into public.audit_log (id, org_id, seq, actor_type, actor_id, action, object_type, object_id,
                                      before_hash, after_hash, request_id, trace_id, metadata, prev_hash, entry_hash, created_at)
        values (@id, @org_id, @seq, @actor_type, @actor_id, @action, @object_type, @object_id,
                @before_hash, @after_hash, @request_id, @trace_id, @metadata::jsonb, @prev_hash, @entry_hash, @created_at)
        """;

    internal const string ChainPage =
        """
        select id as Id, org_id as OrgId, seq as Seq, actor_type as ActorType, actor_id as ActorId, action as Action,
               object_type as ObjectType, object_id as ObjectId, before_hash as BeforeHash, after_hash as AfterHash,
               request_id as RequestId, trace_id as TraceId, metadata::text as MetadataJson,
               prev_hash as PrevHash, entry_hash as EntryHash, created_at as CreatedAt
          from public.audit_log
         where org_id = @org_id and seq > @after_seq
         order by seq
         limit @limit
        """;
}

/// <summary>The org's audit lock could not be taken in time; the request is retryable (503).</summary>
public sealed class AuditBusyException(Exception inner)
    : Exception("the audit chain of this organization is busy; retry", inner);
