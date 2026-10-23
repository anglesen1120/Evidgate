using Evidgate.Modules.Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Evidgate.Modules.Catalog.Infrastructure.Persistence;

/// <summary>Write-model mapping of <c>public.test_case</c> (migration 0020); no navigation: other tables are reached by id.</summary>
internal sealed class TestCaseConfiguration : IEntityTypeConfiguration<TestCase>
{
    public void Configure(EntityTypeBuilder<TestCase> builder)
    {
        builder.ToTable("test_case", "public");
        builder.HasKey(row => row.Id).HasName("test_case_pkey");
        builder.HasAlternateKey(row => new { row.OrgId, row.Id }).HasName("test_case_org_id_key");
        builder.HasIndex(row => new { row.OrgId, row.ProjectId, row.AnnotationId }).HasDatabaseName("test_case_project_annotation_key").IsUnique();
        builder.HasIndex(row => new { row.OrgId, row.ProjectId, row.CreatedAt, row.Id }).HasDatabaseName("test_case_list_idx");

        builder.Property(row => row.Id).HasColumnName("id");
        builder.Property(row => row.OrgId).HasColumnName("org_id").IsRequired();
        builder.Property(row => row.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(row => row.AnnotationId).HasColumnName("annotation_id").IsRequired();
        builder.Property(row => row.Name).HasColumnName("name").IsRequired();
        builder.Property(row => row.Framework).HasColumnName("framework").IsRequired();
        builder.Property(row => row.Language).HasColumnName("language");
        builder.Property(row => row.FilePath).HasColumnName("file_path");
        builder.Property(row => row.Status).HasColumnName("status").IsRequired().HasDefaultValue(CaseStatus.Active);
        builder.Property(row => row.OwnerMembershipId).HasColumnName("owner_membership_id");
        builder.Property(row => row.Labels).HasColumnName("labels").IsRequired();
        builder.Property(row => row.FirstSeenCommit).HasColumnName("first_seen_commit").IsRequired();
        builder.Property(row => row.LastSeenCommit).HasColumnName("last_seen_commit");
        builder.Property(row => row.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(row => row.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        builder.Property(row => row.Version).HasColumnName("version").HasDefaultValue(1).IsConcurrencyToken();
    }
}

/// <summary>Mapping of <c>public.case_identity_source</c>.</summary>
internal sealed class CaseIdentitySourceConfiguration : IEntityTypeConfiguration<CaseIdentitySource>
{
    public void Configure(EntityTypeBuilder<CaseIdentitySource> builder)
    {
        builder.ToTable("case_identity_source", "public");
        builder.HasKey(row => row.Id).HasName("case_identity_source_pkey");
        builder.HasAlternateKey(row => new { row.OrgId, row.Id }).HasName("case_identity_source_org_id_key");

        // The composite tenant FK (00 §5.4.6); declaring it also tells EF to insert the case before its source rows.
        builder.HasOne<TestCase>()
            .WithMany()
            .HasForeignKey(row => new { row.OrgId, row.CaseId })
            .HasPrincipalKey(found => new { found.OrgId, found.Id })
            .HasConstraintName("case_identity_source_case_fk")
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(row => row.Id).HasColumnName("id");
        builder.Property(row => row.OrgId).HasColumnName("org_id").IsRequired();
        builder.Property(row => row.CaseId).HasColumnName("case_id").IsRequired();
        builder.Property(row => row.Source).HasColumnName("source").IsRequired();
        builder.Property(row => row.RawKey).HasColumnName("raw_key").IsRequired();
        builder.Property(row => row.CommitSha).HasColumnName("commit_sha").IsRequired();
        builder.Property(row => row.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
    }
}

/// <summary>Mapping of <c>public.case_rename_history</c>.</summary>
internal sealed class CaseRenameHistoryConfiguration : IEntityTypeConfiguration<CaseRenameHistory>
{
    public void Configure(EntityTypeBuilder<CaseRenameHistory> builder)
    {
        builder.ToTable("case_rename_history", "public");
        builder.HasKey(row => row.Id).HasName("case_rename_history_pkey");
        builder.HasAlternateKey(row => new { row.OrgId, row.Id }).HasName("case_rename_history_org_id_key");

        builder.HasOne<TestCase>()
            .WithMany()
            .HasForeignKey(row => new { row.OrgId, row.CaseId })
            .HasPrincipalKey(found => new { found.OrgId, found.Id })
            .HasConstraintName("case_rename_history_case_fk")
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(row => row.Id).HasColumnName("id");
        builder.Property(row => row.OrgId).HasColumnName("org_id").IsRequired();
        builder.Property(row => row.CaseId).HasColumnName("case_id").IsRequired();
        builder.Property(row => row.FromPath).HasColumnName("from_path").IsRequired();
        builder.Property(row => row.ToPath).HasColumnName("to_path").IsRequired();
        builder.Property(row => row.CommitSha).HasColumnName("commit_sha").IsRequired();
        builder.Property(row => row.DetectedAt).HasColumnName("detected_at").HasDefaultValueSql("now()");
    }
}
