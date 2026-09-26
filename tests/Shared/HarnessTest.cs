using Xunit;

namespace Evidgate.Testing;

/// <summary>
/// The shared harness (01.6.1-T2.1, T2.2, T2.4): one container and one migrated
/// database per assembly, reset between tests. Every test of an adopting project
/// derives from <see cref="HarnessTest"/>, so it starts from the migrated-but-empty
/// state.
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
    public ValueTask InitializeAsync() => new(Fixture.ResetAsync(Token));

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
