using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed partial class IncidentRecordingTests(PostgresTenancyFixture fixture, TenancyHostPool hosts)
    : IClassFixture<TenancyHostPool>
{
    private static readonly string[] Statuses = ["skipped", "blocked", "failed", "failed", "failed"];

    [Fact]
    public async Task Freeze_Creates_Incidents()
    {
        await using var world = await StartAsync(3);
        var first = await AttemptAsync(world, 0, 1, "failed");
        await AttemptAsync(world, 1, 1, "error");
        await AttemptAsync(world, 2, 1, "passed");
        await FreezeAsync(world, 1);
        (await RecordAsync(world, 1)).ShouldBe(2);
        (await CountAsync(world, "incident", "state='open' and context_state='pending' and version=1 and engine='playwright'")).ShouldBe(2);
        var execution = world.Plan.Executions[0];
        (await CountAsync(world, "incident", $"attempt_id='{first}' and case_id='{execution.CaseId}' and case_revision_id='{execution.CaseRevisionId}' and execution_key='{execution.ExecutionKey}' and run_id='{world.Plan.RunId}' and project_id='{world.Project}'")).ShouldBe(1);
        (await EventsAsync(world)).ShouldBe(2);
        (await AuditsAsync(world)).ShouldBe(2);
    }

    [Fact]
    public async Task Retry_Attempt_Attaches_To_Incident()
    {
        await using var world = await StartAsync(1);
        var first = await AttemptAsync(world, 0, 1, "failed");
        await FreezeAsync(world, 1);
        var retry = await AttemptAsync(world, 0, 2, "timed_out");
        await FreezeAsync(world, 2);
        // Later revision delivered first: the incident still points at the original failure.
        (await RecordAsync(world, 2)).ShouldBe(2);
        (await RecordAsync(world, 1)).ShouldBe(0);
        (await CountAsync(world, "incident", $"attempt_id='{first}' and state='open' and version=1")).ShouldBe(1);
        (await CountAsync(world, "incident_attempt", $"attempt_id='{retry}' and attempt_no=2 and result='timed_out' and incident_id=(select id from public.incident where attempt_id='{first}')")).ShouldBe(1);
        (await EventsAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task Freeze_Redelivery_Is_Noop()
    {
        await using var world = await StartAsync(1);
        await AttemptAsync(world, 0, 1, "failed");
        await FreezeAsync(world, 1);
        await AttemptAsync(world, 0, 2, "error");
        (await RecordAsync(world, 1)).ShouldBe(1);
        (await RecordAsync(world, 1)).ShouldBe(0);
        (await CountAsync(world, "incident_attempt")).ShouldBe(0);
        await FreezeAsync(world, 2);
        (await RecordAsync(world, 2)).ShouldBe(1);
        (await RecordAsync(world, 2)).ShouldBe(0);
        (await CountAsync(world, "incident_attempt")).ShouldBe(1);
        (await EventsAsync(world)).ShouldBe(1);
        (await AuditsAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task Earlier_Admission_Transaction_Committed_After_Freeze_Attaches_Retry_Only_Once()
    {
        await using var world = await StartAsync(1);
        await AttemptAsync(world, 0, 1, "failed");
        await using var late = await TenantScope.BeginAsync(fixture.OwnerConnectionString(world.Database), world.Org);
        await FreezeAsync(world, 1);
        (await RecordAsync(world, 1)).ShouldBe(1);
        var retry = await InsertAttemptAsync(late, world, 0, 2, "error");
        await late.CommitAsync();

        await AssertReadersAgreeAsync(world, "error");
        (await RecordAsync(world, 1)).ShouldBe(1);
        (await RecordAsync(world, 1)).ShouldBe(0);
        (await CountAsync(world, "incident")).ShouldBe(1);
        (await CountAsync(world, "incident_attempt", $"attempt_id='{retry}'")).ShouldBe(1);
        (await EventsAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task Earlier_Freeze_Transaction_Excludes_Attempt_Committed_Before_Revision_Insert()
    {
        await using var world = await StartAsync(1);
        await using var freeze = await TenantScope.BeginAsync(fixture.OwnerConnectionString(world.Database), world.Org);
        await AttemptAsync(world, 0, 1, "failed");
        await InsertFreezeAsync(freeze, world, 1);
        await freeze.CommitAsync();

        await AssertReadersAgreeAsync(world, null);
        (await RecordAsync(world, 1)).ShouldBe(0);
        await FreezeAsync(world, 2);
        (await RecordAsync(world, 2)).ShouldBe(1);
    }

    private static async Task AssertReadersAgreeAsync(World world, string? status)
    {
        await using var scope = await ContractScope.BeginAsync(world.Api, world.Org);
        var evaluated = await scope.Resolve<IFrozenEvidence>().GetFrozenInputAsync(world.Org, world.Plan.RunId, 1, Ct);
        var failures = await scope.Resolve<IFrozenIncidentFailures>().ReadAsync(world.Plan.RunId, 1, Ct);
        var latest = failures.ShouldNotBeNull().Attempts.GroupBy(row => row.ExecutionKey)
            .Select(group => new FrozenAttemptStatus(group.Key, group.MaxBy(row => row.AttemptNo)!.Result)).ToArray();
        var expected = status is null ? Array.Empty<FrozenAttemptStatus>() : [new(world.Plan.Executions[0].ExecutionKey, status)];
        evaluated.ShouldNotBeNull().Attempts.ToArray().ShouldBe(expected);
        latest.ShouldBe(expected);
    }

    [Fact]
    public async Task Replay_Run_Creates_No_Incident()
    {
        await using var world = await StartAsync(1);
        await AttemptAsync(world, 0, 1, "failed");
        await FreezeAsync(world, 1);
        await fixture.ExecuteAsync(world.Database, $"update public.run set purpose='replay', replay_grant_id=uuidv7() where id='{world.Plan.RunId}'");
        (await RecordAsync(world, 1)).ShouldBe(0);
        (await CountAsync(world, "incident")).ShouldBe(0);
        (await CountAsync(world, "incident_attempt")).ShouldBe(0);
        (await EventsAsync(world)).ShouldBe(0);
        (await AuditsAsync(world)).ShouldBe(0);
        await fixture.ExecuteAsync(world.Database, $"update public.run set purpose='regular', replay_grant_id=null where id='{world.Plan.RunId}'");
        (await RecordAsync(world, 1)).ShouldBe(1);
    }

    [Fact]
    public async Task Freeze_Skipped_Blocked_Optional_And_Recovered_Are_Not_Failures()
    {
        await using var world = await StartAsync(5);
        await fixture.ExecuteAsync(world.Database, $"update public.planned_execution set required=false where id='{world.Plan.Executions[2].ExecutionId}'");
        foreach (var (index, status) in Statuses.Index())
        {
            await AttemptAsync(world, index, 1, status);
        }

        await AttemptAsync(world, 3, 2, "passed");
        await FreezeAsync(world, 1);
        (await RecordAsync(world, 1)).ShouldBe(1);
        (await CountAsync(world, "incident", $"execution_key='{world.Plan.Executions[4].ExecutionKey}'")).ShouldBe(1);
        (await CountAsync(world, "incident_attempt")).ShouldBe(0);
    }

    [Fact]
    public async Task Record_Incidents_Batch()
    {
        await using var world = await StartAsync(5);
        for (var index = 0; index < 5; index++)
        {
            await AttemptAsync(world, index, 1, "failed");
        }

        await AttemptAsync(world, 0, 2, "error");
        await AttemptAsync(world, 1, 2, "timed_out");
        await FreezeAsync(world, 1);
        world.Api.Statements.Clear();
        (await RecordAsync(world, 1)).ShouldBe(7);
        (await CountAsync(world, "incident", "engine = 'playwright'")).ShouldBe(5);
        (await CountAsync(world, "incident_attempt", "(attempt_no, result) in ((2, 'error'), (2, 'timed_out'))")).ShouldBe(2);
        var statements = world.Api.Statements.Snapshot();
        var insert = statements.Single(s => s.Contains("insert into public.incident (", StringComparison.OrdinalIgnoreCase));
        insert.ShouldContain("unnest(");
        statements.Count(s => s.Contains("from public.test_revision", StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
    }

    [Fact]
    public async Task Frozen_Failures_And_Revision_Frameworks_Are_Tenant_Exact()
    {
        await using var world = await StartAsync(1);
        await AttemptAsync(world, 0, 1, "failed");
        await FreezeAsync(world, 1);
        var revision = world.Plan.Executions[0].CaseRevisionId;
        foreach (var org in new[] { world.Org, Guid.CreateVersion7() })
        {
            await using var scope = await ContractScope.BeginAsync(world.Api, org);
            var failures = await scope.Resolve<IFrozenIncidentFailures>().ReadAsync(world.Plan.RunId, 1, Ct);
            var frameworks = await scope.Resolve<IIncidentRevisionFrameworks>().ReadAsync(world.Project, [revision], Ct);
            if (org == world.Org)
            {
                failures.ShouldNotBeNull().Attempts.Count.ShouldBe(1);
                frameworks[revision].ShouldBe("playwright");
            }
            else
            {
                failures.ShouldBeNull();
                frameworks.ShouldBeEmpty();
            }
        }
    }

    [Fact]
    public async Task Unknown_Revision_Is_Refused_Without_Audit()
    {
        await using var world = await StartAsync(1);
        await AttemptAsync(world, 0, 1, "failed");
        await FreezeAsync(world, 1);
        await using (var scope = await ContractScope.BeginAsync(world.Api, world.Org))
        {
            var result = await scope.Resolve<RecordIncidentsForEvidenceRevisionHandler>().Handle(new(world.Plan.RunId, 2), Ct);
            result.IsSuccess.ShouldBeFalse();
        }

        (await AuditsAsync(world)).ShouldBe(0);
        (await EventsAsync(world)).ShouldBe(0);
        (await RecordAsync(world, 1)).ShouldBe(1);
    }
}
