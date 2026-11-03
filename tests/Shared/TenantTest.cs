using Xunit;

namespace Evidgate.Testing;

/// <summary>How a test isolates itself from the other tests of its assembly.</summary>
public enum DatabaseIsolation
{
    /// <summary>
    /// The assembly's one migrated database. The test creates its own organization and
    /// reads only rows it created, so classes can run in parallel.
    /// </summary>
    Shared,

    /// <summary>
    /// A private copy of the migrated template, dropped after the test — for whole-table
    /// counts, DDL, migration re-runs and catalog sweeps.
    /// </summary>
    Dedicated,
}

/// <summary>
/// The isolation base of the integration suites: the assembly's shared database with a
/// tenant per test, or a private database per test for a class that cannot share.
/// <para>
/// A sharing test creates its own organizations (<see cref="DatabaseFixture.SeedOrgAsync"/>)
/// and asserts only on its own rows — never a whole-table count. A class declares the
/// dedicated mode in its constructor and must not touch <see cref="Fixture"/> before
/// <see cref="InitializeAsync"/> returned, because the private database does not exist yet.
/// </para>
/// </summary>
public abstract class TenantTest : IAsyncLifetime
{
    private readonly DatabaseFixture _root;
    private readonly DatabaseIsolation _isolation;
    private DatabaseFixture? _dedicated;

    /// <param name="root">The assembly fixture, shared by every test of the assembly.</param>
    /// <param name="isolation">Whether this test shares it or gets a private copy.</param>
    protected TenantTest(DatabaseFixture root, DatabaseIsolation isolation = DatabaseIsolation.Shared)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = root;
        _isolation = isolation;
    }

    /// <summary>
    /// The database of this test: the assembly's shared one, or this test's private copy
    /// once <see cref="InitializeAsync"/> has run.
    /// </summary>
    /// <exception cref="InvalidOperationException">A dedicated test asks before its copy exists.</exception>
    protected DatabaseFixture Fixture =>
        _isolation == DatabaseIsolation.Shared
            ? _root
            : _dedicated ?? throw new InvalidOperationException(
                "this test's private database does not exist until InitializeAsync has run; "
                + "access Fixture after base.InitializeAsync()");

    /// <summary>Cancellation token of the current test (xUnit1051).</summary>
    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Creates the private database of a dedicated test; a sharing test does nothing.
    /// An overriding test class calls this before arranging any row.
    /// </summary>
    public virtual async ValueTask InitializeAsync()
    {
        if (_isolation == DatabaseIsolation.Dedicated)
        {
            _dedicated = await _root.CreateDedicatedAsync(Token).ConfigureAwait(false);
        }
    }

    /// <summary>Drops the private database; the shared one is never reset or altered here.</summary>
    public virtual async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (_dedicated is null)
        {
            return;
        }

        var dedicated = _dedicated;
        _dedicated = null;
        await dedicated.DisposeAsync().ConfigureAwait(false);
    }
}
