using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.BuildingBlocks.Pipeline.Authorization;

/// <summary>
/// The C2 authorization stage's verdict (02.2.1-T2.1): granted, or denied with the
/// stable 00 §9.2 <c>error.code</c> the 403 envelope must carry. A bare <c>bool</c>
/// cannot express which of the three documented denials happened — <c>forbidden</c>
/// (the principal lacks the permission, or the handler declares one the catalogue
/// does not know), <c>project_scope_mismatch</c> (the request addresses a project
/// outside the token's scope, 00 §9.1) and <c>no_membership</c> (the member has no
/// active membership in the org, 02.1.2 AC2) — and a client acts differently on
/// each, so the code travels with the verdict.
/// <para>
/// A reference type on purpose: a struct's <c>default</c> would be a granted
/// verdict, and fail-closed authorization must not have a value that grants by
/// accident. <see cref="Granted"/> is the only granting value.
/// </para>
/// </summary>
public sealed record PermissionDecision
{
    private PermissionDecision(string? denyCode) => DenyCode = denyCode;

    /// <summary>True only for <see cref="Granted"/>.</summary>
    public bool IsGranted => DenyCode is null;

    /// <summary>The 00 §9.2 code of a denial (<c>forbidden</c>, <c>project_scope_mismatch</c>, <c>no_membership</c>); null when granted.</summary>
    public string? DenyCode { get; }

    /// <summary>The single granting verdict.</summary>
    public static PermissionDecision Granted { get; } = new((string?)null);

    /// <summary>A denial carrying the code the endpoint renders (00 §5.5 C8).</summary>
    public static PermissionDecision Denied(string denyCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(denyCode);

        return new PermissionDecision(denyCode);
    }

    /// <summary>
    /// The 00 §9.2 failure of this verdict (403, C8). Only called for a denial —
    /// the pipeline answers a granted verdict with the handler's own result.
    /// </summary>
    public Error ToError()
    {
        if (IsGranted)
        {
            throw new InvalidOperationException(
                "A granted PermissionDecision has no error; only a denial maps to the 00 §9.2 envelope.");
        }

        return DenyCode switch
        {
            ApiErrorCode.ProjectScopeMismatch => Error.Denied(
                ApiErrorCode.ProjectScopeMismatch,
                "the request addresses a project outside the principal's project scope"),
            ApiErrorCode.NoMembership => Error.Denied(
                ApiErrorCode.NoMembership,
                "the principal is not an active member of this organization"),
            var code => Error.Denied(code!, "the principal is not allowed to perform this operation"),
        };
    }
}
