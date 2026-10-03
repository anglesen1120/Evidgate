namespace Evidgate.Modules.Tenancy.Contracts;

/// <summary>
/// Wire shape of an organization (02.1.1-T1.3), read into by the Dapper queries
/// of <c>TenancyReadSql</c> and serialized snake_case by the API.
/// </summary>
/// <remarks>
/// Published as the <c>Organization</c> schema of <c>contracts/openapi.yaml</c>
/// (02.1.1-T2); the field set is the feature plan §4 column list, so
/// <c>OrgProjectSchemaTests.DtoFieldsMatchSchemaColumns</c> keeps the two equal,
/// including 02.1.1-T3's limit columns (also read through <c>ITenantLimits</c>).
/// </remarks>
/// <param name="Id">Organization id (UUIDv7, 00 §7).</param>
/// <param name="Name">Display name, ≤ 128 chars.</param>
/// <param name="Slug">Globally unique slug, <c>^[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$</c> (D-A1).</param>
/// <param name="Status">One of <c>active</c>, <c>suspended</c>, <c>deleted</c> (00 §8.9).</param>
/// <param name="RetentionDefaultDays">Default retention for the org's projects; null when unset.</param>
/// <param name="AllowedEmailDomains">Optional Google Workspace domain allowlist (FR-02-3); null when unset.</param>
/// <param name="CreatedAt">Row creation, UTC.</param>
/// <param name="UpdatedAt">Last write, UTC.</param>
/// <param name="Version">Optimistic concurrency token (ETag source, 00 §9.1).</param>
/// <param name="MaxProjects">Projects the org may hold that are not <c>deleted</c>; null = no limit (02.1.1-T3).</param>
/// <param name="MaxSeats">Active memberships the org may hold; null = no limit (02.1.1-T3, enforced by plan 12).</param>
public sealed record OrganizationDto(
    Guid Id,
    string Name,
    string Slug,
    string Status,
    int? RetentionDefaultDays,
    string[]? AllowedEmailDomains,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version,
    int? MaxProjects,
    int? MaxSeats);
