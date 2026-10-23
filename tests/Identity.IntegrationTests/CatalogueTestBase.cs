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
/// 02 §8), not test fixtures, so this base re-applies the catalogue seed of every
/// migration that carries one — <c>0010_identity_permission_catalogue.sql</c> owns the
/// base catalogue and <c>0030_policy_add_quarantine_entry.sql</c> adds
/// <c>quarantine:write</c> — after each reset. A migration that seeds the catalogue wraps
/// its rows in a <c>do $seed$ … $seed$;</c> block, and the seed is read from the migration
/// files themselves — never copied into the test project — so the tests assert against the
/// very bytes the database is seeded from, and each migration's idempotency is exercised on
/// every test.
/// </para>
/// <para>
/// The suite joins <see cref="HarnessSuite"/> (one container and one database per
/// assembly, serialised), like every other adopting project.
/// </para>
/// </remarks>
public abstract class CatalogueTestBase : IAsyncLifetime
{
    /// <summary>Start of a migration's catalogue seed block (one <c>do</c> statement).</summary>
    private const string SeedStart = "do $seed$";

    /// <summary>End of the seed block, including the statement terminator.</summary>
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
        // Every migration that seeds the catalogue wraps its rows in a `do $seed$ … $seed$;` block (0010 owns the
        // base catalogue, 0030 adds `quarantine:write`). Respawn's blanket truncate empties `permission`, `role` and
        // `role_permission` like any other table, so every such block is re-applied here, in migration order, after
        // each reset. The seed is read from the migration files themselves, never copied into the test project.
        var seeds = 0;
        foreach (var path in Directory.EnumerateFiles(DatabaseFixture.MigrationsPath, "*.sql").Order(StringComparer.Ordinal))
        {
            var migration = await File.ReadAllTextAsync(path, Token);

            var start = migration.IndexOf(SeedStart, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            var end = migration.IndexOf(SeedEnd, start, StringComparison.Ordinal);
            if (end < start)
            {
                throw new InvalidOperationException(
                    $"'{Path.GetFileName(path)}' opens a catalogue seed block ('{SeedStart}') but never closes it ('{SeedEnd}').");
            }

            await Fixture.SeedAsync(migration[start..(end + SeedEnd.Length)], [], Token);
            seeds++;
        }

        if (seeds == 0)
        {
            throw new InvalidOperationException(
                $"no migration under '{DatabaseFixture.MigrationsPath}' carries a catalogue seed block "
                + $"('{SeedStart}' … '{SeedEnd}'): the catalogue suites re-apply them after every reset and must fail loudly when the marker moves.");
        }
    }
}
