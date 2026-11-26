using System.Net;
using Evidgate.Modules.Healing.Application;
using Npgsql;
using NpgsqlTypes;
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

    /// <summary>
    /// Triaged is a rare state of a 10 000-incident project. Both statement shapes must read it from
    /// <c>incident_project_state_list</c> in page order: with only the state index the page sorts what the state matched.
    /// </summary>
    [Fact]
    public async Task list_incidents_state_page_streams_from_state_order_index()
    {
        await using var owned = await fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartDedicatedAsync(owned.Name);
        await SeedPlanIncidentsAsync(world);

        await using var connection = new NpgsqlConnection(fixture.AppConnectionString(world.Database));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var scope = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
        {
            scope.Parameters.AddWithValue("org", world.Org.ToString());
            await scope.ExecuteScalarAsync(Ct);
        }

        // Seeded creation instants run one per second from 2026-01-01T00:00:01Z; this is the 5 000th.
        var midRange = new DateTimeOffset(2026, 1, 1, 1, 23, 20, TimeSpan.Zero);
        foreach (var plan in new[]
        {
            await ExplainStatePageAsync(connection, transaction, world, IncidentSql.FirstPage, null),
            await ExplainStatePageAsync(connection, transaction, world, IncidentSql.NextPage, midRange),
        })
        {
            var nodes = PlanJson.Nodes(plan);
            PlanJson.UsesIndex(nodes, "incident_project_state_list").ShouldBeTrue(plan);
            PlanJson.ScansSequentially(nodes, "incident").ShouldBeFalse(plan);
            PlanJson.SortsScanOf(plan, "incident").ShouldBeFalse(plan);
        }
    }

    private const int PlanSiblingRuns = 4;
    private const int PlanIncidentsPerRun = 2500;
    private const string PlanVariantHash = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    /// <summary>The same world as <c>StartAsync</c> in a database of its own, so the planner statistics are this test's alone.</summary>
    private async Task<World> StartDedicatedAsync(string database)
    {
        var api = hosts.RealResolver(database);
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("incident"));
        var project = await CreateProjectAsync(client, user, org, "incident-probe");
        var plan = await EvaluationPlanSeeder.SeedAsync(fixture, database, org, 1);
        return new World(api, client, database, org, project, plan);
    }

    /// <summary>
    /// <see cref="PlanIncidentsPerRun"/> failed attempts in each of <see cref="PlanSiblingRuns"/> sibling runs of the
    /// project, one incident per attempt with creation instants one second apart, and every 40th incident triaged.
    /// </summary>
    private async Task SeedPlanIncidentsAsync(World world)
    {
        var execution = world.Plan.Executions[0];
        await using (var scope = await TenantScope.BeginAsync(fixture.OwnerConnectionString(world.Database)))
        {
            (await scope.ExecuteAsync(
                """
                with own as (
                    select r.org_id, r.project_id, r.manifest_id, r.suite_revision_id, r.environment_revision_id
                      from public.run r
                     where r.org_id = @org_id and r.id = @own_run_id
                ), other as (
                    insert into public.run
                      (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id,
                       collection_deadline_at, late_window_minutes)
                    select gen_random_uuid(), o.org_id, o.project_id, o.manifest_id, o.suite_revision_id,
                           o.environment_revision_id, now() + interval '2 hours', 1440
                      from own o cross join generate_series(1, @runs) g
                    returning id
                ), planned as (
                    insert into public.planned_execution
                      (id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id)
                    select gen_random_uuid(), @org_id, o.id,
                           md5(o.id::text || ':' || g::text) || md5(g::text || ':' || o.id::text),
                           @case_revision_id, @variant_hash, 'staging', 'shard-1'
                      from other o cross join generate_series(1, @per_run) g
                    returning run_id, execution_key
                )
                insert into public.case_attempt
                  (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status, started_at)
                select gen_random_uuid(), @org_id, p.run_id, p.execution_key, @case_id, 1, 'failed', 'failed', now()
                  from planned p
                """,
                ("org_id", world.Org), ("own_run_id", world.Plan.RunId), ("case_id", execution.CaseId),
                ("case_revision_id", execution.CaseRevisionId), ("runs", PlanSiblingRuns),
                ("per_run", PlanIncidentsPerRun), ("variant_hash", PlanVariantHash)))
                .ShouldBe(PlanSiblingRuns * PlanIncidentsPerRun);
            (await scope.ExecuteAsync(
                """
                insert into public.incident
                  (id, org_id, project_id, run_id, attempt_id, case_id, case_revision_id, execution_key, engine, created_at)
                select uuidv7(), a.org_id, @project_id, a.run_id, a.id, a.case_id, p.case_revision_id, a.execution_key,
                       'playwright',
                       timestamptz '2026-01-01T00:00:00Z'
                         + row_number() over (order by a.run_id, a.execution_key) * interval '1 second'
                  from public.case_attempt a
                  join public.planned_execution p
                    on p.org_id = a.org_id and p.run_id = a.run_id and p.execution_key = a.execution_key
                 where a.org_id = @org_id and a.run_id <> @own_run_id
                """,
                ("org_id", world.Org), ("project_id", world.Project), ("own_run_id", world.Plan.RunId)))
                .ShouldBe(PlanSiblingRuns * PlanIncidentsPerRun);
            // 10 000 consecutive seconds hold exactly 250 multiples of 40, whatever the epoch offset.
            (await scope.ExecuteAsync(
                "update public.incident set state = 'triaged' "
                + "where org_id = @org_id and extract(epoch from created_at)::bigint % 40 = 0",
                ("org_id", world.Org))).ShouldBe(250);
            await scope.CommitAsync();
        }

        await fixture.ExecuteAsync(
            world.Database,
            "analyze public.incident; analyze public.planned_execution; analyze public.case_attempt");
    }

    /// <summary>Explains one served incident statement with its real parameters for the triaged state.</summary>
    private static async Task<string> ExplainStatePageAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, World world, string sql, DateTimeOffset? cursor)
    {
        await using var command = new NpgsqlCommand("explain (format json) " + sql, connection, transaction);
        command.Parameters.AddWithValue("org_id", world.Org);
        command.Parameters.AddWithValue("project_id", world.Project);
        command.Parameters.AddWithValue("state", "triaged");
        command.Parameters.Add(new NpgsqlParameter("execution_key", NpgsqlDbType.Text) { Value = DBNull.Value });
        command.Parameters.AddWithValue("take", 51);
        if (cursor is not null)
        {
            command.Parameters.AddWithValue("cursor_created_at", cursor.Value);
            command.Parameters.AddWithValue("cursor_id", Guid.CreateVersion7());
        }

        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }
}
