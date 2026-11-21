using Evidgate.Modules.Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Evidgate.Modules.Catalog.Infrastructure.Persistence;

/// <summary>
/// Mapping of <c>public.draft</c> (migration 0117). <c>version</c> is the optimistic-concurrency token, so a PATCH
/// sends <c>WHERE version = @original</c> and a lost race surfaces as a stale precondition rather than an overwrite;
/// the shared trigger then increments it by exactly one per write.
/// </summary>
internal sealed class DraftConfiguration : IEntityTypeConfiguration<Draft>
{
    public void Configure(EntityTypeBuilder<Draft> builder)
    {
        builder.ToTable("draft", "public");
        builder.HasKey(row => row.Id).HasName("draft_pkey");
        builder.HasAlternateKey(row => new { row.OrgId, row.Id }).HasName("draft_org_id_key");
        builder.HasIndex(row => new { row.OrgId, row.ProjectId, row.CreatedAt, row.Id }).HasDatabaseName("draft_project_list");
        builder.HasIndex(row => new { row.OrgId, row.ProjectId, row.Status, row.CreatedAt, row.Id }).HasDatabaseName("draft_status");

        builder.Property(row => row.Id).HasColumnName("id");
        builder.Property(row => row.OrgId).HasColumnName("org_id").IsRequired();
        builder.Property(row => row.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(row => row.AuthorMembershipId).HasColumnName("author_membership_id").IsRequired();
        builder.Property(row => row.Kind).HasColumnName("kind").IsRequired();
        builder.Property(row => row.BaseCommit).HasColumnName("base_commit").IsRequired();
        builder.Property(row => row.Status).HasColumnName("status").IsRequired().HasDefaultValue(DraftStatus.Draft);
        builder.Property(row => row.Version).HasColumnName("version").HasDefaultValue(1).IsConcurrencyToken();
        builder.Property(row => row.Content).HasColumnName("content").HasColumnType("jsonb").IsRequired();
        builder.Property(row => row.Provenance).HasColumnName("provenance").HasColumnType("jsonb");
        builder.Property(row => row.AiRunId).HasColumnName("ai_run_id");
        builder.Property(row => row.DryRunId).HasColumnName("dry_run_id");
        builder.Property(row => row.AllowUnverified).HasColumnName("allow_unverified").HasDefaultValue(false);
        builder.Property(row => row.UnverifiedReason).HasColumnName("unverified_reason");
        builder.Property(row => row.PrBranch).HasColumnName("pr_branch");
        builder.Property(row => row.PrCommitSha).HasColumnName("pr_commit_sha");
        builder.Property(row => row.PrUrl).HasColumnName("pr_url");
        builder.Property(row => row.PrNumber).HasColumnName("pr_number");
        builder.Property(row => row.LinkedCaseId).HasColumnName("linked_case_id");
        builder.Property(row => row.LinkedRevisionId).HasColumnName("linked_revision_id");
        builder.Property(row => row.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(row => row.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
    }
}
