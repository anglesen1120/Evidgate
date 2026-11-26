using System.Text.Json.Serialization;

namespace Evidgate.Modules.Policy.Contracts;

/// <summary>One component of a stored release aggregate, as <c>GET /v1/releases/{id}/decision</c> serves it.</summary>
/// <param name="ComponentId">The release component's key.</param>
/// <param name="RunId">The run the component's verdict came from; null while the component is missing.</param>
/// <param name="EvidenceRevision">The frozen revision that run's verdict judged; null while missing.</param>
/// <param name="Status"><c>evaluated</c> when a bound, digest-matching run has a live verdict, otherwise <c>missing</c>.</param>
/// <param name="DecisionValue">The run's verdict (<c>PASS</c>, <c>FAIL</c>, <c>INCONCLUSIVE</c>); null while missing.</param>
/// <param name="Reasons">The closed component reason codes, verbatim.</param>
public sealed record ReleaseComponentDecision(
    string ComponentId,
    Guid? RunId,
    int? EvidenceRevision,
    string Status,
    [property: JsonPropertyName("decision")] string? DecisionValue,
    IReadOnlyList<string> Reasons);

/// <summary>
/// The one body of <c>GET /v1/releases/{id}/decision</c>. A release that has no stored aggregate yet answers
/// <c>pending</c> with every other member null: no verdict is invented, and "pending" is a read projection, never a
/// stored state.
/// </summary>
/// <param name="Status"><see cref="ReleaseDecisionStatus.Pending"/> or <see cref="ReleaseDecisionStatus.Aggregated"/>.</param>
/// <param name="DecisionValue"><c>PASS</c>, <c>FAIL</c>, <c>INCONCLUSIVE</c>, or <c>pending</c> without a stored row.</param>
/// <param name="ReleaseId">The release asked about.</param>
/// <param name="Id">The stored <c>release_decision</c> row every other member was copied from; null while pending.</param>
/// <param name="PolicyRevisionId">The policy revision the aggregate is stored under; null while pending.</param>
/// <param name="Completeness"><c>complete</c> only when every component is evaluated; null while pending.</param>
/// <param name="Reasons">Distinct component and release-level reason codes; null while pending.</param>
/// <param name="Components">One entry per declared component, in component-key order; null while pending.</param>
/// <param name="InputsHash">The content address of the aggregated inputs; null while pending.</param>
/// <param name="CreatedAt">When the row was stored; null while pending.</param>
public sealed record ReleaseDecision(
    string Status,
    [property: JsonPropertyName("decision")] string DecisionValue,
    Guid ReleaseId,
    Guid? Id,
    Guid? PolicyRevisionId,
    string? Completeness,
    IReadOnlyList<string>? Reasons,
    IReadOnlyList<ReleaseComponentDecision>? Components,
    string? InputsHash,
    DateTimeOffset? CreatedAt)
{
    /// <summary>A release with no stored aggregate.</summary>
    /// <param name="releaseId">The release asked about.</param>
    public static ReleaseDecision Pending(Guid releaseId) =>
        new(ReleaseDecisionStatus.Pending, ReleaseDecisionStatus.Pending, releaseId, null, null, null, null, null, null, null);
}

/// <summary>The two values of <see cref="ReleaseDecision.Status"/>.</summary>
public static class ReleaseDecisionStatus
{
    /// <summary>No aggregate row exists yet.</summary>
    public const string Pending = "pending";

    /// <summary>The body was copied from one stored aggregate row.</summary>
    public const string Aggregated = "aggregated";
}
