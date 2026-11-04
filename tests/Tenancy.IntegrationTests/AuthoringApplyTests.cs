using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Evidgate.Modules.Authoring.Domain;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The batched apply (13.1.2 AC4–AC8, AC13, AC14) against real Postgres: one transaction that writes at most one version
/// per changed entity, refuses a stale selection without writing anything, and answers a replay with the first result.
/// </summary>
[Trait("Category", "Docker")]
public sealed class AuthoringApplyTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AuthoringApplyTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>AC4 and AC8: a delete lands one version, one batch, and flips the change to applied.</summary>
    [Fact]
    public async Task Apply_Delete_WritesOneVersionAndFlipsTheChange()
    {
        await using var world = await StartAsync();
        var (item, version) = await CreateItemAsync(world, "Pay online");
        var change = await QueueDeleteAsync(world, item);
        var before = await CountVersionsAsync(world, item);

        var applied = await ApplyAsync(world, [change], BatchEtag((item, version)));

        applied.Status.ShouldBe(HttpStatusCode.OK);
        var batch = applied.Body.GetProperty("batch_id").GetGuid();
        applied.Body.GetProperty("applied_change_ids")[0].GetGuid().ShouldBe(change);
        var versions = applied.Body.GetProperty("versions");
        versions.GetArrayLength().ShouldBe(1);
        versions[0].GetProperty("entity_id").GetGuid().ShouldBe(item);
        versions[0].GetProperty("version_no").GetInt32().ShouldBe(version + 1);
        applied.Body.GetProperty("lineage").GetArrayLength().ShouldBe(0);

        // Exactly one version more than before, all of them under this one batch, and the row moved once.
        (await CountVersionsAsync(world, item)).ShouldBe(before + 1);
        (await CountAsync(world, "select count(*) from public.authoring_version where batch_id = @batch", ("batch", batch))).ShouldBe(1);
        (await CountAsync(world, "select count(*) from public.authoring_change where id = @id and state = 'applied' and applied_batch_id = @batch", ("id", change), ("batch", batch))).ShouldBe(1);
        // The delete is a soft delete: the row is retained with deleted_at set, never physically removed.
        (await CountAsync(world, "select count(*) from public.checklist_item where id = @id and deleted_at is not null", ("id", item))).ShouldBe(1);
    }

    /// <summary>AC4: a comment-only batch versions nothing, so no editor's <c>If-Match</c> is invalidated by it.</summary>
    [Fact]
    public async Task Apply_CommentOnly_WritesNoVersion()
    {
        await using var world = await StartAsync();
        var (item, version) = await CreateItemAsync(world, "Pay online");
        var queued = await world.Client.SendAsync(
            HttpMethod.Post,
            world.Changes,
            world.Admin,
            new { change_type = "comment", target_id = item, payload = new { body = "please rename this item" } },
            NewKey());
        queued.Status.ShouldBe(HttpStatusCode.Created);
        var change = queued.Id("change_id");
        var before = await CountVersionsAsync(world, item);

        var applied = await ApplyAsync(world, [change], BatchEtag((item, version)));

        applied.Status.ShouldBe(HttpStatusCode.OK);
        applied.Body.GetProperty("versions").GetArrayLength().ShouldBe(0);
        (await CountVersionsAsync(world, item)).ShouldBe(before);
        (await IntAsync(world, "select version from public.checklist_item where id = @id", ("id", item))).ShouldBe(version);
        (await CountAsync(world, "select count(*) from public.authoring_change where id = @id and state = 'applied'", ("id", change))).ShouldBe(1);
    }

    /// <summary>AC5: a tag that is not the current batch ETag is refused before the first write.</summary>
    [Fact]
    public async Task Apply_StaleIfMatch_RefusesAndWritesNothing()
    {
        await using var world = await StartAsync();
        var (item, version) = await CreateItemAsync(world, "Pay online");
        var change = await QueueDeleteAsync(world, item);
        var before = await CountVersionsAsync(world, item);

        var refused = await ApplyAsync(world, [change], "0f0e0d0c0b0a09080706050403020100");

        refused.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        refused.ErrorCode.ShouldBe("precondition_failed");
        Reasons(refused).ShouldContain("batch_stale");
        Paths(refused).ShouldContain("/If-Match");

        (await CountVersionsAsync(world, item)).ShouldBe(before);
        (await TextAsync(world, "select state from public.authoring_change where id = @id", ("id", change))).ShouldBe("pending");
        (await IntAsync(world, "select version from public.checklist_item where id = @id", ("id", item))).ShouldBe(version);
    }

    /// <summary>
    /// AC5 and AC14(b): a <c>PATCH</c> on an affected item after queueing is refused even when the caller's tag was
    /// recomputed from the current versions, and the 412 names that item rather than the header.
    /// </summary>
    [Fact]
    public async Task Apply_ItemMovedAfterQueue_412_TargetChanged()
    {
        await using var world = await StartAsync();
        var (item, version) = await CreateItemAsync(world, "Pay online");
        var change = await QueueDeleteAsync(world, item);

        var edited = await world.Client.SendAsync(
            HttpMethod.Patch,
            $"{world.Items}/{item:D}",
            world.Admin,
            new { title = "Pay online with a card" },
            NewKey(),
            "\"1\"");
        edited.Status.ShouldBe(HttpStatusCode.OK);
        var moved = edited.Body.GetProperty("version").GetInt32();
        moved.ShouldBe(version + 1);
        var afterEdit = await CountVersionsAsync(world, item);

        var refused = await ApplyAsync(world, [change], BatchEtag((item, moved)));

        refused.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        Reasons(refused).ShouldContain("target_changed");
        Paths(refused).ShouldContain(item.ToString("D"));
        // The create and the edit wrote one version each and the refused apply wrote none.
        (await CountVersionsAsync(world, item)).ShouldBe(afterEdit);
        (await TextAsync(world, "select state from public.authoring_change where id = @id", ("id", change))).ShouldBe("pending");
    }

    /// <summary>AC6: the same key replays the first result instead of applying a second time.</summary>
    [Fact]
    public async Task Apply_Replay_AnswersTheFirstResult()
    {
        await using var world = await StartAsync();
        var (item, version) = await CreateItemAsync(world, "Pay online");
        var change = await QueueDeleteAsync(world, item);
        var key = NewKey();
        var etag = BatchEtag((item, version));
        var before = await CountVersionsAsync(world, item);

        var first = await ApplyAsync(world, [change], etag, key);
        var replay = await ApplyAsync(world, [change], etag, key);

        first.Status.ShouldBe(HttpStatusCode.OK);
        replay.Status.ShouldBe(HttpStatusCode.OK);
        replay.Body.GetProperty("batch_id").GetGuid().ShouldBe(first.Body.GetProperty("batch_id").GetGuid());
        // The replay writes nothing: one version more than the create's, not two.
        (await CountVersionsAsync(world, item)).ShouldBe(before + 1);
    }

    /// <summary>AC6: a reused key whose selection differs is refused fail-closed, never served the old result.</summary>
    [Fact]
    public async Task Apply_SameKeyDifferentSelection_Refused()
    {
        await using var world = await StartAsync();
        var (first, firstVersion) = await CreateItemAsync(world, "Pay online");
        var (second, _) = await CreateItemAsync(world, "Pay by card");
        var firstChange = await QueueDeleteAsync(world, first);
        var secondChange = await QueueDeleteAsync(world, second);
        var key = NewKey();

        var applied = await ApplyAsync(world, [firstChange], BatchEtag((first, firstVersion)), key);
        applied.Status.ShouldBe(HttpStatusCode.OK);
        var beforeSecond = await CountVersionsAsync(world, second);

        var refused = await ApplyAsync(world, [secondChange], BatchEtag((second, 1)), key);

        ((int)refused.Status is >= 200 and < 300).ShouldBeFalse();
        refused.ErrorCode.ShouldNotBeNull();
        (await TextAsync(world, "select state from public.authoring_change where id = @id", ("id", secondChange))).ShouldBe("pending");
        (await CountVersionsAsync(world, second)).ShouldBe(beforeSecond);
    }

    /// <summary>AC7: two appliers of one selection serialize on the locked rows; exactly one lands, neither deadlocks.</summary>
    [Fact]
    public async Task Apply_Concurrent_SameSelection_ExactlyOneLands()
    {
        await using var world = await StartAsync();
        var (item, version) = await CreateItemAsync(world, "Pay online");
        var change = await QueueDeleteAsync(world, item);
        var etag = BatchEtag((item, version));
        var before = await CountVersionsAsync(world, item);

        var both = await Task.WhenAll(
            ApplyAsync(world, [change], etag),
            ApplyAsync(world, [change], etag));

        both.Count(answer => answer.Status == HttpStatusCode.OK).ShouldBe(1);
        var refused = both.Single(answer => answer.Status != HttpStatusCode.OK);

        // A refusal, never a 40P01: the loser blocked on the change row and read the committed `applied` state.
        refused.Status.ShouldBeOneOf(HttpStatusCode.Conflict, HttpStatusCode.PreconditionFailed);
        refused.ErrorMessage.ShouldNotBeNull();
        // The winner wrote the batch's one version and the loser wrote nothing.
        (await CountVersionsAsync(world, item)).ShouldBe(before + 1);
    }

    /// <summary>AC13: the apply-time re-check refuses a delete whose target gained a live manual case.</summary>
    [Fact]
    public async Task Apply_DeleteTargetWithLiveCase_409_AndNothingWritten()
    {
        await using var world = await StartAsync();
        var (item, version) = await CreateItemAsync(world, "Pay online");
        var change = await QueueDeleteAsync(world, item);
        await InsertCaseAsync(world, item);
        var before = await CountVersionsAsync(world, item);

        var refused = await ApplyAsync(world, [change], BatchEtag((item, version)));

        refused.Status.ShouldBe(HttpStatusCode.Conflict);
        refused.ErrorCode.ShouldBe("authoring_not_empty");
        Reasons(refused).ShouldContain("item_has_cases");
        (await CountVersionsAsync(world, item)).ShouldBe(before);
        (await CountAsync(world, "select count(*) from public.checklist_item where id = @id and deleted_at is null", ("id", item))).ShouldBe(1);
    }

    /// <summary>
    /// AC14/main decision: a selection holding a structural change this route does not execute is refused fail-closed —
    /// the whole batch, nothing written, and the row stays <c>pending</c> rather than being marked applied.
    /// </summary>
    [Fact]
    public async Task Apply_MergeSelection_RefusedAndStaysPending()
    {
        await using var world = await StartAsync();
        var (survivor, survivorVersion) = await CreateItemAsync(world, "Pay online");
        var (source, sourceVersion) = await CreateItemAsync(world, "Pay online by card");

        var queued = await world.Client.SendAsync(
            HttpMethod.Post,
            world.Changes,
            world.Admin,
            new
            {
                change_type = "merge",
                target_id = survivor,
                payload = new { survivor_item_id = survivor, source_item_ids = new[] { survivor, source } },
            },
            NewKey());

        queued.Status.ShouldBe(HttpStatusCode.Created);
        var change = queued.Id("change_id");
        var etag = AuthoringHashes.OrderHash([(survivor, survivorVersion), (source, sourceVersion)]);

        var refused = await ApplyAsync(world, [change], etag);

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Reasons(refused).ShouldContain("payload_invalid");
        Paths(refused).ShouldContain("/change_ids/0");
        (await TextAsync(world, "select state from public.authoring_change where id = @id", ("id", change))).ShouldBe("pending");
        (await CountVersionsAsync(world, survivor)).ShouldBe(1);
        (await CountVersionsAsync(world, source)).ShouldBe(1);
    }

    /// <summary>The ETag a client computes itself: the order hash of every affected item with its current version.</summary>
    /// <param name="item">The one affected item.</param>
    private static string BatchEtag((Guid Id, int Version) item) => AuthoringHashes.OrderHash([item]);

    /// <summary>Queues a delete change and answers its id.</summary>
    /// <param name="world">The test world.</param>
    /// <param name="item">The item to delete.</param>
    private static async Task<Guid> QueueDeleteAsync(World world, Guid item)
    {
        var queued = await world.Client.SendAsync(
            HttpMethod.Post,
            world.Changes,
            world.Admin,
            new { change_type = "delete", target_id = item, payload = new { } },
            NewKey());

        queued.Status.ShouldBe(HttpStatusCode.Created);
        queued.Text("state").ShouldBe("pending");

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
