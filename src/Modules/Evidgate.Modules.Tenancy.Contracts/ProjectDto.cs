namespace Evidgate.Modules.Tenancy.Contracts;

/// <summary>
/// Wire shape of a project (02.1.1-T1.3), read into by the Dapper queries of
/// <c>TenancyReadSql</c> and serialized snake_case by the API.
/// </summary>
/// <remarks>
/// Published as the <c>Project</c> schema of <c>contracts/openapi.yaml</c>
/// (02.1.1-T2); the field set is the feature plan §4 column list, and
/// <c>retention_days</c>/<c>monthly_run_quota</c> are 02.1.1-T3's limit columns.
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
/// <param name="RetentionDays">Overrides the org's <c>retention_default_days</c>; null = inherit (02.1.1-T3).</param>
/// <param name="MonthlyRunQuota">Runs per calendar month; null = no limit (02.1.1-T3, enforced by plan 12).</param>
/// <param name="CasePrefix">
/// The stored case-code prefix (<c>&lt;PREFIX&gt;-&lt;n&gt;</c> of every manual test case, 13.1.1 AC4/OI-13-101), filled by
/// the <c>project_case_prefix_default</c> trigger on insert and changed only through <c>PATCH /v1/projects/{p}</c> by a
/// <c>tenant_admin</c>. A change never rewrites an existing case code.
/// </param>
public sealed record ProjectDto(
    Guid Id,
    Guid OrgId,
    string Name,
    string Slug,
    string Status,
    Guid? DefaultEnvironmentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version,
    int? RetentionDays,
    int? MonthlyRunQuota,
    string CasePrefix);
