using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Domain;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Authoring.Infrastructure.Persistence;

/// <summary>
/// The EF Core 10 write model of the authoring tree (00 §5.5 C4, 13.1.1-T1.3): the three versioned entities, the
/// append-only <c>authoring_version</c> and the per-project code counter of migration 0039. Created per command through
/// <see cref="ITenantDbContextFactory{TContext}"/>, so it runs on the request's tenant connection and transaction and RLS
/// scopes every statement.
/// </summary>
/// <remarks>
/// <c>AuthoringVersionWriter</c> keeps its own statements in <see cref="AuthoringSql"/>: a version row is written once,
/// next to the row lock of its entity, and the batched insert has no EF equivalent. This context is what the T2
/// commands read and reorder through.
/// </remarks>
internal sealed class AuthoringDbContext(DbContextOptions<AuthoringDbContext> options) : EvidgateDbContext(options)
{
    /// <summary>The nodes of every project's tree.</summary>
    public DbSet<FeatureNode> FeatureNodes => Set<FeatureNode>();

    /// <summary>The checklist items under the tree's categories and sub-categories.</summary>
    public DbSet<ChecklistItem> ChecklistItems => Set<ChecklistItem>();

    /// <summary>The manual test cases of the checklist items.</summary>
    public DbSet<ManualTestCase> ManualTestCases => Set<ManualTestCase>();

    /// <summary>The append-only version history (select and insert only, AC6).</summary>
    public DbSet<AuthoringVersion> AuthoringVersions => Set<AuthoringVersion>();

    /// <summary>The per-project manual case code counter.</summary>
    public DbSet<AuthoringCodeCounter> AuthoringCodeCounters => Set<AuthoringCodeCounter>();
}
