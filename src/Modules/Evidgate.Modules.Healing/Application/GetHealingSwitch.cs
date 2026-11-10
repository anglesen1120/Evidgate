using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Healing.Contracts;
using Mediator;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// The switch as one scope stored it (<c>PUT</c> response and one member of the <c>GET</c> resolution, 08.6.3 §5).
/// </summary>
/// <param name="Scope"><c>system</c>, <c>tenant</c> or <c>project</c>.</param>
/// <param name="ProjectId">The project of a <c>project</c> row, null otherwise.</param>
/// <param name="Mode">The stored mode.</param>
/// <param name="Reason">The reason recorded with it, or null.</param>
/// <param name="SetBy">Who set it.</param>
/// <param name="At">When this scope's mode was last set.</param>
/// <param name="Version">The row's version.</param>
internal sealed record HealingSwitchView(
    string Scope,
    Guid? ProjectId,
    string Mode,
    string? Reason,
    Guid? SetBy,
    DateTimeOffset At,
    int Version)
{
    /// <summary>The wire shape of a stored row.</summary>
    /// <param name="row">The stored row.</param>
    internal static HealingSwitchView Of(HealingSwitchRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new HealingSwitchView(row.Scope, row.ProjectId, row.Mode, row.Reason, row.SetBy, row.At, row.Version);
    }

    /// <summary>
    /// The <c>system</c> row as a <em>tenant</em> read may see it: the mode (which governs the caller) and the
    /// row's <c>at</c>/<c>version</c>, but neither the operator's <c>set_by</c> nor the free-text <c>reason</c> —
    /// those are the platform operator's identity and incident notes, not something every tenant may read. The
    /// operator's own response to <c>POST /internal/operator/healing/switch</c> keeps both (it is the writer).
    /// </summary>
    /// <param name="row">The stored system row.</param>
    internal static HealingSwitchView Redacted(HealingSwitchRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new HealingSwitchView(row.Scope, row.ProjectId, row.Mode, Reason: null, SetBy: null, row.At, row.Version);
    }
}

/// <summary>
/// The dependency observations of the healing subsystem (08.6.3 §5): both fields are null until 08.6.3-T2 wires the
/// health probes, and null means "not observed" — never "healthy".
/// </summary>
/// <param name="Llm">The model endpoint's observation, null until T2.</param>
/// <param name="KnowledgeStore">The knowledge store's observation, null until T2.</param>
internal sealed record DependencyHealth(string? Llm, string? KnowledgeStore);

/// <summary>
/// The resolved switch (<c>GET /v1/healing/switch</c>, 08.6.3 §5, AC2): each scope's row (null when it has none), the
/// most restrictive mode in force and the dependency observations.
/// </summary>
/// <param name="System">The platform row, or null.</param>
/// <param name="Tenant">The org row, or null.</param>
/// <param name="Project">The project row, or null.</param>
/// <param name="Resolved">
/// The most restrictive stored mode clamped by the deployment's apply setting — <c>shadow</c> when a scope stored
/// <c>enabled</c> and healing may not apply here — or <c>shadow</c> when no scope has a row.
/// </param>
/// <param name="DependencyHealth">The observations; both null until T2.</param>
internal sealed record HealingSwitchResolutionView(
    HealingSwitchView? System,
    HealingSwitchView? Tenant,
    HealingSwitchView? Project,
    string Resolved,
    DependencyHealth DependencyHealth);

/// <summary>Read the healing switch in force (<c>GET /v1/healing/switch?project_id=</c>, 08.6.3 §5).</summary>
/// <param name="ProjectId">The project whose override applies, or null for the org-level answer.</param>
internal sealed record GetHealingSwitchQuery(Guid? ProjectId) : IQuery<Result<HealingSwitchResolutionView>>;

/// <summary>
/// Handler of <see cref="GetHealingSwitchQuery"/> (08.6.3 AC2/AC9): <c>healing:triage</c> reads the switch (00 §2 —
/// triage sees whether healing may act), a named project must be in scope and live (404 otherwise, like the waiver
/// list), and the answer is one SQL statement over at most three rows. Missing rows are null and contribute nothing;
/// with no row at any scope the resolution is <c>shadow</c>, the V1 posture of healing that never applies. The
/// <c>system</c> member is redacted: mode, <c>at</c> and <c>version</c> only, with <c>set_by</c> and <c>reason</c>
/// null, so the platform operator's identity and incident notes never reach a tenant read.
/// </summary>
[Permission(Permissions.Healing.Triage)]
internal sealed class GetHealingSwitchQueryHandler(
    HealingSwitchStore store,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IHealingApplyPolicy apply)
    : IQueryHandler<GetHealingSwitchQuery, Result<HealingSwitchResolutionView>>
{
    /// <inheritdoc />
    public async ValueTask<Result<HealingSwitchResolutionView>> Handle(GetHealingSwitchQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Healing, nameof(GetHealingSwitchQuery));

        if (query.ProjectId is { } named)
        {
            if (HealingScopeGuard.Project(named, principals, tenant) is { } mismatch)
            {
                return Result.Failure<HealingSwitchResolutionView>(mismatch);
            }

            if (!await store.ProjectExistsAsync(named, forShare: false, cancellationToken).ConfigureAwait(false))
            {
                return Result.Failure<HealingSwitchResolutionView>(
                    Error.NotFound("no live project with that id is visible to this tenant"));
            }
        }

        // AC9: one statement, at most the three scope rows.
        var rows = await store.ReadAsync(query.ProjectId, cancellationToken).ConfigureAwait(false);

        HealingSwitchView? system = null;
        HealingSwitchView? tenantRow = null;
        HealingSwitchView? projectRow = null;
        var modes = new List<HealingAvailability>(rows.Count);

        foreach (var row in rows)
        {
            if (!HealingAvailabilityModes.TryParse(row.Mode, out var mode))
            {
                // The CHECK constraint makes this unreachable; a stray value is dropped rather than guessed at.
                continue;
            }

            modes.Add(mode);
            switch (row.Scope)
            {
                case HealingSwitchScopes.System:
                    // The platform row governs every org, but its operator identity and reason are not the tenants'
                    // to read: mode/at/version only (SR-F2, decision (b)).
                    system = HealingSwitchView.Redacted(row);
                    break;
                case HealingSwitchScopes.Tenant:
                    tenantRow = HealingSwitchView.Of(row);
                    break;
                case HealingSwitchScopes.Project:
                    projectRow = HealingSwitchView.Of(row);
                    break;
                default:
                    break;
            }
        }

        return Result.Success(new HealingSwitchResolutionView(
            system,
            tenantRow,
            projectRow,
            HealingAvailabilityModes.ToWire(HealingApplyClamp.Apply(
                HealingAvailabilityResolution.Resolve(modes),
                apply.IsApplyEnabled)),
            new DependencyHealth(Llm: null, KnowledgeStore: null)));
    }
}
