using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Evidgate.Modules.Ingestion.Infrastructure.Persistence;

/// <summary>
/// The write-model row of <c>public.event</c> (migration 0034, 04.1.1-T1): one producer envelope of one
/// accepted ingest batch, append-only (the migration revokes UPDATE/DELETE, C6). No property carries a
/// <c>set</c> or <c>init</c> accessor (00 §5.5 C6), so the write model cannot be mutated after
/// construction; EF Core binds the row through this constructor and the compiler-generated backing
/// fields.
/// <para>
/// The class is named <c>Event</c>, not <c>EventRow</c>, because
/// <c>TenantModelConventions.ApplyTableName</c> (01.4.2-T1.3) requires the table name to be the
/// snake_case singular CLR type name: <c>Event</c> maps to <c>event</c>, while an <c>EventRow</c>
/// mapped to <c>event</c> fails the model build by name.
/// </para>
/// <para>
/// The batch's events are inserted by ONE raw <c>unnest</c> statement (04.1.1 §5, P2, via the tenant
/// Dapper path), so this entity is the module's mapping of the table — the shape 05.3.2's reconciler
/// and 09's run-detail reads build on — and not the write path itself. There is no navigation: the run
/// is reached by id and the composite tenant key <c>event_slot_fk</c> stays a database constraint (the
/// direct <c>event_run_fk</c> is deferred to 05.2.1 — 0034's header [DECISION]).
/// </para>
/// </summary>
internal sealed class Event
{
    /// <summary>Materialises one event row (EF Core constructor binding, 00 §5.5 C4).</summary>
    public Event(
        Guid id,
        Guid orgId,
        Guid runId,
        Guid eventId,
        string producerId,
        long sequence,
        string executionKey,
        int attemptNo,
        string type,
        string payloadHash,
        Guid? jobId = null,
        long? epoch = null,
        string? rawStatus = null,
        string? normalizedStatus = null,
        long? durationMs = null,
        DateTimeOffset? occurredAt = null,
        string payload = "{}",
        Guid[]? artifactIds = null,
        bool afterCancel = false,
        bool late = false,
        bool stale = false,
        DateTimeOffset receivedAt = default)
    {
        Id = id;
        OrgId = orgId;
        RunId = runId;
        EventId = eventId;
        ProducerId = producerId;
        Sequence = sequence;
        ExecutionKey = executionKey;
        AttemptNo = attemptNo;
        Type = type;
        PayloadHash = payloadHash;
        JobId = jobId;
        Epoch = epoch;
        RawStatus = rawStatus;
        NormalizedStatus = normalizedStatus;
        DurationMs = durationMs;
        OccurredAt = occurredAt;
        Payload = payload;
        ArtifactIds = artifactIds ?? [];
        AfterCancel = afterCancel;
        Late = late;
        Stale = stale;
        ReceivedAt = receivedAt;
    }

    /// <summary>UUIDv7 id (00 §7).</summary>
    public Guid Id { get; }

    /// <summary>RLS scope (00 §5.4).</summary>
    public Guid OrgId { get; }

    /// <summary>The run the event belongs to; guaranteed transitively by <c>event_slot_fk</c> → <c>producer_slot</c> → <c>run</c>.</summary>
    public Guid RunId { get; }

    /// <summary>The producer-supplied dedupe identity; <c>unique (org_id, run_id, event_id)</c> (T50).</summary>
    public Guid EventId { get; }

    /// <summary>The producer the batch belongs to; part of <c>event_slot_fk</c> (T9).</summary>
    public string ProducerId { get; }

    /// <summary>The producer's monotonic sequence; <c>unique (org_id, run_id, producer_id, sequence)</c> (FR-04-3).</summary>
    public long Sequence { get; }

    /// <summary>The 00 §7 <c>sha256(case_id ‖ variant_hash ‖ target_env)</c> hex of the planned execution (T10).</summary>
    public string ExecutionKey { get; }

    /// <summary>The 1-based attempt number (00 §7).</summary>
    public int AttemptNo { get; }

    /// <summary>The envelope's dotted lower_snake event type (00 §9.4).</summary>
    public string Type { get; }

    /// <summary>The 00 §7 RFC 8785 content address of the event, <c>sha256:&lt;64 hex&gt;</c>.</summary>
    public string PayloadHash { get; }

    /// <summary>The agent-mode job id; null in CI mode (05.7.2 creates <c>job</c>, so no FK yet).</summary>
    public Guid? JobId { get; }

    /// <summary>The producer's epoch, when it declares one (04.1.2-T2); null otherwise.</summary>
    public long? Epoch { get; }

    /// <summary>The producer's raw status verbatim, when the type carries one (00 §8.3).</summary>
    public string? RawStatus { get; }

    /// <summary>The server's normalised status (00 §8.3); left NULL by 04.1.1-T1 (05.3.3 owns it, D8).</summary>
    public string? NormalizedStatus { get; }

    /// <summary>The reported duration in milliseconds; null when the producer sent none.</summary>
    public long? DurationMs { get; }

    /// <summary>The runner clock; informative only, never an ordering key (04.1.1-T4.1).</summary>
    public DateTimeOffset? OccurredAt { get; }

    /// <summary>The <c>ext</c> allow-list residue as a jsonb object; <c>{}</c> when nothing survived (T1.2).</summary>
    public string Payload { get; }

    /// <summary>The artifact ids the event references (06 evidence); empty when none.</summary>
    public Guid[] ArtifactIds { get; }

    /// <summary>True when the run was <c>canceled</c> when the batch committed (T12).</summary>
    public bool AfterCancel { get; }

    /// <summary>True when the run was <c>incomplete</c>/<c>complete</c> inside its late window (T81); always false in T1.</summary>
    public bool Late { get; }

    /// <summary>True for an event from a foreign epoch (04.1.2-T2).</summary>
    public bool Stale { get; }

    /// <summary>Commit timestamp (00 §7); the table's only time column, since a row is never updated.</summary>
    public DateTimeOffset ReceivedAt { get; }
}

/// <summary>
/// Write-model mapping of <c>public.event</c> (migration 0034, 04.1.1-T1): every key, unique
/// constraint and index the migration declares, so a model that drifts from the SQL fails
/// <c>WriteModelMappingTests</c>/the model build instead of at runtime.
/// </summary>
internal sealed class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        builder.ToTable("event", "public");
        builder.HasKey(row => row.Id).HasName("event_pkey");
        builder.HasAlternateKey(row => new { row.OrgId, row.Id }).HasName("event_org_id_key");
        builder.HasIndex(row => new { row.OrgId, row.RunId, row.ProducerId, row.Sequence })
            .HasDatabaseName("event_run_producer_sequence_key").IsUnique();
        builder.HasIndex(row => new { row.OrgId, row.RunId, row.EventId })
            .HasDatabaseName("event_run_event_id_key").IsUnique();

        // Every property is mapped explicitly: a property without a setter is not mapped by convention (EF Core
        // "Entity types with constructors"), and EF binds each one through the constructor and its backing field.
        builder.Property(row => row.Id).HasColumnName("id");
        builder.Property(row => row.OrgId).HasColumnName("org_id").IsRequired();
        builder.Property(row => row.RunId).HasColumnName("run_id").IsRequired();
        builder.Property(row => row.EventId).HasColumnName("event_id").IsRequired();
        builder.Property(row => row.ProducerId).HasColumnName("producer_id").IsRequired();
        builder.Property(row => row.Sequence).HasColumnName("sequence").IsRequired();
        builder.Property(row => row.ExecutionKey).HasColumnName("execution_key").IsRequired();
        builder.Property(row => row.AttemptNo).HasColumnName("attempt_no").IsRequired();
        builder.Property(row => row.Type).HasColumnName("type").IsRequired();
        builder.Property(row => row.PayloadHash).HasColumnName("payload_hash").IsRequired();
        builder.Property(row => row.JobId).HasColumnName("job_id");
        builder.Property(row => row.Epoch).HasColumnName("epoch");
        builder.Property(row => row.RawStatus).HasColumnName("raw_status");
        builder.Property(row => row.NormalizedStatus).HasColumnName("normalized_status");
        builder.Property(row => row.DurationMs).HasColumnName("duration_ms");
        builder.Property(row => row.OccurredAt).HasColumnName("occurred_at");
        builder.Property(row => row.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        builder.Property(row => row.ArtifactIds).HasColumnName("artifact_ids").HasColumnType("uuid[]").IsRequired();
        builder.Property(row => row.AfterCancel).HasColumnName("after_cancel").HasDefaultValue(false);
        builder.Property(row => row.Late).HasColumnName("late").HasDefaultValue(false);
        builder.Property(row => row.Stale).HasColumnName("stale").HasDefaultValue(false);
        builder.Property(row => row.ReceivedAt).HasColumnName("received_at").HasDefaultValueSql("now()");
    }
}
