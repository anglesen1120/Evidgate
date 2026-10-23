using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// The Identity module's EF Core write model (00 §5.5 C4): <c>role</c> and
/// <c>role_permission</c>, the two tables the flagged custom-role commands of
/// 02.2.1-T2.2 write. Created by <c>ITenantDbContextFactory&lt;IdentityDbContext&gt;</c>,
/// which binds it to the ambient tenant scope's connection and transaction — a
/// context obtained without an open scope throws instead of running unscoped
/// (01.4.2-T1.5).
/// </summary>
/// <remarks>
/// The read side of these tables is the Dapper path of 00 §5.5 C5
/// (<c>IdentityReadSql</c>); only the writes come through here, exactly as C4
/// prescribes.
/// </remarks>
internal sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : EvidgateDbContext(options)
{
    /// <summary>The catalogue roles and the org's custom roles (RLS decides which are visible).</summary>
    public DbSet<Role> Roles => Set<Role>();

    /// <summary>The grants of the roles above.</summary>
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    /// <summary>The org memberships and pending invites (02.1.2-T1.2).</summary>
    public DbSet<Membership> Memberships => Set<Membership>();

    /// <summary>The roles a membership holds (02.1.2-T1.2).</summary>
    public DbSet<MembershipRole> MembershipRoles => Set<MembershipRole>();

    public DbSet<CiBinding> CiBindings => Set<CiBinding>();

    public DbSet<CiTokenIssue> CiTokenIssues => Set<CiTokenIssue>();

    public DbSet<IngestToken> IngestTokens => Set<IngestToken>();
}
