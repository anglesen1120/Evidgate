using System.Globalization;
using Npgsql;
using Shouldly;
using Xunit;

namespace Persistence.IntegrationTests;

/// <summary>
/// The <c>authoring_change</c> schema as the migration leaves it: the composite target key that pins a
/// proposal to its own project, the state guard that freezes a queued row and every terminal one, the
/// payload and <c>affected_versions</c> bounds, and the indexes the list pages and the retention sweep
/// read. The index assertions are on the key order the queries need, not on a label.
/// </summary>
/// <remarks>
/// Rows are arranged over the owner connection: <c>organization</c>, <c>project</c>,
/// <c>membership</c>, <c>feature_node</c>, <c>checklist_item</c> and <c>authoring_change</c> all carry
/// the permissive <c>evidgate_owner</c> policy, and arranging rows is the one job a test may not do
/// through the tenant path. Every assertion about a trigger or a CHECK runs as the owner too — both are
/// the same for every role, and the app role's own path is covered by the module's suites.
/// </remarks>
[Collection(PersistenceSuite.Name)]
[Trait("Category", "Docker")]
public sealed class AuthoringChangeSchemaTests
{
    private readonly PostgresPersistenceFixture _fixture;

    public AuthoringChangeSchemaTests(PostgresPersistenceFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The target foreign key is composite <c>(org_id, project_id, target_id)</c>, so a proposal whose
    /// target lives in another project of the same org is refused at 23503 whatever the caller sends; the
    /// same insert against the target's own project is the positive control.
    /// </summary>
    [Fact]
    public async Task CrossProjectTarget_IsRejectedByTheFk()
    {
        await using var connection = await OpenOwnerAsync();
        var world = await SeedWorldAsync(connection);

        var accepted = await InsertChangeAsync(connection, world, world.Project, world.Item, "comment");
        (await CountAsync(connection, accepted)).ShouldBe(1, "the same-project target is the control");

        var crossProject = await InsertErrorAsync(connection, world, world.SiblingProject, world.Item, "comment");
        crossProject.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        crossProject.ConstraintName.ShouldBe("authoring_change_target_fk");

        // The other half of the same key: the target belongs to another organization altogether.
        var crossOrg = await InsertErrorAsync(connection, world, world.Project, world.ForeignItem, "comment");
        crossOrg.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        crossOrg.ConstraintName.ShouldBe("authoring_change_target_fk");
    }

    /// <summary>
    /// A queued proposal is never rewritten and a terminal row never moves again. The
    /// pending-to-applied update is the control that proves the guard lets the one legal transition through.
    /// </summary>
    [Fact]
    public async Task StateGuard_KeepsQueuedContentAndFreezesTerminalRows()
    {
        await using var connection = await OpenOwnerAsync();
        var world = await SeedWorldAsync(connection);
        var change = await InsertChangeAsync(connection, world, world.Project, world.Item, "comment");

        var rewritten = await UpdateErrorAsync(
            connection, change, "update public.authoring_change set payload = '{\"body\":\"other\"}'::jsonb where id = @id");
        rewritten.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        rewritten.MessageText.ShouldContain("never rewritten");

        (await ExecuteAsync(
            connection,
            """
            update public.authoring_change
               set state = 'applied', applied_at = now(), applied_batch_id = gen_random_uuid(),
                   applied_by_membership_id = @author
             where id = @id
            """,
            ("id", change),
            ("author", world.Author))).ShouldBe(1, "pending -> applied is the one legal transition");

        var reopened = await UpdateErrorAsync(
            connection, change, "update public.authoring_change set state = 'pending' where id = @id");
        reopened.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        reopened.MessageText.ShouldContain("terminal");

        var restamped = await UpdateErrorAsync(
            connection, change, "update public.authoring_change set applied_batch_id = gen_random_uuid() where id = @id");
        restamped.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        restamped.MessageText.ShouldContain("terminal");
    }

    /// <summary>
    /// <c>affected_versions</c> is a non-empty object of at most 50 entries, and the payload cap is
    /// measured on the stored <c>jsonb</c> text. That text spells one space after each <c>:</c> and
    /// <c>,</c>, so a raw payload of 32 768 bytes is 32 769 here and is refused — the headroom a
    /// route-level raw cap has to keep below the bound.
    /// </summary>
    [Fact]
    public async Task Bounds_AreCheckedOnTheStoredJsonbText()
    {
        await using var connection = await OpenOwnerAsync();
        var world = await SeedWorldAsync(connection);

        // A comment, so the structural trigger never enumerates these keys: `jsonb_object_keys` raises its own
        // 22023 for an array or a scalar before any CHECK is evaluated, and that would test the trigger instead.
        foreach (var (label, affected) in new[]
                 {
                     ("an array", "[]"),
                     ("an empty object", "{}"),
                     ("51 entries", VersionMap(51)),
                 })
        {
            var error = await InsertErrorAsync(
                connection, world, world.Project, world.Item, "comment", affectedVersions: affected);
            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation, $"{label} is not a version map");
            error.ConstraintName.ShouldBe("authoring_change_affected_versions_check");
        }

        (await InsertChangeAsync(
            connection, world, world.Project, world.Item, "delete", affectedVersions: VersionMap(50)))
            .ShouldNotBe(Guid.Empty, "50 entries are the documented maximum");

        // A one-member object: `{"a":"…"}` adds exactly one space (its colon), so the largest raw payload
        // whose text fits the bound is 32 767 bytes, and one byte more is refused.
        var accepted = PayloadOfRawLength(32_767);
        var refused = PayloadOfRawLength(32_768);

        // Each probe gets its own item: the accepted one leaves a pending structural row behind, and a second
        // structural proposal on the same item would trip the overlap guard before the payload bound is read.
        (await InsertChangeAsync(connection, world, world.Project, world.Item3, "delete", payload: accepted))
            .ShouldNotBe(Guid.Empty, "a raw payload of 32 767 bytes normalises to 32 768 and is inside the cap");

        // Item4 carries both refusals: neither insert leaves a row, so no pending proposal can mask the bound.
        var over = await InsertErrorAsync(connection, world, world.Project, world.Item4, "delete", payload: refused);
        over.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        over.ConstraintName.ShouldBe("authoring_change_payload_size_check");

        var notAnObject = await InsertErrorAsync(connection, world, world.Project, world.Item4, "delete", payload: "[]");
        notAnObject.ConstraintName.ShouldBe("authoring_change_payload_size_check");
    }

    /// <summary>
    /// The list pages and the retention sweep: the two list shapes walk <c>(org_id, project_id, created_at, id)</c>
    /// and the per-item shape <c>(org_id, project_id, target_id, created_at, id)</c> in cursor order, while
    /// the cross-organization purge batch leads with <c>discarded_at</c>. Sequential and bitmap scans are
    /// off inside one rolled-back transaction, so this asserts that those indexes are usable for the served
    /// shapes rather than the planner's choice on a small table.
    /// </summary>
    [Fact]
    public async Task ListAndPurgeQueries_UseTheirIndexes()
    {
        await using var connection = await OpenOwnerAsync();
        var world = await SeedWorldAsync(connection);
        await SeedPageRowsAsync(connection, world);
        await ExecuteAsync(connection, "analyze public.authoring_change");

        var indexes = (await ScalarAsync<string>(
            connection,
            """
            select coalesce(string_agg(indexname || ': ' || indexdef, E'\n'), '')
              from pg_indexes
             where schemaname = 'public' and tablename = 'authoring_change'
            """))!;

        indexes.ShouldContain("(org_id, project_id, created_at, id)", customMessage: "the default page is a keyset walk");
        indexes.ShouldContain("(org_id, project_id, target_id, created_at, id)", customMessage: "one item's history is the same walk");
        indexes.ShouldNotContain(
            "(org_id, project_id, state, created_at, id)",
            customMessage: "a filter-led list index cannot serve the default page's cursor order");

        var purgeIndex = (await ScalarAsync<string>(
            connection,
            "select pg_get_indexdef(indexrelid) from pg_index where indrelid = 'public.authoring_change'::regclass "
            + "and indexrelid = 'public.authoring_change_discarded_idx'::regclass"))!;
        purgeIndex.ShouldContain("(discarded_at)");
        purgeIndex.ShouldNotContain("org_id", customMessage: "the sweep orders across every organization");

        await using var scope = await connection.BeginTransactionAsync(Token);
        await ExecuteAsync(connection, scope, "set local enable_seqscan = off");
        await ExecuteAsync(connection, scope, "set local enable_bitmapscan = off");

        (string Name, object Value)[] cursor =
            [("cursor_created_at", DateTimeOffset.UnixEpoch), ("cursor_id", Guid.Empty)];

        var page = await ExplainAsync(
            connection,
            scope,
            """
            select c.id from public.authoring_change c
             where c.org_id = @org_id and c.project_id = @project_id
               and (c.created_at, c.id) > (@cursor_created_at::timestamptz, @cursor_id::uuid)
             order by c.created_at, c.id
             limit 50
            """,
            [("org_id", world.Org), ("project_id", world.Project), .. cursor]);
        page.ShouldContain("\"Index Name\": \"authoring_change_list_idx\"", Case.Sensitive);
        page.ShouldNotContain("Seq Scan", Case.Insensitive);
        page.ShouldNotContain("\"Node Type\": \"Sort\"", Case.Sensitive);

        var filtered = await ExplainAsync(
            connection,
            scope,
            """
            select c.id from public.authoring_change c
             where c.org_id = @org_id and c.project_id = @project_id
               and (c.created_at, c.id) > (@cursor_created_at::timestamptz, @cursor_id::uuid)
               and c.state = @state and c.change_type = @change_type
             order by c.created_at, c.id
             limit 50
            """,
            [("org_id", world.Org), ("project_id", world.Project), ("state", "applied"), ("change_type", "comment"), .. cursor]);
        filtered.ShouldContain("\"Index Name\": \"authoring_change_list_idx\"", Case.Sensitive);
        filtered.ShouldNotContain("Seq Scan", Case.Insensitive);
        filtered.ShouldNotContain("\"Node Type\": \"Sort\"", Case.Sensitive);

        var byTarget = await ExplainAsync(
            connection,
            scope,
            """
            select c.id from public.authoring_change c
             where c.org_id = @org_id and c.project_id = @project_id and c.target_id = @target_id
               and (c.created_at, c.id) > (@cursor_created_at::timestamptz, @cursor_id::uuid)
             order by c.created_at, c.id
             limit 50
            """,
            [("org_id", world.Org), ("project_id", world.Project), ("target_id", world.Item), .. cursor]);
        byTarget.ShouldContain("\"Index Name\": \"authoring_change_target_idx\"", Case.Sensitive);
        byTarget.ShouldNotContain("Seq Scan", Case.Insensitive);
        byTarget.ShouldNotContain("\"Node Type\": \"Sort\"", Case.Sensitive);

        var purge = await ExplainAsync(
            connection,
            scope,
            """
            select c.id from public.authoring_change c
             where c.state = 'discarded' and c.discarded_at < now() - interval '180 days'
             order by c.discarded_at
             limit 100
            """,
            []);
        purge.ShouldContain("\"Index Name\": \"authoring_change_discarded_idx\"", Case.Sensitive);
        purge.ShouldNotContain("Seq Scan", Case.Insensitive);
        purge.ShouldNotContain("\"Node Type\": \"Sort\"", Case.Sensitive);

        await scope.RollbackAsync(Token);
    }

    // ------------------------------------------------------------------ seeding

    /// <summary>One organization with two projects and two items, plus a foreign organization with an item.</summary>
    private sealed record World(
        Guid Org,
        Guid Project,
        Guid Item,
        Guid Item2,
        Guid Item3,
        Guid Item4,
        Guid SiblingProject,
        Guid ForeignOrg,
        Guid ForeignItem,
        Guid Author);

    private static async Task<World> SeedWorldAsync(NpgsqlConnection connection)
    {
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var sibling = Guid.CreateVersion7();
        var foreignOrg = Guid.CreateVersion7();
        var foreignProject = Guid.CreateVersion7();
        var author = Guid.CreateVersion7();

        await SeedOrganizationAsync(connection, org, "Schema org");
        await SeedOrganizationAsync(connection, foreignOrg, "Foreign org");
        await ExecuteAsync(
            connection,
            "insert into public.membership (id, org_id, invited_email) values (@id, @org_id, @email)",
            ("id", author), ("org_id", org), ("email", $"author-{org:N}"[..8] + "@example.test"));

        await SeedProjectAsync(connection, org, project, "one");
        await SeedProjectAsync(connection, org, sibling, "two");
        await SeedProjectAsync(connection, foreignOrg, foreignProject, "one");

        var item = await SeedItemAsync(connection, org, project, "Item one", position: 0);
        var item2 = await SeedItemAsync(connection, org, project, "Item two", position: 1);
        var item3 = await SeedItemAsync(connection, org, project, "Item three", position: 2);
        var item4 = await SeedItemAsync(connection, org, project, "Item four", position: 3);
        var foreignItem = await SeedItemAsync(connection, foreignOrg, foreignProject, "Foreign item", position: 0);

        return new World(org, project, item, item2, item3, item4, sibling, foreignOrg, foreignItem, author);
    }

    private static Task<int> SeedOrganizationAsync(NpgsqlConnection connection, Guid id, string name) =>
        ExecuteAsync(
            connection,
            "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
            ("id", id), ("name", name), ("slug", Slug("org")));

    private static Task<int> SeedProjectAsync(NpgsqlConnection connection, Guid org, Guid project, string name) =>
        ExecuteAsync(
            connection,
            "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
            ("id", project), ("org_id", org), ("name", $"Project {name}"), ("slug", Slug(name)));

    private static async Task<Guid> SeedItemAsync(NpgsqlConnection connection, Guid org, Guid project, string title, int position)
    {
        var node = Guid.CreateVersion7();
        var item = Guid.CreateVersion7();

        await ExecuteAsync(
            connection,
            "insert into public.feature_node (id, org_id, project_id, kind, title, position) "
            + "values (@id, @org_id, @project_id, 'module', @title, @position)",
            ("id", node), ("org_id", org), ("project_id", project), ("title", title), ("position", position));
        await ExecuteAsync(
            connection,
            "insert into public.checklist_item "
            + "(id, org_id, project_id, feature_node_id, title, priority, state, source, position) "
            + "values (@id, @org_id, @project_id, @node, @title, 'high', 'active', 'manual', @position)",
            ("id", item), ("org_id", org), ("project_id", project), ("node", node), ("title", title), ("position", position));

        return item;
    }

    /// <summary>
    /// 400 applied and 200 long-discarded proposals of the project, spread over 40 items so the per-item page
    /// is a selective equality and both partial indexes are populated.
    /// </summary>
    private static async Task SeedPageRowsAsync(NpgsqlConnection connection, World world)
    {
        var node = Guid.CreateVersion7();

        await ExecuteAsync(
            connection,
            "insert into public.feature_node (id, org_id, project_id, kind, title, position) "
            + "values (@id, @org_id, @project_id, 'module', 'Spread node', 4)",
            ("id", node), ("org_id", world.Org), ("project_id", world.Project));
        await ExecuteAsync(
            connection,
            """
            insert into public.checklist_item
              (id, org_id, project_id, feature_node_id, title, priority, state, source, position)
            select gen_random_uuid(), @org_id, @project_id, @node, 'Spread item ' || g, 'high', 'active', 'manual', 100 + g
              from generate_series(1, 40) g
            """,
            ("org_id", world.Org), ("project_id", world.Project), ("node", node));

        await ExecuteAsync(
            connection,
            """
            with page as (
              select g,
                     case when g % 40 = 0
                          then (select c2.id from public.checklist_item c2
                                 where c2.project_id = @project_id and c2.position = 0)
                          else (select c2.id from public.checklist_item c2
                                 where c2.project_id = @project_id and c2.position = 100 + g % 40)
                     end as target_id
                from generate_series(1, 400) g)
            insert into public.authoring_change
              (id, org_id, project_id, target_id, change_type, payload, base_version, affected_versions,
               state, author_membership_id, applied_at, applied_batch_id, applied_by_membership_id,
               created_at, updated_at)
            select gen_random_uuid(), @org_id, @project_id, page.target_id, 'comment', '{"body":"probe"}'::jsonb, 1,
                   jsonb_build_object(page.target_id::text, 1), 'applied', @author,
                   now() - page.g * interval '1 minute', gen_random_uuid(), @author,
                   now() - page.g * interval '1 minute', now() - page.g * interval '1 minute'
              from page
            """,
            ("org_id", world.Org), ("project_id", world.Project), ("author", world.Author));

        await ExecuteAsync(
            connection,
            """
            with page as (
              select g,
                     case when g % 40 = 0
                          then (select c2.id from public.checklist_item c2
                                 where c2.project_id = @project_id and c2.position = 0)
                          else (select c2.id from public.checklist_item c2
                                 where c2.project_id = @project_id and c2.position = 100 + g % 40)
                     end as target_id
                from generate_series(1, 200) g)
            insert into public.authoring_change
              (id, org_id, project_id, target_id, change_type, payload, base_version, affected_versions,
               state, author_membership_id, discarded_at, discarded_by_membership_id, created_at, updated_at)
            select gen_random_uuid(), @org_id, @project_id, page.target_id, 'comment', '{"body":"probe"}'::jsonb, 1,
                   jsonb_build_object(page.target_id::text, 1), 'discarded', @author,
                   now() - make_interval(days => 181 + page.g % 5), @author,
                   now() - make_interval(days => 200), now() - make_interval(days => 200)
              from page
            """,
            ("org_id", world.Org), ("project_id", world.Project), ("author", world.Author));
    }

    /// <summary>A version map of <paramref name="keys"/> entries, one per fresh item id.</summary>
    private static string VersionMap(int keys) =>
        "{" + string.Join(", ", Enumerable.Range(0, keys)
            .Select(index => $"\"{Guid.CreateVersion7():D}\": {index + 1}")) + "}";

    /// <summary>The one-member payload <c>{"a":"…"}</c> whose minified text is exactly <paramref name="length"/> bytes.</summary>
    private static string PayloadOfRawLength(int length)
    {
        const string prefix = "{\"a\":\"";
        const string suffix = "\"}";

        return prefix + new string('x', length - prefix.Length - suffix.Length) + suffix;
    }

    private static string Slug(string prefix) =>
        $"{prefix}-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12]}";

    // ------------------------------------------------------------------ statements

    private async Task<NpgsqlConnection> OpenOwnerAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var connection = new NpgsqlConnection(_fixture.OwnerConnectionString(database));
        await connection.OpenAsync(Token);

        return connection;
    }

    private static Task<Guid> InsertChangeAsync(
        NpgsqlConnection connection,
        World world,
        Guid project,
        Guid target,
        string changeType,
        string? payload = null,
        string? affectedVersions = null) =>
        ScalarAsync<Guid>(
            connection,
            """
            insert into public.authoring_change
              (id, org_id, project_id, target_id, change_type, payload, base_version, affected_versions,
               author_membership_id)
            values (gen_random_uuid(), @org_id, @project_id, @target_id, @change_type, @payload::jsonb, 1,
                    @affected::jsonb, @author)
            returning id
            """,
            ("org_id", world.Org),
            ("project_id", project),
            ("target_id", target),
            ("change_type", changeType),
            ("payload", payload ?? "{\"body\":\"probe\"}"),
            ("affected", affectedVersions ?? $"{{\"{target:D}\": 1}}"),
            ("author", world.Author));

    private static async Task<PostgresException> InsertErrorAsync(
        NpgsqlConnection connection,
        World world,
        Guid project,
        Guid target,
        string changeType,
        string? payload = null,
        string? affectedVersions = null) =>
        await Should.ThrowAsync<PostgresException>(
            () => InsertChangeAsync(connection, world, project, target, changeType, payload, affectedVersions));

    private static async Task<PostgresException> UpdateErrorAsync(NpgsqlConnection connection, Guid id, string sql) =>
        await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connection, sql, ("id", id)));

    private static Task<long> CountAsync(NpgsqlConnection connection, Guid id) =>
        ScalarAsync<long>(connection, "select count(*) from public.authoring_change where id = @id", ("id", id));

    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] values) =>
        await ExecuteAsync(connection, transaction: null, sql, values);

    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        params (string Name, object Value)[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(Token))!;
    }

    private static async Task<string> ExplainAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        (string Name, object Value)[] values)
    {
        await using var command = new NpgsqlCommand("explain (format json) " + sql, connection, transaction);
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (string)(await command.ExecuteScalarAsync(Token))!;
    }
}
