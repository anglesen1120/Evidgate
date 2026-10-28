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
    /// <summary>The configuration section the options bind from.</summary>
    public const string SectionName = "Healing";

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
/// The one place the switch body's mode and reason are judged (08.6.3 §3, AC1/AC2/AC7), shared by the tenant route
/// and the operator's system-scope writer so the two can never drift: the mode is one of the three storable values
/// (<c>unavailable</c> and every other token is 422 <c>invalid_switch_mode</c>), <c>enabled</c> is 422
/// <c>mode_not_available</c> while <c>Healing:Apply</c> is off, and the reason is 1–2000 storable characters when
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

        if (reason.Length == 0)
        {
            return Error.Invalid("the switch request is not valid", [new ErrorDetail("reason", "empty")]);
        }

        if (reason.Length > ReasonMaxLength)
        {
            return Error.Invalid("the switch request is not valid", [new ErrorDetail("reason", "too_long")]);
        }

        return reason.Contains('\0')
            ? Error.Invalid("the switch request is not valid", [new ErrorDetail("reason", "invalid_value")])
            : null;
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
    /// The operator's system-scope write (migration 0052's definer): the one path to the tenant-less row. The casts
    /// carry the NULLs Npgsql cannot type.
    /// </summary>
    internal const string SetSystem = """
        select mode, reason, set_by, at, version
          from evidgate.healing_switch_set_system(@mode::text, @reason::text, @set_by::uuid)
        """;

    /// <summary>The live-project check of a <c>project</c> scope, under RLS (another tenant's project reads nothing).</summary>
    internal const string ProjectExists = """
        select 1 from public.project where id = @project_id::uuid
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

    /// <summary>Whether <paramref name="projectId"/> is a project of the caller's org (404 when it is not).</summary>
    /// <param name="projectId">The project named by the body or query.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<bool> ProjectExistsAsync(Guid projectId, CancellationToken cancellationToken) =>
        await db.ScalarAsync<int?>(HealingSwitchSql.ProjectExists, new { project_id = projectId }, cancellationToken)
            .ConfigureAwait(false) is not null;
}

/// <summary>
/// The <see cref="IHealingAvailabilityResolver"/> over the store: read the scopes that carry a row in one statement,
/// parse them, and let <see cref="HealingAvailabilityResolution"/> answer the most restrictive one. No health probe
/// is consulted (T2 owns that), so the answer is never <c>unavailable</c> in this story.
/// </summary>
internal sealed class HealingAvailabilityResolver(HealingSwitchStore store) : IHealingAvailabilityResolver
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

        return HealingAvailabilityResolution.Resolve(modes);
    }
}
