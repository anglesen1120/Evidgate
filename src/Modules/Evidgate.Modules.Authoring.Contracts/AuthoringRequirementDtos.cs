namespace Evidgate.Modules.Authoring.Contracts;

/// <summary>
/// The body of <c>POST …/authoring/requirements</c>: a documented shape of the request. The route reads the raw body,
/// so one bad member is answered with its own 422 path instead of an opaque refusal.
/// </summary>
/// <param name="Kind"><c>user_story</c>, <c>acceptance_criterion</c>, <c>epic</c>, <c>spec_section</c> or <c>other</c>.</param>
/// <param name="Title">A 1..200 code-point title.</param>
/// <param name="Body">The first body, at most 20 000 code points; absent is an empty body.</param>
/// <param name="Reason">An optional 1..500 code-point reason recorded on the first version.</param>
/// <param name="ExternalRef">An optional label of the catalog reference alphabet, at most 128 characters; never a tracker key.</param>
/// <param name="FeatureNodeId">An optional live feature node of the same project.</param>
/// <param name="ParentRequirementId">An optional live requirement of the same project.</param>
public sealed record CreateRequirementRequest(
    string Kind,
    string Title,
    string? Body = null,
    string? Reason = null,
    string? ExternalRef = null,
    Guid? FeatureNodeId = null,
    Guid? ParentRequirementId = null);

/// <summary>
/// The body of <c>PATCH …/requirements/{r}</c>. An absent member is unchanged and an explicit <c>null</c> on
/// <c>external_ref</c>, <c>feature_node_id</c> or <c>parent_requirement_id</c> clears it. Every accepted edit appends
/// one version, carrying the old body forward when <c>body</c> is absent.
/// </summary>
/// <param name="Kind">A new kind, when sent.</param>
/// <param name="Title">A new title, when sent.</param>
/// <param name="Body">A new body, when sent.</param>
/// <param name="Reason">An optional reason recorded on the new version.</param>
/// <param name="ExternalRef">A new label, null to clear it, or absent to keep it.</param>
/// <param name="FeatureNodeId">A new feature node, null to clear it, or absent to keep it.</param>
/// <param name="ParentRequirementId">A new parent, null to make the requirement a root, or absent to keep it.</param>
public sealed record UpdateRequirementRequest(
    string? Kind = null,
    string? Title = null,
    string? Body = null,
    string? Reason = null,
    string? ExternalRef = null,
    Guid? FeatureNodeId = null,
    Guid? ParentRequirementId = null);

/// <summary>The body of <c>POST …/requirements/{r}/restore</c>.</summary>
/// <param name="VersionId">The earlier version of this requirement whose body becomes the newest version.</param>
/// <param name="Reason">A 1..500 code-point reason.</param>
public sealed record RestoreRequirementRequest(Guid VersionId, string Reason);

/// <summary>
/// The answer of create, edit and restore: the version the write appended. <paramref name="Version"/> is the identity
/// row's version, the value to send back as <c>If-Match</c>; it is not the version number of history.
/// </summary>
/// <param name="RequirementId">The stable identity.</param>
/// <param name="VersionId">The appended version row.</param>
/// <param name="VersionNo">Its number, which is also the head number now.</param>
/// <param name="Version">The identity row's version after the write; the response's <c>ETag</c> is the same number.</param>
/// <param name="ContentHash">The <c>sha256:…</c> hash of the appended body.</param>
public sealed record RequirementWriteResponse(
    Guid RequirementId,
    Guid VersionId,
    int VersionNo,
    int Version,
    string ContentHash);

/// <summary>One row of <c>GET …/authoring/requirements</c>: identity and head numbers, never a body.</summary>
/// <param name="RequirementId">The <c>requirement.id</c>.</param>
/// <param name="Kind">The kind.</param>
/// <param name="Title">The title.</param>
/// <param name="ExternalRef">The label, or null.</param>
/// <param name="FeatureNodeId">The feature node, or null.</param>
/// <param name="ParentRequirementId">The parent, or null for a root.</param>
/// <param name="HeadVersionNo">The number of the newest version.</param>
/// <param name="Version">The row's version, the value a client sends back as <c>If-Match</c>.</param>
/// <param name="CreatedAt">When the requirement was created.</param>
/// <param name="UpdatedAt">When the requirement last changed.</param>
public sealed record RequirementListItemDto(
    Guid RequirementId,
    string Kind,
    string Title,
    string? ExternalRef,
    Guid? FeatureNodeId,
    Guid? ParentRequirementId,
    int HeadVersionNo,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>One keyset page of live requirements, oldest first; <paramref name="NextCursor"/> is null on the last page.</summary>
/// <param name="Items">The page, in the fixed <c>(created_at, id)</c> order.</param>
/// <param name="NextCursor">The opaque cursor of the next page, or null when this page is the last.</param>
public sealed record RequirementListResponse(IReadOnlyList<RequirementListItemDto> Items, string? NextCursor);

/// <summary>One requirement with its head body. A deleted requirement stays readable by id so a pin can still resolve it.</summary>
/// <param name="RequirementId">The <c>requirement.id</c>.</param>
/// <param name="Kind">The kind.</param>
/// <param name="Title">The title.</param>
/// <param name="ExternalRef">The label, or null.</param>
/// <param name="FeatureNodeId">The feature node, or null.</param>
/// <param name="ParentRequirementId">The parent, or null for a root.</param>
/// <param name="Body">The head version's body.</param>
/// <param name="ContentHash">The head version's hash.</param>
/// <param name="HeadVersionId">The head version row.</param>
/// <param name="HeadVersionNo">The head version's number.</param>
/// <param name="Version">The row's version; the response's <c>ETag</c> is the same number.</param>
/// <param name="DeletedAt">When the requirement was hidden from the live list, or null.</param>
/// <param name="CreatedAt">When the requirement was created.</param>
/// <param name="UpdatedAt">When the requirement last changed.</param>
public sealed record RequirementDetailDto(
    Guid RequirementId,
    string Kind,
    string Title,
    string? ExternalRef,
    Guid? FeatureNodeId,
    Guid? ParentRequirementId,
    string Body,
    string ContentHash,
    Guid HeadVersionId,
    int HeadVersionNo,
    int Version,
    DateTimeOffset? DeletedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>One immutable version of a requirement.</summary>
/// <param name="VersionId">The <c>requirement_version.id</c>.</param>
/// <param name="VersionNo">Its number, 1 for the first.</param>
/// <param name="Body">The body of this version.</param>
/// <param name="ContentHash">The <c>sha256:…</c> hash of the body.</param>
/// <param name="Source"><c>manual</c>, <c>import</c>, <c>tracker</c> or <c>ai</c>.</param>
/// <param name="AuthorMembershipId">The author's membership, or null for a service principal.</param>
/// <param name="Reason">The reason recorded with the version, or null.</param>
/// <param name="CreatedAt">When the version was written.</param>
public sealed record RequirementVersionDto(
    Guid VersionId,
    int VersionNo,
    string Body,
    string ContentHash,
    string Source,
    Guid? AuthorMembershipId,
    string? Reason,
    DateTimeOffset CreatedAt);

/// <summary>
/// One page of history, newest first. A page is cut by row count and by the size of the bodies, so it can hold fewer
/// rows than the requested limit; follow <paramref name="NextCursor"/> until it is null.
/// </summary>
/// <param name="Items">The versions, newest first.</param>
/// <param name="NextCursor">The opaque cursor of the older versions, or null when the page reached version 1.</param>
public sealed record RequirementVersionListResponse(IReadOnlyList<RequirementVersionDto> Items, string? NextCursor);

/// <summary>
/// The body of <c>POST …/requirements/{r}/links</c>: names exactly one target and pins it to the requirement's head.
/// </summary>
/// <param name="ChecklistItemId">A live checklist item of the same project; exclusive with the case.</param>
/// <param name="ManualTestCaseId">A live manual test case of the same project; exclusive with the item.</param>
/// <param name="ExpectedVersionId">Optional head version the caller read; a moved head is a 412.</param>
public sealed record CreateRequirementLinkRequest(
    Guid? ChecklistItemId = null,
    Guid? ManualTestCaseId = null,
    Guid? ExpectedVersionId = null);

/// <summary>The answer of a link create: 201 with <c>changed</c> true, or 200 with <c>changed</c> false for a repeat.</summary>
/// <param name="LinkId">The link row.</param>
/// <param name="RequirementId">The linked requirement.</param>
/// <param name="VersionId">The pinned version.</param>
/// <param name="VersionNo">Its number.</param>
/// <param name="ChecklistItemId">The linked item, when the target is an item.</param>
/// <param name="ManualTestCaseId">The linked case, when the target is a case.</param>
/// <param name="Changed">False when the target was already linked at the head and nothing was written.</param>
public sealed record RequirementLinkResponse(
    Guid LinkId,
    Guid RequirementId,
    Guid VersionId,
    int VersionNo,
    Guid? ChecklistItemId,
    Guid? ManualTestCaseId,
    bool Changed);

/// <summary>One pinned target of a requirement.</summary>
/// <param name="LinkId">The link row.</param>
/// <param name="VersionId">The pinned version.</param>
/// <param name="VersionNo">Its number.</param>
/// <param name="ChecklistItemId">The linked item, when the target is an item.</param>
/// <param name="ManualTestCaseId">The linked case, when the target is a case.</param>
/// <param name="Stale">True when the pin is older than the requirement's head.</param>
public sealed record RequirementCoverageTargetDto(
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
/// <param name="State"><c>draft</c>, <c>approved</c> or <c>retired</c>.</param>
/// <param name="ChecklistItemId">The item the case belongs to.</param>
/// <param name="LinkPath"><c>direct</c>, <c>via_item</c> or <c>both</c>.</param>
/// <param name="AutomatedCaseIds">Catalog case identities the manual case is linked to, each once.</param>
/// <param name="ResultAvailability">
/// The availability of the latest manual result: <c>unavailable</c> until a result source exists, so a missing figure
/// is never read as "no failures".
/// </param>
public sealed record RequirementCoverageCaseDto(
    Guid CaseId,
    string Code,
    string Title,
    string State,
    Guid ChecklistItemId,
    string LinkPath,
    IReadOnlyList<Guid> AutomatedCaseIds,
    string ResultAvailability);

/// <summary>The coverage view of one requirement: counts over every link, the pinned targets and one page of cases.</summary>
/// <param name="RequirementId">The requirement.</param>
/// <param name="HeadVersionId">Its head version now.</param>
/// <param name="HeadVersionNo">The head's number.</param>
/// <param name="LinkedCaseCount">Distinct live manual cases reached, over every page.</param>
/// <param name="ApprovedCaseCount">How many of those are approved.</param>
/// <param name="LinkCount">Every link of the requirement.</param>
/// <param name="StaleLinkCount">Links pinned to a version older than the head.</param>
/// <param name="ResultSourceAvailable">False while no latest-result source exists; then no result figure is given at all.</param>
/// <param name="Targets">The pinned targets, at most 500.</param>
/// <param name="TargetsTruncated">True when more targets exist than are listed; the counts still cover them.</param>
/// <param name="Cases">One page of cases in code order.</param>
/// <param name="NextCode">The <c>after_code</c> for the next page, or null on the last page.</param>
public sealed record RequirementCoverageResponse(
    Guid RequirementId,
    Guid HeadVersionId,
    int HeadVersionNo,
    int LinkedCaseCount,
    int ApprovedCaseCount,
    int LinkCount,
    int StaleLinkCount,
    bool ResultSourceAvailable,
    IReadOnlyList<RequirementCoverageTargetDto> Targets,
    bool TargetsTruncated,
    IReadOnlyList<RequirementCoverageCaseDto> Cases,
    string? NextCode);
