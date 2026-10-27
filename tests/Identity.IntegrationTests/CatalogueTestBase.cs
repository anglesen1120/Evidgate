using Evidgate.Testing;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// Base of the catalogue suites (02.2.1-T1): the shared harness's migrated database,
/// reset between tests, with the migration-seeded permission catalogue restored
/// afterwards.
/// </summary>
/// <remarks>
/// <para>
/// Why the extra step: <see cref="DatabaseFixture.ResetAsync"/> is Respawn's blanket
/// <c>truncate … cascade</c> over every application table, which empties
/// <c>permission</c>, <c>role</c> and <c>role_permission</c> exactly like any other
/// table. Those rows are migration-owned reference data (built-ins are never deleted,
/// 02 §8), not test fixtures, so this base restores them after each reset through
/// <see cref="CatalogueSeed"/>, the same step the session suites that arrange a
/// built-in role take. A migration that seeds the catalogue wraps its rows in a
/// <c>do $seed$ … $seed$;</c> block — <c>0010_identity_permission_catalogue.sql</c> owns
/// the base catalogue and <c>0030_policy_add_quarantine_entry.sql</c> adds
/// <c>quarantine:write</c> — and the seed is read from the migration files themselves,
/// never copied into the test project.
/// </para>
/// <para>
/// The suite joins <see cref="HarnessSuite"/> (one container and one database per
/// assembly, serialised), like every other adopting project.
/// </para>
/// </remarks>
public abstract class CatalogueTestBase : IAsyncLifetime
{
    protected CatalogueTestBase(DatabaseFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        Fixture = fixture;
        Catalogue = PermissionCatalogueFile.Load();
    }

    /// <summary>The assembly fixture: container, migrations, app-role data source and Respawn.</summary>
    protected DatabaseFixture Fixture { get; }

    /// <summary>The catalogue file the seed is diffed against.</summary>
    protected PermissionCatalogueFile Catalogue { get; }

    /// <summary>Cancellation token of the current test (xUnit1051).</summary>
    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Resets the database (01.6.1-T2.2, AC2), then restores the seeded catalogue.</summary>
    public async ValueTask InitializeAsync()
    {
        await Fixture.ResetAsync(Token);
        await SeedCatalogueAsync();
    }

    /// <summary>Nothing to tear down; the next test resets and re-seeds.</summary>
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>Seeds the catalogue rows through the harness's owner seam.</summary>
    protected Task SeedCatalogueAsync() => CatalogueSeed.ApplyAsync(Fixture, Token);
}
