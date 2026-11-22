using Evidgate.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// The write model of <c>public.support_grant</c>: approval and revocation touch three columns, and every other
/// column is immutable after the request (the table's guard trigger refuses a change). The list is the Dapper read
/// path in <see cref="SupportGrantReadSql"/>.
/// </summary>
internal sealed class SupportGrantConfiguration : IEntityTypeConfiguration<SupportGrant>
{
    public void Configure(EntityTypeBuilder<SupportGrant> builder)
    {
        builder.ToTable("support_grant", "public");
        builder.HasKey(grant => grant.Id).HasName("support_grant_pkey");
        builder.Property(grant => grant.Id).HasColumnName("id");
        builder.Property(grant => grant.OrgId).HasColumnName("org_id").IsRequired();
        builder.Property(grant => grant.OperatorUserId).HasColumnName("operator_user_id").IsRequired();
        builder.Property(grant => grant.Scopes).HasColumnName("scopes").IsRequired();
        builder.Property(grant => grant.ProjectIds).HasColumnName("project_ids").IsRequired();
        builder.Property(grant => grant.Reason).HasColumnName("reason").IsRequired();
        builder.Property(grant => grant.ApprovedBy).HasColumnName("approved_by");
        builder.Property(grant => grant.ApprovedAt).HasColumnName("approved_at");
        builder.Property(grant => grant.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(grant => grant.RevokedAt).HasColumnName("revoked_at");
        builder.Property(grant => grant.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(grant => grant.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        builder.Property(grant => grant.Version).HasColumnName("version").HasDefaultValue(1);
    }
}
