namespace Evidgate.BuildingBlocks.SharedKernel.Authorization;

/// <summary>
/// Marks the few handlers that run before the caller can hold any tenant permission:
/// the request creates the tenant itself, so there is no membership to evaluate
/// (02.1.1 §5: <c>POST /v1/orgs</c> is open to any authenticated user). The C2
/// authorization stage grants such a handler to a <c>Member</c> principal with a
/// user id and to nobody else — no service token, no operator, no anonymous caller.
/// </summary>
/// <remarks>
/// This is a deliberate hole in "every handler declares a permission" (00 §5.7 S2),
/// so it is closed by an allowlist: <c>Architecture.Tests</c> fails the build when a
/// handler outside <c>ArchitectureRules.AuthenticatedMemberAllowlist</c> carries it,
/// or when a handler carries it together with <see cref="PermissionAttribute"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AuthenticatedMemberAttribute : Attribute
{
}
