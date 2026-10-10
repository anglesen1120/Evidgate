using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Evidgate.Modules.Evidence.Infrastructure.Persistence;

/// <summary>
/// The EF write model of the evidence module (00 §5.5 C4): the artifact rows of 06.1.1 and the per-project kind
/// policies they inherit. Created per command through <see cref="ITenantDbContextFactory{TContext}"/>, so it runs on
/// the request's tenant connection and transaction and RLS scopes every statement.
/// </summary>
internal sealed class EvidenceDbContext(DbContextOptions<EvidenceDbContext> options) : EvidgateDbContext(options)
{
    /// <summary>The tenant's artifacts.</summary>
    public DbSet<Artifact> Artifacts => Set<Artifact>();

    /// <summary>The tenant's per-project artifact kind policies.</summary>
    public DbSet<ArtifactKindPolicy> KindPolicies => Set<ArtifactKindPolicy>();
}

/// <summary>
/// Write-model mapping of <c>public.artifact</c> (migration 0028, 06.1.1-T1); no navigation: the project is reached
/// by id, and the composite FK <c>artifact_project_fk</c> stays a database constraint. There is no FK to <c>run</c>:
/// the table does not exist yet (05.2.1-T1 creates it), so <c>run_id</c> is a plain column.
/// </summary>
internal sealed class ArtifactConfiguration : IEntityTypeConfiguration<Artifact>
{
    public void Configure(EntityTypeBuilder<Artifact> builder)
    {
        builder.ToTable("artifact", "public");
        builder.HasKey(row => row.Id).HasName("artifact_pkey");
        builder.HasAlternateKey(row => new { row.OrgId, row.Id }).HasName("artifact_org_id_key");
        builder.HasIndex(row => new { row.OrgId, row.ObjectKey }).HasDatabaseName("artifact_org_object_key_key").IsUnique();
        builder.HasIndex(row => new { row.OrgId, row.RunId }).HasDatabaseName("artifact_run_idx").HasFilter("state <> 'deleted'");
        builder.HasIndex(row => row.ExpiresAt).HasDatabaseName("artifact_expiry_idx").HasFilter("state = 'ready'");

        // Every property is mapped explicitly: a property without a setter is not mapped by convention (EF Core
        // "Entity types with constructors"), and EF binds each one through the constructor and its backing field.
        builder.Property(row => row.Id).HasColumnName("id");
        builder.Property(row => row.OrgId).HasColumnName("org_id").IsRequired();
        builder.Property(row => row.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(row => row.RunId).HasColumnName("run_id").IsRequired();
        builder.Property(row => row.ExecutionKey).HasColumnName("execution_key").IsRequired();
        builder.Property(row => row.AttemptNo).HasColumnName("attempt_no").IsRequired();
        builder.Property(row => row.AttemptId).HasColumnName("attempt_id");
        builder.Property(row => row.Kind).HasColumnName("kind").IsRequired();
        builder.Property(row => row.ObjectKey).HasColumnName("object_key").IsRequired();
        builder.Property(row => row.MediaType).HasColumnName("media_type").IsRequired();
        builder.Property(row => row.Bytes).HasColumnName("bytes");
        builder.Property(row => row.Checksum).HasColumnName("checksum").IsRequired();
        builder.Property(row => row.Sensitivity).HasColumnName("sensitivity").IsRequired();
        builder.Property(row => row.ScanState).HasColumnName("scan_state").HasDefaultValue(ArtifactScanState.Pending);
        builder.Property(row => row.State).HasColumnName("state").HasDefaultValue(ArtifactState.PendingUpload);
        builder.Property(row => row.ExpiresAt).HasColumnName("expires_at");
        builder.Property(row => row.FailureCode).HasColumnName("failure_code");
        builder.Property(row => row.Version).HasColumnName("version").HasDefaultValue(1).IsConcurrencyToken();
        builder.Property(row => row.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(row => row.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
    }
}

/// <summary>
/// Write-model mapping of <c>public.artifact_kind_policy</c> (migration 0028, 06.1.1-T1); no navigation: the project
/// is reached by id, and the composite FK <c>artifact_kind_policy_project_fk</c> stays a database constraint.
/// </summary>
internal sealed class ArtifactKindPolicyConfiguration : IEntityTypeConfiguration<ArtifactKindPolicy>
{
    public void Configure(EntityTypeBuilder<ArtifactKindPolicy> builder)
    {
        builder.ToTable("artifact_kind_policy", "public");
        builder.HasKey(row => row.Id).HasName("artifact_kind_policy_pkey");
        builder.HasAlternateKey(row => new { row.OrgId, row.Id }).HasName("artifact_kind_policy_org_id_key");
        builder.HasIndex(row => new { row.OrgId, row.ProjectId, row.Kind })
            .HasDatabaseName("artifact_kind_policy_project_kind_key").IsUnique();

        builder.Property(row => row.Id).HasColumnName("id");
        builder.Property(row => row.OrgId).HasColumnName("org_id").IsRequired();
        builder.Property(row => row.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(row => row.Kind).HasColumnName("kind").IsRequired();
        builder.Property(row => row.MaxBytes).HasColumnName("max_bytes").IsRequired();
        builder.Property(row => row.MediaTypes).HasColumnName("media_types").HasColumnType("text[]").IsRequired();
        builder.Property(row => row.DefaultSensitivity).HasColumnName("default_sensitivity").IsRequired();
        builder.Property(row => row.RejectOnSecret).HasColumnName("reject_on_secret").HasDefaultValue(false);
        builder.Property(row => row.CaptureEnabled).HasColumnName("capture_enabled").HasDefaultValue(true);
        builder.Property(row => row.Version).HasColumnName("version").HasDefaultValue(1).IsConcurrencyToken();
        builder.Property(row => row.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(row => row.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
    }
}
