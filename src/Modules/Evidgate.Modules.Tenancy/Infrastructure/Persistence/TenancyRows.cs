namespace Evidgate.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>One row of <see cref="TenancyReadSql.ProjectCreateGate"/> (02.1.1-T3.2).</summary>
/// <param name="Status">The org's status.</param>
/// <param name="MaxProjects">The org's project limit, null when none is set.</param>
internal sealed record ProjectCreateGateRow(string Status, int? MaxProjects);
