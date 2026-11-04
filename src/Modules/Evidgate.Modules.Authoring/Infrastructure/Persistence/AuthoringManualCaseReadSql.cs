using System.Text.Json;
using Evidgate.Modules.Authoring.Contracts;

namespace Evidgate.Modules.Authoring.Infrastructure.Persistence;

/// <summary>
/// The statements and Dapper rows of the manual test case read surface (routes 12 and 14) and of the four
/// guards a case route takes before its writer: the project-liveness probe (<see cref="ProjectExists"/>), the
/// caller's membership, the create's target item probe (<see cref="CaseParent"/>), and the two locks (the case's
/// <c>If-Match</c> precondition and the move's target item).
/// </summary>
/// <remarks>
/// <para>
/// One statement per read: the list is one keyset page (+ the project probe and the linked-case id read that precedes
/// it, <see cref="LinkedCaseIds"/>), and the detail is one row whose own
/// <c>exists</c> subquery carries the project's liveness — no second round trip, and no case of a soft-deleted project
/// is ever served. Every statement filters on <c>org_id</c> <em>and</em> the project of the URL, so a case of another
/// project of the same tenant is indistinguishable from a missing one (404) instead of being readable by id.
/// </para>
/// <para>
/// The rows are mutable classes with settable properties: Dapper binds them through
/// <c>DefaultTypeMap.MatchNamesWithUnderscores</c> (switched on by <c>AddEvidgateTenancy</c>), and a positional record
/// cannot bind a projection with extra columns.
/// </para>
/// </remarks>
internal static class AuthoringManualCaseReadSql
{
    /// <summary>
    /// The project probe of the list (the catalog's own statement, 03.1.2), with the tenant made explicit rather than
    /// left to row level security.
    /// </summary>
    /// <remarks>
    /// The predicate is the policy form itself — <c>org_id = evidgate.current_org_id()</c>, the helper of migration
    /// 0002:30-44 that the policies must use instead of raw <c>current_setting</c> (0003:22-24, TL T2; the project
    /// policy at 0005:69-70) — so the row must belong to the organization the connection runs as and an unset tenant
    /// setting answers "no project" instead of every project. The <c>status</c> clause is the part a row level policy
    /// does not express, which is where the defence in depth lives (security S-10). It takes no <c>@org_id</c>
    /// parameter on purpose: the statement is shared with the case list's handler and with the item and case routes'
    /// probe, and a binding the caller could forget is a worse guard than one the scope always sets.
    /// </remarks>
    internal const string ProjectExists = """
        select 1
          from public.project p
         where p.id = @project_id
           and p.status <> 'deleted'
           and p.org_id = evidgate.current_org_id()
        """;

    /// <summary>
    /// The create's placement probe: the checklist item a new case names, as the writer's own
    /// <c>CheckCaseParentAsync</c> reads it, answered as one word — <c>live</c>, <c>deleted</c>, <c>retired</c> — or
    /// nothing at all when this project holds no such item (another project's item is indistinguishable).
    /// </summary>
    /// <remarks>
    /// It exists so a create the writer would refuse burns no code: the allocator commits on its own connection, so its
    /// number is spent even when the caller's transaction rolls back. The verdict is the writer's, not a second
    /// rule — <c>ManualCaseCommands.CaseParentRefusalAsync</c> maps each word to the writer's own code, reason and
    /// member path, and the writer still decides again under its locks.
    /// </remarks>
    internal const string CaseParent = """
        select case
                 when i.deleted_at is not null then 'deleted'
                 when i.state = 'retired' then 'retired'
                 else 'live'
               end
          from public.checklist_item i
         where i.org_id = @org_id
           and i.project_id = @project_id
           and i.id = @item_id
        """;

    /// <summary>
    /// The authoring member behind the caller's user id, or nothing when the caller is not a member of the tenant.
    /// </summary>
    /// <remarks>
    /// The authoring version row records <c>actor_membership_id</c>, and a handler holds the principal rather than
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

    /// <summary>
    /// The catalog cases the project's links name, each once: the candidate set the catalog then tests for its archived
    /// status. Bounded by the link count instead of the project's catalog, and served by <c>manual_case_link_uq</c>.
    /// </summary>
    internal const string LinkedCaseIds = """
        select distinct l.test_case_id
          from public.manual_case_link l
         where l.org_id = @org_id
           and l.project_id = @project_id
        """;

    /// <summary>The list projection: the row a page carries, joined to the item for its node.</summary>
    private const string CaseListColumns = """
        m.id as case_id, m.code, m.checklist_item_id, i.feature_node_id as node_id,
        m.title, m.priority, m.state, m.version
        """;

    /// <summary>
    /// The authoring coverage of one case, computed in the page's own statement: the count and ids of the links whose
    /// catalog case is not archived. A link row names a case of the same project by construction (its composite FK and
    /// the insert trigger), so the only exclusion left is the catalog's own archived status, which the caller resolves
    /// through the catalog's contract because it may not read <c>test_case</c> itself.
    /// </summary>
    /// <remarks>
    /// Served by <c>manual_case_link_case_idx</c>: <c>test_case_id</c> is included in its leaf pages, so a case with
    /// many links is still one index-only lookup, and a case with none costs one empty probe. The archived ids travel
    /// as a <c>uuid[]</c> over the project's own linked cases, so the membership test is one array scan however many
    /// links a case holds; reading that set before this statement leaves one request eventual, never torn, against the
    /// catalog's later writes.
    /// </remarks>
    private const string CaseCoverage = """
        left join lateral (
          select count(*) as linked_live_count,
                 coalesce(array_agg(l.test_case_id order by l.test_case_id), '{}') as linked_case_ids
            from public.manual_case_link l
           where l.org_id = m.org_id
             and l.project_id = m.project_id
             and l.manual_test_case_id = m.id
             and not (l.test_case_id = any(@archived_case_ids::uuid[]))
        ) coverage on true
        """;

    /// <summary>The <c>covered</c> filter: the same case-level number the page projects, tested once.</summary>
    private const string CaseCoveredFilter = """
        and (@covered::boolean is null
             or (@covered and coverage.linked_live_count > 0)
             or (not @covered and coverage.linked_live_count = 0))
        """;

    /// <summary>
    /// First page of a project's live manual test cases, ordered <c>(code, id)</c>.
    /// </summary>
    /// <remarks>
    /// The order is the plan's, and it is <b>lexical</b> on <c>code</c>: <c>SHOP-1000</c> sorts before <c>SHOP-999</c>
    /// because the code is text. Migration 0039 stores no allocated number beside it (<c>manual_test_case</c> has
    /// <c>code</c> only; <c>authoring_code_counter</c> holds the counter per project), so a numeric order cannot be
    /// derived without re-parsing the code in SQL. The keyset predicate compares the same two columns, so paging is
    /// total and stable; a client that wants numeric order sorts the page it holds (it is bounded by <c>limit</c>).
    /// </remarks>
    internal const string CasesFirstPage = $"""
        select {CaseListColumns}, coverage.linked_live_count, coverage.linked_case_ids
          from public.manual_test_case m
          join public.checklist_item i
            on i.org_id = m.org_id
           and i.project_id = m.project_id
           and i.id = m.checklist_item_id
          {CaseCoverage}
         where m.org_id = @org_id
           and m.project_id = @project_id
           and m.deleted_at is null
           and (@item_id::uuid is null or m.checklist_item_id = @item_id)
           and (@node_id::uuid is null or i.feature_node_id = @node_id)
           and (@state::text is null or m.state = @state)
           and (@priority::text is null or m.priority = @priority)
           and (@q::text is null or position(lower(@q) in lower(m.title)) > 0)
           {CaseCoveredFilter}
         order by m.code, m.id
         limit @take
        """;

    /// <summary>
    /// The next page: the first page plus the row comparison on the pair the order is built from. The cursor carries
    /// both members, so the seek is a single index range scan on <c>(org_id, project_id, code)</c>.
    /// </summary>
    internal const string CasesNextPage = $"""
        select {CaseListColumns}, coverage.linked_live_count, coverage.linked_case_ids
          from public.manual_test_case m
          join public.checklist_item i
            on i.org_id = m.org_id
           and i.project_id = m.project_id
           and i.id = m.checklist_item_id
          {CaseCoverage}
         where m.org_id = @org_id
           and m.project_id = @project_id
           and m.deleted_at is null
           and (m.code, m.id) > (@cursor_code, @cursor_id)
           and (@item_id::uuid is null or m.checklist_item_id = @item_id)
           and (@node_id::uuid is null or i.feature_node_id = @node_id)
           and (@state::text is null or m.state = @state)
           and (@priority::text is null or m.priority = @priority)
           and (@q::text is null or position(lower(@q) in lower(m.title)) > 0)
           {CaseCoveredFilter}
         order by m.code, m.id
         limit @take
        """;

    /// <summary>One live case of the project with everything route 14 answers and route 15 needs to rebuild the snapshot.</summary>
    /// <remarks>
    /// The project's liveness is part of the same statement: route 14 has no separate probe, and a case of a
    /// soft-deleted project would otherwise stay readable — its row is still stored and the project id still matches.
    /// The clause is the list route's own predicate, so a deleted project answers the same 404 to both reads.
    /// </remarks>
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
           and exists (select 1
                         from public.project p
                        where p.id = m.project_id
                          and p.org_id = m.org_id
                          and p.status <> 'deleted')
        """;

    /// <summary>
    /// The <c>If-Match</c> precondition: the case row locked <c>for update</c>, so two writers of one case
    /// serialise here and the second one compares its stale version to the fresh row. Its column list is the detail's,
    /// so the same row type carries the snapshot a PATCH rebuilds.
    /// </summary>
    /// <remarks>
    /// The item's node is a <b>correlated subquery</b>, never a join: <c>manual_test_case.checklist_item_id</c> is the
    /// one column a concurrent command changes (route 19), and a READ COMMITTED <c>FOR UPDATE</c> that blocks and is
    /// then re-checked against the updated tuple can drop the row when the search condition spans the joined table —
    /// measured in <c>AuthoringManualCaseTests.MoveBetweenItems_Concurrent_412</c>, where the loser of two concurrent
    /// moves read "no such case" (404) instead of the 412. The locked row's own columns are the only search
    /// condition here, so its identity, item and version are always the tuple the lock holder committed.
    /// </remarks>
    internal const string LockCase = """
        select m.id as case_id, m.code, m.checklist_item_id,
               (select i.feature_node_id
                  from public.checklist_item i
                 where i.org_id = m.org_id
                   and i.project_id = m.project_id
                   and i.id = m.checklist_item_id) as node_id,
               m.title, m.description, m.precondition, m.steps::text as steps,
               m.priority, m.state, m.version, m.created_at, m.updated_at
          from public.manual_test_case m
         where m.org_id = @org_id
           and m.project_id = @project_id
           and m.id = @case_id
           and m.deleted_at is null
           for update of m
        """;

    /// <summary>
    /// The target item of a move, locked <c>for update</c> <em>before</em> the case: the writer locks
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

    /// <summary>How many of its links reach a live catalog case (archived cases are not counted).</summary>
    public int LinkedLiveCount { get; set; }

    /// <summary>The live catalog case ids behind <see cref="LinkedLiveCount"/>, ascending.</summary>
    public Guid[] LinkedCaseIds { get; set; } = [];

    /// <summary>The row as the wire type of route 12.</summary>
    public ManualCaseListItemDto ToDto() => new(
        CaseId,
        Code,
        ChecklistItemId,
        NodeId,
        Title,
        Priority,
        State,
        Version,
        LinkedLiveCount,
        LinkedCaseIds);
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
