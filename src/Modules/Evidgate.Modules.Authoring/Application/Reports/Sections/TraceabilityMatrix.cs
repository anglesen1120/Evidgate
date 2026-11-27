using System.Runtime.CompilerServices;

namespace Evidgate.Modules.Authoring.Application.Reports.Sections;

/// <summary>
/// One requirement-to-test link expanded to what it reaches. <paramref name="Case"/> is null for a link to a checklist
/// item that holds no case in the source; <paramref name="Stale"/> says the link pins a version older than the head.
/// </summary>
internal sealed record TraceLink(
    ReportRequirement Requirement,
    ReportItem? Item,
    ReportCase? Case,
    int PinnedVersionNo,
    bool Stale);

/// <summary>
/// The two-sided requirement and case relation of a source. A link to a superseded requirement version is kept apart
/// from current coverage, so a requirement or a case reached only by stale links is neither covered nor unlinked by
/// accident: the matrix reports each of those states on its own.
/// </summary>
internal sealed class TraceabilityMatrix
{
    private TraceabilityMatrix(
        IReadOnlyList<ReportRequirement> requirements,
        IReadOnlyList<ReportCase> cases,
        IReadOnlyList<TraceLink> links)
    {
        Requirements = requirements;
        Cases = cases;
        Links = links;
        ByRequirement = links.ToLookup(link => link.Requirement.Id);
        ByCase = links.Where(link => link.Case is not null).ToLookup(link => link.Case!.Id);
    }

    internal IReadOnlyList<ReportRequirement> Requirements { get; }

    internal IReadOnlyList<ReportCase> Cases { get; }

    internal IReadOnlyList<TraceLink> Links { get; }

    internal ILookup<Guid, TraceLink> ByRequirement { get; }

    internal ILookup<Guid, TraceLink> ByCase { get; }

    /// <summary>A requirement is covered by at least one current link to a case or item the source holds.</summary>
    /// <param name="requirement">A requirement of the source.</param>
    internal bool IsCovered(ReportRequirement requirement) => ByRequirement[requirement.Id].Any(link => !link.Stale);

    /// <summary>A case is unlinked when no link of any version reaches it; one reached only by stale links is not.</summary>
    /// <param name="testCase">A case of the source.</param>
    internal bool IsUnlinked(ReportCase testCase) => !ByCase[testCase.Id].Any();

    /// <summary>The distinct requirements that reach <paramref name="testCase"/> by current (<paramref name="stale"/> false) or stale links, in display order.</summary>
    /// <param name="testCase">A case of the source.</param>
    /// <param name="stale">Which kind of link to follow.</param>
    internal IReadOnlyList<ReportRequirement> RequirementsOf(ReportCase testCase, bool stale) =>
        ReportOrder.Requirements(ByCase[testCase.Id].Where(link => link.Stale == stale).Select(link => link.Requirement).DistinctBy(requirement => requirement.Id));

    /// <summary>The distinct cases <paramref name="requirement"/> reaches by current or stale links, in display order.</summary>
    /// <param name="requirement">A requirement of the source.</param>
    /// <param name="stale">Which kind of link to follow.</param>
    internal IReadOnlyList<ReportCase> CasesOf(ReportRequirement requirement, bool stale) =>
        ReportOrder.Cases(ByRequirement[requirement.Id].Where(link => link.Stale == stale && link.Case is not null).Select(link => link.Case!).DistinctBy(testCase => testCase.Id));

    private static readonly ConditionalWeakTable<ReportSource, TraceabilityMatrix> Shared = new();

    /// <summary>The matrix of <paramref name="source"/>, built once however many sections read it.</summary>
    /// <param name="source">The source the job read.</param>
    internal static TraceabilityMatrix For(ReportSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return Shared.GetValue(source, Create);
    }

    /// <summary>Expands every link once, drops a link whose target the source does not hold, and orders the result so the input order never reaches the output.</summary>
    /// <param name="source">The source the job read.</param>
    private static TraceabilityMatrix Create(ReportSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var requirements = source.Requirements.ToDictionary(requirement => requirement.Id);
        var items = source.Items.ToDictionary(item => item.Id);
        var cases = source.Cases.ToDictionary(testCase => testCase.Id);
        var casesByItem = source.Cases.ToLookup(testCase => testCase.ChecklistItemId);
        RefuseOversizeExpansion(source, requirements, items, cases, casesByItem);

        var links = new List<TraceLink>();
        foreach (var link in source.Links)
        {
            if (!requirements.TryGetValue(link.RequirementId, out var requirement))
            {
                continue;
            }

            var stale = link.RequirementVersionNo < requirement.VersionNo;
            if (link.ManualTestCaseId is { } caseId && cases.TryGetValue(caseId, out var linked))
            {
                links.Add(new TraceLink(requirement, items.GetValueOrDefault(linked.ChecklistItemId), linked, link.RequirementVersionNo, stale));
            }

            if (link.ChecklistItemId is { } itemId && items.TryGetValue(itemId, out var item))
            {
                AddItemLink(links, requirement, item, casesByItem[itemId], link.RequirementVersionNo, stale);
            }
        }

        return new TraceabilityMatrix(ReportOrder.Requirements(source.Requirements), ReportOrder.Cases(source.Cases), Canonical(links));
    }

    // An item link fans out to every case of the item, so the row count is the sum of those fan-outs; it is counted
    // here, before any row exists, and a source that expands past the cap is a size outcome rather than an allocation.
    private static void RefuseOversizeExpansion(
        ReportSource source,
        Dictionary<Guid, ReportRequirement> requirements,
        Dictionary<Guid, ReportItem> items,
        Dictionary<Guid, ReportCase> cases,
        ILookup<Guid, ReportCase> casesByItem)
    {
        long rows = 0;
        foreach (var link in source.Links)
        {
            if (!requirements.ContainsKey(link.RequirementId))
            {
                continue;
            }

            if (link.ManualTestCaseId is { } caseId && cases.ContainsKey(caseId))
            {
                rows++;
            }

            if (link.ChecklistItemId is { } itemId && items.ContainsKey(itemId))
            {
                rows += Math.Max(1, casesByItem[itemId].Count());
            }

            if (rows > TestReportLimits.MaxTraceRows)
            {
                throw new ReportBuildException(ReportBuildException.TooLarge, "the traceability matrix expands to more rows than one report may hold");
            }
        }
    }

    private static List<TraceLink> Canonical(List<TraceLink> links) =>
        [.. links
            .OrderBy(link => link.Requirement.ExternalRef ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(link => link.Requirement.Title, StringComparer.Ordinal)
            .ThenBy(link => link.Requirement.Id)
            .ThenBy(link => link.Case?.Code ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(link => link.Case?.Id ?? Guid.Empty)
            .ThenBy(link => link.Item?.Title ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(link => link.Item?.Id ?? Guid.Empty)
            .ThenBy(link => link.PinnedVersionNo)
            .ThenBy(link => link.Stale)
            // A case link and an item link can expand to the same pair; one row says it once.
            .DistinctBy(link => (link.Requirement.Id, CaseId: link.Case?.Id, ItemId: link.Item?.Id, link.PinnedVersionNo))];

    private static void AddItemLink(
        List<TraceLink> links,
        ReportRequirement requirement,
        ReportItem item,
        IEnumerable<ReportCase> itemCases,
        int pinnedVersionNo,
        bool stale)
    {
        var any = false;
        foreach (var testCase in itemCases)
        {
            any = true;
            links.Add(new TraceLink(requirement, item, testCase, pinnedVersionNo, stale));
        }

        if (!any)
        {
            links.Add(new TraceLink(requirement, item, null, pinnedVersionNo, stale));
        }
    }
}
