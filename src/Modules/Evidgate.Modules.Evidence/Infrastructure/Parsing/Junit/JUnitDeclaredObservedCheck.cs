using Evidgate.Modules.Evidence.Application;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;

/// <summary>
/// The observed side of one report: the cases the mapper resolved, counted the way the row stores them.
/// </summary>
/// <param name="Tests">The mapped cases, i.e. the events this report can write.</param>
/// <param name="Failures">The mapped cases the mapper stored as <c>failed</c>.</param>
/// <param name="Errors">The mapped cases the mapper stored as <c>error</c>.</param>
/// <param name="Skipped">The mapped cases the mapper stored as <c>skipped</c>.</param>
internal readonly record struct ObservedCounters(int Tests, int Failures, int Errors, int Skipped);

/// <summary>
/// Compares one report's declared counters with the counts its mapping observed, and names each difference in
/// <see cref="JUnitDiscrepancies"/>' closed vocabulary. It only reports: no code here decides that a case passed, so a
/// discrepancy never turns missing evidence into a pass.
/// </summary>
internal static class JUnitDeclaredObservedCheck
{
    /// <summary>
    /// The one order every code list is written in, so the same bytes always store the same array and a reader can
    /// rely on a code's position rather than on the order a walk happened to meet it.
    /// </summary>
    private static readonly string[] CanonicalOrder =
    [
        JUnitDiscrepancies.Malformed,
        JUnitDiscrepancies.CountMismatch,
        JUnitDiscrepancies.UnmappedCase,
        JUnitDiscrepancies.DuplicateCase,
        JUnitDiscrepancies.StatusMismatch,
    ];

    /// <summary>
    /// The final code list of one parsed report: the codes the mapper already established, plus one
    /// <c>status_mismatch</c> for each declared failure/error/skip count that disagrees with the observed cases.
    /// </summary>
    /// <param name="declared">The counters the document declared; a missing or unusable counter is absent, never zero.</param>
    /// <param name="observed">The counts of the cases the mapper resolved.</param>
    /// <param name="mapperCodes">
    /// What the mapper reported: <c>count_mismatch</c> (declared <c>tests</c> against the document's own testcases),
    /// <c>unmapped_case</c> and <c>duplicate_case</c>.
    /// </param>
    /// <returns>Every occurrence of every code, in canonical order.</returns>
    public static IReadOnlyList<string> Compare(
        JUnitDeclaredCounters declared,
        ObservedCounters observed,
        IReadOnlyList<string> mapperCodes)
    {
        var codes = new List<string>(mapperCodes.Count + 3);
        codes.AddRange(mapperCodes);

        if (!CoversWholeDocument(mapperCodes))
        {
            AddStatusMismatch(codes, declared.Failures, observed.Failures);
            AddStatusMismatch(codes, declared.Errors, observed.Errors);
            AddStatusMismatch(codes, declared.Skipped, observed.Skipped);
        }

        return Canonical(codes);
    }

    /// <summary>
    /// The code list one stored row means. A refused row's <c>malformed</c> outcome is the discrepancy the row cannot
    /// store (its own CHECK forces the array empty), while a parsed row keeps the codes it wrote.
    /// </summary>
    /// <param name="outcome">The row's outcome: one of <see cref="RawReportOutcomes"/>.</param>
    /// <param name="reason">
    /// The refusal's reason word. It explains the row in its own column and never becomes a code: one word names every
    /// refusal, so the vocabulary cannot grow with each profile reason.
    /// </param>
    /// <param name="storedCodes">The row's stored codes; ignored for a refused row.</param>
    /// <returns>The effective code list, in canonical order.</returns>
    public static IReadOnlyList<string> EffectiveCodes(
        string outcome,
        string? reason,
        IReadOnlyList<string> storedCodes)
    {
        if (string.Equals(outcome, RawReportOutcomes.Malformed, StringComparison.Ordinal))
        {
            // Reading the reason here makes it explicit that it is the row's own explanation, not a code.
            _ = reason;

            return [JUnitDiscrepancies.Malformed];
        }

        return Canonical(storedCodes);
    }

    /// <summary>
    /// Whether the mapper's codes show that the resolved cases speak for the whole document. Any of them means some
    /// part of the document produced no case, so the declared per-status counts cannot be compared with the cases: the
    /// mapper's own code already explains the difference.
    /// </summary>
    private static bool CoversWholeDocument(IReadOnlyList<string> mapperCodes)
    {
        foreach (var code in mapperCodes)
        {
            if (string.Equals(code, JUnitDiscrepancies.CountMismatch, StringComparison.Ordinal)
                || string.Equals(code, JUnitDiscrepancies.UnmappedCase, StringComparison.Ordinal)
                || string.Equals(code, JUnitDiscrepancies.DuplicateCase, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One <c>status_mismatch</c> for a declared count that disagrees with the observed cases.</summary>
    private static void AddStatusMismatch(List<string> codes, int? declared, int observed)
    {
        if (declared is { } claimed && claimed != observed)
        {
            codes.Add(JUnitDiscrepancies.StatusMismatch);
        }
    }

    /// <summary>
    /// Groups the codes into the canonical order, keeping every occurrence, so a code's entries stay together. A word
    /// outside the vocabulary (a later code) goes last, grouped in first-seen order.
    /// </summary>
    private static IReadOnlyList<string> Canonical(IReadOnlyList<string> codes)
    {
        if (codes.Count < 2)
        {
            return codes.ToArray();
        }

        var ordered = new List<string>(codes.Count);
        foreach (var canonical in CanonicalOrder)
        {
            AppendOccurrences(ordered, codes, canonical);
        }

        var future = new List<string>();
        foreach (var code in codes)
        {
            if (!IsCanonical(code) && !future.Contains(code))
            {
                future.Add(code);
            }
        }

        foreach (var code in future)
        {
            AppendOccurrences(ordered, codes, code);
        }

        return ordered;
    }

    /// <summary>Copies every occurrence of one code, preserving the relative order of identical entries.</summary>
    private static void AppendOccurrences(List<string> target, IReadOnlyList<string> codes, string code)
    {
        foreach (var candidate in codes)
        {
            if (string.Equals(candidate, code, StringComparison.Ordinal))
            {
                target.Add(candidate);
            }
        }
    }

    /// <summary>Whether the code is one of the vocabulary words this check orders.</summary>
    private static bool IsCanonical(string code)
    {
        foreach (var canonical in CanonicalOrder)
        {
            if (string.Equals(canonical, code, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
