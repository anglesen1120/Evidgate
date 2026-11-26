using System.Text.Json;

namespace Evidgate.Modules.Healing.Contracts;

/// <summary>The work announced when a required execution first becomes an incident.</summary>
public static class IncidentCreated
{
    /// <summary>Stable task and audit action name.</summary>
    public const string Action = "incident.created";

    /// <summary>Identifier-only payload for consumers, which re-read in their own tenant transaction.</summary>
    /// <param name="orgId">The tenant of the incident.</param>
    /// <param name="projectId">The run's project.</param>
    /// <param name="runId">The run with the verified failure.</param>
    /// <param name="incidentId">The new incident.</param>
    public static string Payload(Guid orgId, Guid projectId, Guid runId, Guid incidentId) =>
        JsonSerializer.Serialize(new { org_id = orgId, project_id = projectId, run_id = runId, incident_id = incidentId });
}
