namespace Evidgate.BuildingBlocks.SharedKernel.Authorization;

/// <summary>
/// The ingest exchange resolves a project and token id from the public prefix before entering
/// the tenant scope. Only a service principal published by that path may reach the handler,
/// which verifies the secret and token lifecycle before issuing any bearer credential.
/// The permitted handlers are closed by an architecture test.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CredentialAuthenticatedAttribute : Attribute
{
}
