using Xunit;

namespace Evidgate.Testing;

/// <summary>
/// The reset-based harness (01.6.1-T2.1, T2.2, T2.4): one container and one migrated
/// database per assembly, reset between tests. A project that cannot isolate by tenant —
/// <c>tests/Isolation.Tests</c> is the one — derives from this class and joins
/// <see cref="HarnessSuite"/>, so it starts from the migrated-but-empty state. Suites that
/// share the database through their own organizations derive from <c>TenantTest</c>
/// instead and never reset it.
/// </summary>
public abstract class HarnessTest : IAsyncLifetime
{
    protected HarnessTest(DatabaseFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        Fixture = fixture;
    }

    /// <summary>The assembly fixture: container, migrations, app-role data source and Respawn.</summary>
    protected DatabaseFixture Fixture { get; }

    /// <summary>Cancellation token of the current test (xUnit1051).</summary>
    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Respawn between tests (01.6.1-T2.2, AC2). xUnit v3 constructs the test class
    /// and runs this once per test case, which is exactly the per-test reset the
    /// story asks for; a test therefore never sees the previous test's rows, without
    /// a per-test database (and a per-test migration run).
    /// </summary>
    /// <remarks>
    /// Virtual for the one suite shape that needs more than an empty database: a
    /// suite that arranges a membership with a built-in role (or a custom role
    /// carrying a catalogue permission) must restore the migration-seeded catalogue
    /// after the reset, because Respawn's blanket truncate empties
    /// <c>permission</c>/<c>role</c>/<c>role_permission</c> too. An overriding base
    /// resets first, then re-applies the migration seed blocks.
    /// </remarks>
    public virtual ValueTask InitializeAsync() => new(Fixture.ResetAsync(Token));

    /// <summary>
    /// Nothing to tear down (the next test's <see cref="InitializeAsync"/> resets
    /// again). <c>IAsyncLifetime</c> still requires the member, and
    /// <c>SuppressFinalize</c> is the CA1816 contract for a dispose-shaped one.
    /// </summary>
    ValueTask IAsyncDisposable.DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
