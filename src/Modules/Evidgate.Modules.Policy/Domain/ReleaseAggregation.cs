using System.Text.Json.Serialization;

namespace Evidgate.Modules.Policy.Domain;

/// <summary>One bound run as the rule sees it: the binding's facts, in binding order, without any I/O type.</summary>
internal sealed class AggregationBinding
{
    /// <summary>Creates the value.</summary>
    public AggregationBinding(Guid runId, string componentKey, string purpose, string? runDigest)
    {
        RunId = runId;
        ComponentKey = componentKey;
        Purpose = purpose;
        RunDigest = runDigest;
    }

    /// <summary>RunId.</summary>
    public Guid RunId { get; }

    /// <summary>ComponentKey.</summary>
    public string ComponentKey { get; }

    /// <summary>Purpose.</summary>
    public string Purpose { get; }

    /// <summary>RunDigest.</summary>
    public string? RunDigest { get; }
}

/// <summary>The newest live verdict of one run.</summary>
internal sealed class AggregationVerdict
{
    /// <summary>Creates the value.</summary>
    public AggregationVerdict(Guid runId, Guid evaluationId, int evidenceRevision, Guid policyRevisionId, string decision)
    {
        RunId = runId;
        EvaluationId = evaluationId;
        EvidenceRevision = evidenceRevision;
        PolicyRevisionId = policyRevisionId;
        Decision = decision;
    }

    /// <summary>RunId.</summary>
    public Guid RunId { get; }

    /// <summary>EvaluationId.</summary>
    public Guid EvaluationId { get; }

    /// <summary>EvidenceRevision.</summary>
    public int EvidenceRevision { get; }

    /// <summary>PolicyRevisionId.</summary>
    public Guid PolicyRevisionId { get; }

    /// <summary>Decision.</summary>
    public string Decision { get; }
}

/// <summary>One component of the stored aggregate; the member names are the wire and the stored document's names.</summary>
internal sealed class ComponentEvaluation
{
    /// <summary>Creates the value.</summary>
    public ComponentEvaluation(string componentId, Guid? runId, int? evidenceRevision, string status, string? decision, IReadOnlyList<string> reasons)
    {
        ComponentId = componentId;
        RunId = runId;
        EvidenceRevision = evidenceRevision;
        Status = status;
        Decision = decision;
        Reasons = reasons;
    }

    /// <summary>ComponentId.</summary>
    [JsonPropertyName("component_id")]
    public string ComponentId { get; }

    /// <summary>RunId.</summary>
    [JsonPropertyName("run_id")]
    public Guid? RunId { get; }

    /// <summary>EvidenceRevision.</summary>
    [JsonPropertyName("evidence_revision")]
    public int? EvidenceRevision { get; }

    /// <summary>Status.</summary>
    [JsonPropertyName("status")]
    public string Status { get; }

    /// <summary>Decision.</summary>
    [JsonPropertyName("decision")]
    public string? Decision { get; }

    /// <summary>Reasons.</summary>
    [JsonPropertyName("reasons")]
    public IReadOnlyList<string> Reasons { get; }
}

/// <summary>The aggregate the rule computes for one release.</summary>
internal sealed class ReleaseAggregate
{
    /// <summary>Creates the value.</summary>
    public ReleaseAggregate(Guid? policyRevisionId, IReadOnlyList<ComponentEvaluation> components, string completeness, string decision, IReadOnlyList<string> reasons)
    {
        PolicyRevisionId = policyRevisionId;
        Components = components;
        Completeness = completeness;
        Decision = decision;
        Reasons = reasons;
    }

    /// <summary>PolicyRevisionId.</summary>
    public Guid? PolicyRevisionId { get; }

    /// <summary>Components.</summary>
    public IReadOnlyList<ComponentEvaluation> Components { get; }

    /// <summary>Completeness.</summary>
    public string Completeness { get; }

    /// <summary>Decision.</summary>
    public string Decision { get; }

    /// <summary>Reasons.</summary>
    public IReadOnlyList<string> Reasons { get; }
}

/// <summary>
/// The release-level rule, pure and deterministic. A component counts only through a binding of a non-replay run whose
/// recorded digest equals the digest the release declares; there is no fallback to another run. A missing component
/// never reads as a pass, a failing one fails the release whatever else is missing, and a release judged under more
/// than one policy revision is never a pass.
/// </summary>
internal static class ReleaseAggregationRules
{
    internal const string Evaluated = "evaluated";

    internal const string Missing = "missing";

    internal const string DigestMismatch = "component_digest_mismatch";

    internal const string RunUnbound = "component_run_unbound";

    internal const string NotEvaluated = "component_not_evaluated";

    /// <summary>The release holds more bindings than one aggregation reads.</summary>
    internal const string BoundExceeded = "aggregation_bound_exceeded";

    internal const string Failed = "component_failed";

    internal const string Inconclusive = "component_inconclusive";

    internal const string MixedPolicyRevisions = "mixed_policy_revisions";

    /// <summary>The aggregate of a release too large to read: every component is missing and nothing can pass.</summary>
    internal static ReleaseAggregate Exceeded(IReadOnlyList<(string Key, string Digest)> components)
    {
        ArgumentNullException.ThrowIfNull(components);

        return new ReleaseAggregate(
            null,
            [.. components.Select(c => Gap(c.Key, BoundExceeded))],
            "incomplete",
            "INCONCLUSIVE",
            [BoundExceeded]);
    }

    /// <summary>Aggregates one release.</summary>
    internal static ReleaseAggregate Aggregate(
        IReadOnlyList<(string Key, string Digest)> components,
        IReadOnlyList<AggregationBinding> bindings,
        IReadOnlyDictionary<Guid, AggregationVerdict> verdicts)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(verdicts);

        var evaluations = new List<ComponentEvaluation>(components.Count);
        // (binding position, verdict) of every evaluated component, to pick the policy revision deterministically.
        var judged = new List<(int Position, AggregationVerdict Verdict)>();

        foreach (var (key, digest) in components)
        {
            var (evaluation, position, verdict) = Component(key, digest, bindings, verdicts);
            evaluations.Add(evaluation);
            if (verdict is not null)
            {
                judged.Add((position, verdict));
            }
        }

        var reasons = new SortedSet<string>(evaluations.SelectMany(e => e.Reasons), StringComparer.Ordinal);
        var policyRevisions = judged.Select(j => j.Verdict.PolicyRevisionId).Distinct().ToList();
        if (policyRevisions.Count > 1)
        {
            reasons.Add(MixedPolicyRevisions);
        }

        // The newest binding with a verdict names the revision; the binding order is the stored (created_at, id) order.
        Guid? policyRevision = judged.Count == 0 ? null : judged.MaxBy(j => j.Position).Verdict.PolicyRevisionId;
        var complete = evaluations.Count > 0 && evaluations.All(e => e.Status == Evaluated);

        string decision;
        if (evaluations.Any(e => e.Decision == "FAIL"))
        {
            decision = "FAIL";
        }
        else if (complete && policyRevisions.Count <= 1 && evaluations.All(e => e.Decision == "PASS"))
        {
            decision = "PASS";
        }
        else
        {
            decision = "INCONCLUSIVE";
        }

        return new ReleaseAggregate(policyRevision, evaluations, complete ? "complete" : "incomplete", decision, [.. reasons]);
    }

    private static (ComponentEvaluation Evaluation, int Position, AggregationVerdict? Verdict) Component(
        string key,
        string digest,
        IReadOnlyList<AggregationBinding> bindings,
        IReadOnlyDictionary<Guid, AggregationVerdict> verdicts)
    {
        var usable = false;
        var chosen = -1;
        for (var i = 0; i < bindings.Count; i++)
        {
            var binding = bindings[i];
            if (!string.Equals(binding.ComponentKey, key, StringComparison.Ordinal)
                || string.Equals(binding.Purpose, "replay", StringComparison.Ordinal))
            {
                continue;
            }

            usable = true;
            if (string.Equals(binding.RunDigest, digest, StringComparison.Ordinal))
            {
                chosen = i;
            }
        }

        if (!usable)
        {
            return (Gap(key, RunUnbound), -1, null);
        }

        if (chosen < 0)
        {
            return (Gap(key, DigestMismatch), -1, null);
        }

        var run = bindings[chosen].RunId;
        if (!verdicts.TryGetValue(run, out var verdict))
        {
            return (new ComponentEvaluation(key, run, null, Missing, null, [NotEvaluated]), -1, null);
        }

        string[] reasons = verdict.Decision switch
        {
            "PASS" => [],
            "FAIL" => [Failed],
            _ => [Inconclusive],
        };

        return (new ComponentEvaluation(key, run, verdict.EvidenceRevision, Evaluated, verdict.Decision, reasons), chosen, verdict);
    }

    private static ComponentEvaluation Gap(string key, string reason) =>
        new(key, null, null, Missing, null, [reason]);
}
