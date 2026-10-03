using Evidgate.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

internal sealed class CiBindingConfiguration : IEntityTypeConfiguration<CiBinding>
{
    public void Configure(EntityTypeBuilder<CiBinding> builder)
    {
        builder.ToTable("ci_binding", "public");
        builder.HasKey(binding => binding.Id).HasName("ci_binding_pkey");
        builder.Property(binding => binding.Id).HasColumnName("id");
        builder.Property(binding => binding.OrgId).HasColumnName("org_id").IsRequired();
        builder.Property(binding => binding.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(binding => binding.Provider).HasColumnName("provider").IsRequired();
        builder.Property(binding => binding.RepositoryId).HasColumnName("repository_id").IsRequired();
        builder.Property(binding => binding.RepositoryFullName).HasColumnName("repository_full_name").IsRequired();
        builder.Property(binding => binding.AllowedRefPatterns).HasColumnName("allowed_ref_patterns").IsRequired();
        builder.Property(binding => binding.AllowedWorkflowRefs).HasColumnName("allowed_workflow_refs").IsRequired();
        builder.Property(binding => binding.Status).HasColumnName("status").IsRequired().HasDefaultValue(CiBindingStatus.PendingVerification);
        builder.Property(binding => binding.BindingChallengeHash).HasColumnName("binding_challenge_hash");
        builder.Property(binding => binding.ChallengePepperVersion).HasColumnName("challenge_pepper_version");
        builder.Property(binding => binding.ChallengeIssuedAt).HasColumnName("challenge_issued_at");
        builder.Property(binding => binding.ActivatedAt).HasColumnName("activated_at");
        builder.Property(binding => binding.CreatedBy).HasColumnName("created_by");
        builder.Property(binding => binding.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(binding => binding.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        builder.Property(binding => binding.Version).HasColumnName("version").HasDefaultValue(1);
    }
}

internal sealed class CiTokenIssueConfiguration : IEntityTypeConfiguration<CiTokenIssue>
{
    public void Configure(EntityTypeBuilder<CiTokenIssue> builder)
    {
        builder.ToTable("ci_token_issue", "public");
        builder.HasKey(issue => issue.Id).HasName("ci_token_issue_pkey");
        builder.Property(issue => issue.Id).HasColumnName("id");
        builder.Property(issue => issue.OrgId).HasColumnName("org_id").IsRequired();
        builder.Property(issue => issue.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(issue => issue.BindingId).HasColumnName("binding_id").IsRequired();
        builder.Property(issue => issue.Provider).HasColumnName("provider").IsRequired();
        builder.Property(issue => issue.Jti).HasColumnName("jti").IsRequired();
        builder.Property(issue => issue.RepositoryId).HasColumnName("repository_id").IsRequired();
        builder.Property(issue => issue.RepositoryFullName).HasColumnName("repository_full_name").IsRequired();
        builder.Property(issue => issue.Ref).HasColumnName("ref").IsRequired();
        builder.Property(issue => issue.Sha).HasColumnName("sha").IsRequired();
        builder.Property(issue => issue.RunId).HasColumnName("run_id").IsRequired();
        builder.Property(issue => issue.RunAttempt).HasColumnName("run_attempt").IsRequired();
        builder.Property(issue => issue.JobWorkflowRef).HasColumnName("job_workflow_ref").IsRequired();
        builder.Property(issue => issue.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(issue => issue.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(issue => issue.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
        builder.Property(issue => issue.Version).HasColumnName("version").HasDefaultValue(1);
    }
}

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
