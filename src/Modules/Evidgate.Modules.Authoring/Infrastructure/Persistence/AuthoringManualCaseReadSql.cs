using System.Text.Json;
using Evidgate.Modules.Authoring.Contracts;

namespace Evidgate.Modules.Authoring.Infrastructure.Persistence;

/// <summary>
/// The statements and Dapper rows of the manual test case read surface (13.1.1 §5 routes 12 and 14) and of the two
/// preconditions a case write needs (<c>If-Match</c> on the case row, the target item lock of AC22).
/// </summary>
/// <remarks>
/// <para>
/// One statement per method: the list is one keyset page (+ the project probe) and the detail is one row (+ the same
/// probe is unnecessary because the filter carries the project), so the C5 statement bounds hold. Every statement
/// filters on <c>org_id</c> <em>and</em> the project of the URL, so a case of another project of the same tenant is
/// indistinguishable from a missing one (404, AC10) instead of being readable by id.
/// </para>
/// <para>
/// The rows are mutable classes with settable properties: Dapper binds them through
/// <c>DefaultTypeMap.MatchNamesWithUnderscores</c> (switched on by <c>AddEvidgateTenancy</c>), and a positional record
/// cannot bind a projection with extra columns.
/// </para>
/// </remarks>
internal static class AuthoringManualCaseReadSql
{
    /// <summary>The project probe of the list (the catalog's own statement, 03.1.2).</summary>
    internal const string ProjectExists = "select 1 from public.project where id = @project_id and status <> 'deleted'";

    /// <summary>
    /// The authoring member behind the caller's user id, or nothing when the caller is not a member of the tenant.
    /// </summary>
    /// <remarks>
    /// The authoring version row records <c>actor_membership_id</c> (§4), and a handler holds the principal rather than
    /// a membership: this is the one lookup that turns the former into the latter. It is module-local on purpose —
    /// <c>Evidgate.Modules.Authoring.Endpoints.AuthoringRequestScope</c> is the shared helper the wave's other routes
    /// use, and this statement is what that helper would run.
    /// </remarks>
    internal const string ActorMembership = """
        select m.id
          from public.membership m
         where m.org_id = @org_id
           and m.user_id = @user_id
           and m.status = 'active'
         order by m.id
         limit 1
        """;

    /// <summary>The list projection: the row a page carries, joined to the item for its node.</summary>
    private const string CaseListColumns = """
        m.id as case_id, m.code, m.checklist_item_id, i.feature_node_id as node_id,
        m.title, m.priority, m.state, m.version
        """;

    /// <summary>
    /// First page of a project's live manual test cases, ordered <c>(code, id)</c> (13.1.1 §5).
    /// </summary>
    /// <remarks>
    /// The order is the plan's, and it is <b>lexical</b> on <c>code</c>: <c>SHOP-1000</c> sorts before <c>SHOP-999</c>
    /// because the code is text. Migration 0039 stores no allocated number beside it (<c>manual_test_case</c> has
    /// <c>code</c> only; <c>authoring_code_counter</c> holds the counter per project), so a numeric order cannot be
    /// derived without re-parsing the code in SQL. The keyset predicate compares the same two columns, so paging is
    /// total and stable; a client that wants numeric order sorts the page it holds (it is bounded by <c>limit</c>).
    /// </remarks>
    internal const string CasesFirstPage = $"""
        select {CaseListColumns}
          from public.manual_test_case m
          join public.checklist_item i
            on i.org_id = m.org_id
           and i.project_id = m.project_id
           and i.id = m.checklist_item_id
         where m.org_id = @org_id
           and m.project_id = @project_id
           and m.deleted_at is null
           and (@item_id::uuid is null or m.checklist_item_id = @item_id)
           and (@node_id::uuid is null or i.feature_node_id = @node_id)
           and (@state::text is null or m.state = @state)
           and (@priority::text is null or m.priority = @priority)
           and (@q::text is null or position(lower(@q) in lower(m.title)) > 0)
         order by m.code, m.id
         limit @take
        """;

    /// <summary>
    /// The next page: the first page plus the row comparison on the pair the order is built from. The cursor carries
    /// both members, so the seek is a single index range scan on <c>(org_id, project_id, code)</c>.
    /// </summary>
    internal const string CasesNextPage = $"""
        select {CaseListColumns}
          from public.manual_test_case m
          join public.checklist_item i
            on i.org_id = m.org_id
           and i.project_id = m.project_id
           and i.id = m.checklist_item_id
         where m.org_id = @org_id
           and m.project_id = @project_id
           and m.deleted_at is null
           and (m.code, m.id) > (@cursor_code, @cursor_id)
           and (@item_id::uuid is null or m.checklist_item_id = @item_id)
           and (@node_id::uuid is null or i.feature_node_id = @node_id)
           and (@state::text is null or m.state = @state)
           and (@priority::text is null or m.priority = @priority)
           and (@q::text is null or position(lower(@q) in lower(m.title)) > 0)
         order by m.code, m.id
         limit @take
        """;

    /// <summary>One live case of the project with everything route 14 answers and route 15 needs to rebuild the snapshot.</summary>
    internal const string CaseDetail = """
        select m.id as case_id, m.code, m.checklist_item_id, i.feature_node_id as node_id,
               m.title, m.description, m.precondition, m.steps::text as steps,
               m.priority, m.state, m.version, m.created_at, m.updated_at
          from public.manual_test_case m
          join public.checklist_item i
            on i.org_id = m.org_id
           and i.project_id = m.project_id
           and i.id = m.checklist_item_id
         where m.org_id = @org_id
           and m.project_id = @project_id
           and m.id = @case_id
           and m.deleted_at is null
        """;

    /// <summary>
    /// The <c>If-Match</c> precondition of AC19: the case row locked <c>for update</c>, so two writers of one case
    /// serialise here and the second one compares its stale version to the fresh row. Its column list is the detail's,
    /// so the same row type carries the snapshot a PATCH rebuilds.
    /// </summary>
    internal const string LockCase = """
        select m.id as case_id, m.code, m.checklist_item_id, i.feature_node_id as node_id,
               m.title, m.description, m.precondition, m.steps::text as steps,
               m.priority, m.state, m.version, m.created_at, m.updated_at
          from public.manual_test_case m
          join public.checklist_item i
            on i.org_id = m.org_id
           and i.project_id = m.project_id
           and i.id = m.checklist_item_id
         where m.org_id = @org_id
           and m.project_id = @project_id
           and m.id = @case_id
           and m.deleted_at is null
           for update of m
        """;

    /// <summary>
    /// The target item of a move, locked <c>for update</c> <em>before</em> the case (AC22, OI-13-127): the writer locks
    /// the target first and the case second, so taking them in that order here keeps the two callers' lock sequences
    /// identical and a deadlock impossible. A target that does not exist in this project locks nothing and the writer
    /// names the refusal.
    /// </summary>
    internal const string LockItem = """
        select i.id
          from public.checklist_item i
         where i.org_id = @org_id
           and i.project_id = @project_id
           and i.id = @item_id
           and i.deleted_at is null
           for update
        """;
}

/// <summary>One row of a <c>GET …/manual-cases</c> page (route 12).</summary>
internal sealed class ManualCaseListRow
{
    /// <summary>The <c>manual_test_case.id</c>.</summary>
    public Guid CaseId { get; set; }

    /// <summary>The immutable code.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>The item the case sits under.</summary>
    public Guid ChecklistItemId { get; set; }

    /// <summary>The feature node of that item.</summary>
    public Guid NodeId { get; set; }

    /// <summary>The title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The priority.</summary>
    public string Priority { get; set; } = string.Empty;

    /// <summary>The state.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>The row's version.</summary>
    public int Version { get; set; }

    /// <summary>The row as the wire type of route 12.</summary>
    public ManualCaseListItemDto ToDto() => new(CaseId, Code, ChecklistItemId, NodeId, Title, Priority, State, Version);
}

/// <summary>
/// One manual test case row: the detail projection (route 14) and the locked snapshot a PATCH, a state move or a delete
/// is built from.
/// </summary>
internal sealed class ManualCaseRow
{
    /// <summary>The <c>manual_test_case.id</c>.</summary>
    public Guid CaseId { get; set; }

    /// <summary>The immutable code.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>The item the case sits under.</summary>
    public Guid ChecklistItemId { get; set; }

    /// <summary>The feature node of that item.</summary>
    public Guid NodeId { get; set; }

    /// <summary>The title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The description, or null.</summary>
    public string? Description { get; set; }

    /// <summary>The precondition, or null.</summary>
    public string? Precondition { get; set; }

    /// <summary>The stored <c>{"steps":[…]}</c> document as JSON text (<c>steps::text</c>, so Dapper never sees jsonb).</summary>
    public string Steps { get; set; } = "{}";

    /// <summary>The priority.</summary>
    public string Priority { get; set; } = string.Empty;

    /// <summary>The state.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>The row's version.</summary>
    public int Version { get; set; }

    /// <summary>When the case was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the case last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The row as the wire type of route 14.</summary>
    public ManualCaseDetailDto ToDto() => new(
        CaseId,
        Code,
        ChecklistItemId,
        NodeId,
        Title,
        Description,
        Precondition,
        StepsOf(Steps),
        Priority,
        State,
        Version,
        CreatedAt,
        UpdatedAt);

    /// <summary>
    /// The steps of the stored document as the wire type. The document is validated against
    /// <c>authoring-steps.v1.json</c> before it is stored, so every element carries <c>no</c>, <c>action</c> and
    /// <c>expected</c>; a value that no longer parses (a hand-edited row) answers an empty list instead of a 500.
    /// </summary>
    /// <param name="document">The row's <c>steps</c> text.</param>
    private static List<ManualCaseStepDto> StepsOf(string document)
    {
        var steps = new List<ManualCaseStepDto>();

        try
        {
            using var parsed = JsonDocument.Parse(document);

            if (parsed.RootElement.ValueKind != JsonValueKind.Object
                || !parsed.RootElement.TryGetProperty("steps", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return steps;
            }

            foreach (var step in array.EnumerateArray())
            {
                if (step.ValueKind != JsonValueKind.Object
                    || !step.TryGetProperty("no", out var no)
                    || no.ValueKind != JsonValueKind.Number
                    || !step.TryGetProperty("action", out var action)
                    || action.ValueKind != JsonValueKind.String
                    || !step.TryGetProperty("expected", out var expected)
                    || expected.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var data = step.TryGetProperty("data", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;

                steps.Add(new ManualCaseStepDto(no.GetInt32(), action.GetString()!, expected.GetString()!, data));
            }
        }
        catch (JsonException)
        {
            return steps;
        }

        return steps;
    }
}
