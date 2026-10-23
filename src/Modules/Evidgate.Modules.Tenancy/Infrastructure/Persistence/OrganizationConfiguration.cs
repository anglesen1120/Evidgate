using Evidgate.Modules.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Evidgate.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// Write-model mapping of <c>public.organization</c> for migration 0004
/// (02.1.1-T1.3, 00 §5.5 C4). Table and column names are spelled out in
/// snake_case here: no <c>HasDefaultSchema</c> and no reliance on a global naming
/// convention, so this class is the single authority for the mapping and stays
/// correct whatever 01.4.2's model convention does.
/// </summary>
/// <remarks>
/// Registered by the host through
/// <c>EntityFrameworkCore.ApplyConfigurationsFromAssembly</c> (01.4.2's factory
/// takes the module assemblies), which is why the class is internal but still has
/// a public parameterless constructor.
/// </remarks>
internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("organization", "public", table =>
        {
            // D-A1 grammar (0012 replaced the 0004 check).
            table.HasCheckConstraint("organization_slug_format_check", "slug ~ '^[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$'");
            table.HasCheckConstraint("organization_status_check", "status in ('active', 'suspended', 'deleted')");
        });

        // `organization` is the T4 exception: its key is `id` alone, and the RLS
        // policy compares that same column to evidgate.current_org_id().
        builder.HasKey(organization => organization.Id)
            .HasName("organization_pkey");

        builder.Property(organization => organization.Id)
            .HasColumnName("id"); // generation (UUIDv7, client-side) is the tenant convention (01.4.2-T3)

        builder.Property(organization => organization.Name)
            .HasColumnName("name")
            .IsRequired();

        builder.Property(organization => organization.Slug)
            .HasColumnName("slug")
            .IsRequired();

        builder.Property(organization => organization.Status)
            .HasColumnName("status")
            .IsRequired()
            .HasDefaultValue(TenancyStatus.Active);

        builder.Property(organization => organization.RetentionDefaultDays)
            .HasColumnName("retention_default_days");

        // text[] — the PostgreSQL array, read by the login callback for the
        // per-org Google Workspace allowlist (FR-02-3), not a JSON column.
        builder.Property(organization => organization.AllowedEmailDomains)
            .HasColumnName("allowed_email_domains")
            .HasColumnType("text[]");

        builder.Property(organization => organization.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("now()");

        builder.Property(organization => organization.UpdatedAt)
            .HasColumnName("updated_at")
            .HasDefaultValueSql("now()");

        builder.Property(organization => organization.Version)
            .HasColumnName("version")
            .HasDefaultValue(1)
            .IsConcurrencyToken();

        // 02.1.1-T3 (0013): tenant limits read by ITenantLimits; null = no limit.
        builder.Property(organization => organization.MaxProjects)
            .HasColumnName("max_projects");

        builder.Property(organization => organization.MaxSeats)
            .HasColumnName("max_seats");
    }
}
