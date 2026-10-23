namespace Evidgate.BuildingBlocks.Pipeline.Tenancy;

/// <summary>
/// The tenant the current request or task runs as, resolved by the host BEFORE
/// the Mediator pipeline dispatches (00 §5.5 C2: the authenticated principal —
/// the session lookup or the §5.4.8 pre-tenant resolver of 02 — feeds
/// <c>evidgate.org_id</c>; a tenant id is never taken from request input).
/// <see cref="IRequestTenantContextSetter.Publish"/> publishes it for the async flow;
/// the C2 tenant transaction behaviour reads it through
/// <see cref="IRequestTenantContextAccessor"/> and fails closed when it is absent.
/// </summary>
public interface IRequestTenantContext
{
    /// <summary>The organization the request runs as (<c>evidgate.org_id</c>).</summary>
    Guid OrgId { get; }

    /// <summary>The project the scope is narrowed to (<c>evidgate.project_id</c>), or null for org-scoped work.</summary>
    Guid? ProjectId { get; }
}

/// <summary>The host-resolved request tenant, as a value.</summary>
/// <param name="OrgId">The organization the request runs as.</param>
/// <param name="ProjectId">The project to narrow to, or null for an org-wide scope.</param>
public sealed record RequestTenantContext(Guid OrgId, Guid? ProjectId) : IRequestTenantContext;

/// <summary>
/// The ambient request tenant of the current async flow — the pipeline-side mirror
/// of <c>ITenantScopeAccessor</c>. The ambient slot is needed because the pipeline
/// behaviours are singletons under Mediator's default lifetime (Mediator
/// resolves <c>IEnumerable&lt;IPipelineBehavior&lt;,&gt;&gt;</c> from the root
/// container), so a scoped service could never be injected into them.
/// </summary>
public interface IRequestTenantContextAccessor
{
    /// <summary>The context of the current async flow, or null when the host resolved none.</summary>
    IRequestTenantContext? Current { get; }

    /// <summary>
    /// The context of the current async flow; throws
    /// <see cref="TenantContextMissingException"/> when none was set — the C2
    /// fail-closed rule: never a default organization.
    /// </summary>
    IRequestTenantContext Require();
}

/// <summary>
/// The host's write half of the ambient context: the Api's tenant-resolution
/// middleware (02.6.2) — or a worker's task claim — sets it once for the flow and
/// the returned scope restores the previous value on disposal. Business code never
/// calls this: nothing downstream of the principal resolver may pick a tenant.
/// </summary>
public interface IRequestTenantContextSetter
{
    /// <summary>Publishes <paramref name="orgId"/>/<paramref name="projectId"/> for the current async flow.</summary>
    IDisposable Publish(Guid orgId, Guid? projectId);
}
