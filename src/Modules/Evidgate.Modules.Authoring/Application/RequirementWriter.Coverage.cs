using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;

namespace Evidgate.Modules.Authoring.Application;

/// <summary>One pinned target of a requirement.</summary>
/// <param name="LinkId">The link row.</param>
/// <param name="VersionId">The pinned version.</param>
/// <param name="VersionNo">Its number.</param>
/// <param name="ChecklistItemId">The linked item, when the target is an item.</param>
/// <param name="ManualTestCaseId">The linked case, when the target is a case.</param>
/// <param name="Stale">True when the pin is older than the requirement's head.</param>
internal sealed record CoverageTarget(
    Guid LinkId,
    Guid VersionId,
    int VersionNo,
    Guid? ChecklistItemId,
    Guid? ManualTestCaseId,
    bool Stale);

/// <summary>One live manual case a requirement reaches, listed once however many ways reach it.</summary>
/// <param name="CaseId">The manual case.</param>
/// <param name="Code">Its project-unique code.</param>
/// <param name="Title">Its title.</param>
/// <param name="State">draft, approved or retired.</param>
/// <param name="ChecklistItemId">The item the case belongs to.</param>
/// <param name="LinkPath">One of direct, via_item, both.</param>
/// <param name="AutomatedCaseIds">Catalog case identities the manual case is linked to, each once.</param>
/// <param name="ResultAvailability">Always <see cref="RequirementLinkRules.ResultUnavailable"/> until a result source exists.</param>
internal sealed record CoverageCase(
    Guid CaseId,
    string Code,
    string Title,
    string State,
    Guid ChecklistItemId,
    string LinkPath,
    IReadOnlyList<Guid> AutomatedCaseIds,
    string ResultAvailability);

/// <summary>The coverage view of one requirement.</summary>
/// <param name="RequirementId">The requirement.</param>
/// <param name="HeadVersionId">Its head version now.</param>
/// <param name="HeadVersionNo">The head's number.</param>
/// <param name="LinkedCaseCount">Distinct live manual cases reached, over every page.</param>
/// <param name="ApprovedCaseCount">How many of those are approved.</param>
/// <param name="LinkCount">Every link of the requirement.</param>
/// <param name="StaleLinkCount">Links pinned to a version older than the head.</param>
/// <param name="ResultSourceAvailable">False: no latest-result source exists, so no result figure is given at all.</param>
/// <param name="Targets">The pinned targets, at most <see cref="RequirementLinkRules.MaximumListedTargets"/>.</param>
/// <param name="TargetsTruncated">True when more targets exist than are listed; the counts still cover them.</param>
/// <param name="Cases">One page of cases in code order.</param>
/// <param name="NextCode">The keyset to pass for the next page, or null on the last page.</param>
internal sealed record RequirementCoverage(
    Guid RequirementId,
    Guid HeadVersionId,
    int HeadVersionNo,
    int LinkedCaseCount,
    int ApprovedCaseCount,
    int LinkCount,
    int StaleLinkCount,
    bool ResultSourceAvailable,
    IReadOnlyList<CoverageTarget> Targets,
    bool TargetsTruncated,
    IReadOnlyList<CoverageCase> Cases,
    string? NextCode);

internal sealed partial class RequirementWriter
{
    private static readonly JsonSerializerOptions CoverageJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>
    /// The coverage of a requirement: counts over every link, the pinned targets, and one keyset page of the distinct
    /// cases it reaches. One statement answers it, so the head, the counts and the lists are the same snapshot, and the
    /// cost does not grow per case.
    /// </summary>
    /// <param name="orgId">The tenant; must equal the open scope's organization.</param>
    /// <param name="projectId">The project the requirement must belong to.</param>
    /// <param name="requirementId">The requirement; a soft-deleted one is still readable.</param>
    /// <param name="limit">The page size, 1 to <see cref="MaximumPageSize"/>.</param>
    /// <param name="afterCode">The last code of the previous page, or null for the first; it must be a well-formed case code.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async ValueTask<Result<RequirementCoverage>> GetCoverageAsync(
        Guid orgId,
        Guid projectId,
        Guid requirementId,
        int limit,
        string? afterCode,
        CancellationToken cancellationToken = default)
    {
        EnsureScope(orgId);

        if (limit is < 1 or > MaximumPageSize)
        {
            return Failure<RequirementCoverage>(AuthoringErrors.Invalid("/limit", "range", "the page size must be 1 to 200"));
        }

        // A cursor outside the code grammar can match no case; refusing it here also keeps a NUL away from the database.
        if (afterCode is not null && !AuthoringCode.IsWellFormed(afterCode))
        {
            return Failure<RequirementCoverage>(AuthoringErrors.Invalid("/after_code", "invalid", "after_code is not a case code this list issued"));
        }

        var row = await db.QuerySingleOrDefaultAsync<CoverageRow>(
            RequirementCoverageSql.Coverage,
            new
            {
                org_id = orgId,
                project_id = projectId,
                requirement_id = requirementId,
                after_code = afterCode,
                take_targets = RequirementLinkRules.MaximumListedTargets + 1,
                take_cases = limit + 1,
            },
            cancellationToken).ConfigureAwait(false);

        return row is null
            ? Failure<RequirementCoverage>(AuthoringErrors.NotFound("no requirement with that id is visible in this project"))
            : Result.Success(Cover(requirementId, row, limit));
    }

    private static RequirementCoverage Cover(Guid requirementId, CoverageRow row, int limit)
    {
        var targets = JsonSerializer.Deserialize<List<TargetRow>>(row.Targets, CoverageJson) ?? [];
        var cases = JsonSerializer.Deserialize<List<CaseRow>>(row.Cases, CoverageJson) ?? [];

        var listed = targets.Take(RequirementLinkRules.MaximumListedTargets)
            .Select(target => new CoverageTarget(target.LinkId, target.VersionId, target.VersionNo, target.ChecklistItemId, target.ManualTestCaseId, target.Stale))
            .ToList();
        var page = cases.Take(limit)
            .Select(found => new CoverageCase(
                found.CaseId,
                found.Code,
                found.Title,
                found.State,
                found.ChecklistItemId,
                RequirementLinkRules.PathOf(found.Direct, found.ViaItem),
                found.AutomatedCaseIds,
                RequirementLinkRules.ResultUnavailable))
            .ToList();

        return new RequirementCoverage(
            requirementId,
            row.HeadVersionId,
            row.HeadVersionNo,
            row.LinkedCaseCount,
            row.ApprovedCaseCount,
            row.LinkCount,
            row.StaleLinkCount,
            ResultSourceAvailable: false,
            listed,
            targets.Count > RequirementLinkRules.MaximumListedTargets,
            page,
            cases.Count > limit ? page[^1].Code : null);
    }

    private sealed class CoverageRow
    {
        public Guid HeadVersionId { get; set; }

        public int HeadVersionNo { get; set; }

        public int LinkedCaseCount { get; set; }

        public int ApprovedCaseCount { get; set; }

        public int LinkCount { get; set; }

        public int StaleLinkCount { get; set; }

        public string Targets { get; set; } = "[]";

        public string Cases { get; set; } = "[]";
    }

    private sealed class TargetRow
    {
        public Guid LinkId { get; set; }

        public Guid VersionId { get; set; }

        public int VersionNo { get; set; }

        public Guid? ChecklistItemId { get; set; }

        public Guid? ManualTestCaseId { get; set; }

        public bool Stale { get; set; }
    }

    private sealed class CaseRow
    {
        public Guid CaseId { get; set; }

        public string Code { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public Guid ChecklistItemId { get; set; }

        public bool Direct { get; set; }

        public bool ViaItem { get; set; }

        public Guid[] AutomatedCaseIds { get; set; } = [];
    }
}
