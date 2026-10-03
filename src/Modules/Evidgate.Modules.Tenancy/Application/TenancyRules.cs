using System.Globalization;
using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Evidgate.Modules.Tenancy.Application;

/// <summary>
/// The input rules shared by the org and project commands (02.1.1 §4, 00 §5.7 S3):
/// the D-A1 slug grammar, name and list limits. The migrations hold the same checks;
/// these exist so a bad value is a 422 with a field path, not a 23514 turned into 500.
/// </summary>
internal static partial class TenancyRules
{
    /// <summary>DECISIONS §8.1 D-A1, identical to the 0012 check constraints.</summary>
    internal const string SlugPattern = "^[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$";

    /// <summary>Name length cap of 0004/0005 (<c>char_length(name) between 1 and 128</c>).</summary>
    internal const int MaxNameLength = 128;

    /// <summary><c>organization_email_domains_check</c> (0004): at most 64 entries.</summary>
    internal const int MaxEmailDomains = 64;

    /// <summary>A DNS name is at most 253 characters (RFC 1035 §2.3.4).</summary>
    internal const int MaxDomainLength = 253;

    /// <summary>Upper bound of a retention period in days (ten years; plan 06/10 enforce the real policy).</summary>
    internal const int MaxRetentionDays = 3650;

    /// <summary>True when <paramref name="slug"/> matches the D-A1 grammar.</summary>
    internal static bool IsSlug(string? slug) => slug is not null && Slug().IsMatch(slug);

    /// <summary>True when <paramref name="name"/> is 1–128 characters after trimming.</summary>
    internal static bool IsName(string? name) =>
        name is not null && name.Trim().Length is > 0 and <= MaxNameLength;

    /// <summary>A lowercase DNS name with at least one dot (an allowlist entry, FR-02-3).</summary>
    internal static bool IsDomain(string? domain) =>
        domain is not null && domain.Length <= MaxDomainLength && Domain().IsMatch(domain);

    [GeneratedRegex(SlugPattern, RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Slug();

    [GeneratedRegex(
        "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex Domain();
}

/// <summary>The 00 §9.2 errors of the registry routes, one definition each.</summary>
internal static class TenancyErrors
{
    /// <summary>The route's <c>{o}</c> is not the request's org: indistinguishable from a missing org (AC3).</summary>
    internal static Error OrgNotFound => Error.NotFound("no organization with that id is visible to this tenant");

    /// <summary>The project is not visible to this tenant: another org's id is the same 404 (AC3).</summary>
    internal static Error ProjectNotFound => Error.NotFound("no project with that id is visible to this tenant");

    /// <summary>A unique slug key refused the write (AC2).</summary>
    internal static Error SlugTaken => Error.Conflict(ApiErrorCode.SlugTaken, "that slug is already taken");

    /// <summary>Mutations of a suspended org's registry are refused (02.1.1 §6).</summary>
    internal static Error OrgSuspended => Error.Conflict(
        ApiErrorCode.OrgSuspended,
        "the organization is suspended; its registry cannot change until it is active again");

    /// <summary>A token scoped to another project addressed this one (AC4, 00 §9.1).</summary>
    internal static Error ProjectScopeMismatch => Error.Denied(
        ApiErrorCode.ProjectScopeMismatch,
        "the credential is scoped to a different project");

    /// <summary>
    /// The 412 of a stale <c>If-Match</c> (AC5): <c>error.details</c> carries the
    /// row's current version so the client can re-read and retry.
    /// </summary>
    internal static Error Stale(string what, int currentVersion) => new(
        ApiErrorCode.PreconditionFailed,
        string.Create(CultureInfo.InvariantCulture, $"the {what} changed since it was read (current version {currentVersion})"),
        StatusCodes.Status412PreconditionFailed,
        Retryable: false,
        Details:
        [
            new ErrorDetail("If-Match", "stale"),
            new ErrorDetail("version", currentVersion.ToString(CultureInfo.InvariantCulture)),
        ]);

    /// <summary>The 429 of a reached tenant limit (02.1.1-T3.2).</summary>
    internal static Error QuotaExceeded(string limit, int value) => new(
        ApiErrorCode.QuotaExceeded,
        string.Create(CultureInfo.InvariantCulture, $"the organization's {limit} limit ({value}) is reached"),
        StatusCodes.Status429TooManyRequests,
        Retryable: false,
        Details: [new ErrorDetail(limit, "reached")]);

    /// <summary>True when the write failed on one of the registry's slug unique keys.</summary>
    internal static bool IsSlugConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "organization_slug_key" or "project_org_slug_key",
        };
}

/// <summary>
/// The scope half of 02.1.1-T2.1: the org comes from the request (never the URL
/// alone), and a credential narrowed to one project may address only that project
/// (AC4). The C2 evaluator compares the token scope with the request's project; this
/// guard compares both with the project the route names.
/// </summary>
internal static class ScopeGuard
{
    /// <summary>
    /// Null when <paramref name="projectId"/> is inside every scope the request
    /// carries, else the 403 <c>project_scope_mismatch</c>.
    /// </summary>
    internal static Error? Project(
        Guid projectId,
        IRequestPrincipalAccessor principals,
        IRequestTenantContextAccessor tenant)
    {
        if (principals.Current?.ProjectScope is { } tokenScope && tokenScope != projectId)
        {
            return TenancyErrors.ProjectScopeMismatch;
        }

        if (tenant.Current?.ProjectId is { } requestProject && requestProject != projectId)
        {
            return TenancyErrors.ProjectScopeMismatch;
        }

        return null;
    }
}
