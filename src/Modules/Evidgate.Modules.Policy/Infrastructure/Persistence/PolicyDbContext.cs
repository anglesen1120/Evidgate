using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Policy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Evidgate.Modules.Policy.Infrastructure.Persistence;

/// <summary>
/// The EF write model of the policy module (00 §5.5 C4): the append-only policy revisions of 07.1.1. Created per
/// command through <see cref="ITenantDbContextFactory{TContext}"/>, so it runs on the request's tenant connection
/// and transaction and RLS scopes every statement.
/// </summary>
internal sealed class PolicyDbContext(DbContextOptions<PolicyDbContext> options) : EvidgateDbContext(options)
{
    /// <summary>The tenant's policy revisions.</summary>
    public DbSet<PolicyRevision> Revisions => Set<PolicyRevision>();
}

/// <summary>
/// Write-model mapping of <c>public.policy_revision</c> (migration 0025, 07.1.1-T1); no navigation: the project is
/// reached by id, and the composite FK <c>policy_revision_project_fk</c> stays a database constraint.
/// </summary>
internal sealed class PolicyRevisionConfiguration : IEntityTypeConfiguration<PolicyRevision>
{
    public void Configure(EntityTypeBuilder<PolicyRevision> builder)
    {
        builder.ToTable("policy_revision", "public");
        builder.HasKey(row => row.Id).HasName("policy_revision_pkey");
        builder.HasAlternateKey(row => new { row.OrgId, row.Id }).HasName("policy_revision_org_id_key");
        builder.HasIndex(row => new { row.OrgId, row.ProjectId, row.RevisionNo }).HasDatabaseName("policy_revision_revision_no_key").IsUnique();
        builder.HasIndex(row => new { row.OrgId, row.ProjectId, row.Hash }).HasDatabaseName("policy_revision_hash_key").IsUnique();

        // Every property is mapped explicitly: a property without a setter is not mapped by convention (EF Core
        // "Entity types with constructors"), and EF binds each one through the constructor and its backing field.
        builder.Property(row => row.Id).HasColumnName("id");
        builder.Property(row => row.OrgId).HasColumnName("org_id").IsRequired();
        builder.Property(row => row.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(row => row.RevisionNo).HasColumnName("revision_no").IsRequired();
        builder.Property(row => row.Hash).HasColumnName("hash").IsRequired();
        builder.Property(row => row.Body).HasColumnName("body").HasColumnType("jsonb").IsRequired();
        builder.Property(row => row.State).HasColumnName("state").IsRequired().HasDefaultValue(PolicyRevisionState.Draft);
        builder.Property(row => row.CreatedBy).HasColumnName("created_by").IsRequired();
        builder.Property(row => row.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(row => row.Version).HasColumnName("version").HasDefaultValue(1).IsConcurrencyToken();
    }
}
