using Evidgate.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// Write-model mapping of <c>public.membership_role</c> for migration 0011
/// (02.1.2-T1.2, 00 §5.5 C4). Same conventions as
/// <see cref="MembershipConfiguration"/>: explicit snake_case names that must agree
/// with 01.4.2-T1.3's convention or the model build fails.
/// </summary>
/// <remarks>
/// The composite tenant foreign key <c>(org_id, membership_id) → membership
/// (org_id, id)</c> of 0011 is intentionally not modelled: EF never creates the
/// schema here (C4), and the database is the enforcement point. <c>role_id</c> has no
/// foreign key by design (see <see cref="MembershipRole"/> and the 0011 header).
/// </remarks>
internal sealed class MembershipRoleConfiguration : IEntityTypeConfiguration<MembershipRole>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MembershipRole> builder)
    {
        builder.ToTable("membership_role", "public");

        builder.HasKey(membershipRole => membershipRole.Id)
            .HasName("membership_role_pkey");

        builder.Property(membershipRole => membershipRole.Id)
            .HasColumnName("id");

        builder.Property(membershipRole => membershipRole.OrgId)
            .HasColumnName("org_id")
            .IsRequired();

        builder.Property(membershipRole => membershipRole.MembershipId)
            .HasColumnName("membership_id")
            .IsRequired();

        builder.Property(membershipRole => membershipRole.RoleId)
            .HasColumnName("role_id")
            .IsRequired();

        builder.Property(membershipRole => membershipRole.GrantedBy)
            .HasColumnName("granted_by");

        builder.Property(membershipRole => membershipRole.GrantedAt)
            .HasColumnName("granted_at")
            .HasDefaultValueSql("now()");

        builder.Property(membershipRole => membershipRole.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("now()");

        builder.Property(membershipRole => membershipRole.UpdatedAt)
            .HasColumnName("updated_at")
            .HasDefaultValueSql("now()");

        builder.Property(membershipRole => membershipRole.Version)
            .HasColumnName("version")
            .HasDefaultValue(1);
    }
}
