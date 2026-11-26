using Evidgate.Modules.Healing.Application;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class IncidentRecordingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid Org, Guid Project,
        EvaluationPlanSeeder.Plan Plan) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync(int executions)
    {
        var database = await fixture.SharedDatabaseAsync();
        var api = hosts.RealResolver(database);
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("incident"));
        var project = await CreateProjectAsync(client, user, org, "incident-probe");
        var plan = await EvaluationPlanSeeder.SeedAsync(fixture, database, org, executions);
        return new World(api, client, database, org, project, plan);
    }

    private async Task<Guid> AttemptAsync(World world, int index, int number, string status)
    {
        await using var owner = await TenantScope.BeginAsync(fixture.OwnerConnectionString(world.Database));
        var id = await InsertAttemptAsync(owner, world, index, number, status);
        await owner.CommitAsync();
        return id;
    }

    private static async Task<Guid> InsertAttemptAsync(TenantScope owner, World world, int index, int number, string status)
    {
        var id = Guid.CreateVersion7();
        var execution = world.Plan.Executions[index];
        (await owner.ExecuteAsync(
            """
            insert into public.case_attempt
              (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status)
            values (@id, @org_id, @run_id, @execution_key, @case_id, @attempt_no, @status, @status)
            """,
            ("id", id), ("org_id", world.Org), ("run_id", world.Plan.RunId), ("execution_key", execution.ExecutionKey),
            ("case_id", execution.CaseId), ("attempt_no", number), ("status", status))).ShouldBe(1);
        return id;
    }

    private async Task FreezeAsync(World world, int revision)
    {
        await using var owner = await TenantScope.BeginAsync(fixture.OwnerConnectionString(world.Database));
        await InsertFreezeAsync(owner, world, revision);
        await owner.CommitAsync();
    }

    private static async Task InsertFreezeAsync(TenantScope owner, World world, int revision)
    {
        (await owner.ExecuteAsync(
            """
            insert into public.evidence_revision
              (id, org_id, run_id, revision_no, completeness, missing, manifest_hash, event_watermark)
            values (@id, @org_id, @run_id, @revision_no, 'incomplete', '{missing_result}'::text[], @manifest_hash, 7)
            """,
            ("id", Guid.CreateVersion7()), ("org_id", world.Org), ("run_id", world.Plan.RunId), ("revision_no", revision),
            ("manifest_hash", "sha256:" + new string('a', 64)))).ShouldBe(1);
    }

    /// <summary>Runs the recorder in the tenant's transaction, as the worker does, and answers the rows it wrote.</summary>
    private static async Task<int> RecordAsync(World world, int revision)
    {
        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var result = await caller.Resolve<RecordIncidentsForEvidenceRevisionHandler>()
            .Handle(new RecordIncidentsForEvidenceRevision(world.Plan.RunId, revision), Ct);
        result.IsSuccess.ShouldBeTrue();
        await caller.CommitAsync();
        return result.Value;
    }

    private Task<int> CountAsync(World world, string table, string filter = "true") =>
        fixture.ScalarAsync<int>(world.Database, $"select count(*)::int from public.{table} where org_id='{world.Org}' and {filter}");

    private Task<int> AuditsAsync(World world) => CountAsync(world, "audit_log",
        "action='incident.created' and actor_type='system' and object_type='incident' and object_id in (select id::text from public.incident)");

    private Task<int> EventsAsync(World world) => CountAsync(world, "outbox",
        "task_type='incident.created' and aggregate_type='incident' and aggregate_id in (select id from public.incident)");
}
