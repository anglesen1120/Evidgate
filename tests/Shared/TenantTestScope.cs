using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Npgsql;

namespace Evidgate.Testing;

/// <summary>
/// A tenant scope for tests (01.6.1-T2.3, AC4): the real
/// <see cref="ITenantScope"/> of 00 §5.5 C3 — one app-role connection, one
/// transaction, <c>set_config('evidgate.org_id', …, true)</c> and, where given,
/// <c>evidgate.project_id</c> — plus the Dapper read path bound to it. A test that
/// wants a row of a seeded org reads it through this object, so it exercises
/// exactly the path the API uses instead of a hand-built <c>SET LOCAL</c>.
/// <para>
/// The scope is disposable: a scope that is not committed rolls back (01.4.2 §6),
/// which is what keeps a harness test from leaving rows behind even if it forgets
/// to reset.
/// </para>
/// </summary>
public sealed class TenantTestScope : IAsyncDisposable
{
    private readonly ITenantScope _scope;

    internal TenantTestScope(ITenantScope scope, ITenantDb db)
    {
        _scope = scope;
        Db = db;
    }

    /// <summary>The tenant every statement of this scope runs as.</summary>
    public Guid OrgId => _scope.OrgId;

    /// <summary>The project the scope is narrowed to, or null for an org-wide scope.</summary>
    public Guid? ProjectId => _scope.ProjectId;

    /// <summary>True when the scope was opened for a query (read-only transaction, C1).</summary>
    public bool IsReadOnly => _scope.IsReadOnly;

    /// <summary>The Dapper path of the production registration, on this scope's connection.</summary>
    public ITenantDb Db { get; }

    /// <summary>The scope's connection, for a test that needs raw <c>NpgsqlCommand</c> SQL.</summary>
    public NpgsqlConnection Connection => _scope.Connection;

    /// <summary>
    /// The scope's transaction. Exposed because an EF Core context joins the scope
    /// by <c>Database.UseTransaction</c> (00 §5.5 C3) — it must never be committed
    /// directly; <see cref="CommitAsync"/> is the commit boundary.
    /// </summary>
    public NpgsqlTransaction Transaction => _scope.Transaction;

    /// <summary>Commits the scope (C2's commit boundary). A scope disposed without this rolls back.</summary>
    public Task CommitAsync(CancellationToken cancellationToken = default) =>
        _scope.CommitAsync(cancellationToken);

    /// <summary>
    /// A command on the scope's connection and transaction. The caller owns it;
    /// parameters are bound, never interpolated (00 §5.7 S1).
    /// </summary>
    public NpgsqlCommand Command(string sql)
    {
        var command = Connection.CreateCommand();
        command.Transaction = _scope.Transaction;
        command.CommandText = sql;
        return command;
    }

    /// <summary>Runs a scalar query on the scope's connection and transaction.</summary>
    public async Task<T> ScalarAsync<T>(string sql, CancellationToken cancellationToken = default)
    {
        await using var command = Command(sql);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? default! : (T)value;
    }

    /// <summary>Runs a statement on the scope's connection and transaction and returns the affected rows.</summary>
    public async Task<int> ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        await using var command = Command(sql);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rolls back unless <see cref="CommitAsync"/> ran (01.4.2 §6), then releases the connection.</summary>
    public ValueTask DisposeAsync() => _scope.DisposeAsync();
}
