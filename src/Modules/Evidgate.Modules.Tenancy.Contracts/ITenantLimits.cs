namespace Evidgate.Modules.Tenancy.Contracts;

/// <summary>
/// The tenant limit surface plan 12 reads (02.1.1-T3, feature plan §7): storage and a
/// read contract only, no metering. Both reads run in the caller's ambient tenant
/// scope, so RLS decides visibility: another org's project is null, never an error.
/// </summary>
public interface ITenantLimits
{
    /// <summary>The current tenant's org-level limits, or null when the org is not visible.</summary>
    ValueTask<OrganizationLimits?> ForOrganizationAsync(CancellationToken cancellationToken);

    /// <summary>The limits of one project of the current tenant, or null when it is not visible.</summary>
    ValueTask<ProjectLimits?> ForProjectAsync(Guid projectId, CancellationToken cancellationToken);
}

/// <summary>Org-level limits; a null value means "no limit set".</summary>
/// <param name="MaxProjects">Projects that are not <c>deleted</c> the org may hold.</param>
/// <param name="MaxSeats">Active memberships the org may hold.</param>
/// <param name="RetentionDefaultDays">Default retention for the org's projects.</param>
public sealed record OrganizationLimits(int? MaxProjects, int? MaxSeats, int? RetentionDefaultDays);

/// <summary>Project-level limits; a null value means "no limit set" or "inherit".</summary>
/// <param name="MonthlyRunQuota">Runs per calendar month.</param>
/// <param name="EffectiveRetentionDays">The project override when set, else the org default.</param>
public sealed record ProjectLimits(int? MonthlyRunQuota, int? EffectiveRetentionDays);
