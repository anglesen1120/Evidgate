using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>The seed, caller and probe helpers of the queue suite, split out to keep each file under the size cap.</summary>
public sealed partial class AuthoringChangeTests
{
    /// <summary>The bound a race test waits on an observable condition for (a parked backend, never a fixed sleep).</summary>
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

    private async Task<World> StartAsync(int itemCount)
    {
        var database = await fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "changes-" + Guid.CreateVersion7().ToString("N"));
        var project = Guid.CreateVersion7();
        var node = Guid.CreateVersion7();
        var items = Enumerable.Range(0, itemCount).Select(_ => Guid.CreateVersion7()).ToArray();
        var rows = string.Join(
            ',', items.Select((item, index) =>
                $"('{item}', '{org}', '{project}', '{node}', 'Item {index}', 'high', 'active', 'manual', {index})"));

        await fixture.ExecuteAsync(database, $"""
            insert into public.project (id, org_id, name, slug)
            values ('{project}', '{org}', 'Changes', 'changes-{project:N}');
            insert into public.feature_node (id, org_id, project_id, kind, title, position)
            values ('{node}', '{org}', '{project}', 'module', 'Changes', 0);
            insert into public.checklist_item
                (id, org_id, project_id, feature_node_id, title, priority, state, source, position)
            values {rows};
            """);

        var membership = await fixture.ScalarAsync<Guid>(database,
            $"select id from public.membership where org_id = '{org}' and user_id = '{user}'");
        var caller = TenancyApiFactory.Resolved(user: user, orgHeader: org);
        return new World(database, api, client, org, project, user, membership, items, caller);
    }

    private static Task<ApiAnswer> QueueAsync(
        World world, Guid target, string changeType, string payload, string? key = null) =>
        world.Client.SendAsync(
            HttpMethod.Post,
            world.ChangesPath,
            world.Caller,
            new
            {
                target_kind = "checklist_item",
                target_id = target,
                change_type = changeType,
                payload = JsonDocument.Parse(payload).RootElement.Clone(),
                reason = "queued by the test",
            },
            key ?? NewKey());

    /// <summary>
    /// The queue route with a body built by the caller: the shape a projection cannot produce (an absent
    /// <c>payload</c>, a numeric <c>reason</c>, an unknown root member) needs its own object.
    /// </summary>
    private static Task<ApiAnswer> QueueObjectAsync(World world, object body, string? key = null) =>
        world.Client.SendAsync(HttpMethod.Post, world.ChangesPath, world.Caller, body, key ?? NewKey());

    /// <summary>The apply route with a body built by the caller, so a malformed selection reaches the endpoint.</summary>
    private static Task<ApiAnswer> ApplyAsync(World world, object body, string? ifMatch = null) =>
        world.Client.SendAsync(HttpMethod.Post, world.ApplyPath, world.Caller, body, NewKey(), ifMatch);

    private static Task<ApiAnswer> ListAsync(World world, string query) =>
        world.Client.SendAsync(HttpMethod.Get, world.ChangesPath + query, world.Caller);

    private async Task FillPendingAsync(World world, int count)
    {
        await fixture.ExecuteAsync(world.Database, $$"""
            insert into public.authoring_change
                (id, org_id, project_id, target_id, change_type, payload, base_version, affected_versions,
                 author_membership_id)
            select gen_random_uuid(), '{{world.Org}}', '{{world.Project}}', '{{world.Items[0]}}', 'comment',
                   '{"body":"bulk"}'::jsonb, 1, jsonb_build_object('{{world.Items[0]}}', 1), '{{world.Membership}}'
              from generate_series(1, {{count}})
            """);
    }

    private async Task<Guid> AddMemberAsync(World world, string? roleCode)
    {
        var user = Guid.CreateVersion7();
        var membership = Guid.CreateVersion7();
        await fixture.ExecuteAsync(world.Database, $"""
            insert into public."user" (id, email) values ('{user}', 'member-{user:N}@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{membership}', '{world.Org}', '{user}', 'member-{user:N}@example.test', 'active');
            """);

        if (roleCode is not null)
        {
            var role = await fixture.ScalarAsync<Guid>(
                world.Database, $"select id from public.role where code = '{roleCode}' and org_id is null");
            var admin = TenancyApiFactory.Resolved(user: world.User, orgHeader: world.Org);
            var granted = await world.Client.SendAsync(
                HttpMethod.Post, $"/v1/orgs/{world.Org}/memberships/{membership}/roles", admin,
                new { role_id = role }, NewKey());
            granted.Status.ShouldBe(HttpStatusCode.OK, granted.ErrorMessage);
        }

        return user;
    }

    private static string MergeJson(Guid first, Guid survivor) =>
        $$"""{"source_item_ids":["{{first}}","{{survivor}}"],"survivor_item_id":"{{survivor}}"}""";

    private static string SplitJson(string first, string second) =>
        $$"""{"new_items":[{"title":"{{first}}"},{"title":"{{second}}"}]}""";

    /// <summary>A split whose one assignment sends <paramref name="caseId"/> to the first new item.</summary>
    private static string SplitWithAssignment(Guid caseId) =>
        $$"""
        {"new_items":[{"title":"First half"},{"title":"Second half"}],
         "case_assignments":[{"case_id":"{{caseId:D}}","item_index":0}]}
        """;

    /// <summary>
    /// Holds one superuser transaction open so a request under test can be proven parked on the lock it took: the
    /// advisory key every queue writer meets, or an uncommitted pending structural change whose visibility is the point.
    /// </summary>
    private async Task<Holder> OpenHolderAsync(World world)
    {
        var connection = await fixture.OpenSuperuserAsync(world.Database);
        var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var holder = new Holder(connection, transaction);

        await holder.ExecuteAsync("select set_config('application_name', 'authoring-change-race', true)");

        return holder;
    }

    /// <summary>Takes the project's queue lock in the holder's transaction, the key the handler and the trigger share.</summary>
    private static Task<int> LockProjectAsync(World world, Holder holder) =>
        holder.ExecuteAsync($"""
            select pg_advisory_xact_lock(
                hashtextextended('authoring_change:' || '{world.Org:D}' || ':' || '{world.Project:D}', 0))
            """);

    /// <summary>
    /// Writes one uncommitted pending structural change for <paramref name="itemId"/>, which takes the project's queue
    /// lock through the guard trigger; whichever writer comes next parks until the holder commits.
    /// </summary>
    private static Task<int> InsertPendingStructuralAsync(World world, Holder holder, Guid itemId, string changeType = "delete") =>
        holder.ExecuteAsync($$"""
            insert into public.authoring_change
                (id, org_id, project_id, target_id, change_type, payload, base_version, affected_versions,
                 author_membership_id)
            values ('{{Guid.CreateVersion7():D}}', '{{world.Org:D}}', '{{world.Project:D}}', '{{itemId:D}}',
                '{{changeType}}', '{}'::jsonb, 1, jsonb_build_object('{{itemId:D}}', 1), '{{world.Membership:D}}')
            """);

    /// <summary>
    /// Waits until a backend is parked on the project's queue lock — the observed condition, not a timer: the split
    /// project key lands in <c>pg_locks</c> as its two 32-bit halves, which is how both the handler's lock statement and
    /// the guard trigger's appear.
    /// </summary>
    private async Task AwaitProjectLockWaiterAsync(World world)
    {
        var deadline = DateTime.UtcNow + WaitLimit;

        while (DateTime.UtcNow < deadline)
        {
            var waiting = await ProjectLockWaiterCountAsync(world);
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("no backend parked on the project's authoring-change lock");
    }

    private Task<int> ProjectLockWaiterCountAsync(World world) => fixture.ScalarAsync<int>(world.Database, $"""
        select count(*)::int
          from pg_locks l
         where l.locktype = 'advisory'
           and not l.granted
           and l.classid::bigint
               = ((hashtextextended('authoring_change:' || '{world.Org:D}' || ':' || '{world.Project:D}', 0) >> 32)
                  & 4294967295)
           and l.objid::bigint
               = (hashtextextended('authoring_change:' || '{world.Org:D}' || ':' || '{world.Project:D}', 0)
                  & 4294967295)
        """);

    private Task<int> ChangeCountAsync(World world) => fixture.ScalarAsync<int>(world.Database,
        $"select count(*)::int from public.authoring_change where org_id = '{world.Org}'");

    private Task<int> PendingCountAsync(World world) => fixture.ScalarAsync<int>(world.Database,
        $"select count(*)::int from public.authoring_change where org_id = '{world.Org}' and state = 'pending'");

    private Task<int> AuditCountAsync(World world, string action) => fixture.ScalarAsync<int>(world.Database,
        $"select count(*)::int from public.audit_log where org_id = '{world.Org}' and action = '{action}'");

    private static List<Guid> RowIds(ApiAnswer answer) =>
        answer.Body.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("change_id").GetGuid())
            .ToList();

    private static string WithoutRequestId(ApiAnswer answer) =>
        System.Text.RegularExpressions.Regex.Replace(
            answer.Body.GetRawText(), "\"request_id\":\"[^\"]*\",?", string.Empty);

    private static void AssertInvalid(ApiAnswer answer, string path, string reason)
    {
        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        var detail = answer.Body.GetProperty("error").GetProperty("details")[0];
        detail.GetProperty("path").GetString().ShouldBe(path);
        detail.GetProperty("reason").GetString().ShouldBe(reason);
    }

    private static void AssertAlreadyPending(ApiAnswer answer, string path)
    {
        answer.Status.ShouldBe(HttpStatusCode.Conflict, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("invalid_transition");
        var detail = answer.Body.GetProperty("error").GetProperty("details")[0];
        detail.GetProperty("path").GetString().ShouldBe(path);
        detail.GetProperty("reason").GetString().ShouldBe("change_already_pending");
    }

    private sealed record World(
        string Database,
        TenancyApiFactory Api,
        HttpClient Client,
        Guid Org,
        Guid Project,
        Guid User,
        Guid Membership,
        IReadOnlyList<Guid> Items,
        Action<HttpRequestHeaders> Caller) : IAsyncDisposable
    {
        internal string ChangesPath => $"/v1/projects/{Project:D}/authoring/changes";

        internal string ChangePath(Guid change) => $"{ChangesPath}/{change:D}";

        internal string ApplyPath => $"{ChangesPath}:apply";

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>One superuser transaction a race test holds open; disposing it without committing rolls it back.</summary>
    private sealed class Holder(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        internal async Task<int> ExecuteAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;

            return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        internal Task CommitAsync() => transaction.CommitAsync(TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
