using Evidgate.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

internal sealed class IngestTokenConfiguration : IEntityTypeConfiguration<IngestToken>
{
    public void Configure(EntityTypeBuilder<IngestToken> builder)
    {
        builder.ToTable("ingest_token", "public");
        builder.HasKey(token => token.Id).HasName("ingest_token_pkey");
        builder.Property(token => token.Id).HasColumnName("id");
        builder.Property(token => token.OrgId).HasColumnName("org_id").IsRequired();
        builder.Property(token => token.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(token => token.Name).HasColumnName("name").IsRequired();
        builder.Property(token => token.Prefix).HasColumnName("prefix").IsRequired();
        builder.Property(token => token.TokenHmac).HasColumnName("token_hmac").IsRequired();
        builder.Property(token => token.PepperVersion).HasColumnName("pepper_version").IsRequired();
        builder.Property(token => token.Scopes).HasColumnName("scopes").IsRequired();
        builder.Property(token => token.ExpiresAt).HasColumnName("expires_at");
        builder.Property(token => token.OverlapEndsAt).HasColumnName("overlap_ends_at");
        builder.Property(token => token.RevokedAt).HasColumnName("revoked_at");
        builder.Property(token => token.RotatedToId).HasColumnName("rotated_to_id");
        builder.Property(token => token.LastUsedAt).HasColumnName("last_used_at");
        builder.Property(token => token.CreatedBy).HasColumnName("created_by");
        builder.Property(token => token.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(token => token.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        builder.Property(token => token.Version).HasColumnName("version").HasDefaultValue(1);
    }
}
