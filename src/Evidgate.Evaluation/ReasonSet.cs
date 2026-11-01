namespace Evidgate.Evaluation;

/// <summary>The one shape of <c>reasons[]</c>: no duplicates, ordered by token so two processes agree byte for byte.</summary>
internal static class ReasonSet
{
    /// <summary>Deduplicates and orders the reasons by their contract token.</summary>
    public static IReadOnlyList<EvaluationReason> Normalize(IEnumerable<EvaluationReason> reasons) =>
        [.. reasons.Distinct().OrderBy(reason => EvaluationCodes.Code(reason), StringComparer.Ordinal)];
}
