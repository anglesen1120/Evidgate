namespace Evidgate.Modules.Ingestion.Domain;

/// <summary>
/// The write-model row of <c>public.producer_slot</c> (migration 0029, 04.1.2-T4.1): one manifest
/// shard's producer registry entry, bound to a token identity by the first accepted write (T9).
/// No property carries a <c>set</c> or <c>init</c> accessor (00 §5.5 C6), so the write model cannot
/// be mutated after construction; EF Core binds the row through this constructor and the
/// compiler-generated backing fields.
/// <para>
/// The binding columns (<see cref="BoundTokenJti"/>, <see cref="BoundCiRunRef"/>) are written by
/// <c>IProducerRegistry.BindOrCheckAsync</c> as one conditional <c>UPDATE</c> (T9), not through this
/// entity: the app role holds a column-level UPDATE grant on exactly those two columns (migration
/// 0029), and a single statement is what makes two racing first writers safe. The entity is the
/// module's mapping of the table - the shape 04.1.2-T5's <c>CloseSlotAsync</c> and the 05
/// reconciliation read build on.
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
        string? boundCiRunRef = null,
        long lastSequence = 0,
        DateTimeOffset? closedAt = null,
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
        BoundCiRunRef = boundCiRunRef;
        LastSequence = lastSequence;
        ClosedAt = closedAt;
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

    /// <summary>The token id the first accepted write bound the slot to (T9); null while unbound.</summary>
    public string? BoundTokenJti { get; }

    /// <summary>The GitHub CI reference the binding carries (T88); null for a non-CI caller.</summary>
    public string? BoundCiRunRef { get; }

    /// <summary>The highest sequence the slot has stored; 0 before the first write.</summary>
    public long LastSequence { get; }

    /// <summary>When <c>CloseSlotAsync</c> closed the slot (T68); null while open.</summary>
    public DateTimeOffset? ClosedAt { get; }

    /// <summary>Optimistic-concurrency token (00 §7); the trigger owns the increment.</summary>
    public int Version { get; }

    /// <summary>Commit timestamp (00 §7).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Last write, UTC; the trigger owns it (00 §7).</summary>
    public DateTimeOffset UpdatedAt { get; }
}
