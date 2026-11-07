using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Identity.Application;

/// <summary>The refusals of the API-token routes, in one place so their codes cannot drift apart.</summary>
internal static class ApiTokenErrors
{
    /// <summary>422: the body names no permissions, so the token would authenticate with no right at all.</summary>
    public static Error PermissionsRequired { get; } = Error.Unprocessable(
        ApiTokenErrorCodes.SchemaInvalid,
        "permissions must name at least one catalogue code",
        [new ErrorDetail("permissions", "required")]);

    /// <summary>503: the pepper is absent, so nothing may be hashed or minted.</summary>
    public static Error NotConfigured { get; } = new(
        ApiErrorCode.Unavailable,
        "API tokens are not configured on this deployment",
        StatusCodes.Status503ServiceUnavailable,
        Retryable: true);

    /// <summary>503: the platform-wide prefix index rejected the draw; nothing was written.</summary>
    public static Error MintingUnavailable { get; } = new(
        ApiErrorCode.Unavailable,
        "the token could not be minted; retry",
        StatusCodes.Status503ServiceUnavailable,
        Retryable: true);

    /// <summary>403: the request asks for a permission its creator does not hold on the project.</summary>
    public static Error Escalation { get; } = Error.Denied(
        ApiTokenErrorCodes.PermissionEscalation,
        "the requested permissions exceed what the creator holds on this project");

    /// <summary>
    /// 403: the caller has no active membership in this org, so there is no membership row the token could record as
    /// its creator — the same <c>no_membership</c> the C2 gate denies with, and the reason a token is never minted
    /// anonymously.
    /// </summary>
    public static Error NoMembership { get; } = Error.Denied(
        ApiErrorCode.NoMembership,
        "API tokens are created by an active member of the org: no membership to record as the creator");

    /// <summary>422: <c>expires_at</c> is absent, in the past, or beyond the configured maximum.</summary>
    /// <param name="reason">The field reason: <c>required</c>, <c>past</c> or <c>out_of_range</c>.</param>
    /// <param name="message">The secret-free explanation.</param>
    public static Error Expiry(string reason, string message) =>
        Error.Unprocessable(ApiTokenErrorCodes.SchemaInvalid, message, [new ErrorDetail("expires_at", reason)]);

    /// <summary>422: the set names a denied code, an unknown code, or nothing at all.</summary>
    /// <param name="requested">The submitted codes, in the client's order (the detail carries each position).</param>
    /// <param name="offending">The codes the refusal is about.</param>
    /// <param name="code">The machine code of the refusal.</param>
    /// <param name="reason">The per-item reason, e.g. <c>forbidden</c> or <c>unknown</c>.</param>
    /// <param name="message">The secret-free explanation.</param>
    public static Error Permissions(
        IReadOnlyList<string> requested,
        IReadOnlyList<string> offending,
        string code,
        string reason,
        string message) =>
        Error.Unprocessable(
            code,
            message,
            [.. offending.Select(entry => new ErrorDetail("permissions", reason, Index: Index(requested, entry)))]);

    private static int? Index(IReadOnlyList<string> requested, string code)
    {
        for (var position = 0; position < requested.Count; position++)
        {
            if (string.Equals(requested[position], code, StringComparison.Ordinal))
            {
                return position;
            }
        }

        return null;
    }
}

/// <summary>The machine codes these routes add to the 00 §9.2 vocabulary.</summary>
internal static class ApiTokenErrorCodes
{
    /// <summary>422: the body fails the route's schema — for this story an unusable <c>expires_at</c> or permission set.</summary>
    public const string SchemaInvalid = "schema_invalid";

    /// <summary>403: a token may not hold more than its creator holds.</summary>
    public const string PermissionEscalation = "permission_escalation";

    /// <summary>422: a token may never hold an administrative or token-managing permission.</summary>
    public const string ForbiddenTokenPermission = "forbidden_token_permission";
}
