namespace Evidgate.BuildingBlocks.Persistence.Tenancy;

/// <summary>
/// Opens the one tenant scope a request or task runs in (00 §5.4.2, §5.5 C2):
/// the tenant transaction behaviour calls it from the principal resolved before
/// the pipeline, and so does the worker dispatcher from <c>task.org_id</c>. The
/// scope becomes the ambient one (<see cref="ITenantScopeAccessor"/>) until it is
/// disposed.
/// </summary>
public interface ITenantScopeFactory : ITenantScopeAccessor
{
    /// <summary>
    /// Opens a connection from the app-role <c>NpgsqlDataSource</c>, starts its
    /// transaction, applies the tenant settings and returns the scope. Throws
    /// <see cref="InvalidOperationException"/> when a scope is already open in
    /// this async flow — one scope per request/task.
    /// </summary>
    /// <param name="orgId">Tenant the scope runs as (`evidgate.org_id`).</param>
    /// <param name="projectId">Project to narrow to (`evidgate.project_id`), or null for org-wide work.</param>
    /// <param name="readOnlyTransaction">
    /// True for query handlers (00 §5.5 C1): the transaction is opened read-only
    /// before the tenant settings are applied (T95).
    /// </param>
    /// <param name="cancellationToken">Cancels opening the connection.</param>
    Task<ITenantScope> BeginAsync(
        Guid orgId,
        Guid? projectId = null,
        bool readOnlyTransaction = false,
        CancellationToken cancellationToken = default);
}
