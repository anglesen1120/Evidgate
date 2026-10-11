using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Ingestion.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Evidgate.Modules.Ingestion.Infrastructure.Persistence;

/// <summary>
/// The EF write model of the ingestion module (00 §5.5 C4): the producer slots of 04.1.2-T4.
/// Created per command through <see cref="ITenantDbContextFactory{TContext}"/>, so it runs on the
/// request's tenant connection and transaction and RLS scopes every statement.
/// </summary>
internal sealed class IngestionDbContext(DbContextOptions<IngestionDbContext> options) : EvidgateDbContext(options)
{
    /// <summary>The run's producer slots.</summary>
    public DbSet<ProducerSlot> ProducerSlots => Set<ProducerSlot>();
}

/// <summary>
/// Write-model mapping of <c>public.producer_slot</c> (migration 0029, 04.1.2-T4.1); no navigation:
/// the run is reached by id, and the deferred composite FK <c>(org_id, run_id) -&gt; run (org_id, id)</c>
/// stays a database constraint 05.2.1-T1 adds. There is no FK to <c>run</c> yet: the table does not
/// exist (05.2.1-T1 creates it), so <c>run_id</c> is a plain column.
/// </summary>
internal sealed class ProducerSlotConfiguration : IEntityTypeConfiguration<ProducerSlot>
{
    public void Configure(EntityTypeBuilder<ProducerSlot> builder)
    {
        builder.ToTable("producer_slot", "public");
        builder.HasKey(row => row.Id).HasName("producer_slot_pkey");
        builder.HasAlternateKey(row => new { row.OrgId, row.Id }).HasName("producer_slot_org_id_key");
        builder.HasIndex(row => new { row.OrgId, row.RunId, row.ProducerId })
            .HasDatabaseName("producer_slot_run_producer_key").IsUnique();
        builder.HasIndex(row => new { row.OrgId, row.RunId, row.ShardIndex })
            .HasDatabaseName("producer_slot_run_shard_key").IsUnique();
        builder.HasIndex(row => new { row.OrgId, row.RunId }).HasDatabaseName("producer_slot_run");

        // Every property is mapped explicitly: a property without a setter is not mapped by convention (EF Core
        // "Entity types with constructors"), and EF binds each one through the constructor and its backing field.
        builder.Property(row => row.Id).HasColumnName("id");
        builder.Property(row => row.OrgId).HasColumnName("org_id").IsRequired();
        builder.Property(row => row.RunId).HasColumnName("run_id").IsRequired();
        builder.Property(row => row.ShardIndex).HasColumnName("shard_index").IsRequired();
        builder.Property(row => row.ProducerId).HasColumnName("producer_id").IsRequired();
        builder.Property(row => row.BoundTokenJti).HasColumnName("bound_token_jti");
        builder.Property(row => row.BoundCiRunRef).HasColumnName("bound_ci_run_ref");
        builder.Property(row => row.LastSequence).HasColumnName("last_sequence").HasDefaultValue(0L);
        builder.Property(row => row.ClosedAt).HasColumnName("closed_at");
        builder.Property(row => row.Version).HasColumnName("version").HasDefaultValue(1).IsConcurrencyToken();
        builder.Property(row => row.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(row => row.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
    }
}
