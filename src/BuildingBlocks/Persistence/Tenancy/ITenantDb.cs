using System.Data.Common;

namespace Evidgate.BuildingBlocks.Persistence.Tenancy;

/// <summary>
/// The Dapper path (00 §5.5 C5) over the ambient tenant scope: module read SQL
/// runs on the scope's connection and transaction, so a read sees the uncommitted
/// writes of the same scope and RLS applies to it (C3). Every member demands an
/// active scope <em>before</em> it touches the database, which is the Dapper half
/// of the interceptor guard (01.4.2-T1.5, AC1): Dapper has no interception point,
/// so the accessor is the enforcement point.
/// </summary>
public interface ITenantDb
{
    /// <summary>
    /// The scope's connection. Throws <see cref="TenantScopeMissingException"/> without an active scope.
    /// <para>
    /// The accessor checks that a scope is open; it does not police what is run on
    /// the connection (M1W3-04). Session-level channels a caller can reach through
    /// it — <c>COPY</c>, <c>NpgsqlBatch</c>/raw commands built on the connection, or
    /// the large-object API — never pass through the EF Core interceptor. The tenant
    /// boundary is row level security, which applies to every statement of the
    /// scope's transaction regardless of the API that sent it; the interceptor is an
    /// earlier, cheaper failure for the ordinary EF path, not the boundary itself.
    /// </para>
    /// </summary>
    DbConnection Connection { get; }

    /// <summary>The scope's transaction. Throws <see cref="TenantScopeMissingException"/> without an active scope.</summary>
    DbTransaction Transaction { get; }

    /// <summary>Runs a query and materialises its rows.</summary>
    Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>Runs a query and returns its single row, or <c>default</c> when there is none.</summary>
    Task<T?> QuerySingleOrDefaultAsync<T>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>Runs a statement and returns the number of affected rows.</summary>
    Task<int> ExecuteAsync(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>Runs a query and returns its first column of the first row.</summary>
    Task<T?> ScalarAsync<T>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default);
}
