using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Evidgate.Modules.Authoring.Application.Commands;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The node half of the 13.1.1-T2 review fixes, each pinned at the edge that the review named: AC2's group
/// serialisation (a create or a move into a group against a renumber of it), the sibling cap a group may grow to, the
/// audit rule of AC18 for a no-op, the code-point length unit of every authoring text, and the bounded lock wait
/// (55P03 answering the retryable 503 instead of a 500).
/// </summary>
/// <remarks>
/// The tests drive the real host and a real PostgreSQL like <c>AuthoringTreeTests</c>; where a race cannot be forced from
/// the outside (two HTTP requests are only <em>likely</em> to overlap) the assertion is on the invariant instead — no 500,
/// a unique final position, and the only two outcomes AC2 can produce.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AuthoringFixNodesTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AuthoringFixNodesTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> As => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public string AuthoringUrl => "/v1/projects/" + Project.ToString("D", CultureInfo.InvariantCulture) + "/authoring";

        public string NodeUrl(Guid node) => AuthoringUrl + "/nodes/" + node.ToString("D", CultureInfo.InvariantCulture);

        public string ReorderUrl(Guid node) => NodeUrl(node) + "/reorder";

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    private static async Task<Guid> CreateAsync(World world, Guid? parentId, string kind, string title)
    {
        var answer = await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, parentId, kind, title);
        answer.Status.ShouldBe(HttpStatusCode.Created);
        return answer.Member("node_id").GetGuid();
    }

    private Task<long> AuditRowsAsync(World world) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            "select count(*) from public.audit_log where org_id = '" + world.Org.ToString("D", CultureInfo.InvariantCulture) + "'");

    /// <summary>
    /// AC2/review F1: a renumber of one group and a create in the same group serialise on the group's lock, so the create
    /// can never meet a parked value of the renumber (<c>feature_node_sibling_position_uq</c> would refuse the
    /// <c>UPDATE</c> with a 23505, i.e. a 500). Three rounds of <c>Task.WhenAll</c>: the create always lands and appends,
    /// the renumber either wins (200) or reads a group that already changed (412), and the four final positions are unique.
    /// </summary>
    [Fact]
    public async Task Reorder_RacingACreate_SerializesOnTheGroupLock()
    {
        await using var world = await StartAsync();

        var module = await CreateAsync(world, null, "module", "Checkout");

        for (var round = 0; round < 3; round++)
        {
            var label = round.ToString(CultureInfo.InvariantCulture);
            var group = await CreateAsync(world, module, "feature", "Round " + label);
            var first = await CreateAsync(world, group, "category", "A");
            var second = await CreateAsync(world, group, "category", "B");
            var third = await CreateAsync(world, group, "category", "C");

            var before = await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project, parentId: group);
            before.Status.ShouldBe(HttpStatusCode.OK);

            var reorder = AuthoringCalls.SendAsync(
                world.Client,
                world.As,
                HttpMethod.Post,
                world.ReorderUrl(second),
                JsonSerializer.Serialize(new { ordered_ids = new[] { third, second, first } }),
                ifMatch: before.ETag);
            var create = AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, group, "category", "Appended " + label);

            var answers = await Task.WhenAll(reorder, create);
            var renumbered = answers[0];
            var created = answers[1];

            created.Status.ShouldBe(HttpStatusCode.Created, "an omitted position is an append, whatever the renumber does");
            renumbered.Status.ShouldBeOneOf(
                HttpStatusCode.OK,
                HttpStatusCode.PreconditionFailed,
                HttpStatusCode.UnprocessableEntity);

            switch (renumbered.Status)
            {
                case HttpStatusCode.OK:
                    // The renumber won the group lock: the create appended after the group it renumbered (0..2).
                    renumbered.Member("changed").GetBoolean().ShouldBeTrue();
                    break;

                case HttpStatusCode.PreconditionFailed:
                    renumbered.Body!.Value.GetProperty("error").GetProperty("code").GetString().ShouldBe("precondition_failed");
                    break;

                default:
                    // The create committed first, so the list the caller sent no longer describes the group: AC2's own
                    // refusal, never a 500 from the sibling index.
                    renumbered.Body!.Value.GetProperty("error").GetProperty("code").GetString().ShouldBe("request_invalid");
                    renumbered.Reasons().ShouldContain("incomplete");
                    break;
            }

            var after = await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project, parentId: group);
            var items = after.Member("items").EnumerateArray().ToList();
            items.Count.ShouldBe(4);
            items.Select(item => item.GetProperty("position").GetInt32()).Distinct().Count().ShouldBe(4);
            items.Single(item => item.GetProperty("node_id").GetGuid() == created.Member("node_id").GetGuid())
                .GetProperty("position").GetInt32()
                .ShouldBe(3, "the appended node lands after the renumbered group, whose positions stay 0..2");
        }
    }

    /// <summary>
    /// AC2/review F6: creates that omit <c>position</c> append. Before the group lock the losers read one maximum and both
    /// sent it, so three of four creates answered <c>422 request_invalid</c> on <c>/position</c> — a member they never sent.
    /// </summary>
    [Fact]
    public async Task ConcurrentCreates_WithoutPosition_AllAppend()
    {
        await using var world = await StartAsync();

        var module = await CreateAsync(world, null, "module", "Checkout");

        var calls = Enumerable.Range(0, 4)
            .Select(index => AuthoringCalls.CreateNodeAsync(
                world.Client,
                world.As,
                world.Project,
                module,
                "feature",
                "Feature " + index.ToString(CultureInfo.InvariantCulture)))
            .ToArray();
        var answers = await Task.WhenAll(calls);

        foreach (var answer in answers)
        {
            answer.Status.ShouldBe(HttpStatusCode.Created);
        }

        var tree = await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project, parentId: module);
        var positions = tree.Member("items").EnumerateArray().Select(item => item.GetProperty("position").GetInt32()).ToList();
        positions.ShouldBe([0, 1, 2, 3]);
    }

    /// <summary>
    /// AC2/review F5-perf F9/N1: the reorder refuses more than 1 000 ids, so a group must never grow past 1 000 — the
    /// create and the move-in (with or without an explicit <c>position</c>) answer <c>422 request_invalid</c>,
    /// <c>details[].path = /parent_id</c>, <c>details[].reason = children_limit_exceeded</c>, and the group is left as it
    /// was.
    /// </summary>
    [Fact]
    public async Task GroupAtSiblingCap_RefusesCreateAndMoveIn()
    {
        await using var world = await StartAsync();

        var module = await CreateAsync(world, null, "module", "Big");
        var full = await CreateAsync(world, module, "feature", "Full");
        var other = await CreateAsync(world, module, "feature", "Other");

        // 1 000 live categories under `full` in one statement — the group a reorder can still take (MaxOrderedIds).
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.feature_node (id, org_id, project_id, parent_id, kind, title, position)
            select cast(md5('s55-cap-' || g) as uuid),
                   '{world.Org:D}',
                   '{world.Project:D}',
                   '{full:D}',
                   'category',
                   'Seeded ' || g,
                   g - 1
              from generate_series(1, 1000) as g
            """);

        (await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project, nodeId: full))
            .Member("node").GetProperty("child_count").GetInt32().ShouldBe(1000);

        var refused = await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, full, "category", "One too many");
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.Body!.Value.GetProperty("error").GetProperty("code").GetString().ShouldBe("request_invalid");
        refused.Paths().ShouldContain("/parent_id");
        refused.Reasons().ShouldContain("children_limit_exceeded");

        var mover = await CreateAsync(world, other, "category", "Mover");
        var moveIn = await AuthoringCalls.SendAsync(
            world.Client,
            world.As,
            HttpMethod.Patch,
            world.NodeUrl(mover),
            JsonSerializer.Serialize(new { parent_id = full }),
            ifMatch: "\"1\"");
        moveIn.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        moveIn.Paths().ShouldContain("/parent_id");
        moveIn.Reasons().ShouldContain("children_limit_exceeded");

        // Review N1: the cap belongs to the target group, not to the position the request happens to carry, so a move-in
        // at an explicit position is refused the same way — otherwise a caller could push a group past 1 000 with a
        // `PATCH {parent_id, position}`, and the reorder of that group would then refuse the whole set forever.
        var moveInAtPosition = await AuthoringCalls.SendAsync(
            world.Client,
            world.As,
            HttpMethod.Patch,
            world.NodeUrl(mover),
            JsonSerializer.Serialize(new { parent_id = full, position = 5 }),
            ifMatch: "\"1\"");
        moveInAtPosition.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        moveInAtPosition.Body!.Value.GetProperty("error").GetProperty("code").GetString().ShouldBe("request_invalid");
        moveInAtPosition.Paths().ShouldContain("/parent_id");
        moveInAtPosition.Reasons().ShouldContain("children_limit_exceeded");

        // Nothing moved: the group still holds exactly the seeded rows and the mover still sits under `other`.
        (await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project, nodeId: full))
            .Member("node").GetProperty("child_count").GetInt32().ShouldBe(1000);
        (await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project, nodeId: mover))
            .Member("node").GetProperty("parent_id").GetGuid().ShouldBe(other);
    }

    /// <summary>
    /// AC18/review S-4: a mutation that changed nothing writes no audit row. A no-op <c>PATCH</c> and a reorder that sends
    /// the stored order answer <c>changed:false</c> and append none, while a real renumber appends exactly one — the rule
    /// the checklist item and manual case handlers already applied.
    /// </summary>
    [Fact]
    public async Task NoOpPatchAndReorder_WriteNoAuditRow()
    {
        await using var world = await StartAsync();

        var first = await CreateAsync(world, null, "module", "A");
        var second = await CreateAsync(world, null, "module", "B");
        var afterCreates = await AuditRowsAsync(world);

        var noOpPatch = await AuthoringCalls.SendAsync(
            world.Client,
            world.As,
            HttpMethod.Patch,
            world.NodeUrl(first),
            JsonSerializer.Serialize(new { title = "A" }),
            ifMatch: "\"1\"");
        noOpPatch.Status.ShouldBe(HttpStatusCode.OK);
        noOpPatch.Member("changed").GetBoolean().ShouldBeFalse();
        (await AuditRowsAsync(world)).ShouldBe(afterCreates);

        var tree = await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project);
        var noOpReorder = await AuthoringCalls.SendAsync(
            world.Client,
            world.As,
            HttpMethod.Post,
            world.ReorderUrl(first),
            JsonSerializer.Serialize(new { ordered_ids = new[] { first, second } }),
            ifMatch: tree.ETag);
        noOpReorder.Status.ShouldBe(HttpStatusCode.OK);
        noOpReorder.Member("changed").GetBoolean().ShouldBeFalse();
        (await AuditRowsAsync(world)).ShouldBe(afterCreates);

        // The positive control: the same route with a different order does write its one row.
        var renumbered = await AuthoringCalls.SendAsync(
            world.Client,
            world.As,
            HttpMethod.Post,
            world.ReorderUrl(first),
            JsonSerializer.Serialize(new { ordered_ids = new[] { second, first } }),
            ifMatch: tree.ETag);
        renumbered.Status.ShouldBe(HttpStatusCode.OK);
        renumbered.Member("changed").GetBoolean().ShouldBeTrue();
        (await AuditRowsAsync(world)).ShouldBe(afterCreates + 1);
    }

    /// <summary>
    /// Review F7: a node title is counted in code points, the unit of the column's <c>char_length(title) between 1 and
    /// 200</c> (migration 0039) — 200 astral characters are 200 code points and 400 UTF-16 units, so the old
    /// <c>string.Length</c> rule refused half the bound on the create route and on the PATCH route alike.
    /// </summary>
    [Fact]
    public async Task NodeTitleLength_IsCountedInCodePoints()
    {
        await using var world = await StartAsync();

        var twoHundred = string.Concat(Enumerable.Repeat("😀", 200));
        var created = await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, null, "module", twoHundred);
        created.Status.ShouldBe(HttpStatusCode.Created);

        var node = created.Member("node_id").GetGuid();
        (await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project, nodeId: node))
            .Member("node").GetProperty("title").GetString().ShouldBe(twoHundred);

        var tooLong = await AuthoringCalls.CreateNodeAsync(
            world.Client,
            world.As,
            world.Project,
            null,
            "module",
            string.Concat(Enumerable.Repeat("😀", 201)));
        tooLong.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        tooLong.Paths().ShouldContain("/title");

        // The same bound on the PATCH route: 200 astral characters are accepted (and change the title), 201 are refused.
        var patched = await AuthoringCalls.SendAsync(
            world.Client,
            world.As,
            HttpMethod.Patch,
            world.NodeUrl(node),
            JsonSerializer.Serialize(new { title = string.Concat(Enumerable.Repeat("🎉", 200)) }),
            ifMatch: "\"1\"");
        patched.Status.ShouldBe(HttpStatusCode.OK);
        patched.Member("changed").GetBoolean().ShouldBeTrue();

        var patchedTooLong = await AuthoringCalls.SendAsync(
            world.Client,
            world.As,
            HttpMethod.Patch,
            world.NodeUrl(node),
            JsonSerializer.Serialize(new { title = string.Concat(Enumerable.Repeat("😀", 201)) }),
            ifMatch: "\"2\"");
        patchedTooLong.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        patchedTooLong.Paths().ShouldContain("/title");
    }

    /// <summary>
    /// Review F7: the delete reason is counted in code points too, and a whitespace-only reason is accepted — the rule the
    /// manual case route's <c>ManualCaseBody.OptionalReason</c> already stated, so both families refuse the same reasons.
    /// </summary>
    [Fact]
    public void DeleteNodeReason_RuleCountsCodePoints()
    {
        var validator = new DeleteNodeCommandValidator();
        var fiveHundred = string.Concat(Enumerable.Repeat("😀", 500));

        IsValid(validator, fiveHundred).ShouldBeTrue("500 astral characters are 500 code points");
        IsValid(validator, fiveHundred + "😀").ShouldBeFalse();
        IsValid(validator, "   ").ShouldBeTrue("a reason is a non-empty string, not a non-blank one");
        IsValid(validator, string.Empty).ShouldBeFalse();

        static bool IsValid(DeleteNodeCommandValidator validator, string reason) =>
            validator.Validate(new DeleteNodeCommand(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                reason,
                1,
                "key-" + Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture))).IsValid;
    }

    /// <summary>
    /// Review F7 at the writer: the case routes reach <c>AuthoringVersionWriter.IsReasonLengthValid</c> with the reason
    /// the handler accepted, so a 500-code-point retire reason must be stored — the column's own
    /// <c>char_length(reason) between 1 and 500</c> accepts it, and a UTF-16 count refused it as 1 000 characters.
    /// </summary>
    [Fact]
    public async Task RetireReason_IsCountedInCodePoints_AtTheWriter()
    {
        await using var world = await StartAsync();

        var module = await CreateAsync(world, null, "module", "Checkout");
        var feature = await CreateAsync(world, module, "feature", "Payment");
        var category = await CreateAsync(world, feature, "category", "Cards");

        var itemAnswer = await AuthoringCalls.SendAsync(
            world.Client,
            world.As,
            HttpMethod.Post,
            world.AuthoringUrl + "/checklist-items",
            JsonSerializer.Serialize(new { node_id = category, title = "Pay by card", priority = "high" }));
        itemAnswer.Status.ShouldBe(HttpStatusCode.Created);
        var item = itemAnswer.Member("item_id").GetGuid();

        var caseAnswer = await AuthoringCalls.SendAsync(
            world.Client,
            world.As,
            HttpMethod.Post,
            world.AuthoringUrl + "/manual-cases",
            JsonSerializer.Serialize(new
            {
                checklist_item_id = item,
                title = "Pay with a card",
                steps = new[] { new { no = 1, action = "Open the cart", expected = "The cart is shown" } },
                priority = "high",
            }));
        caseAnswer.Status.ShouldBe(HttpStatusCode.Created);
        var caseId = caseAnswer.Member("case_id").GetGuid();

        var retired = await AuthoringCalls.SendAsync(
            world.Client,
            world.As,
            HttpMethod.Post,
            world.AuthoringUrl + "/manual-cases/" + caseId.ToString("D", CultureInfo.InvariantCulture) + "/state",
            JsonSerializer.Serialize(new { state = "retired", reason = string.Concat(Enumerable.Repeat("😀", 500)) }),
            ifMatch: "\"1\"");
        retired.Status.ShouldBe(HttpStatusCode.OK);
        retired.Member("state").GetString().ShouldBe("retired");
    }

    /// <summary>
    /// Review F1/perf F1, security S-3: the handler's own <c>for update</c> read runs before the writer applies its bound,
    /// so the command has to carry the same 5 s <c>lock_timeout</c> and the same mapping — a row another transaction holds
    /// answers the retryable <c>503 unavailable</c> of §5, never a parked request or a 500.
    /// </summary>
    [Fact]
    public async Task NodeRow_HeldByAnotherWriter_Answers503()
    {
        await using var world = await StartAsync();

        var node = await CreateAsync(world, null, "module", "Contended");

        // A second, superuser connection holds the row's update lock for the whole request.
        await using var holder = await _fixture.OpenSuperuserAsync(world.Database);
        await using var locked = await holder.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var take = holder.CreateCommand())
        {
            take.Transaction = locked;
            take.CommandText = "select id from public.feature_node where id = '" + node.ToString("D", CultureInfo.InvariantCulture) + "' for update";
            (await take.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        }

        var refused = await AuthoringCalls.SendAsync(
            world.Client,
            world.As,
            HttpMethod.Patch,
            world.NodeUrl(node),
            JsonSerializer.Serialize(new { title = "Blocked" }),
            ifMatch: "\"1\"");

        await locked.RollbackAsync(TestContext.Current.CancellationToken);

        refused.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var error = refused.Body!.Value.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("unavailable");
        error.GetProperty("retryable").GetBoolean().ShouldBeTrue();
    }

    /// <summary>
    /// AC2/review F1/N5: the sibling group's advisory lock is the serialisation a create and a renumber of one group
    /// share, and a holder that never commits must answer the retryable <c>503 unavailable</c> of §5 instead of parking
    /// the request. A second connection takes
    /// <c>pg_advisory_xact_lock(hashtextextended('authoring_group:&lt;project&gt;:root', 0))</c> — the exact key
    /// <c>AuthoringLockGuard.SiblingGroupKey</c> builds for the root group — so both requests wait the real 5 s
    /// <c>lock_timeout</c> out; nothing here depends on a race landing. Once the holder rolls back, the same request
    /// succeeds, which also proves the answer was the lock and not a broken route.
    /// </summary>
    [Fact]
    public async Task GroupAdvisoryLock_HeldByAnotherWriter_CreateAndReorderAnswer503()
    {
        await using var world = await StartAsync();

        var first = await CreateAsync(world, null, "module", "First");
        var second = await CreateAsync(world, null, "module", "Second");
        var tree = await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project);
        tree.Status.ShouldBe(HttpStatusCode.OK);

        // A second, superuser connection holds the root group's advisory lock for the whole of both requests.
        await using var holder = await _fixture.OpenSuperuserAsync(world.Database);
        await using var locking = await holder.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var take = holder.CreateCommand())
        {
            take.Transaction = locking;
            // `pg_advisory_xact_lock` is void, and Npgsql maps a void result to CLR null, so the lock is taken through
            // a subquery and read as `count(*)` (bigint, never null): a count of 1 proves the statement ran — it cannot
            // have waited, because this connection is the one taking the key.
            take.CommandText = "select count(*) from (select pg_advisory_xact_lock(hashtextextended('authoring_group:"
                + world.Project.ToString("D", CultureInfo.InvariantCulture)
                + ":root', 0))) as held";
            Convert.ToInt64(
                    await take.ExecuteScalarAsync(TestContext.Current.CancellationToken),
                    CultureInfo.InvariantCulture)
                .ShouldBe(1L);
        }

        // Both keys of that one lock: a create of the root group and the reorder of it. Neither is given a way out early,
        // so the bounded wait decides each answer.
        var create = AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, null, "module", "Blocked");
        var reorder = AuthoringCalls.SendAsync(
            world.Client,
            world.As,
            HttpMethod.Post,
            world.ReorderUrl(first),
            JsonSerializer.Serialize(new { ordered_ids = new[] { second, first } }),
            ifMatch: tree.ETag);

        var answers = await Task.WhenAll(create, reorder)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        await locking.RollbackAsync(TestContext.Current.CancellationToken);

        foreach (var refused in answers)
        {
            refused.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
            var error = refused.Body!.Value.GetProperty("error");
            error.GetProperty("code").GetString().ShouldBe("unavailable");
            error.GetProperty("retryable").GetBoolean().ShouldBeTrue();
        }

        // The lock is released: the refused create now lands, so the 503 was the holder and nothing else.
        var retried = await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, null, "module", "Blocked");
        retried.Status.ShouldBe(HttpStatusCode.Created);
    }
}
