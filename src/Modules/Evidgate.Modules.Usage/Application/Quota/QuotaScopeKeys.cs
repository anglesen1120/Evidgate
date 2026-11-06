using Evidgate.Modules.Usage.Contracts;

namespace Evidgate.Modules.Usage.Application.Quota;

/// <summary>
/// The counter scopes one gate call charges, in the order every statement locks them.
/// <para>
/// A scope is the key a counter row carries: its project id, or the all-zero uuid a null <c>project_id</c> stands for
/// in the table's unique index. The array is the gate's whole notion of "which counters", so the tenant scope is named
/// once and always first — two callers of one tenant then queue on the organization-wide row before either reaches a
/// project row, and the project's counter stays the last contended lock of the ingest transaction.
/// </para>
/// </summary>
internal static class QuotaScopeKeys
{
    /// <summary>The key of the organization-wide counter: <c>quota_counter.project_id</c> is null.</summary>
    internal static readonly Guid Tenant = Guid.Empty;

    /// <summary>
    /// The scopes of <paramref name="request"/>: the tenant's and the project's, or the project's alone, and only the
    /// tenant's for a call that names no project. A project scope is never named twice.
    /// </summary>
    /// <param name="request">The gate question.</param>
    /// <returns>The scope keys to lock and charge, tenant first.</returns>
    internal static Guid[] Of(QuotaCheckRequest request)
    {
        var project = request.ProjectId ?? Tenant;

        return request.IncludeTenantScope && project != Tenant ? [Tenant, project] : [project];
    }

    /// <summary>The kind of counter a scope key addresses.</summary>
    /// <param name="scopeKey">A key from <see cref="Of"/>.</param>
    /// <returns>The tenant scope for the all-zero key, the project scope otherwise.</returns>
    internal static QuotaScope KindOf(Guid scopeKey) => scopeKey == Tenant ? QuotaScope.Tenant : QuotaScope.Project;

    /// <summary>The project a scope key charges, or null for the organization-wide counter.</summary>
    /// <param name="scopeKey">A key from <see cref="Of"/>.</param>
    /// <returns>The project id, or null for <see cref="Tenant"/>.</returns>
    internal static Guid? ProjectOf(Guid scopeKey) => scopeKey == Tenant ? null : scopeKey;
}
