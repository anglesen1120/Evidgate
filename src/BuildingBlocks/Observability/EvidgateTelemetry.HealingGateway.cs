using System.Diagnostics.Metrics;

namespace Evidgate.BuildingBlocks.Observability;

/// <summary>
/// The healing gateway's refusal instrument, kept in its own file so the metric vocabulary stays readable: a call the
/// kill switch, the provider selection or the project's budget stopped before any provider was reached, which an
/// operator alerts on as a rate rather than on one tenant's calls.
/// </summary>
public static partial class EvidgateTelemetry
{
    /// <summary>
    /// Counter of healing LLM calls refused before a provider call, tagged by <c>reason</c> from a closed set
    /// (<c>provider_disabled</c>, <c>healing_disabled</c>, <c>budget_exceeded</c>, <c>budget_not_provisioned</c>,
    /// <c>budget_unreadable</c>). Low cardinality only: never an org id, a project id, an incident or candidate id, a
    /// token count or any request value.
    /// </summary>
    public const string HealingLlmRefusedMetricName = "evidgate_healing_llm_refused_total";

    private static readonly Counter<long> HealingLlmRefused = Meter.CreateCounter<long>(
        HealingLlmRefusedMetricName,
        unit: "{call}",
        description: "Healing LLM calls refused before any provider call, by reason. Never labelled with an org or project id, an incident or candidate id, or any request value.");

    /// <summary>
    /// Counts one refused healing call, tagged by <paramref name="reason"/> from the closed set only — never an org,
    /// project, incident or candidate id.
    /// </summary>
    /// <param name="reason">
    /// <c>provider_disabled</c>, <c>healing_disabled</c>, <c>budget_exceeded</c>, <c>budget_not_provisioned</c> or
    /// <c>budget_unreadable</c>.
    /// </param>
    public static void RecordHealingLlmRefused(string reason) =>
        HealingLlmRefused.Add(1, new KeyValuePair<string, object?>("reason", reason));
}
