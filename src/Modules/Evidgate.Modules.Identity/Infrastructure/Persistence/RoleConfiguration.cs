using Evidgate.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// Write-model mapping of <c>public.role</c> for migration 0010 (02.2.1-T2.2,
/// 00 §5.5 C4). Table, column and constraint names are spelled out in snake_case,
/// matching the tenant model conventions of 01.4.2-T1.3: an explicit name that
/// disagreed with the convention would fail the model build rather than be
/// silently overwritten, so this class is the single authority for the mapping.
/// </summary>
/// <remarks>
/// Registered by the host through <c>ApplyConfigurationsFromAssembly</c> on the
/// module assembly (<c>AddEvidgateDbContext&lt;IdentityDbContext&gt;</c>), which is
/// why the class is internal with a public parameterless constructor.
/// </remarks>
internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("role", "public", table =>
        {
            table.HasCheckConstraint("role_code_format_check", "code ~ '^[a-z][a-z0-9_]*$'");
            table.HasCheckConstraint("role_name_length_check", "char_length(name) between 1 and 64");
            // 0010 (W7-SEC-03): who may hold the role — `member` only for the roles a
            // membership may reference. Same values the domain's RoleKind carries.
            table.HasCheckConstraint("role_kind_check", "kind in ('member', 'service', 'staff')");
            // 0010: a built-in role is exactly the platform row, so the flag can
            // never disagree with the scope the RLS policy keys on (02 §4.5).
            table.HasCheckConstraint("role_builtin_matches_scope_check", "is_builtin = (org_id is null)");
        });

        builder.HasKey(role => role.Id)
            .HasName("role_pkey");

        builder.Property(role => role.Id)
            .HasColumnName("id");

        builder.Property(role => role.OrgId)
            .HasColumnName("org_id");

        builder.Property(role => role.Code)
            .HasColumnName("code")
            .IsRequired();

        builder.Property(role => role.Name)
            .HasColumnName("name")
            .IsRequired();

        builder.Property(role => role.IsBuiltin)
            .HasColumnName("is_builtin")
            .IsRequired();

        builder.Property(role => role.Kind)
            .HasColumnName("kind")
            .IsRequired();

        builder.Property(role => role.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("now()");

        builder.Property(role => role.UpdatedAt)
            .HasColumnName("updated_at")
            .HasDefaultValueSql("now()");

        // The conventions make this the concurrency token and store-generated on
        // update; the trigger `role_set_updated_at_and_version` (0010) is what
        // actually increments it in the statement EF excludes it from.
        builder.Property(role => role.Version)
            .HasColumnName("version")
            .HasDefaultValue(1);
    }
}
