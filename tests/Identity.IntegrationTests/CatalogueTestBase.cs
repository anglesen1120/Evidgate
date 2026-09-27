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
/// 02 §8), not test fixtures, so this base re-applies the seed block of
/// <c>migrations/0010_identity_permission_catalogue.sql</c> as <c>evidgate_owner</c>
/// after each reset. The seed is read from the migration file itself — never copied
/// into the test project — so the tests assert against the very bytes the database is
/// seeded from, and the migration's idempotency is exercised on every test.
/// </para>
/// <para>
/// The suite joins <see cref="HarnessSuite"/> (one container and one database per
/// assembly, serialised), like every other adopting project.
/// </para>
/// </remarks>
public abstract class CatalogueTestBase : IAsyncLifetime
{
    /// <summary>The migration that owns the catalogue tables and their seed.</summary>
    private const string CatalogueMigration = "0010_identity_permission_catalogue.sql";

    /// <summary>Start of the migration's seed block (one <c>do</c> statement).</summary>
    private const string SeedStart = "do $seed$";

    /// <summary>End of the migration's seed block, including the statement terminator.</summary>
    private const string SeedEnd = "$seed$;";

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
    protected async Task SeedCatalogueAsync()
    {
        var path = Path.Combine(DatabaseFixture.MigrationsPath, CatalogueMigration);
        var migration = await File.ReadAllTextAsync(path, Token);

        var start = migration.IndexOf(SeedStart, StringComparison.Ordinal);
        var end = migration.IndexOf(SeedEnd, StringComparison.Ordinal);

        if (start < 0 || end < start)
        {
            throw new InvalidOperationException(
                $"'{CatalogueMigration}' no longer carries its seed block ('{SeedStart}' … '{SeedEnd}'): "
                + "the catalogue suites re-apply it after every reset and must fail loudly when it moves.");
        }

        await Fixture.SeedAsync(migration[start..(end + SeedEnd.Length)], [], Token);
    }
}
