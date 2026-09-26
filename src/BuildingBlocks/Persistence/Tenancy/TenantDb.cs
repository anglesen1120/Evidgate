using System.Data.Common;
using Dapper;

namespace Evidgate.BuildingBlocks.Persistence.Tenancy;

/// <summary>
/// Dapper over the ambient tenant scope (01.4.2-T1.4/T1.5). Stateless: the scope
/// is resolved per call, so one registered instance serves every request and task
/// flow. Parameters are always Dapper parameters (00 §5.7 S1).
/// </summary>
public sealed class TenantDb : ITenantDb
{
    private readonly ITenantScopeAccessor _scopes;

    /// <summary>Creates the accessor over the registered scope accessor.</summary>
    public TenantDb(ITenantScopeAccessor scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        _scopes = scopes;
    }

    /// <inheritdoc />
    public DbConnection Connection => _scopes.Require().Connection;

    /// <inheritdoc />
    public DbTransaction Transaction => _scopes.Require().Transaction;

    /// <inheritdoc />
    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var scope = _scopes.Require();
        var rows = await scope.Connection
            .QueryAsync<T>(Command(sql, parameters, scope, cancellationToken))
            .ConfigureAwait(false);

        return rows as IReadOnlyList<T> ?? [.. rows];
    }

    /// <inheritdoc />
    public async Task<T?> QuerySingleOrDefaultAsync<T>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var scope = _scopes.Require();
        return await scope.Connection
            .QuerySingleOrDefaultAsync<T>(Command(sql, parameters, scope, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> ExecuteAsync(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var scope = _scopes.Require();
        return await scope.Connection
            .ExecuteAsync(Command(sql, parameters, scope, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<T?> ScalarAsync<T>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var scope = _scopes.Require();
        return await scope.Connection
            .ExecuteScalarAsync<T>(Command(sql, parameters, scope, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Binds the Dapper command to the scope's transaction, so every statement
    /// runs inside the tenant transaction (00 §5.5 C3) — a Dapper command that is
    /// not bound to it would be a statement without the tenant setting.
    /// </summary>
    private static CommandDefinition Command(
        string sql,
        object? parameters,
        ITenantScope scope,
        CancellationToken cancellationToken) =>
        new(sql, parameters, scope.Transaction, cancellationToken: cancellationToken);
}
