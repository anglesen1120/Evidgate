using System.Globalization;
using System.Text;
using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence.Tenancy;

/// <summary>
/// The live tenant scope (01.4.2-T1.1/T1.2): one connection, one transaction, the
/// tenant settings, and the <c>pending → open → committed | rolled-back</c>
/// lifecycle of 01.4.2 §6. The settings are transaction-scoped
/// (<c>set_config(…, true)</c>), so a pooled connection is handed back clean, and
/// the lifecycle decisions are the only place a transaction ends.
/// </summary>
internal sealed class TenantScope : ITenantScope
{
    private NpgsqlConnection? _connection;
    private NpgsqlTransaction? _transaction;
    private readonly List<Action> afterCommit = [];
    private ScopeState _state = ScopeState.Pending;
    private bool _disposed;

    internal TenantScope(
        Guid orgId,
        Guid? projectId,
        bool readOnlyTransaction)
    {
        OrgId = orgId;
        ProjectId = projectId;
        IsReadOnly = readOnlyTransaction;
    }

    /// <inheritdoc />
    public Guid OrgId { get; }

    /// <inheritdoc />
    public Guid? ProjectId { get; }

    /// <inheritdoc />
    public bool IsReadOnly { get; }

    /// <inheritdoc />
    public bool IsActive => _state == ScopeState.Open && !_disposed;

    /// <summary>
    /// True while this scope holds the ambient slot: it was published and has
    /// neither closed nor been disposed. The factory refuses a second
    /// <c>BeginAsync</c> in the same async flow while a scope is still opening, so
    /// two concurrent calls cannot each open a connection.
    /// </summary>
    internal bool IsOccupying => !_disposed && _state is ScopeState.Pending or ScopeState.Open;

    // The two getters are gated on the lifecycle rather than on the fields: after
    // CommitAsync the objects still exist (disposal needs them), but no caller may
    // keep issuing SQL through a scope that is no longer open — the documented
    // contract of ITenantScope.Connection/Transaction. The class's own open/close
    // path uses the fields directly.

    /// <inheritdoc />
    public NpgsqlConnection Connection =>
        IsActive && _connection is not null ? _connection : throw NotOpen();

    /// <inheritdoc />
    public NpgsqlTransaction Transaction =>
        IsActive && _transaction is not null ? _transaction : throw NotOpen();

    /// <summary>
    /// Opens the connection, starts the transaction and applies the tenant settings
    /// (01.4.2-T1.1). The scope is already the ambient one when this runs — the
    /// factory publishes it from its synchronous prologue — and it stays inactive
    /// until the last setting is applied, so no caller can use a half-open scope.
    /// </summary>
    internal async Task<ITenantScope> OpenAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        try
        {
            _connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            _transaction = (NpgsqlTransaction)await _connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            // 00 §5.5 C1 / AC6: everything runs on one app-role connection with
            // BEGIN, the read-only mode for queries, then the parameterised
            // set_config(…, true) calls of 00 §5.4.2. Npgsql has no read-only
            // overload of BeginTransactionAsync, so `BEGIN` followed by
            // `set transaction read only` as the transaction's first statement is
            // the equivalent of `BEGIN READ ONLY`. The requirement is that the mode
            // is in force before the tenant setting is applied — not that PostgreSQL
            // rejects a later statement: probed on the pinned postgres:18.6, a late
            // `set transaction read only` is accepted and takes effect (a write then
            // fails with 25006), while `set transaction read write` and
            // `set transaction isolation level` after the first statement are
            // rejected with 25001. Setting the mode first is what proves every
            // statement of the query path, `set_config` included, ran read-only.
            //
            // The mode and both settings go out as ONE command (M1W3-F3): Npgsql
            // splits a multi-statement command with named parameters into an
            // implicit batch, so this is a single round trip after BEGIN rather than
            // one per statement, and the values stay bound parameters — the key
            // names are the only literals, and they are constants.
            var sql = new StringBuilder();
            if (IsReadOnly)
            {
                sql.Append("set transaction read only; ");
            }

            sql.Append("select set_config('evidgate.org_id', @org_id, true)");

            if (ProjectId is not null)
            {
                sql.Append(", set_config('evidgate.project_id', @project_id, true)");
            }

            await ExecuteAsync(
                sql.ToString(),
                parameters =>
                {
                    parameters.AddWithValue(
                        "org_id",
                        OrgId.ToString("D", CultureInfo.InvariantCulture));

                    if (ProjectId is { } projectId)
                    {
                        parameters.AddWithValue(
                            "project_id",
                            projectId.ToString("D", CultureInfo.InvariantCulture));
                    }
                },
                cancellationToken).ConfigureAwait(false);

            _state = ScopeState.Open;
            return this;
        }
        catch
        {
            // A half-opened scope must never reach the caller or the pool.
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (!IsActive)
        {
            throw new InvalidOperationException("The tenant scope is no longer open: a scope commits at most once.");
        }

        await _transaction!.CommitAsync(cancellationToken).ConfigureAwait(false);
        _state = ScopeState.Committed;

        // Only durable work reaches a deferred action, and each runs at most once: the list is cleared before the
        // first call, so an action that itself commits nothing cannot have it run a second time.
        var deferred = afterCommit.ToArray();
        afterCommit.Clear();
        foreach (var action in deferred)
        {
            action();
        }
    }

    /// <inheritdoc />
    public void AfterCommit(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!IsActive)
        {
            throw new InvalidOperationException(
                "The tenant scope is not open, so deferred work would never run: register it before the commit.");
        }

        afterCommit.Add(action);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_state == ScopeState.Open)
        {
            // Rollback is the default end of a scope (01.4.2 §6): a handler that
            // never commits leaves nothing behind, including on an exception path.
            try
            {
                await _transaction!.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (NpgsqlException)
            {
                // The connection or the server is already gone; there is nothing
                // left to roll back and disposal must still release the slot.
            }
            catch (InvalidOperationException)
            {
                // The server already aborted the transaction, so Npgsql refuses
                // another ROLLBACK: the work is rolled back either way.
            }

            _state = ScopeState.RolledBack;
        }

        if (_transaction is not null)
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs a statement of the scope itself: no tenant data, so no interceptor.
    /// <paramref name="bind"/> adds the statement's parameters; they are always
    /// real parameters, never interpolated values (00 §5.7 S1).
    /// </summary>
    private async Task ExecuteAsync(
        string sql,
        Action<NpgsqlParameterCollection>? bind = null,
        CancellationToken cancellationToken = default)
    {
        await using var command = _connection!.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        bind?.Invoke(command.Parameters);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static TenantScopeMissingException NotOpen() => new(
        "The tenant scope is not open: use it after BeginAsync has completed and before CommitAsync or "
        + "disposal closes it (00 §5.4.2, §5.5 C3).");

    private enum ScopeState
    {
        /// <summary>Published as the ambient scope, not usable yet: the connection is still being opened.</summary>
        Pending,

        /// <summary>Transaction open, tenant settings applied.</summary>
        Open,

        Committed,

        RolledBack,
    }
}
