using System.Diagnostics.Metrics;

namespace Evidgate.BuildingBlocks.Observability;

/// <summary>
/// The incident recorder's creation counter, kept in its own file so the metric vocabulary stays readable: how many
/// incidents the worker opened from verified failures, which an operator watches as a rate.
/// </summary>
public static partial class EvidgateTelemetry
{
    /// <summary>
    /// Counter of incidents created from a frozen evidence revision. It carries no tag at all: never an org, project,
    /// run or incident id, an attempt payload or an error text.
    /// </summary>
    public const string IncidentCreatedMetricName = "evidgate_incident_created_total";

    private static readonly Counter<long> IncidentCreated = EvidgateMeter.Instance.CreateCounter<long>(
        IncidentCreatedMetricName,
        unit: "{incident}",
        description: "Incidents created from a frozen evidence revision, counted once per new incident after its transaction commits. Carries no label.");

    /// <summary>Counts newly created incidents; a repeat that attaches to existing incidents adds nothing.</summary>
    /// <param name="created">How many incidents the committed transaction inserted.</param>
    public static void RecordIncidentCreated(int created)
    {
        if (created > 0)
        {
            IncidentCreated.Add(created);
        }
    }
}
