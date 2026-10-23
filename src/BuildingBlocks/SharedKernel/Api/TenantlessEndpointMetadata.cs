namespace Evidgate.BuildingBlocks.SharedKernel.Api;

/// <summary>
/// Endpoint metadata marking a route that runs before any tenant exists: the host
/// resolves the caller's principal but neither requires nor publishes a tenant
/// (02.6.2 §3). Only <c>POST /v1/orgs</c> (the tenant is being created) and
/// <c>GET /v1/orgs</c> (the caller's orgs across tenants) carry it, attached with
/// <c>.WithMetadata(TenantlessEndpointMetadata.Instance)</c>; an architecture
/// test pins that allowlist, because a route with this mark skips tenant resolution.
/// </summary>
public sealed class TenantlessEndpointMetadata
{
    /// <summary>The single shared instance.</summary>
    public static TenantlessEndpointMetadata Instance { get; } = new();

    private TenantlessEndpointMetadata()
    {
    }
}
