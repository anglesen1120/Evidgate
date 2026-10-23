using Evidgate.Modules.Authoring.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Evidgate.Modules.Authoring.Infrastructure.Persistence;

/// <summary>Write-model mapping of <c>public.feature_node</c> (migration 0039, 13.1.1 §4); other tables are reached by id.</summary>
internal sealed class FeatureNodeConfiguration : IEntityTypeConfiguration<FeatureNode>
{
    public void Configure(EntityTypeBuilder<FeatureNode> builder)
    {
        builder.ToTable("feature_node", "public");
        builder.HasKey(row => row.Id).HasName("feature_node_pkey");
        builder.HasAlternateKey(row => new { row.OrgId, row.Id }).HasName("feature_node_org_id_key");
        builder.HasAlternateKey(row => new { row.OrgId, row.ProjectId, row.Id }).HasName("feature_node_project_id_key");
        builder.HasIndex(row => new { row.OrgId, row.ProjectId, row.ParentId, row.Position })
            .HasDatabaseName("feature_node_sibling_position_uq")
            .HasFilter("deleted_at is null")
            .IsUnique();

        // AC1's ladder: the parent lives in the same project, so the composite key cannot match another project's node.
        builder.HasOne<FeatureNode>()
            .WithMany()
            .HasForeignKey(row => new { row.OrgId, row.ProjectId, row.ParentId })
            .HasPrincipalKey(row => new { row.OrgId, row.ProjectId, row.Id })
            .HasConstraintName("feature_node_parent_fk")
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(row => row.Id).HasColumnName("id");
        builder.Property(row => row.OrgId).HasColumnName("org_id").IsRequired();
        builder.Property(row => row.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(row => row.ParentId).HasColumnName("parent_id");
        builder.Property(row => row.Kind).HasColumnName("kind").IsRequired();
        builder.Property(row => row.Title).HasColumnName("title").IsRequired();
        builder.Property(row => row.Position).HasColumnName("position").IsRequired();
        builder.Property(row => row.DeletedAt).HasColumnName("deleted_at");
        builder.Property(row => row.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(row => row.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        builder.Property(row => row.Version).HasColumnName("version").HasDefaultValue(1).IsConcurrencyToken();
    }
}

/// <summary>Mapping of <c>public.checklist_item</c> (13.1.1 §4, AC3).</summary>
internal sealed class ChecklistItemConfiguration : IEntityTypeConfiguration<ChecklistItem>
{
    public void Configure(EntityTypeBuilder<ChecklistItem> builder)
    {
        builder.ToTable("checklist_item", "public");
        builder.HasKey(row => row.Id).HasName("checklist_item_pkey");
        builder.HasAlternateKey(row => new { row.OrgId, row.Id }).HasName("checklist_item_org_id_key");
        builder.HasAlternateKey(row => new { row.OrgId, row.ProjectId, row.Id }).HasName("checklist_item_project_id_key");
        builder.HasIndex(row => new { row.OrgId, row.ProjectId, row.FeatureNodeId, row.State, row.Position, row.Id })
            .HasDatabaseName("checklist_item_list_idx")
            .HasFilter("deleted_at is null");

        builder.HasOne<FeatureNode>()
            .WithMany()
            .HasForeignKey(row => new { row.OrgId, row.ProjectId, row.FeatureNodeId })
            .HasPrincipalKey(row => new { row.OrgId, row.ProjectId, row.Id })
            .HasConstraintName("checklist_item_feature_node_fk")
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(row => row.Id).HasColumnName("id");
        builder.Property(row => row.OrgId).HasColumnName("org_id").IsRequired();
        builder.Property(row => row.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(row => row.FeatureNodeId).HasColumnName("feature_node_id").IsRequired();
        builder.Property(row => row.Title).HasColumnName("title").IsRequired();
        builder.Property(row => row.Description).HasColumnName("description");
        builder.Property(row => row.Priority).HasColumnName("priority").IsRequired();
        builder.Property(row => row.State).HasColumnName("state").IsRequired();
        builder.Property(row => row.Source).HasColumnName("source").IsRequired();
        builder.Property(row => row.Position).HasColumnName("position").IsRequired();
        builder.Property(row => row.DeletedAt).HasColumnName("deleted_at");
        builder.Property(row => row.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(row => row.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        builder.Property(row => row.Version).HasColumnName("version").HasDefaultValue(1).IsConcurrencyToken();
    }
}

/// <summary>Mapping of <c>public.manual_test_case</c> (13.1.1 §4, AC4).</summary>
internal sealed class ManualTestCaseConfiguration : IEntityTypeConfiguration<ManualTestCase>
{
    public void Configure(EntityTypeBuilder<ManualTestCase> builder)
    {
        builder.ToTable("manual_test_case", "public");
        builder.HasKey(row => row.Id).HasName("manual_test_case_pkey");
        builder.HasAlternateKey(row => new { row.OrgId, row.Id }).HasName("manual_test_case_org_id_key");
        builder.HasAlternateKey(row => new { row.OrgId, row.ProjectId, row.Id }).HasName("manual_test_case_project_id_key");
        builder.HasIndex(row => new { row.OrgId, row.ProjectId, row.Code }).HasDatabaseName("manual_test_case_code_key").IsUnique();
        builder.HasIndex(row => new { row.OrgId, row.ProjectId, row.ChecklistItemId, row.State, row.Code })
            .HasDatabaseName("manual_test_case_list_idx");

        builder.HasOne<ChecklistItem>()
            .WithMany()
            .HasForeignKey(row => new { row.OrgId, row.ProjectId, row.ChecklistItemId })
            .HasPrincipalKey(row => new { row.OrgId, row.ProjectId, row.Id })
            .HasConstraintName("manual_test_case_checklist_item_fk")
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(row => row.Id).HasColumnName("id");
        builder.Property(row => row.OrgId).HasColumnName("org_id").IsRequired();
        builder.Property(row => row.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(row => row.ChecklistItemId).HasColumnName("checklist_item_id").IsRequired();
        builder.Property(row => row.Code).HasColumnName("code").IsRequired();
        builder.Property(row => row.Title).HasColumnName("title").IsRequired();
        builder.Property(row => row.Description).HasColumnName("description");
        builder.Property(row => row.Precondition).HasColumnName("precondition");
        builder.Property(row => row.Steps).HasColumnName("steps").HasColumnType("jsonb").IsRequired();
        builder.Property(row => row.Priority).HasColumnName("priority").IsRequired();
        builder.Property(row => row.State).HasColumnName("state").IsRequired();
        builder.Property(row => row.Source).HasColumnName("source").IsRequired();
        builder.Property(row => row.DeletedAt).HasColumnName("deleted_at");
        builder.Property(row => row.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(row => row.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        builder.Property(row => row.Version).HasColumnName("version").HasDefaultValue(1).IsConcurrencyToken();
    }
}

/// <summary>
/// Mapping of <c>public.authoring_version</c> (13.1.1 §4, AC5/AC6): append-only, so the model declares no
/// navigation it could cascade through, and <c>UPDATE</c>/<c>DELETE</c> are refused by the database.
/// </summary>
internal sealed class AuthoringVersionConfiguration : IEntityTypeConfiguration<AuthoringVersion>
{
    public void Configure(EntityTypeBuilder<AuthoringVersion> builder)
    {
        builder.ToTable("authoring_version", "public");
        builder.HasKey(row => row.Id).HasName("authoring_version_pkey");
        builder.HasAlternateKey(row => new { row.OrgId, row.Id }).HasName("authoring_version_org_id_key");
        builder.HasIndex(row => new { row.OrgId, row.ProjectId, row.EntityKind, row.EntityId, row.VersionNo })
            .HasDatabaseName("authoring_version_entity_key")
            .IsUnique();

        builder.Property(row => row.Id).HasColumnName("id");
        builder.Property(row => row.OrgId).HasColumnName("org_id").IsRequired();
        builder.Property(row => row.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(row => row.EntityKind).HasColumnName("entity_kind").IsRequired();
        builder.Property(row => row.EntityId).HasColumnName("entity_id").IsRequired();
        builder.Property(row => row.VersionNo).HasColumnName("version_no").IsRequired();
        builder.Property(row => row.BatchId).HasColumnName("batch_id").IsRequired();
        builder.Property(row => row.ChangeKind).HasColumnName("change_kind").IsRequired();
        builder.Property(row => row.Content).HasColumnName("content").HasColumnType("jsonb").IsRequired();
        builder.Property(row => row.ContentHash).HasColumnName("content_hash").IsRequired();
        builder.Property(row => row.Source).HasColumnName("source").IsRequired();
        builder.Property(row => row.AuthorMembershipId).HasColumnName("author_membership_id");
        builder.Property(row => row.Reason).HasColumnName("reason");
        builder.Property(row => row.AiRunId).HasColumnName("ai_run_id");
        builder.Property(row => row.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("clock_timestamp()");
    }
}

/// <summary>Mapping of <c>public.authoring_code_counter</c> (13.1.1 §4, AC4).</summary>
internal sealed class AuthoringCodeCounterConfiguration : IEntityTypeConfiguration<AuthoringCodeCounter>
{
    public void Configure(EntityTypeBuilder<AuthoringCodeCounter> builder)
    {
        builder.ToTable("authoring_code_counter", "public");
        builder.HasKey(row => new { row.OrgId, row.ProjectId }).HasName("authoring_code_counter_pkey");
        builder.HasAlternateKey(row => new { row.OrgId, row.Id }).HasName("authoring_code_counter_org_id_key");

        builder.Property(row => row.Id).HasColumnName("id").IsRequired();
        builder.Property(row => row.OrgId).HasColumnName("org_id").IsRequired();
        builder.Property(row => row.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(row => row.NextValue).HasColumnName("next_value").HasDefaultValue(1L).IsRequired();
    }
}
