using Evidgate.Testing;

namespace Identity.IntegrationTests;

/// <summary>
/// Re-applies every migration's catalogue seed block (<c>do $seed$ … $seed$;</c>) after a
/// harness reset.
/// </summary>
/// <remarks>
/// <para>
/// The reason is <see cref="DatabaseFixture.ResetAsync"/>: it is Respawn's blanket
/// <c>truncate … cascade</c> over every application table, which empties
/// <c>permission</c>, <c>role</c> and <c>role_permission</c> exactly like any other
/// table. Those rows are migration-owned reference data (built-ins are never deleted,
/// 02 §8), not test fixtures, so a suite that arranges a membership with a built-in
/// role, or a custom role carrying a catalogue permission, restores them after each
/// reset — otherwise <c>RoleIdAsync</c> finds no <c>tenant_admin</c>/<c>viewer</c> and a
/// <c>role_permission</c> insert violates <c>role_permission_permission_fk</c>.
/// </para>
/// <para>
/// The seed is read from the migration files themselves — never copied into the test
/// project — so the tests assert against the very bytes the database is seeded from,
/// and each migration's idempotency is exercised on every test. A migration that
/// seeds the catalogue wraps its rows in the <c>do</c> block, and one that does not
/// carry the marker is simply skipped.
/// </para>
/// </remarks>
internal static class CatalogueSeed
{
    /// <summary>Start of a migration's catalogue seed block (one <c>do</c> statement).</summary>
    private const string SeedStart = "do $seed$";

    /// <summary>End of the seed block, including the statement terminator.</summary>
    private const string SeedEnd = "$seed$;";

    /// <summary>Seeds the catalogue rows through the harness's owner seam.</summary>
    /// <param name="fixture">The assembly fixture whose migrated database is re-seeded.</param>
    /// <param name="cancellationToken">Cancellation token of the current test.</param>
    internal static async Task ApplyAsync(DatabaseFixture fixture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var seeds = 0;
        foreach (var path in Directory.EnumerateFiles(DatabaseFixture.MigrationsPath, "*.sql").Order(StringComparer.Ordinal))
        {
            var migration = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

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

            await fixture.SeedAsync(migration[start..(end + SeedEnd.Length)], [], cancellationToken).ConfigureAwait(false);
            seeds++;
        }

        if (seeds == 0)
        {
            throw new InvalidOperationException(
                $"no migration under '{DatabaseFixture.MigrationsPath}' carries a catalogue seed block "
                + $"('{SeedStart}' … '{SeedEnd}'): a suite that needs the catalogue re-applies them after every reset "
                + "and must fail loudly when the marker moves.");
        }
    }
}
