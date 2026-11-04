using System.Net;
using System.Net.Http.Headers;
using Evidgate.Modules.Authoring.Domain;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The batched apply (13.1.2 AC4–AC8, AC11, AC13, AC14) against real Postgres: one transaction that writes at most one
/// version per changed entity, refuses a stale selection without writing anything, and answers a replay with the first
/// result.
/// </summary>
/// <remarks>
/// The cases live in the class's other files, one per concern (the batch's own writes, its refusals, and its statement
/// budget), so no file carries the whole subject. The helpers here are shared by all of them.
/// </remarks>
[Trait("Category", "Docker")]
public sealed partial class AuthoringApplyTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AuthoringApplyTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>The ETag a client computes itself: the order hash of every affected item with its current version.</summary>
    /// <param name="versions">The affected items with their current versions.</param>
    private static string BatchEtag(params (Guid Id, int Version)[] versions) => AuthoringHashes.OrderHash(versions);

    /// <summary>Queues a delete change and answers its id.</summary>
    /// <param name="world">The test world.</param>
    /// <param name="item">The item to delete.</param>
    private static async Task<Guid> QueueDeleteAsync(World world, Guid item)
    {
        var queued = await world.Client.SendAsync(
            HttpMethod.Post,
            world.Changes,
            world.Admin,
            new { target_kind = "checklist_item", target_id = item, change_type = "delete", payload = new { } },
            NewKey());

        queued.Status.ShouldBe(HttpStatusCode.Created);
        queued.Text("state").ShouldBe("pending");

        return queued.Id("change_id");
    }

    /// <summary>Queues a comment change and answers its id.</summary>
    /// <param name="world">The test world.</param>
    /// <param name="item">The commented item.</param>
    /// <param name="body">The comment body.</param>
    private static async Task<Guid> QueueCommentAsync(World world, Guid item, string body)
    {
        var queued = await world.Client.SendAsync(
            HttpMethod.Post,
            world.Changes,
            world.Admin,
            new { target_kind = "checklist_item", target_id = item, change_type = "comment", payload = new { body } },
            NewKey());

        queued.Status.ShouldBe(HttpStatusCode.Created);

        return queued.Id("change_id");
    }

    /// <summary>Queues a merge of <paramref name="source"/> into <paramref name="survivor"/> and answers its id.</summary>
    /// <param name="world">The test world.</param>
    /// <param name="survivor">The surviving item.</param>
    /// <param name="source">The item merged into it.</param>
    private static async Task<Guid> QueueMergeAsync(World world, Guid survivor, Guid source)
    {
        var queued = await world.Client.SendAsync(
            HttpMethod.Post,
            world.Changes,
            world.Admin,
            new
            {
                target_kind = "checklist_item",
                target_id = survivor,
                change_type = "merge",
                payload = new { survivor_item_id = survivor, source_item_ids = new[] { survivor, source } },
            },
            NewKey());

        queued.Status.ShouldBe(HttpStatusCode.Created);

        return queued.Id("change_id");
    }

    /// <summary>Queues a split of <paramref name="item"/> into two items and answers its id.</summary>
    /// <param name="world">The test world.</param>
    /// <param name="item">The item being split.</param>
    private static async Task<Guid> QueueSplitAsync(World world, Guid item)
    {
        var queued = await world.Client.SendAsync(
            HttpMethod.Post,
            world.Changes,
            world.Admin,
            new
            {
                target_kind = "checklist_item",
                target_id = item,
                change_type = "split",
                payload = new { new_items = new[] { new { title = "Split first" }, new { title = "Split second" } } },
            },
            NewKey());

        queued.Status.ShouldBe(HttpStatusCode.Created);

        return queued.Id("change_id");
    }

    /// <summary>Sends one apply, authorized as the founder.</summary>
    /// <param name="world">The test world.</param>
    /// <param name="changeIds">The selection.</param>
    /// <param name="etag">The <c>If-Match</c> value, without quotes.</param>
    /// <param name="key">A reused idempotency key, or null for a fresh one.</param>
    private static Task<ApiAnswer> ApplyAsync(World world, Guid[] changeIds, string etag, string? key = null) =>
        world.Client.SendAsync(
            HttpMethod.Post,
            $"{world.Changes}:apply",
            world.Admin,
            new { change_ids = changeIds },
            key ?? NewKey(),
            $"\"{etag}\"");

    /// <summary>Discards one change, authorized as the founder.</summary>
    /// <param name="world">The test world.</param>
    /// <param name="changeId">The change to discard.</param>
    private static Task<ApiAnswer> DiscardAsync(World world, Guid changeId) =>
        world.Client.SendAsync(
            HttpMethod.Post,
            $"{world.Changes}/{changeId:D}/discard",
            world.Admin,
            null,
            NewKey());

    /// <summary>Creates one live checklist item under the seeded category and answers its id and version.</summary>
    /// <param name="world">The test world.</param>
    /// <param name="title">The item title.</param>
    private static async Task<(Guid Id, int Version)> CreateItemAsync(World world, string title)
    {
        var created = await world.Client.SendAsync(
            HttpMethod.Post,
            world.Items,
            world.Admin,
            new { node_id = world.Category, title, priority = "high" },
            NewKey());

        created.Status.ShouldBe(HttpStatusCode.Created);

        return (created.Id("item_id"), created.Body.GetProperty("version").GetInt32());
    }

    /// <summary>Counts the version rows one item holds, as the owner (the app role cannot read another tenant's rows).</summary>
    /// <param name="world">The test world.</param>
    /// <param name="item">The item.</param>
    private static Task<long> CountVersionsAsync(World world, Guid item) =>
        CountAsync(
            world,
            "select count(*) from public.authoring_version where entity_id = @item",
            ("item", item));

    /// <summary>Counts the audit rows of an action in this organization.</summary>
    /// <param name="world">The test world.</param>
    /// <param name="action">The audited action.</param>
    private static Task<long> AuditCountAsync(World world, string action) =>
        CountAsync(
            world,
            "select count(*) from public.audit_log where org_id = @org and action = @action",
            ("org", world.Org),
            ("action", action));

    /// <summary>Seeds one live manual case on an item, as the owner: the guard only cares that the row exists.</summary>
    /// <param name="world">The test world.</param>
    /// <param name="item">The item the case covers.</param>
    private static Task InsertCaseAsync(World world, Guid item) =>
        ExecuteAsync(
            world,
            """
            insert into public.manual_test_case (id, org_id, project_id, checklist_item_id, code, title, steps, priority, state, source)
            values (@id, @org, @project, @item, @code, @title, '[]'::jsonb, 'high', 'active', 'manual')
            """,
            ("id", Guid.CreateVersion7()),
            ("org", world.Org),
            ("project", world.Project),
            ("item", item),
            ("code", "qa_case_" + Guid.NewGuid().ToString("N")[..8]),
            ("title", "Pay with a saved card"));

    /// <summary>
    /// Waits until <paramref name="holderPid"/>'s backend is observed blocking another one: the observable proof that
    /// an apply really overlapped the transaction under test, instead of a sleep that only hopes it did. A row lock's
    /// waiter parks on the holder's transaction id, so <c>pg_locks</c> carries no relation for it; <c>pg_blocking_pids</c>
    /// resolves that wait to the holder, and unlike <c>wait_event_type</c> it is not hidden for another role's backend.
    /// Bounded by attempts, 25 ms apart.
    /// </summary>
    /// <param name="world">The test world.</param>
    /// <param name="holderPid">The backend that holds the selected change row.</param>
    /// <param name="apply">The in-flight apply: a completed answer means it never blocked, so the wait is over.</param>
    private static async Task WaitForChangeRowWaiterAsync(World world, int holderPid, Task<ApiAnswer> apply)
    {
        for (var attempt = 0; attempt < 800; attempt++)
        {
            if (apply.IsCompleted)
            {
                // A completed apply never blocked, so no waiter will ever appear; report that instead of timing out.
                // The answer itself stays with the caller, which owns the task (VSTHRD003 forbids awaiting it here).
                throw new InvalidOperationException(
                    apply.IsFaulted
                        ? $"the apply failed instead of waiting on the row: {apply.Exception?.GetBaseException().Message}"
                        : "the apply completed instead of waiting on the row");
            }

            var waiting = await IntAsync(
                world,
                """
                select count(*)::int
                  from pg_stat_activity a
                 where a.datname = current_database()
                   and a.pid <> @holder
                   and @holder = any(pg_blocking_pids(a.pid))
                """,
                ("holder", holderPid));

            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException($"no backend was ever observed waiting on a lock held by backend {holderPid}");
    }

    /// <summary>A count as the owner connection: the version and row probes the routes cannot read back.</summary>
    /// <param name="world">The test world.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="parameters">Positional parameters.</param>
    private static Task<long> CountAsync(World world, string sql, params (string Name, object Value)[] parameters) =>
        WithOwnerAsync(
            world,
            async command =>
            {
                AddParameters(command, parameters);

                var scalar = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

                return Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);
            },
            sql);

    /// <summary>A stored text as the owner connection: a change's <c>state</c>.</summary>
    /// <param name="world">The test world.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="parameters">Positional parameters.</param>
    private static Task<string?> TextAsync(World world, string sql, params (string Name, object Value)[] parameters) =>
        WithOwnerAsync(
            world,
            async command =>
            {
                AddParameters(command, parameters);

                return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) as string;
            },
            sql);

    /// <summary>A stored integer as the owner connection: an item's <c>version</c>.</summary>
    /// <param name="world">The test world.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="parameters">Positional parameters.</param>
    private static Task<int> IntAsync(World world, string sql, params (string Name, object Value)[] parameters) =>
        WithOwnerAsync(
            world,
            async command =>
            {
                AddParameters(command, parameters);

                var scalar = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

                return Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);
            },
            sql);

    /// <summary>Binds the positional parameters one probe statement declares.</summary>
    /// <param name="command">The prepared command.</param>
    /// <param name="parameters">Positional parameters.</param>
    private static void AddParameters(NpgsqlCommand command, params (string Name, object Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
    }

    /// <summary>One statement as the owner connection; the seed and the probes both use it.</summary>
    /// <param name="world">The test world.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="parameters">Positional parameters.</param>
    private static async Task ExecuteAsync(World world, string sql, params (string Name, object Value)[] parameters)
    {
        await WithOwnerAsync(
            world,
            static async command =>
            {
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

                return 0;
            },
            sql,
            parameters);
    }

    /// <summary>Runs one statement on a fresh owner connection, outside the tenant scope the routes use.</summary>
    /// <param name="world">The test world.</param>
    /// <param name="run">The body, handed the prepared command.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="parameters">Positional parameters for the statement.</param>
    private static async Task<T> WithOwnerAsync<T>(
        World world,
        Func<NpgsqlCommand, Task<T>> run,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(world.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await run(command);
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("checkout"));

        var module = Guid.CreateVersion7();
        var feature = Guid.CreateVersion7();
        var category = Guid.CreateVersion7();

        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            await InsertNodeAsync(scope, module, org, project, null, "module", "Shop", 0);
            await InsertNodeAsync(scope, feature, org, project, module, "feature", "Checkout", 0);
            await InsertNodeAsync(scope, category, org, project, feature, "category", "Payment", 0);
            await scope.CommitAsync();
        }

        return new World(api, client, database, user, org, project, category, _fixture.OwnerConnectionString(database));
    }

    private static Task<int> InsertNodeAsync(
        TenantScope scope,
        Guid id,
        Guid org,
        Guid project,
        Guid? parent,
        string kind,
        string title,
        int position) => scope.ExecuteAsync(
        """
        insert into public.feature_node (id, org_id, project_id, parent_id, kind, title, position)
        values (@id, @org_id, @project_id, @parent_id, @kind, @title, @position)
        """,
        ("id", id), ("org_id", org), ("project_id", project), ("parent_id", (object?)parent ?? DBNull.Value),
        ("kind", kind), ("title", title), ("position", position));

    /// <summary>The <c>details[].reason</c> values of an error envelope.</summary>
    /// <param name="answer">The answer.</param>
    private static IReadOnlyList<string> Reasons(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray().Select(detail => detail.GetProperty("reason").GetString()!)];

    /// <summary>The <c>details[].path</c> values of an error envelope.</summary>
    /// <param name="answer">The answer.</param>
    private static IReadOnlyList<string?> Paths(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray().Select(detail => detail.GetProperty("path").GetString())];

    /// <summary>One project with a category, its founder's client, and the owner connection for the probes.</summary>
    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project,
        Guid Category,
        string OwnerConnectionString) : IAsyncDisposable
    {
        /// <summary>The founder, whose <c>tenant_admin</c> membership holds <c>authoring:write</c>.</summary>
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        /// <summary>The 13.1.1 item route.</summary>
        public string Items => $"/v1/projects/{Project}/authoring/checklist-items";

        /// <summary>The 13.1.2 change route.</summary>
        public string Changes => $"/v1/projects/{Project}/authoring/changes";

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }
}
