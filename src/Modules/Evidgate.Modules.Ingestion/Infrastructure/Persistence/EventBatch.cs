using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Evidgate.Modules.Ingestion.Infrastructure.Persistence;

/// <summary>
/// The write-model row of <c>public.event_batch</c> (migration 0034, 04.1.1-T1): the immutable receipt
/// of one accepted ingest batch — the row the response's <c>batch_id</c> names and the
/// <c>usage.batch_accepted</c> outbox row meters (T83/T43/T50). Append-only: the migration revokes
/// UPDATE/DELETE. No property carries a <c>set</c> or <c>init</c> accessor (00 §5.5 C6).
/// <para>
/// The class is named <c>EventBatch</c>, not <c>EventBatchRow</c>, because
/// <c>TenantModelConventions.ApplyTableName</c> (01.4.2-T1.3) requires the table name to be the
/// snake_case singular CLR type name: <c>EventBatch</c> maps to <c>event_batch</c>.
/// </para>
/// </summary>
internal sealed class EventBatch
{
    /// <summary>Materialises one batch receipt (EF Core constructor binding, 00 §5.5 C4).</summary>
    public EventBatch(
        Guid id,
        Guid orgId,
        Guid runId,
        string producerId,
        int eventCount,
        long bytesIn,
        long bytesExpanded,
        int accepted,
        int duplicate,
        string requestId,
        DateTimeOffset createdAt = default)
    {
        Id = id;
        OrgId = orgId;
        RunId = runId;
        ProducerId = producerId;
        EventCount = eventCount;
        BytesIn = bytesIn;
        BytesExpanded = bytesExpanded;
        Accepted = accepted;
        Duplicate = duplicate;
        RequestId = requestId;
        CreatedAt = createdAt;
    }

    /// <summary>UUIDv7 id (00 §7); the response's <c>batch_id</c>.</summary>
    public Guid Id { get; }

    /// <summary>RLS scope (00 §5.4).</summary>
    public Guid OrgId { get; }

    /// <summary>The run the batch was posted to; <c>event_batch_run_fk (org_id, run_id) -&gt; run (org_id, id)</c>.</summary>
    public Guid RunId { get; }

    /// <summary>The one producer every event of the batch carries (D4).</summary>
    public string ProducerId { get; }

    /// <summary>How many events the request carried (1..500, T3).</summary>
    public int EventCount { get; }

    /// <summary>Bytes received, before decompression (P8).</summary>
    public long BytesIn { get; }

    /// <summary>Bytes after decompression, the gzip-bomb guard's input (T3).</summary>
    public long BytesExpanded { get; }

    /// <summary>How many events committed as new (the metered quantity, T83).</summary>
    public int Accepted { get; }

    /// <summary>How many events were already present with the same <c>payload_hash</c> (T50).</summary>
    public int Duplicate { get; }

    /// <summary>The request correlation id (the <c>X-Request-Id</c> the response echoes).</summary>
    public string RequestId { get; }

    /// <summary>Commit timestamp (00 §7); the table's only time column, since a row is never updated.</summary>
    public DateTimeOffset CreatedAt { get; }
}

/// <summary>
/// Write-model mapping of <c>public.event_batch</c> (migration 0034, 04.1.1-T1): the keys and the
/// <c>event_batch_run</c> index the migration declares.
/// </summary>
internal sealed class EventBatchConfiguration : IEntityTypeConfiguration<EventBatch>
{
    public void Configure(EntityTypeBuilder<EventBatch> builder)
    {
        builder.ToTable("event_batch", "public");
        builder.HasKey(row => row.Id).HasName("event_batch_pkey");
        builder.HasAlternateKey(row => new { row.OrgId, row.Id }).HasName("event_batch_org_id_key");
        builder.HasIndex(row => new { row.OrgId, row.RunId, row.CreatedAt, row.Id }).HasDatabaseName("event_batch_run");

        // Every property is mapped explicitly: a property without a setter is not mapped by convention (EF Core
        // "Entity types with constructors"), and EF binds each one through the constructor and its backing field.
        builder.Property(row => row.Id).HasColumnName("id");
        builder.Property(row => row.OrgId).HasColumnName("org_id").IsRequired();
        builder.Property(row => row.RunId).HasColumnName("run_id").IsRequired();
        builder.Property(row => row.ProducerId).HasColumnName("producer_id").IsRequired();
        builder.Property(row => row.EventCount).HasColumnName("event_count").IsRequired();
        builder.Property(row => row.BytesIn).HasColumnName("bytes_in").IsRequired();
        builder.Property(row => row.BytesExpanded).HasColumnName("bytes_expanded").IsRequired();
        builder.Property(row => row.Accepted).HasColumnName("accepted").IsRequired();
        builder.Property(row => row.Duplicate).HasColumnName("duplicate").IsRequired();
        builder.Property(row => row.RequestId).HasColumnName("request_id").IsRequired();
        builder.Property(row => row.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
    }
}
