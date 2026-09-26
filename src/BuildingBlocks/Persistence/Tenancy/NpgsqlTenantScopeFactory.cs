using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence.Tenancy;

/// <summary>
/// The app-role scope factory (01.4.2-T1.1/T1.2): every <see cref="BeginAsync"/>
/// borrows a connection from the shared app-role <see cref="NpgsqlDataSource"/>,
/// starts the transaction, applies the tenant settings and publishes the scope as
/// the ambient one for the current async flow, so EF Core and Dapper reach the
/// same connection and transaction without it being passed around (00 §5.5 C3).
/// The ambient slot lives in this instance, so a scope started in one test or one
/// host never leaks into another.
/// </summary>
public sealed class NpgsqlTenantScopeFactory : ITenantScopeFactory
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly AsyncLocal<TenantScope?> _ambient = new();

    /// <summary>Creates the factory over the app-role data source registered by <c>AddEvidgatePersistence</c>.</summary>
    public NpgsqlTenantScopeFactory(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public ITenantScope? Current => _ambient.Value is { IsActive: true } scope ? scope : null;

    /// <inheritdoc />
    public ITenantScope Require() => Current ?? throw new TenantScopeMissingException();

    /// <inheritdoc />
    public Task<ITenantScope> BeginAsync(
        Guid orgId,
        Guid? projectId = null,
        bool readOnlyTransaction = false,
        CancellationToken cancellationToken = default)
    {
        if (_ambient.Value is { IsOccupying: true } open)
        {
            throw new InvalidOperationException(
                $"A tenant scope for organization {open.OrgId:D} is already open in this async flow: "
                + "one scope per request/task (00 §5.4.2).");
        }

        // This method is deliberately NOT async: an AsyncLocal write inside an async
        // method happens in that method's own ExecutionContext and never reaches the
        // caller, so the ambient scope would be invisible to the very flow that
        // opened it. Publishing it here, synchronously, is what makes the scope
        // visible to the caller's continuation; OpenAsync then fills the same object
        // in place and only then marks it active.
        var scope = new TenantScope(orgId, projectId, readOnlyTransaction);
        _ambient.Value = scope;

        return scope.OpenAsync(_dataSource, cancellationToken);
    }
}
