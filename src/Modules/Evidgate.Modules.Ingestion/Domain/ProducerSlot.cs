namespace Evidgate.Modules.Ingestion.Domain;

/// <summary>
/// The write-model row of <c>public.producer_slot</c> (migration 0029, 04.1.2-T4.1): one manifest
/// shard's producer registry entry, bound to a token identity by the first accepted write (T9).
/// No property carries a <c>set</c> or <c>init</c> accessor (00 §5.5 C6), so the write model cannot
/// be mutated after construction; EF Core binds the row through this constructor and the
/// compiler-generated backing fields.
/// <para>
/// The owner column (<see cref="BoundTokenJti"/>, which now holds the ingest-token id despite its historical name) is
/// written by <c>IProducerRegistry.BindOrCheckAsync</c> as one conditional <c>UPDATE</c> (T9), not through
/// this entity: the app role holds a column-level UPDATE grant on it (migration 0029), and a single
/// statement is what makes two racing first writers safe. The entity is the module's mapping of the table.
/// </para>
/// </summary>
internal sealed class ProducerSlot
{
    /// <summary>Materialises one slot (EF Core constructor binding, 00 §5.5 C4).</summary>
    public ProducerSlot(
        Guid id,
        Guid orgId,
        Guid runId,
        int shardIndex,
        string producerId,
        string? boundTokenJti = null,
        long lastSequence = 0,
        DateTimeOffset? closedAt = null,
        long? declaredLastSequence = null,
        int? declaredCount = null,
        int version = 1,
        DateTimeOffset createdAt = default,
        DateTimeOffset updatedAt = default)
    {
        Id = id;
        OrgId = orgId;
        RunId = runId;
        ShardIndex = shardIndex;
        ProducerId = producerId;
        BoundTokenJti = boundTokenJti;
        LastSequence = lastSequence;
        ClosedAt = closedAt;
        DeclaredLastSequence = declaredLastSequence;
        DeclaredCount = declaredCount;
        Version = version;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>UUIDv7 id (00 §7).</summary>
    public Guid Id { get; }

    /// <summary>RLS scope (00 §5.4).</summary>
    public Guid OrgId { get; }

    /// <summary>The run the slot belongs to; no foreign key yet (05.2.1-T1 creates <c>run</c>).</summary>
    public Guid RunId { get; }

    /// <summary>The 1-based manifest shard index (1..256).</summary>
    public int ShardIndex { get; }

    /// <summary>The deterministic producer id, <c>shard-&lt;index&gt;</c> (T54).</summary>
    public string ProducerId { get; }

    /// <summary>The authenticated credential id the first accepted write bound the slot to (T9); null while unbound.</summary>
    public string? BoundTokenJti { get; }

    /// <summary>The highest sequence the slot has stored; 0 before the first write.</summary>
    public long LastSequence { get; }

    /// <summary>When <c>CloseSlotAsync</c> closed the slot (T68); null while open.</summary>
    public DateTimeOffset? ClosedAt { get; }

    /// <summary>The declared high-water mark <c>CloseSlotAsync</c> stored (T68, migration 0035); null while open.</summary>
    public long? DeclaredLastSequence { get; }

    /// <summary>The declared count <c>CloseSlotAsync</c> stored (T68, migration 0035); null while open.</summary>
    public int? DeclaredCount { get; }

    /// <summary>Optimistic-concurrency token (00 §7); the trigger owns the increment.</summary>
    public int Version { get; }

    /// <summary>Commit timestamp (00 §7).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Last write, UTC; the trigger owns it (00 §7).</summary>
    public DateTimeOffset UpdatedAt { get; }
}
