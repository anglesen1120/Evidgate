using System.Text.Json.Serialization;

namespace Evidgate.Modules.Authoring.Contracts;

/// <summary>
/// One step of a manual test case (13.1.1 §5, routes 13/14/15), in the shape
/// <c>contracts/schemas/authoring-steps.v1.json</c> validates: a number, the action, the expected result and the
/// optional test data, all text. The request DTOs of this file carry the same type, so the documented schema of a
/// request and of a response step cannot drift.
/// </summary>
/// <param name="No">The step number; the array order is the order the tester performs the steps in.</param>
/// <param name="Action">What the tester does (at most 2 000 characters).</param>
/// <param name="Expected">What the tester must observe (at most 2 000 characters).</param>
/// <param name="Data">The test data the step needs, when it needs any; never a nested object.</param>
public sealed record ManualCaseStepDto(
    int No,
    string Action,
    string Expected,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Data = null);

/// <summary>
/// One row of <c>GET …/authoring/manual-cases</c> (route 12, 13.1.1 §5). The list projection carries no
/// <c>description</c>, <c>precondition</c> or <c>steps</c> — a page of 200 cases must stay one bounded statement — and
/// no coverage field: <c>linked_live_count</c>/<c>linked_case_ids</c> belong to 13.1.1-T4 (AC12), which reads the
/// <c>manual_case_link</c> table this task does not create.
/// </summary>
/// <param name="CaseId">The <c>manual_test_case.id</c>.</param>
/// <param name="Code">The allocated, immutable code (<c>&lt;PREFIX&gt;-&lt;n&gt;</c>, AC4).</param>
/// <param name="ChecklistItemId">The item the case sits under.</param>
/// <param name="NodeId">The feature node of that item, so a client can render the case in the tree without a second read.</param>
/// <param name="Title">The case title.</param>
/// <param name="Priority"><c>critical</c>, <c>high</c>, <c>medium</c> or <c>low</c>.</param>
/// <param name="State"><c>draft</c>, <c>approved</c> or <c>retired</c> (§6).</param>
/// <param name="Version">The row's version, the value a client sends back as <c>If-Match</c>.</param>
public sealed record ManualCaseListItemDto(
    Guid CaseId,
    string Code,
    Guid ChecklistItemId,
    Guid NodeId,
    string Title,
    string Priority,
    string State,
    int Version);

/// <summary>One keyset page of manual test cases (route 12); <paramref name="NextCursor"/> is null on the last page.</summary>
/// <param name="Items">The page, in the fixed <c>(code, id)</c> order.</param>
/// <param name="NextCursor">The opaque cursor of the next page, or null when this page is the last.</param>
public sealed record ManualCaseListResponse(IReadOnlyList<ManualCaseListItemDto> Items, string? NextCursor);

/// <summary>
/// One manual test case with its steps (route 14). No link members: <c>links:[{test_case_id, name, status}]</c> is
/// 13.1.1-T4's read model over <c>manual_case_link</c> (AC11/AC12), which this task does not create.
/// </summary>
/// <param name="CaseId">The <c>manual_test_case.id</c>.</param>
/// <param name="Code">The immutable code.</param>
/// <param name="ChecklistItemId">The item the case sits under.</param>
/// <param name="NodeId">The feature node of that item.</param>
/// <param name="Title">The title.</param>
/// <param name="Description">The description, or null.</param>
/// <param name="Precondition">The precondition the tester needs, or null.</param>
/// <param name="Steps">The steps, in array order; 1..100 of them.</param>
/// <param name="Priority">The priority.</param>
/// <param name="State">The state.</param>
/// <param name="Version">The row's version; the response's <c>ETag</c> is the same number.</param>
/// <param name="CreatedAt">When the case was created.</param>
/// <param name="UpdatedAt">When the case last changed.</param>
public sealed record ManualCaseDetailDto(
    Guid CaseId,
    string Code,
    Guid ChecklistItemId,
    Guid NodeId,
    string Title,
    string? Description,
    string? Precondition,
    IReadOnlyList<ManualCaseStepDto> Steps,
    string Priority,
    string State,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// The body of <c>POST …/authoring/manual-cases</c> (route 13, 13.1.1 §5): a documented shape of the request, whose
/// per-member 422s the command produces — the route reads the raw body so one bad member does not hide the others.
/// </summary>
/// <param name="ChecklistItemId">The live checklist item the case sits under.</param>
/// <param name="Title">A 1..500 code-point title.</param>
/// <param name="Description">An optional description (at most 4 000 code points).</param>
/// <param name="Precondition">An optional precondition (at most 4 000 code points).</param>
/// <param name="Steps">1..100 steps, each validated against <c>authoring-steps.v1.json</c>.</param>
/// <param name="Priority"><c>critical</c>, <c>high</c>, <c>medium</c> or <c>low</c>.</param>
public sealed record CreateManualCaseRequest(
    Guid ChecklistItemId,
    string Title,
    string? Description,
    string? Precondition,
    IReadOnlyList<ManualCaseStepDto> Steps,
    string Priority);

/// <summary>The 201 body of route 13: the new case's id, its allocated code and its first version.</summary>
/// <param name="CaseId">The new <c>manual_test_case.id</c>.</param>
/// <param name="Code">The allocated, immutable code (AC4).</param>
/// <param name="Version">The case's version, 1 on create.</param>
public sealed record CreateManualCaseResponse(Guid CaseId, string Code, int Version);

/// <summary>
/// The body of <c>PATCH …/manual-cases/{m}</c> (route 15, 13.1.1 §5). An absent member is unchanged and an explicit
/// <c>null</c> on <c>description</c>/<c>precondition</c> clears it; <c>code</c> and <c>checklist_item_id</c> are refused
/// with their own 422 paths because a move is route 19 (AC22).
/// </summary>
/// <param name="Title">A new 1..500 code-point title, when sent.</param>
/// <param name="Description">A new description, null to clear it, or absent to keep it.</param>
/// <param name="Precondition">A new precondition, null to clear it, or absent to keep it.</param>
/// <param name="Steps">A replacement step list, when sent.</param>
/// <param name="Priority">A new priority, when sent.</param>
public sealed record UpdateManualCaseRequest(
    string? Title = null,
    string? Description = null,
    string? Precondition = null,
    IReadOnlyList<ManualCaseStepDto>? Steps = null,
    string? Priority = null);

/// <summary>The body of <c>POST …/manual-cases/{m}/state</c> (route 17, 13.1.1 §5).</summary>
/// <param name="State">The target state; <c>retired</c> requires <paramref name="Reason"/>.</param>
/// <param name="Reason">An optional 1..500 character reason; required when the move retires the case (AC21).</param>
public sealed record SetManualCaseStateRequest(string State, string? Reason = null);

/// <summary>The 200 body of route 17: the state the case is in now and its version.</summary>
/// <param name="State">The case's state after the call; an unchanged move answers the state it already had.</param>
/// <param name="Version">The case's version after the call; unchanged by a no-op.</param>
public sealed record SetManualCaseStateResponse(string State, int Version);

/// <summary>The body of <c>POST …/manual-cases/{m}/item</c> (route 19, AC22).</summary>
/// <param name="ChecklistItemId">The target item of the same project.</param>
/// <param name="Reason">An optional 1..500 character reason recorded on the update version.</param>
public sealed record MoveManualCaseRequest(Guid ChecklistItemId, string? Reason = null);

/// <summary>The 200 body of route 19: the case's version and the item it now sits under.</summary>
/// <param name="Version">The case's version after the move; unchanged when it already sat under the target item.</param>
/// <param name="ChecklistItemId">The item the case is now under.</param>
public sealed record MoveManualCaseResponse(int Version, Guid ChecklistItemId);
