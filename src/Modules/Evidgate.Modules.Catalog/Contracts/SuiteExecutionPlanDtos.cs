namespace Evidgate.Modules.Catalog.Contracts;

/// <summary>
/// One member of a suite revision's execution plan: the pinned case revision, its variant and the case it belongs to.
/// </summary>
/// <param name="CaseId">The test case the pinned revision belongs to — the manifest's annotation match key.</param>
/// <param name="VariantHash">The variant the member pins, as the revision stored it.</param>
/// <param name="CaseRevisionId">The case revision the member pins.</param>
public sealed record SuiteRevisionExecutionPlanMember(Guid CaseId, string VariantHash, Guid CaseRevisionId);

/// <summary>
/// One cursor page of a suite revision's execution plan (<c>GET /v1/suite-revisions/{r}/execution-plan</c>), for
/// manifest generation by a CI credential that may not read the suite itself.
/// </summary>
/// <param name="SuiteRevisionId">The revision the page was read from.</param>
/// <param name="Members">The page's members, ordered by <c>(ordinal, id)</c>.</param>
/// <param name="NextCursor">The next page's cursor, or null on the last page.</param>
public sealed record SuiteRevisionExecutionPlan(
    Guid SuiteRevisionId,
    IReadOnlyList<SuiteRevisionExecutionPlanMember> Members,
    string? NextCursor);
