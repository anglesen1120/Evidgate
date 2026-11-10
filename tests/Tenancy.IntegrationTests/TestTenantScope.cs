using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Npgsql;

namespace Tenancy.IntegrationTests;

/// <summary>
/// A tenant scope with no connection for tests whose subject is what an attempt does with its scope: it records the
/// work registered for the commit and runs that work only when the test commits, which is the consumer's part.
/// </summary>
internal sealed class TestTenantScope : ITenantScope
{
    private readonly List<Action> _afterCommit = [];
    private int _executed;

    /// <summary>How many actions the attempt registered for its commit.</summary>
    internal int RegisteredCount
    {
        get
        {
            lock (_afterCommit)
            {
                return _afterCommit.Count;
            }
        }
    }

    /// <summary>How many registered actions the commit ran.</summary>
    internal int ExecutedCount => Volatile.Read(ref _executed);

    /// <inheritdoc />
    public Guid OrgId { get; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid? ProjectId => Guid.CreateVersion7();

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public bool IsActive => true;

    /// <inheritdoc />
    public NpgsqlConnection Connection => throw new NotSupportedException("the scope under test opens no connection");

    /// <inheritdoc />
    public NpgsqlTransaction Transaction => throw new NotSupportedException("the scope under test opens no transaction");

    /// <inheritdoc />
    public Task CommitAsync(CancellationToken cancellationToken = default)
    {
        Action[] actions;
        lock (_afterCommit)
        {
            actions = [.. _afterCommit];
            _afterCommit.Clear();
        }

        foreach (var action in actions)
        {
            Interlocked.Increment(ref _executed);
            action();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void AfterCommit(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (_afterCommit)
        {
            _afterCommit.Add(action);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
