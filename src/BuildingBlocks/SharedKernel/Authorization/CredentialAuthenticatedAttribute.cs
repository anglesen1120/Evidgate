namespace Evidgate.BuildingBlocks.SharedKernel.Authorization;

/// <summary>
/// Gate of the credential-exchange handlers (<c>POST /v1/ci/token</c>, 02.4.1/02.4.2). The caller holds no
/// membership and no permission yet: the endpoint verified a credential (a GitHub OIDC token or an ingest token)
/// BEFORE the pipeline and published the tenant and a service principal from the verified result. The attribute
/// only lets a handler run for such a principal; the handler re-checks the credential's binding inside the tenant
/// scope. The set of types allowed to carry it is closed by an architecture test, like
/// <see cref="AuthenticatedMemberAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CredentialAuthenticatedAttribute : Attribute
{
}
