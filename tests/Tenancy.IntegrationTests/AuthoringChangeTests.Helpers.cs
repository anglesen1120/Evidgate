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

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }
}
