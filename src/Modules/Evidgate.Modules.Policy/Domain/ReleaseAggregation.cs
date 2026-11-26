using System.Text.Json.Serialization;

namespace Evidgate.Modules.Policy.Domain;

/// <summary>One bound run as the rule sees it: the binding's facts, in binding order, without any I/O type.</summary>
/// <param name="RunId">The bound run.</param>
/// <param name="ComponentKey">The release component it is bound for.</param>
/// <param name="Purpose"><c>regular</c> or <c>replay</c>.</param>
/// <param name="RunDigest">The digest the run recorded for that key, or null.</param>
internal sealed record AggregationBinding(Guid RunId, string ComponentKey, string Purpose, string? RunDigest);

/// <summary>The newest live verdict of one run.</summary>
/// <param name="RunId">The run judged.</param>
/// <param name="EvaluationId">The evaluation row the verdict was read from.</param>
/// <param name="EvidenceRevision">The frozen revision it judged.</param>
/// <param name="PolicyRevisionId">The policy revision it was reached under.</param>
/// <param name="Decision"><c>PASS</c>, <c>FAIL</c> or <c>INCONCLUSIVE</c>.</param>
internal sealed record AggregationVerdict(
    Guid RunId,
    Guid EvaluationId,
    int EvidenceRevision,
    Guid PolicyRevisionId,
    string Decision);

/// <summary>One component of the stored aggregate; the member names are the wire and the stored document's names.</summary>
/// <param name="ComponentId">The release component key.</param>
/// <param name="RunId">The run the verdict came from, or null when the component is missing.</param>
/// <param name="EvidenceRevision">The revision that run's verdict judged, or null.</param>
/// <param name="Status"><c>evaluated</c> or <c>missing</c>.</param>
/// <param name="Decision">The run's verdict, or null while the component is missing.</param>
/// <param name="Reasons">Closed component reason codes, ordinal order.</param>
internal sealed record ComponentEvaluation(
    [property: JsonPropertyName("component_id")] string ComponentId,
    [property: JsonPropertyName("run_id")] Guid? RunId,
    [property: JsonPropertyName("evidence_revision")] int? EvidenceRevision,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("decision")] string? Decision,
    [property: JsonPropertyName("reasons")] IReadOnlyList<string> Reasons);

/// <summary>The aggregate the rule computes for one release.</summary>
/// <param name="PolicyRevisionId">The policy revision the aggregate is stored under, null when no component was evaluated.</param>
/// <param name="Components">One entry per declared component, in component-key order.</param>
/// <param name="Completeness"><c>complete</c> only when every component is evaluated.</param>
/// <param name="Decision"><c>PASS</c>, <c>FAIL</c> or <c>INCONCLUSIVE</c>.</param>
/// <param name="Reasons">The distinct reason codes of the components plus the release-level ones, ordinal order.</param>
internal sealed record ReleaseAggregate(
    Guid? PolicyRevisionId,
    IReadOnlyList<ComponentEvaluation> Components,
    string Completeness,
    string Decision,
    IReadOnlyList<string> Reasons);

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

    internal const string Failed = "component_failed";

    internal const string Inconclusive = "component_inconclusive";

    internal const string MixedPolicyRevisions = "mixed_policy_revisions";

    /// <summary>Aggregates one release.</summary>
    /// <param name="components">The declared (component key, digest) pairs, ordered by key.</param>
    /// <param name="bindings">The stored bindings, oldest first.</param>
    /// <param name="verdicts">The newest live verdict of each bound run that has one.</param>
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
            return (Gap(key, NotEvaluated) with { RunId = run }, -1, null);
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
