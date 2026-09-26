using Evidgate.Modules.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Evidgate.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// Write-model mapping of <c>public.project</c> for migration 0005
/// (02.1.1-T1.3, 00 §5.5 C4). Table and column names are spelled out in
/// snake_case here: no <c>HasDefaultSchema</c> and no reliance on a global naming
/// convention, so this class is the single authority for the mapping.
/// </summary>
/// <remarks>
/// The primary key is <c>id</c> (globally unique UUIDv7, addressed by
/// <c>/v1/projects/{p}</c>) and <c>(org_id, id)</c> is a separate unique key: it
/// is the composite-FK target every project-scoped table references (00 §5.4.6,
/// TL T3), a job the primary key could only do if it also carried the org.
/// Registered by the host through
/// <c>EntityFrameworkCore.ApplyConfigurationsFromAssembly</c> (01.4.2's factory
/// takes the module assemblies).
/// </remarks>
internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("project", "public", table =>
        {
            table.HasCheckConstraint("project_slug_format_check", "slug ~ '^[a-z0-9][a-z0-9-]{1,62}$'");
            table.HasCheckConstraint("project_status_check", "status in ('active', 'suspended', 'deleted')");
        });

        builder.HasKey(project => project.Id)
            .HasName("project_pkey");

        builder.HasAlternateKey(project => new { project.OrgId, project.Id })
            .HasName("project_org_id_key");

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(project => project.OrgId)
            .HasConstraintName("project_organization_fk")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(project => new { project.OrgId, project.Status, project.CreatedAt, project.Id })
            .HasDatabaseName("project_org_status_created_id_idx");

        builder.HasIndex(project => new { project.OrgId, project.Slug })
            .HasDatabaseName("project_org_slug_key")
            .IsUnique();

        builder.Property(project => project.Id)
            .HasColumnName("id"); // generation (UUIDv7, client-side) is the tenant convention (01.4.2-T3)

        builder.Property(project => project.OrgId)
            .HasColumnName("org_id")
            .IsRequired();

        builder.Property(project => project.Name)
            .HasColumnName("name")
            .IsRequired();

        builder.Property(project => project.Slug)
            .HasColumnName("slug")
            .IsRequired();

        builder.Property(project => project.Status)
            .HasColumnName("status")
            .IsRequired()
            .HasDefaultValue(TenancyStatus.Active);

        builder.Property(project => project.DefaultEnvironmentId)
            .HasColumnName("default_environment_id");

        builder.Property(project => project.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("now()");

        builder.Property(project => project.UpdatedAt)
            .HasColumnName("updated_at")
            .HasDefaultValueSql("now()");

        builder.Property(project => project.Version)
            .HasColumnName("version")
            .HasDefaultValue(1)
            .IsConcurrencyToken();
    }
}
