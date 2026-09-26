namespace Evidgate.Modules.Tenancy.Contracts;

/// <summary>
/// Wire shape of an organization (02.1.1-T1.3), read into by the Dapper queries
/// of <c>TenancyReadSql</c> and serialized snake_case by the API.
/// </summary>
/// <remarks>
/// <c>contracts/openapi.yaml</c> carries only the <c>/orgs/{o}/projects</c> path
/// and no <c>Organization</c> component schema yet, so this DTO follows the field
/// list of the feature plan §4 instead of a published schema; <c>DtoContractTests</c>
/// pins that shape and the gap is recorded for the 02.1.1-T2 endpoint work, which
/// owns adding the schema (this task must not edit <c>openapi.yaml</c>). The
/// quota/limit columns of 02.1.1-T3 are deliberately absent: they are the
/// <c>ITenantLimits</c> surface.
/// </remarks>
/// <param name="Id">Organization id (UUIDv7, 00 §7).</param>
/// <param name="Name">Display name, ≤ 128 chars.</param>
/// <param name="Slug">Globally unique slug, <c>^[a-z0-9][a-z0-9-]{1,62}$</c>.</param>
/// <param name="Status">One of <c>active</c>, <c>suspended</c>, <c>deleted</c> (00 §8.9).</param>
/// <param name="RetentionDefaultDays">Default retention for the org's projects; null when unset.</param>
/// <param name="AllowedEmailDomains">Optional Google Workspace domain allowlist (FR-02-3); null when unset.</param>
/// <param name="CreatedAt">Row creation, UTC.</param>
/// <param name="UpdatedAt">Last write, UTC.</param>
/// <param name="Version">Optimistic concurrency token (ETag source, 00 §9.1).</param>
public sealed record OrganizationDto(
    Guid Id,
    string Name,
    string Slug,
    string Status,
    int? RetentionDefaultDays,
    string[]? AllowedEmailDomains,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version);
