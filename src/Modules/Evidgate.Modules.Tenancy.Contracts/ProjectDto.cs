namespace Evidgate.Modules.Tenancy.Contracts;

/// <summary>
/// Wire shape of a project (02.1.1-T1.3), read into by the Dapper queries of
/// <c>TenancyReadSql</c> and serialized snake_case by the API.
/// </summary>
/// <remarks>
/// <c>contracts/openapi.yaml</c> describes the create response as
/// <c>{project_id}</c> only and has no <c>Project</c> component schema, so this
/// DTO follows the field list of the feature plan §4; the same note as
/// <see cref="OrganizationDto"/> applies. <c>retention_days</c> and
/// <c>monthly_run_quota</c> belong to 02.1.1-T3's <c>ITenantLimits</c>.
/// </remarks>
/// <param name="Id">Project id (UUIDv7); globally unique, addressed by <c>/v1/projects/{p}</c>.</param>
/// <param name="OrgId">Owning organization (composite tenant key).</param>
/// <param name="Name">Display name, ≤ 128 chars.</param>
/// <param name="Slug">Slug unique per org (<c>unique (org_id, slug)</c> → 409 <c>slug_taken</c>).</param>
/// <param name="Status">One of <c>active</c>, <c>suspended</c>, <c>deleted</c> (00 §8.9).</param>
/// <param name="DefaultEnvironmentId">Preferred environment for new runs; null until 05.1.2-T1 adds the FK.</param>
/// <param name="CreatedAt">Row creation, UTC.</param>
/// <param name="UpdatedAt">Last write, UTC.</param>
/// <param name="Version">Optimistic concurrency token (ETag source, 00 §9.1).</param>
public sealed record ProjectDto(
    Guid Id,
    Guid OrgId,
    string Name,
    string Slug,
    string Status,
    Guid? DefaultEnvironmentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version);
