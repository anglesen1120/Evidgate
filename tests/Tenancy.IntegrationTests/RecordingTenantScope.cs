using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Npgsql;

namespace Tenancy.IntegrationTests;

/// <summary>
/// A tenant scope that delegates every member to the attempt's real scope and counts the work registered with
/// <see cref="AfterCommit"/> as well as the work the commit actually ran. A measurement's placement is asserted through
/// these counters, because the instrument itself is process-wide and other suites' attempts share it.
/// </summary>
internal sealed class RecordingTenantScope(ITenantScope inner) : ITenantScope
{
    private readonly List<Action> _registered = [];
    private int _executed;

    /// <summary>How many actions the attempt registered for its commit.</summary>
    internal int RegisteredCount
    {
        get
        {
            lock (_registered)
            {
                return _registered.Count;
            }
        }
    }

    /// <summary>How many registered actions the commit ran (never one, for a rolled-back attempt).</summary>
    internal int ExecutedCount => Volatile.Read(ref _executed);

    /// <inheritdoc />
    public Guid OrgId => inner.OrgId;

    /// <inheritdoc />
    public Guid? ProjectId => inner.ProjectId;

    /// <inheritdoc />
    public bool IsReadOnly => inner.IsReadOnly;

    /// <inheritdoc />
    public bool IsActive => inner.IsActive;

    /// <inheritdoc />
    public NpgsqlConnection Connection => inner.Connection;

    /// <inheritdoc />
    public NpgsqlTransaction Transaction => inner.Transaction;

    /// <inheritdoc />
    public Task CommitAsync(CancellationToken cancellationToken = default) => inner.CommitAsync(cancellationToken);

    /// <inheritdoc />
    public void AfterCommit(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (_registered)
        {
            _registered.Add(action);
        }

        inner.AfterCommit(() =>
        {
            Interlocked.Increment(ref _executed);
            action();
        });
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

/// <summary>
/// The ambient scope accessor a handler reads, wrapping every scope it answers in a <see cref="RecordingTenantScope"/>
/// so a test can tell a measurement registered for the commit from one taken inside the open transaction.
/// </summary>
internal sealed class RecordingTenantScopeAccessor(ITenantScopeAccessor inner) : ITenantScopeAccessor
{
    /// <summary>The scope the handler under test last required.</summary>
    internal RecordingTenantScope? Last { get; private set; }

    /// <inheritdoc />
    public ITenantScope? Current => inner.Current;

    /// <inheritdoc />
    public ITenantScope Require() => Last = new RecordingTenantScope(inner.Require());
}
