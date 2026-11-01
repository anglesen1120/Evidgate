using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence.Tenancy;

/// <summary>
/// One explicit tenant transaction (00 §5.4.2, §5.5 C3): a single app-role
/// <see cref="NpgsqlConnection"/> with a started transaction and the
/// transaction-scoped settings <c>evidgate.org_id</c> / <c>evidgate.project_id</c>
/// applied through the parameterised <c>set_config(…, true)</c>. EF Core write
/// models (<see cref="ITenantDbContextFactory{TContext}"/>) and Dapper reads
/// (<see cref="ITenantDb"/>) both run on <see cref="Connection"/> and
/// <see cref="Transaction"/>; nothing is durable until <see cref="CommitAsync"/>.
/// </summary>
public interface ITenantScope : IAsyncDisposable
{
    /// <summary>The tenant every statement of this scope runs as.</summary>
    Guid OrgId { get; }

    /// <summary>The project the scope is narrowed to, or null for an org-wide scope.</summary>
    Guid? ProjectId { get; }

    /// <summary>
    /// True when the scope was opened for a query (00 §5.5 C1): the transaction is
    /// read-only, so a write inside it fails at the database (T95).
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// True while the transaction is open and the tenant settings are in force.
    /// False after <see cref="CommitAsync"/> (the transaction-scoped settings are
    /// gone with the commit) and after disposal.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// The scope's connection. Owned by the scope — never dispose it. Throws
    /// <see cref="TenantScopeMissingException"/> while the scope is still opening
    /// and after it closed.
    /// </summary>
    NpgsqlConnection Connection { get; }

    /// <summary>
    /// The scope's transaction. Owned by the scope — never commit it directly.
    /// Throws <see cref="TenantScopeMissingException"/> while the scope is still
    /// opening and after it closed.
    /// </summary>
    NpgsqlTransaction Transaction { get; }

    /// <summary>
    /// Commits the scope's transaction (the C2 commit boundary). A scope disposed
    /// without this call rolls back.
    /// </summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers work to run after <see cref="CommitAsync"/> succeeded — a metric for
    /// committed work, never a write: the action runs outside the transaction, so its
    /// statement would not be part of it.
    /// </summary>
    /// <param name="action">Runs once, after the commit; never when the scope rolls back or is disposed uncommitted.</param>
    /// <remarks>
    /// A handler cannot record "this row is durable" itself, because it does not own the
    /// commit: the pipeline (<see cref="Pipeline.Behaviours.TenantTransactionBehaviour"/>)
    /// and the task consumer commit after the handler returned. Registering the action
    /// here is what keeps a measurement from counting an attempt whose row the next
    /// failure rolled back.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The scope is not open, so nothing would ever run the action.</exception>
    void AfterCommit(Action action);
}
