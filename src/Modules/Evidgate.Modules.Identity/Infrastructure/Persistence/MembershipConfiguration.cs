using Evidgate.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// Write-model mapping of <c>public.membership</c> for migration 0011 (02.1.2-T1.2,
/// 00 §5.5 C4). Table, column and constraint names are spelled out in snake_case,
/// matching the tenant model conventions of 01.4.2-T1.3: an explicit name that
/// disagreed with the convention fails the model build rather than being silently
/// overwritten, so this class is the single authority for the mapping.
/// </summary>
/// <remarks>
/// Registered through <c>ApplyConfigurationsFromAssembly</c> on the module assembly
/// (<c>AddEvidgateDbContext&lt;IdentityDbContext&gt;</c>), which is why the class is
/// internal with a public parameterless constructor. The partial unique indexes of
/// 0011 (<c>membership_pending_email_key</c>, <c>membership_org_user_key</c>) and its
/// check constraints are not restated here: the schema is plain SQL and EF never
/// generates migrations (C4), so the model only needs the shape the commands write.
/// </remarks>
internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Membership> builder)
    {
        builder.ToTable("membership", "public", table =>
        {
            table.HasCheckConstraint(
                "membership_status_check",
                "status in ('pending', 'active', 'removed', 'revoked', 'expired')");
            // FR-02-4 / contract §4: the invite address is compared normalised, so a
            // non-normalised value must not be storable (0011 is the authority).
            table.HasCheckConstraint(
                "membership_invited_email_normalised_check",
                "invited_email = lower(invited_email)");
        });

        builder.HasKey(membership => membership.Id)
            .HasName("membership_pkey");

        builder.Property(membership => membership.Id)
            .HasColumnName("id");

        builder.Property(membership => membership.OrgId)
            .HasColumnName("org_id")
            .IsRequired();

        builder.Property(membership => membership.UserId)
            .HasColumnName("user_id");

        builder.Property(membership => membership.InvitedEmail)
            .HasColumnName("invited_email")
            .IsRequired();

        builder.Property(membership => membership.Status)
            .HasColumnName("status")
            .IsRequired()
            .HasDefaultValue(Contracts.MembershipStatus.Pending);

        builder.Property(membership => membership.InvitedBy)
            .HasColumnName("invited_by");

        builder.Property(membership => membership.InviteSecretPrefix)
            .HasColumnName("invite_secret_prefix");

        builder.Property(membership => membership.InviteTokenHash)
            .HasColumnName("invite_token_hash");

        // Migration 0043: the pepper version travels with the hash (T5), and the
        // all-or-none constraint makes it non-null exactly when a token is set.
        builder.Property(membership => membership.InvitePepperVersion)
            .HasColumnName("invite_pepper_version");

        builder.Property(membership => membership.InviteExpiresAt)
            .HasColumnName("invite_expires_at");

        builder.Property(membership => membership.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("now()");

        builder.Property(membership => membership.UpdatedAt)
            .HasColumnName("updated_at")
            .HasDefaultValueSql("now()");

        // The conventions make this the concurrency token and store-generated on
        // update; the trigger `membership_set_updated_at_and_version` (0011) is what
        // actually increments it in the statement EF excludes it from.
        builder.Property(membership => membership.Version)
            .HasColumnName("version")
            .HasDefaultValue(1);
    }
}
