using Evidgate.Testing;

namespace Identity.IntegrationTests;

/// <summary>
/// Base of the catalogue suites: the assembly's migrated database, where a test arranges its
/// own organization and reads only its own tenant.
/// </summary>
/// <remarks>
/// The built-in catalogue (<c>permission</c>, <c>role</c>, <c>role_permission</c>) is
/// migration-owned reference data (built-ins are never deleted) written once at migration
/// time, so a test reads it as it is and no suite re-applies the seed.
/// </remarks>
public abstract class CatalogueTestBase : TenantTest
{
    protected CatalogueTestBase(DatabaseFixture fixture, DatabaseIsolation isolation = DatabaseIsolation.Shared)
        : base(fixture, isolation)
    {
        Catalogue = PermissionCatalogueFile.Load();
    }

    /// <summary>The catalogue file the seed is diffed against.</summary>
    protected PermissionCatalogueFile Catalogue { get; }
}
