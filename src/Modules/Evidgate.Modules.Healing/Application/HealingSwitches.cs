using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// The module's configuration section (08.6.3-T1 AC7): <c>Healing:Apply</c>, the V1 clamp. Absent in every shipped
/// configuration, so a deployment answers <c>false</c> until an operator turns healing on.
/// </summary>
internal sealed class HealingOptions
{
    /// <summary>
    /// The configuration section the options bind from — <c>EVIDGATE:Healing</c>, the deployment prefix every module
    /// section carries (the environment spelling is <c>EVIDGATE__HEALING__APPLY</c>, D-S124-Q3), so the documented key
    /// really is the key that flips the clamp.
    /// </summary>
    public const string SectionName = "EVIDGATE:Healing";

    /// <summary>
    /// Whether healing may be <em>enabled</em> (08.6.3 §3, T33): <c>false</c> (the default) keeps every scope at
    /// <c>shadow</c> or <c>disabled</c> and answers 422 <c>mode_not_available</c> for <c>enabled</c>.
    /// </summary>
    public bool Apply { get; set; }
}

/// <summary>
/// The injectable read of the V1 clamp (08.6.3-T1 AC7): a seam of its own so a test flips one flag through the host
/// configuration instead of re-composing the module, and so the handler never reads <c>IConfiguration</c> itself.
/// </summary>
internal interface IHealingApplyPolicy
{
    /// <summary>Whether this deployment may run healing in <c>enabled</c>.</summary>
    bool IsApplyEnabled { get; }
}

/// <summary>The <see cref="IHealingApplyPolicy"/> over the bound <see cref="HealingOptions"/> (re-read on change).</summary>
internal sealed class HealingApplyPolicy(IOptionsMonitor<HealingOptions> options) : IHealingApplyPolicy
{
    /// <inheritdoc />
    public bool IsApplyEnabled => options.CurrentValue.Apply;
}

/// <summary>
/// The clamp on a resolved mode: a deployment that may not apply healing reports <c>shadow</c> even when a scope stored
/// <c>enabled</c>, so no reader of the resolution is told healing may act here. Only the resolved answer is clamped —
/// the stored rows stay exactly as they were written, and the read of each scope still reports what it holds.
/// </summary>
internal static class HealingApplyClamp
{
    /// <summary>The mode in force once the deployment's apply setting is taken into account.</summary>
    /// <param name="resolved">The most restrictive stored mode.</param>
    /// <param name="applyEnabled">Whether this deployment may run healing in <c>enabled</c>.</param>
    internal static HealingAvailability Apply(HealingAvailability resolved, bool applyEnabled) =>
        resolved == HealingAvailability.Enabled && !applyEnabled ? HealingAvailability.Shadow : resolved;
}

/// <summary>
/// The one place the switch body's mode and reason are judged (08.6.3 §3, AC1/AC2/AC7), shared by the tenant route
/// and the operator's system-scope writer so the two can never drift: the mode is one of the three storable values
/// (<c>unavailable</c> and every other token is 422 <c>invalid_switch_mode</c>), <c>enabled</c> is 422
/// <c>mode_not_available</c> while <c>Healing:Apply</c> is off, and the reason is 1–2000 Unicode code points when
/// present.
/// </summary>
internal static class HealingModeGate
{
    /// <summary>The wire bound of the column (<c>healing_switch_reason_length_check</c>, migration 0052).</summary>
    internal const int ReasonMaxLength = 2000;

    /// <summary>Null when the pair is acceptable, else the 422 the route answers.</summary>
    /// <param name="mode">The requested mode token.</param>
    /// <param name="reason">The optional reason text.</param>
    /// <param name="applyEnabled">Whether the deployment may enable healing (<c>Healing:Apply</c>).</param>
    internal static Error? Check(string? mode, string? reason, bool applyEnabled)
    {
        if (!HealingAvailabilityModes.IsStorable(mode))
        {
            return Error.Unprocessable(
                ApiErrorCode.InvalidSwitchMode,
                "the mode must be one of enabled, shadow or disabled",
                [new ErrorDetail("mode", mode is null ? "required" : "unknown")]);
        }

        if (mode == HealingAvailabilityModes.Enabled && !applyEnabled)
        {
            return Error.Unprocessable(
                ApiErrorCode.ModeNotAvailable,
                "healing may not be enabled: this deployment runs healing in shadow (Healing:Apply is off)",
                [new ErrorDetail("mode", "not_available")]);
        }

        if (reason is null)
        {
            return null;
        }

        // The route's schema already refuses `""` (`minLength: 1`), so on the HTTP path this branch is defence in
        // depth for a direct caller of the gate (the operator writer, a unit test): an empty reason is never stored.
        if (reason.Length == 0)
        {
            return Error.Invalid("the switch request is not valid", [new ErrorDetail("reason", "empty")]);
        }

        // Counted in code points, the unit the column's own `length(reason)` check uses (QuarantineSupport's
        // precedent): `string.Length` is UTF-16 units, so "😀" would otherwise cost 2 and a 1001-emoji reason —
        // 1001 characters to PostgreSQL — would be refused here for a bound the database does not enforce.
        if (LongerThan(reason, ReasonMaxLength))
        {
            return Error.Invalid("the switch request is not valid", [new ErrorDetail("reason", "too_long")]);
        }

        return reason.Contains('\0')
            ? Error.Invalid("the switch request is not valid", [new ErrorDetail("reason", "invalid_value")])
            : null;
    }

    /// <summary>True when <paramref name="text"/> holds more than <paramref name="maximum"/> Unicode code points.</summary>
    /// <param name="text">The reason text.</param>
    /// <param name="maximum">The largest acceptable code-point count.</param>
    private static bool LongerThan(string text, int maximum)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            if (++count > maximum)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The project-scope rule of the switch routes (08.6.3 §3), the same two members the waiver routes apply: a
/// credential scoped to another project, or a request already narrowed to another project, is 403
/// <c>project_scope_mismatch</c> before any row is read.
/// </summary>
internal static class HealingScopeGuard
{
    /// <summary>Null when <paramref name="projectId"/> is inside every scope the request carries.</summary>
    /// <param name="projectId">The project the body or query names.</param>
    /// <param name="principals">The resolved principal.</param>
    /// <param name="tenant">The request's tenant context.</param>
    internal static Error? Project(
        Guid projectId,
        IRequestPrincipalAccessor principals,
        IRequestTenantContextAccessor tenant)
    {
        if (principals.Current?.ProjectScope is { } tokenScope && tokenScope != projectId)
        {
            return Error.Denied(ApiErrorCode.ProjectScopeMismatch, "the project is outside the credential's scope");
        }

        return tenant.Current?.ProjectId is { } requestProject && requestProject != projectId
            ? Error.Denied(ApiErrorCode.ProjectScopeMismatch, "the project is outside the request's scope")
            : null;
    }
}

/// <summary>One stored switch row, as both the upsert and the resolution read project it.</summary>
/// <param name="Id">The row's surrogate id (the outbox aggregate id of a change).</param>
/// <param name="Scope"><c>system</c>, <c>tenant</c> or <c>project</c>.</param>
/// <param name="ProjectId">The project of a <c>project</c> row, null on the other two.</param>
/// <param name="Mode">One of the three storable modes.</param>
/// <param name="Reason">The reason recorded with the change, or null.</param>
/// <param name="SetBy">The member (or platform principal) who set it.</param>
/// <param name="At">When this scope's mode was last set.</param>
/// <param name="Version">The row's version, bumped by the shared trigger on every write.</param>
internal sealed record HealingSwitchRow(
    Guid Id,
    string Scope,
    Guid? ProjectId,
    string Mode,
    string? Reason,
    Guid SetBy,
    DateTimeOffset At,
    int Version);

/// <summary>
/// The Dapper statements of the switch (00 §5.5 C5, S1). Every one is a single parameterised statement: the upsert
/// names only the columns the app role holds INSERT on (migration 0052) and relies on the shared
/// <c>set_updated_at_and_version</c> trigger for <c>updated_at</c> and <c>version</c>; the resolution read is one
/// statement over the three partial unique indexes (AC9) and never writes. Nullable parameters carry an explicit
/// cast, because Npgsql cannot infer a type for a NULL literal.
/// </summary>
internal static class HealingSwitchSql
{
    private const string Returning = "returning id, scope, project_id, mode, reason, set_by, at, version";

    /// <summary>
    /// The resolution read: the platform row every tenant inherits plus this org's tenant and project rows. RLS
    /// already hides other orgs' rows (<c>healing_switch_app_read</c>); the explicit <c>org_id</c> predicates are
    /// what make the partial unique indexes the access path.
    /// </summary>
    internal const string Resolution = """
        select id, scope, project_id, mode, reason, set_by, at, version
          from public.healing_switch
         where scope = 'system'
            or (scope = 'tenant' and org_id = @org_id::uuid)
            or (scope = 'project' and org_id = @org_id::uuid and project_id = @project_id::uuid)
        """;

    /// <summary>The org-level row: one per org (<c>healing_switch_tenant_key</c>), upserted in place.</summary>
    internal const string UpsertTenant = """
        insert into public.healing_switch (id, scope, org_id, project_id, mode, reason, set_by, at)
        values (@id, 'tenant', @org_id::uuid, null, @mode::text, @reason::text, @set_by::uuid, now())
        on conflict (org_id) where scope = 'tenant'
        do update set mode = excluded.mode, reason = excluded.reason, set_by = excluded.set_by, at = now()
        """ + " " + Returning;

    /// <summary>The project override: one per project (<c>healing_switch_project_key</c>), upserted in place.</summary>
    internal const string UpsertProject = """
        insert into public.healing_switch (id, scope, org_id, project_id, mode, reason, set_by, at)
        values (@id, 'project', @org_id::uuid, @project_id::uuid, @mode::text, @reason::text, @set_by::uuid, now())
        on conflict (org_id, project_id) where scope = 'project'
        do update set mode = excluded.mode, reason = excluded.reason, set_by = excluded.set_by, at = now()
        """ + " " + Returning;

    /// <summary>
    /// The live-project check of a <c>project</c> scope, under RLS (another tenant's project reads nothing). A
    /// soft-deleted project is not live: the row's <c>status</c> is the registry's tombstone, so it reads no row and
    /// the route answers the same 404 an unknown id gets (like <c>WaiverSupport.ProjectExists</c>).
    /// </summary>
    internal const string ProjectExists = """
        select 1 from public.project where id = @project_id::uuid and status <> 'deleted'
        """;

    /// <summary>
    /// The same live-project check, taking the row <c>FOR SHARE</c> for the write path: the upsert's check and its
    /// insert run under this lock, so a concurrent soft delete (the registry's <c>status</c> update) cannot retire the
    /// project between the check and the row that would then point at it. <c>evidgate_app</c> holds table-level
    /// <c>UPDATE</c> on <c>public.project</c> (0005), so the lock is permitted — the same posture as
    /// <c>QuarantineSupport.LockProjectForShare</c>.
    /// </summary>
    internal const string ProjectExistsForShare = """
        select 1
          from public.project
         where id = @project_id::uuid
           and status <> 'deleted'
           for share
        """;
}

/// <summary>
/// The module's only data path (00 §5.5 C5): the tenant/project upsert inside the caller's tenant transaction and the
/// one-statement resolution read (AC9). RLS is the boundary — every statement runs on the scope's connection, so
/// another org's rows are invisible and a NULL-<c>org_id</c> row can never be written here.
/// </summary>
internal sealed class HealingSwitchStore(ITenantDb db, ITenantScopeAccessor scopes)
{
    /// <summary>
    /// Upserts the tenant or project row of the caller's org and returns it as the database stored it (the trigger's
    /// version included).
    /// </summary>
    /// <param name="scope">The scope of the row: <c>tenant</c> or <c>project</c>.</param>
    /// <param name="projectId">The project of a <c>project</c> row; null for the tenant row.</param>
    /// <param name="mode">The validated storable mode.</param>
    /// <param name="reason">The validated reason, or null.</param>
    /// <param name="setBy">The member who set it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<HealingSwitchRow> UpsertAsync(
        string scope,
        Guid? projectId,
        string mode,
        string? reason,
        Guid setBy,
        CancellationToken cancellationToken)
    {
        var sql = projectId is null ? HealingSwitchSql.UpsertTenant : HealingSwitchSql.UpsertProject;

        var row = await db.QuerySingleOrDefaultAsync<HealingSwitchRow>(
            sql,
            new
            {
                id = Guid.CreateVersion7(),
                org_id = scopes.Require().OrgId,
                project_id = projectId,
                mode,
                reason,
                set_by = setBy,
            },
            cancellationToken).ConfigureAwait(false);

        return row ?? throw new InvalidOperationException("the healing switch upsert returned no row");
    }

    /// <summary>The platform row plus the org's tenant and project rows: one statement, at most three rows.</summary>
    /// <param name="projectId">The project whose override applies, or null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal Task<IReadOnlyList<HealingSwitchRow>> ReadAsync(Guid? projectId, CancellationToken cancellationToken) =>
        db.QueryAsync<HealingSwitchRow>(
            HealingSwitchSql.Resolution,
            new { org_id = scopes.Require().OrgId, project_id = projectId },
            cancellationToken);

    /// <summary>
    /// Whether <paramref name="projectId"/> is a live project of the caller's org (404 when it is not). The write path
    /// asks for the row <c>FOR SHARE</c> so a concurrent soft delete cannot land between this check and the upsert.
    /// </summary>
    /// <param name="projectId">The project named by the body or query.</param>
    /// <param name="forShare">Whether this is the write path, which locks the project row.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<bool> ProjectExistsAsync(Guid projectId, bool forShare, CancellationToken cancellationToken) =>
        await db.ScalarAsync<int?>(
                forShare ? HealingSwitchSql.ProjectExistsForShare : HealingSwitchSql.ProjectExists,
                new { project_id = projectId },
                cancellationToken)
            .ConfigureAwait(false) is not null;
}

/// <summary>
/// The <see cref="IHealingAvailabilityResolver"/> over the store: read the scopes that carry a row in one statement,
/// parse them, and let <see cref="HealingAvailabilityResolution"/> answer the most restrictive one. No health probe
/// is consulted (T2 owns that), so the answer is never <c>unavailable</c> in this story.
/// </summary>
internal sealed class HealingAvailabilityResolver(HealingSwitchStore store, IHealingApplyPolicy apply)
    : IHealingAvailabilityResolver
{
    /// <inheritdoc />
    public async ValueTask<HealingAvailability> ResolveAsync(Guid? projectId, CancellationToken cancellationToken)
    {
        var rows = await store.ReadAsync(projectId, cancellationToken).ConfigureAwait(false);

        var modes = new List<HealingAvailability>(rows.Count);
        foreach (var row in rows)
        {
            // A row the CHECK constraint accepted always parses; a value that does not is not guessed at, it is
            // dropped from the resolution, because inventing a stricter mode would silently disable healing.
            if (HealingAvailabilityModes.TryParse(row.Mode, out var mode))
            {
                modes.Add(mode);
            }
        }

        // The most restrictive stored row decides; the deployment's apply setting then holds it at shadow, so "enabled"
        // is never reported by a deployment that may not act on it.
        return HealingApplyClamp.Apply(
            HealingAvailabilityResolution.Resolve(modes),
            apply.IsApplyEnabled);
    }
}
