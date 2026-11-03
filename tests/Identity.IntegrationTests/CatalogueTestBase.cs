using Evidgate.Testing;

namespace Identity.IntegrationTests;

/// <summary>
/// Base of the catalogue suites (02.2.1-T1): the assembly's migrated database, where a
/// test arranges its own organization and reads only its own tenant.
/// </summary>
/// <remarks>
/// The built-in permission catalogue (<c>permission</c>, <c>role</c>,
/// <c>role_permission</c>) is migration-owned reference data (built-ins are never
/// deleted, 02 §8). It is written once, when the database is migrated, so a test reads
/// it as it is — no suite re-applies the seed and nothing truncates these tables.
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
