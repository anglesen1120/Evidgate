using System.Text.Json;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Policy.Application;

/// <summary>Errors of the policy HTTP surface (07.1.1 §5, 00 §9.2).</summary>
internal static class PolicyErrors
{
    /// <summary>404: no revision with that id is visible to this tenant (another tenant's id included, 00 §9.2).</summary>
    internal static Error RevisionNotFound => Error.NotFound("no policy revision with that id is visible to this tenant");

    /// <summary>403: the credential is scoped to another project than the route names (00 §9.1).</summary>
    internal static Error ProjectScopeMismatch => Error.Denied(
        ApiErrorCode.ProjectScopeMismatch,
        "the credential is scoped to a different project");

    /// <summary>422: the body carries a member the 00 §8.6 field set does not declare (AC3).</summary>
    internal static Error UnknownField(string message, IReadOnlyList<ErrorDetail> details) =>
        Error.Unprocessable(ApiErrorCode.PolicyUnknownField, message, details);

    /// <summary>422: the body is not JSON, does not match <c>policy-revision.v1</c>, or cannot be canonicalised.</summary>
    internal static Error PolicyInvalid(string message, IReadOnlyList<ErrorDetail> details) =>
        Error.Unprocessable(ApiErrorCode.PolicyInvalid, message, details);

    /// <summary>422: the cursor is not a value this endpoint issued (00 §9.1).</summary>
    internal static Error CursorInvalid => Error.Invalid(
        "the cursor is not a value this endpoint issued",
        [new ErrorDetail("cursor", "malformed")]);

    /// <summary>422: <c>limit</c> is not an integer between 1 and 200 (00 §9.1).</summary>
    internal static Error LimitInvalid(int max) => Error.Invalid(
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"limit must be an integer between 1 and {max} (00 §9.1)"),
        [new ErrorDetail("limit", "out_of_range")]);

    /// <summary>
    /// 412: the caller's cached head is not the project's current head (00 §9.1, AC5). The current head revision id
    /// travels in <c>details</c> the way <c>CatalogErrors.Stale</c> carries the current version; a project with no
    /// revision yet carries no id.
    /// </summary>
    internal static Error Stale(Guid? currentHeadId) => new(
        ApiErrorCode.PreconditionFailed,
        "the project's policy head changed since it was read",
        StatusCodes.Status412PreconditionFailed,
        Retryable: false,
        Details: currentHeadId is { } head
            ? [new ErrorDetail("If-Match", "stale"), new ErrorDetail("revision_id", head.ToString("D"))]
            : [new ErrorDetail("If-Match", "stale")]);

    /// <summary>412: the <c>If-Match</c> header is not a revision id this endpoint issued (00 §9.1).</summary>
    internal static Error IfMatchInvalid => new(
        ApiErrorCode.PreconditionFailed,
        "If-Match must be the ETag this endpoint issued: a revision id (00 §9.1)",
        StatusCodes.Status412PreconditionFailed,
        Retryable: false,
        Details: [new ErrorDetail("If-Match", "invalid")]);
}

/// <summary>
/// The project half of the C2 scope rule for the policy routes: a credential narrowed to one project may touch only
/// that project, and the request's own project (header) must agree (the <c>CatalogScope.Project</c> precedent).
/// </summary>
internal static class PolicyScope
{
    /// <summary>Null when <paramref name="projectId"/> is inside every scope the request carries, else 403 <c>project_scope_mismatch</c>.</summary>
    internal static Error? Project(
        Guid projectId,
        IRequestPrincipalAccessor principals,
        IRequestTenantContextAccessor tenant)
    {
        if (principals.Current?.ProjectScope is { } tokenScope && tokenScope != projectId)
        {
            return PolicyErrors.ProjectScopeMismatch;
        }

        if (tenant.Current?.ProjectId is { } requestProject && requestProject != projectId)
        {
            return PolicyErrors.ProjectScopeMismatch;
        }

        return null;
    }
}

/// <summary>
/// The <c>If-Match</c> header of the policy routes (00 §9.1): the quoted revision id of the caller's cached head.
/// Absent means no check; anything that is not a revision id is refused as 412, never parsed as a page position.
/// </summary>
internal static class PolicyIfMatch
{
    /// <summary>True when <paramref name="raw"/> is a quoted (or weak-quoted) revision id.</summary>
    internal static bool TryParse(string? raw, out Guid revisionId)
    {
        revisionId = default;

        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        var value = raw.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        return Guid.TryParse(value.Trim('"'), out revisionId);
    }
}

/// <summary>
/// The schema-error paths of a policy body. Unlike the manifest's <c>JsonBody.SafePath</c>, the unknown member of an
/// <c>additionalProperties</c> error is kept: AC3 requires <c>details[].path</c> to point at the field itself.
/// </summary>
internal static class PolicyBodyPaths
{
    /// <summary>The reason token the schema engine reports for a member the closed schema does not declare.</summary>
    internal const string AdditionalProperties = "additional_properties";

    /// <summary>True when the verdict row is an unknown member of the closed <c>policy-revision.v1</c> schema.</summary>
    internal static bool IsUnknownField(ContractValidationError error) =>
        string.Equals(error.Reason, AdditionalProperties, StringComparison.Ordinal);

    /// <summary>The absolute JSON pointer of the failing member, capped at 200 characters.</summary>
    internal static string Of(ContractValidationError error) =>
        error.Path.Length > 200 ? error.Path[..200] : error.Path;
}

/// <summary>
/// The cheap pre-check of 07.1.1-T3 finding S1: the schema engine is the expensive path, so a body that is
/// obviously out of bounds is refused before it runs. Every array and every number of <c>policy-revision.v1</c> is
/// a top-level member, so the check is complete for this contract; a member the closed schema does not declare is
/// left to the schema, so it keeps its own code (AC3). One pass over the top-level members, no per-element
/// allocation beyond the enumeration itself.
/// </summary>
internal static class PolicyBodyBounds
{
    /// <summary>The <c>maxItems</c> of every array member of <c>policy-revision.v1</c> (contracts/schemas/policy-revision.v1.json).</summary>
    private static readonly Dictionary<string, int> MaxItems = new(StringComparer.Ordinal)
    {
        ["required_suites"] = 1000,
        ["required_cases"] = 1000,
        ["critical_cases"] = 1000,
        ["allowed_skips"] = 1000,
        ["required_artifacts"] = 100,
        ["quarantine_ids"] = 1000,
    };

    /// <summary>Longest number literal the canonical form can need (RFC 8785 §3.2.2.3 shortest round-trip double).</summary>
    internal const int MaxNumberLength = 32;

    /// <summary>Reason of an array past its member's <c>maxItems</c>.</summary>
    internal const string TooManyItemsReason = "too_many_items";

    /// <summary>Reason of a number literal longer than <see cref="MaxNumberLength"/>.</summary>
    internal const string NumberTooLongReason = "number_too_long";

    /// <summary>Null when every bounded array and every number is inside the schema's limits, else the 422.</summary>
    internal static Error? Check(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var member in root.EnumerateObject())
        {
            var value = member.Value;
            if (value.ValueKind == JsonValueKind.Array
                && MaxItems.TryGetValue(member.Name, out var max)
                && value.GetArrayLength() > max)
            {
                return PolicyErrors.PolicyInvalid(
                    "the body carries an array larger than policy-revision.v1 allows",
                    [new ErrorDetail("/" + member.Name, TooManyItemsReason)]);
            }

            if (value.ValueKind == JsonValueKind.Number && value.GetRawText().Length > MaxNumberLength)
            {
                return PolicyErrors.PolicyInvalid(
                    "the body carries a number literal longer than the canonical form allows",
                    [new ErrorDetail("/" + member.Name, NumberTooLongReason)]);
            }
        }

        return null;
    }
}
