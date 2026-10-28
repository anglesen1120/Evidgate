using System.Text.Json;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 11.5.1-T5.1 SQL level: migration 0053's <c>security_event</c> as the database sees it — the contract shape
/// of the table, the <c>(org_id, id)</c> composite key a later tenant FK targets, the two indexes the page reads
/// are served by, the append-only grant of the application role, and the tenant boundary itself (ENABLE + FORCE
/// row level security over <c>evidgate.current_org_id()</c>, 00 §5.4.6 / DECISIONS F5).
/// </summary>
/// <remarks>
/// The closed <c>type</c> and <c>severity</c> vocabularies are deliberately NOT database constraints: a new type
/// is a docs decision owned elsewhere (11.1.1-T4.1, V2's <c>injection_attempt</c>), so the table carries no
/// CHECK and no enum — asserted here so a later "helpful" constraint fails this test instead of shipping. The
/// policy is declared without <c>TO</c>, so it applies to PUBLIC (<c>polroles = {0}</c>, which
/// <see cref="PostgresTenancyFixture.PoliciesAsync"/> cannot name), hence the local projection below.
/// </remarks>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class SecurityEventSchemaTests
{
    private const string Table = "security_event";

    private readonly PostgresTenancyFixture _fixture;

    public SecurityEventSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Table_DeclaresTheContractShape()
    {
        var database = await _fixture.CreateDatabaseAsync();

        (await _fixture.TableExistsAsync(database, Table)).ShouldBeTrue();

        var columns = await _fixture.ColumnsAsync(database, Table);
        foreach (var prefix in new[]
        {
            "id:uuid:uuid:NO:",
            "org_id:uuid:uuid:NO:",
            "project_id:uuid:uuid:YES:",
            "type:text:text:NO:",
            "severity:text:text:NO:",
            "actor:jsonb:jsonb:NO:",
            "details:jsonb:jsonb:NO:",
            "request_id:text:text:YES:",
            "created_at:timestamp with time zone:timestamptz:NO:",
            "updated_at:timestamp with time zone:timestamptz:NO:",
            "version:integer:int4:NO:",
        })
        {
            columns.ShouldContain(
                column => column.StartsWith(prefix, StringComparison.Ordinal),
                $"{Table} needs the 0053 contract column shape '{prefix}'");
        }

        var constraints = await _fixture.ConstraintsAsync(database, Table);

        constraints.ShouldContain(
            constraint => constraint.StartsWith("p:security_event_pkey:", StringComparison.Ordinal)
                && constraint.Contains("(id)", StringComparison.Ordinal),
            $"{Table} is addressed by its id");

        // TL T3 / 00 §5.4.6: the composite-FK target every business table declares. 0053 declares it unnamed, so
        // PostgreSQL generates the constraint name; the definition is what the rule pins.
        constraints.ShouldContain(
            constraint => constraint.StartsWith("u:", StringComparison.Ordinal)
                && constraint.Contains("UNIQUE (org_id, id)", StringComparison.Ordinal),
            $"{Table} needs unique (org_id, id) as the composite tenant key (TL T3)");

        // No CHECK and no foreign key: the V1 type/severity sets are docs decisions, and no emitter may choose the
        // project reference for an org (0053 declares neither).
        constraints.ShouldNotContain(
            constraint => constraint.StartsWith("c:", StringComparison.Ordinal),
            "0053 deliberately carries no CHECK constraint: the closed type/severity sets are docs decisions");
        constraints.ShouldNotContain(
            constraint => constraint.StartsWith("f:", StringComparison.Ordinal),
            "0053 declares no foreign key on security_event");

        var indexes = await _fixture.IndexesAsync(database, Table);

        // Both page shapes end in the cursor tuple, in the ORDER BY's direction: an ascending id would leave
        // `order by created_at desc, id desc` unsorted, which is the defect the definition pins.
        indexes.ShouldContain(
            index => index.StartsWith("security_event_org_created_idx:", StringComparison.Ordinal)
                && index.Contains("(org_id, created_at DESC, id DESC)", StringComparison.Ordinal),
            "the unfiltered/time-window page is served by security_event_org_created_idx on (org_id, created_at DESC, id DESC)");
        indexes.ShouldContain(
            index => index.StartsWith("security_event_list_idx:", StringComparison.Ordinal)
                && index.Contains("(org_id, project_id, type, severity, created_at DESC, id DESC)", StringComparison.Ordinal),
            "the filter-led page is served by security_event_list_idx on (org_id, project_id, type, severity, created_at DESC, id DESC)");
        indexes.ShouldNotContain(
            index => index.Contains("created_at DESC, id)", StringComparison.Ordinal),
            "no page index may end in an ascending id: the cursor tuple is (created_at, id) descending");

        var rls = await _fixture.RowSecurityAsync(database, Table);
        rls.Enabled.ShouldBeTrue("DECISIONS F5: every business table is under ENABLE row level security");
        rls.Forced.ShouldBeTrue("DECISIONS F5: FORCE is what makes the owner obey its own policies too");

        var policies = await PolicyRowsAsync(database);
        var isolation = policies.ShouldHaveSingleItem();
        isolation.ShouldStartWith(
            "tenant_isolation:PUBLIC:*:(org_id = evidgate.current_org_id()):",
            Case.Sensitive,
            "the one policy is org_id = evidgate.current_org_id(), FOR ALL, for every role");

        // The write half: an ALL policy with no explicit WITH CHECK answers the USING expression for inserted rows,
        // so the row records no separate check ('-'). A later explicit WITH CHECK must be the same expression, or a
        // tenant could write a row it can never read back.
        var withCheck = isolation[(isolation.LastIndexOf(':') + 1)..];
        (withCheck is "-" or "(org_id = evidgate.current_org_id())")
            .ShouldBeTrue($"{Table}'s policy must not widen WITH CHECK beyond its USING expression, was '{withCheck}'");
    }

    /// <summary>
    /// A transaction without a tenant context sees no row — for the application role and, because of FORCE, for
    /// the owner too; the same statements inside a tenant scope see exactly that tenant's rows.
    /// </summary>
    [Fact]
    public async Task Query_WithoutTenantContext_ReturnsZeroRows()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var mine = Guid.CreateVersion7();
        var theirs = Guid.CreateVersion7();

        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database), mine))
        {
            await seed.ExecuteAsync(
                """
                insert into public.security_event (id, org_id, type, severity, actor)
                values (@id, @org_id, 'auth_failure', 'info', '{"kind":"system"}'::jsonb)
                """,
                ("id", Guid.CreateVersion7()),
                ("org_id", mine));
            await seed.CommitAsync();
        }

        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database), theirs))
        {
            await seed.ExecuteAsync(
                """
                insert into public.security_event (id, org_id, type, severity, actor)
                values (@id, @org_id, 'cross_tenant_probe', 'critical', '{"kind":"system"}'::jsonb)
                """,
                ("id", Guid.CreateVersion7()),
                ("org_id", theirs));
            await seed.CommitAsync();
        }

        // No GUC at all: current_org_id() is NULL, so no row matches — never an error, never every row.
        await using (var anonymous = await TenantScope.BeginAsync(_fixture.AppConnectionString(database)))
        {
            (await anonymous.ScalarAsync<long>("select count(*) from public.security_event")).ShouldBe(0);
        }

        // FORCE: the owner obeys the same policy, so its tenant-less connection sees nothing either.
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            (await owner.ScalarAsync<long>("select count(*) from public.security_event")).ShouldBe(0);
        }

        // Each tenant sees its own row and only its own.
        await using (var scoped = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), mine))
        {
            (await scoped.ScalarAsync<long>("select count(*) from public.security_event")).ShouldBe(1);
            (await scoped.ScalarAsync<long>(
                    "select count(*) from public.security_event where org_id = @id",
                    ("id", theirs)))
                .ShouldBe(0);
        }
    }

    /// <summary>
    /// Append-only for the application role: <c>0053</c> revokes UPDATE, DELETE and TRUNCATE, so the effective
    /// grant is select + insert — what an emitter needs and nothing that could rewrite history. Asserted on the
    /// ACL (<c>has_table_privilege</c>, which resolves role membership and default privileges, unlike reading
    /// <c>relacl</c>) and then on the wire, where the refused statements answer 42501 before RLS is consulted.
    /// </summary>
    [Fact]
    public async Task AppRole_CanInsertAndReadButNotRewrite()
    {
        var database = await _fixture.CreateDatabaseAsync();

        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", Table, "select")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", Table, "insert")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", Table, "update"))
            .ShouldBeFalse("0053 revokes UPDATE: a recorded event is append-only for the application role");
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", Table, "delete"))
            .ShouldBeFalse("0053 revokes DELETE: a recorded event is append-only for the application role");
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", Table, "truncate"))
            .ShouldBeFalse("0053 revokes TRUNCATE: a recorded event is append-only for the application role");
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_operator", Table, "select"))
            .ShouldBeTrue("the operator keeps 0001's default select on the table");

        var org = Guid.CreateVersion7();

        await using (var emitters = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            (await emitters.ExecuteAsync(
                    """
                    insert into public.security_event (id, org_id, type, severity, actor)
                    values (@id, @org_id, 'auth_failure', 'info', '{"kind":"system"}'::jsonb)
                    """,
                    ("id", Guid.CreateVersion7()),
                    ("org_id", org)))
                .ShouldBe(1, "select + insert are the application role's whole grant, and an emitter inserts");
        }

        // One scope per refused statement: an ACL error aborts its transaction (25P02 for anything after it),
        // so the second refusal needs its own transaction to be the ACL's answer and not the aborted state's.
        await using (var rewriting = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            (await rewriting.CaptureErrorAsync("update public.security_event set severity = 'critical'")).SqlState
                .ShouldBe("42501", "the application role cannot rewrite a recorded event");
        }

        await using (var removing = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            (await removing.CaptureErrorAsync("delete from public.security_event")).SqlState
                .ShouldBe("42501", "the application role cannot remove a recorded event");
        }
    }

    /// <summary>
    /// Both index-ordered page shapes. With sequential scans disabled on this transaction only (the 07.3.1-T2 /
    /// CaseAttempt precedent), the unfiltered statement — <c>org_id</c> alone — comes out of
    /// <c>security_event_org_created_idx</c> with no Sort node, and a page binding all three filter columns comes
    /// out of <c>security_event_list_idx</c> in cursor order: its equalities come first and its key ends in
    /// <c>(created_at desc, id desc)</c>. A partial bind is deliberately not asserted as sort-free — an unbound
    /// column precedes <c>created_at</c> in that key, so a Sort there is legitimate. Asserted on node types and
    /// index names only, never timing, so the test cannot flake on load.
    /// </summary>
    [Fact]
    public async Task Page_IsIndexOrderedWithoutASort()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();

        // A few hundred rows, each with its own created_at and a mixed type/severity, so the planner weighs
        // indexes for a real page instead of a single row and the full-equality filter has something to select.
        await using (var seed = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            await seed.ExecuteAsync(
                """
                insert into public.security_event (id, org_id, project_id, type, severity, actor, created_at)
                select gen_random_uuid(), @org_id, @project_id,
                       case when g % 4 = 0 then 'auth_failure' else 'support_access' end,
                       case when g % 3 = 0 then 'warning' else 'info' end,
                       '{"kind":"system"}'::jsonb, now() - g * interval '1 second'
                  from generate_series(1, 400) g
                """,
                ("org_id", org),
                ("project_id", project));
            await seed.CommitAsync();
        }

        await _fixture.ExecuteAsync(database, "analyze public.security_event");

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);
        await scope.SetAsync("enable_seqscan", "off");

        var unfiltered = await ExplainPageAsync(scope, org);
        unfiltered.NodeTypes.ShouldNotContain(
            "Sort",
            "the unfiltered page (org_id only) must come out of an index already in cursor order");
        unfiltered.IndexNames.ShouldContain(
            "security_event_org_created_idx",
            "the unfiltered/time-window page is served by (org_id, created_at desc, id desc)");

        var filtered = await ExplainPageAsync(scope, org, project: project, type: "auth_failure", severity: "warning");
        filtered.IndexNames.ShouldContain(
            "security_event_list_idx",
            "the full-equality page is narrowed through (org_id, project_id, type, severity, created_at desc, id desc); plan was: "
            + string.Join(", ", filtered.NodeTypes));

        // Ordering is what makes that index a page index rather than a filter index, and it holds on the ordered
        // index-scan path: a bitmap scan (PostgreSQL's cheaper choice for a small selective page, and it does not
        // preserve index order) legitimately sorts afterwards. Disabling bitmaps on this transaction only forces
        // the ordered path, so the assertion proves the capability instead of the planner's mood.
        await scope.SetAsync("enable_bitmapscan", "off");
        var ordered = await ExplainPageAsync(scope, org, project: project, type: "auth_failure", severity: "warning");
        ordered.NodeTypes.ShouldNotContain(
            "Sort",
            "on the ordered index path the full-equality page comes out in cursor order; plan was: "
            + string.Join(", ", ordered.NodeTypes) + " over " + string.Join(", ", ordered.IndexNames));
        ordered.IndexNames.ShouldContain(
            "security_event_list_idx",
            "the ordered path must be the filter-led index, not the org-created one");
    }

    /// <summary>Every policy of the table as <c>name:role:cmd:using:withcheck</c>, naming the PUBLIC role as
    /// <c>PUBLIC</c> (a <c>TO</c>-less policy stores <c>polroles = {0}</c>, which has no <c>pg_roles</c> row).
    /// </summary>
    private Task<IReadOnlyList<string>> PolicyRowsAsync(string database) =>
        _fixture.ColumnAsync<string>(
            database,
            """
            select p.polname || ':' || coalesce((select r.rolname from pg_roles r where r.oid = role_oid), 'PUBLIC')
                   || ':' || p.polcmd::text || ':' ||
                   coalesce(pg_get_expr(p.polqual, p.polrelid), '-') || ':' ||
                   coalesce(pg_get_expr(p.polwithcheck, p.polrelid), '-')
              from pg_policy p
              cross join lateral unnest(p.polroles) as role_oid
             where p.polrelid = 'public.security_event'::regclass
             order by p.polname
            """);

    /// <summary>
    /// <c>EXPLAIN (FORMAT JSON)</c> of the real page statement (<see cref="SecurityEventReadSql.Page"/>, reached
    /// through <c>InternalsVisibleTo</c>) with the handler's parameter set bound, so the assertion follows the
    /// served query and not a copy of it. Only the filters passed here are narrowed; the rest stay <c>null</c>,
    /// as the handler binds them.
    /// </summary>
    private static async Task<(List<string> NodeTypes, List<string> IndexNames)> ExplainPageAsync(
        TenantScope scope,
        Guid org,
        Guid? project = null,
        string? type = null,
        string? severity = null)
    {
        var plan = await scope.ScalarAsync<string>(
            "explain (format json) " + SecurityEventReadSql.Page,
            ("org_id", org),
            ("project_id", (object?)project ?? DBNull.Value),
            ("type", (object?)type ?? DBNull.Value),
            ("severity", (object?)severity ?? DBNull.Value),
            ("from", DBNull.Value),
            ("to", DBNull.Value),
            ("cursor_created_at", DBNull.Value),
            ("cursor_id", DBNull.Value),
            ("limit", 51));

        return PlanNodes(plan);
    }

    /// <summary>Every node type and index name in an <c>EXPLAIN (FORMAT JSON)</c> document, however the plan nests.</summary>
    private static (List<string> NodeTypes, List<string> IndexNames) PlanNodes(string json)
    {
        var nodeTypes = new List<string>();
        var indexNames = new List<string>();

        using var document = JsonDocument.Parse(json);
        foreach (var root in document.RootElement.EnumerateArray())
        {
            Walk(root.GetProperty("Plan"));
        }

        return (nodeTypes, indexNames);

        void Walk(JsonElement node)
        {
            if (node.TryGetProperty("Node Type", out var type))
            {
                nodeTypes.Add(type.GetString()!);
            }

            if (node.TryGetProperty("Index Name", out var index))
            {
                indexNames.Add(index.GetString()!);
            }

            if (node.TryGetProperty("Plans", out var plans))
            {
                foreach (var child in plans.EnumerateArray())
                {
                    Walk(child);
                }
            }
        }
    }
}
