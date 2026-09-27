using Evidgate.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// Write-model mapping of <c>public.role_permission</c> for migration 0010
/// (02.2.1-T2.2, 00 §5.5 C4). <c>role_id</c> is deliberately a single-column
/// reference: a built-in role carries <c>org_id is null</c>, so a composite
/// <c>(org_id, role_id)</c> reference could never match one — the exemption
/// 0010 documents for this one FK.
/// </summary>
internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("role_permission", "public");

        builder.HasKey(rolePermission => rolePermission.Id)
            .HasName("role_permission_pkey");

        builder.Property(rolePermission => rolePermission.Id)
            .HasColumnName("id");

        builder.Property(rolePermission => rolePermission.OrgId)
            .HasColumnName("org_id");

        builder.Property(rolePermission => rolePermission.RoleId)
            .HasColumnName("role_id")
            .IsRequired();

        builder.Property(rolePermission => rolePermission.PermissionCode)
            .HasColumnName("permission_code")
            .IsRequired();

        builder.Property(rolePermission => rolePermission.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("now()");

        builder.Property(rolePermission => rolePermission.UpdatedAt)
            .HasColumnName("updated_at")
            .HasDefaultValueSql("now()");

        builder.Property(rolePermission => rolePermission.Version)
            .HasColumnName("version")
            .HasDefaultValue(1);
    }
}
