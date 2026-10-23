using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Evidgate.BuildingBlocks.Persistence.Tenancy;

/// <summary>
/// 01.4.2-T1.5 / AC1: refuses any EF Core command that is not running on the
/// active tenant scope's connection — 00 §5.5 C3, "a <c>DbCommandInterceptor</c>
/// throws when a command runs on a connection without the tenant setting".
/// The check is a plain runtime guard (no <c>Debug</c> conditional), so it also
/// protects a Release build and the tests run with <c>-c Release</c>.
/// </summary>
/// <remarks>
/// The guard's reach is exactly the EF Core command pipeline (M1W3-04): it sees
/// <see cref="DbCommand"/>s EF builds, and EF batches included, but nothing else.
/// A <c>COPY</c>, an <c>NpgsqlBatch</c>, a command built directly on the scope's
/// <c>NpgsqlConnection</c>, or the large-object API executes without passing
/// through it — by design, not by oversight. The tenant boundary is row level
/// security, which applies to every statement of the scope's transaction whatever
/// sent it; this interceptor fails earlier and more legibly for the ordinary path,
/// and the Dapper half of the same guard is the scope accessor that
/// <c>ITenantDb</c> resolves per call.
/// </remarks>
public sealed class TenantCommandInterceptor : DbCommandInterceptor
{
    private readonly ITenantScopeAccessor _scopes;

    /// <summary>Creates the interceptor over the registered scope accessor.</summary>
    public TenantCommandInterceptor(ITenantScopeAccessor scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        _scopes = scopes;
    }

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Validate(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Validate(command);
        return new ValueTask<InterceptionResult<DbDataReader>>(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        Validate(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Validate(command);
        return new ValueTask<InterceptionResult<object>>(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Validate(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Validate(command);
        return new ValueTask<InterceptionResult<int>>(result);
    }

    /// <summary>
    /// The tenant setting is transaction-scoped (<c>set_config(…, true)</c>), so a
    /// command may only run on the connection the active scope opened. The
    /// connection identity is the load-bearing check: the scope's connection has
    /// exactly one open explicit transaction for its whole active lifetime, so any
    /// command on it executes inside the server-side tenant transaction, whether or
    /// not the command names that transaction. A command that does name one must
    /// name the scope's; a command with no transaction on the scope connection is
    /// deliberately tolerated for exactly that reason — the transaction check below
    /// only fires when the command names one. Anything else — a pooled connection
    /// borrowed from the data source, a context bound to another connection, or SQL
    /// after the scope committed — would execute without a tenant context and is
    /// refused.
    /// </summary>
    private void Validate(DbCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var scope = _scopes.Current;
        if (scope is null || !scope.IsActive)
        {
            throw new TenantScopeMissingException(
                $"{TenantScopeMissingException.DefaultMessage} EF Core command rejected by "
                + $"{nameof(TenantCommandInterceptor)}.");
        }

        if (!ReferenceEquals(command.Connection, scope.Connection))
        {
            throw new TenantScopeMissingException(
                $"The EF Core command runs on a connection that is not the tenant connection of organization "
                + $"{scope.OrgId:D} (00 §5.5 C3).");
        }

        // A null transaction means the command does not name one — not that it runs
        // outside a transaction: the scope connection carries exactly one open
        // explicit transaction, so the statement is inside the tenant transaction
        // either way. Only a command that names a *different* transaction is unsafe.
        if (command.Transaction is not null && !ReferenceEquals(command.Transaction, scope.Transaction))
        {
            throw new TenantScopeMissingException(
                $"The EF Core command runs in a transaction that is not the tenant transaction of organization "
                + $"{scope.OrgId:D} (00 §5.5 C3).");
        }
    }
}
