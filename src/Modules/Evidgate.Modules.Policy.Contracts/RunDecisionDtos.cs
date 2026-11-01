using System.Text.Json.Serialization;

namespace Evidgate.Modules.Policy.Contracts;

/// <summary>
/// The one body of <c>GET /v1/runs/{r}/decision</c> (07.5.2 §5.1), served for an evaluated run and a pending one alike.
/// Every member is written even when it is null, because a receiver cannot tell "no verdict yet" from a malformed answer
/// when a member is simply absent, and a pending run must never read as a verdict.
/// </summary>
/// <param name="Status"><c>pending</c> until a <c>live</c> evaluation row exists, then <c>evaluated</c>.</param>
/// <param name="DecisionValue">
/// The row's verdict (<c>PASS</c>, <c>FAIL</c>, <c>INCONCLUSIVE</c>) or null. Renamed here because a member may not
/// carry its own type's name; the wire name stays <c>decision</c>.
/// </param>
/// <param name="EvaluationId">The <c>evaluation.id</c> every other evaluated member was copied from.</param>
/// <param name="EvidenceRevision">The frozen revision that row judged, the same number the evidence-revision list reports.</param>
/// <param name="Completeness">The revision's completeness as the verdict recorded it.</param>
/// <param name="Outcome">What the required scope's results said.</param>
/// <param name="Missing">
/// The closed reason codes behind an incomplete verdict, verbatim; null while pending rather than an empty list, which
/// would read as "nothing is missing".
/// </param>
/// <param name="Reasons">The closed decision reason codes, verbatim; null while pending.</param>
/// <param name="PolicyHash">The content address of the policy revision the verdict was reached under.</param>
/// <param name="ManifestHash">The content address of the manifest the run froze.</param>
/// <param name="EvaluatorVersion">The evaluator that produced the verdict.</param>
/// <param name="Mode">
/// The stored slot of the row this was copied from: always <c>live</c>, because a <c>shadow</c> evaluation is a second
/// opinion and is never served here.
/// </param>
/// <param name="Url">The deployment's run-detail page, or null when it has no public base URL to build one from.</param>
public sealed record Decision(
    string Status,
    [property: JsonPropertyName("decision")] string? DecisionValue,
    Guid? EvaluationId,
    int? EvidenceRevision,
    string? Completeness,
    string? Outcome,
    IReadOnlyList<string>? Missing,
    IReadOnlyList<string>? Reasons,
    string? PolicyHash,
    string? ManifestHash,
    string? EvaluatorVersion,
    string? Mode,
    string? Url)
{
    /// <summary>
    /// A run with no <c>live</c> evaluation row yet: no verdict and no value invented in its place. The run page exists
    /// before a decision does, so <paramref name="url"/> is still carried.
    /// </summary>
    public static Decision Pending(string? url) =>
        new(DecisionStatus.Pending, null, null, null, null, null, null, null, null, null, null, null, url);
}

/// <summary>The two values of <see cref="Decision.Status"/>: whether a <c>live</c> evaluation row exists for the run.</summary>
public static class DecisionStatus
{
    /// <summary>No <c>live</c> evaluation row yet; every evaluation-derived member is null.</summary>
    public const string Pending = "pending";

    /// <summary>The body was copied from one <c>live</c> evaluation row.</summary>
    public const string Evaluated = "evaluated";
}
