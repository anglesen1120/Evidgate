using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class IncidentRecordingTests
{
    [Fact]
    public async Task list_incidents_single_statement()
    {
        await using var world = await StartAsync(2);
        await AttemptAsync(world, 0, 1, "failed");
        await AttemptAsync(world, 0, 2, "failed");
        await AttemptAsync(world, 1, 1, "failed");
        await FreezeAsync(world, 1);
        (await RecordAsync(world, 1)).ShouldBe(3);
        await using var api = hosts.GrantAll(world.Database);
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var path = $"/v1/projects/{world.Project}/incidents";
        api.Statements.Clear();
        var page = await client.SendAsync(HttpMethod.Get, path, TenancyApiFactory.Caller(world.Org, user));
        page.Status.ShouldBe(HttpStatusCode.OK);
        var rows = page.Body.GetProperty("items").EnumerateArray().ToArray();
        rows.Select(row => row.GetProperty("attempt_count").GetInt32()).Order().ShouldBe([1, 2]);
        api.Statements.Snapshot().Count(sql => sql.Contains("public.incident", StringComparison.Ordinal)).ShouldBe(1);

        var scoped = TenancyApiFactory.Caller(world.Org, user, project: Guid.CreateVersion7());
        var denied = await client.SendAsync(HttpMethod.Get, path + "?state=invalid", scoped);
        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        denied.ErrorCode.ShouldBe("project_scope_mismatch");
        var detail = await client.SendAsync(HttpMethod.Get, $"/v1/incidents/{rows[0].GetProperty("id").GetGuid()}", scoped);
        detail.Status.ShouldBe(HttpStatusCode.Forbidden);
        detail.ErrorCode.ShouldBe("project_scope_mismatch");
    }

    [Fact]
    public async Task incident_list_view_is_invoker_scoped_and_app_read_only()
    {
        var database = await fixture.SharedDatabaseAsync();
        (await fixture.ScalarAsync<string>(
            database,
            "select coalesce(array_to_string(reloptions, ','), '') from pg_class where oid = 'public.incident_list'::regclass"))
            .Split(',').ShouldContain("security_invoker=true");
        foreach (var (role, privilege, expected) in new[]
        {
            ("evidgate_app", "SELECT", true),
            ("evidgate_app", "INSERT", false),
            ("evidgate_app", "UPDATE", false),
            ("evidgate_app", "DELETE", false),
            ("evidgate_operator", "SELECT", false),
            ("evidgate_worker", "SELECT", false),
            ("evidgate_worker_sched", "SELECT", false),
        })
        {
            (await fixture.ScalarAsync<bool>(
                database,
                $"select has_table_privilege('{role}', 'public.incident_list', '{privilege}')"))
                .ShouldBe(expected, $"{role} {privilege}");
        }
    }
}
