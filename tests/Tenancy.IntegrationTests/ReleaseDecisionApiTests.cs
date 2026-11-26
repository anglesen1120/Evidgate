// GET /v1/releases/{id}/decision through the real host: the stored breakdown, the pending body that writes nothing,
// and the permission refusal.
using System.Net;
using System.Net.Http.Headers;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Policy.Application.Releases;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class ReleaseDecisionApiTests : IClassFixture<TenancyHostPool>
{
    private const string Empty = "{}";

    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;

    public ReleaseDecisionApiTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

    [Fact]
    public async Task Get_ReturnsPerComponentBreakdown()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var release = await ReleaseIntegrationTests.ReleaseWithAsync(world, "breakdown", "active", ("web", 'a'), ("api", 'b'));
        var policy = await PolicyAsync(world);
        var run = await ReleaseIntegrationTests.RunWithAsync(world, ReleaseIntegrationTests.Components(("web", 'a')));
        (await ReleaseIntegrationTests.BindAsync(world, release, run)).Status.ShouldBe(HttpStatusCode.Created);
        await EvaluateAsync(world, run, policy);
        await AggregateAsync(world, release);

        var answer = await GetAsync(world, release, world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("status").ShouldBe("aggregated");
        answer.Text("decision").ShouldBe("INCONCLUSIVE");
        answer.Text("completeness").ShouldBe("incomplete");
        answer.Id("release_id").ShouldBe(release);
        var components = answer.Body.GetProperty("components").EnumerateArray().ToDictionary(c => c.GetProperty("component_id").GetString()!);
        components["web"].GetProperty("status").GetString().ShouldBe("evaluated");
        components["web"].GetProperty("run_id").GetGuid().ShouldBe(run);
        components["api"].GetProperty("status").GetString().ShouldBe("missing");
        components["api"].GetProperty("reasons")[0].GetString().ShouldBe("component_run_unbound");
    }

    [Fact]
    public async Task Get_NoRow_Pending_NoWrites()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var release = await ReleaseIntegrationTests.ReleaseWithAsync(world, "pending", "active", ("web", 'a'));
        var before = await WriteCountsAsync(world);

        var answer = await GetAsync(world, release, world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("status").ShouldBe("pending");
        answer.Text("decision").ShouldBe("pending");
        answer.Id("release_id").ShouldBe(release);
        (await WriteCountsAsync(world)).ShouldBe(before, "the read stores no row, enqueues no task and writes no outbox row");
    }

    [Fact]
    public async Task NoPermission_403()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var release = await ReleaseIntegrationTests.ReleaseWithAsync(world, "forbidden", "active", ("web", 'a'));
        var caller = TenancyApiFactory.Resolved(
            serviceOrg: world.Org, serviceProject: world.Project, servicePermissions: ["run:read"]);

        var answer = await GetAsync(world, release, caller);

        answer.Status.ShouldBe(HttpStatusCode.Forbidden, answer.ErrorMessage);
    }

    [Fact]
    public async Task UnknownRelease_404()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        var answer = await GetAsync(world, Guid.CreateVersion7(), world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.NotFound, answer.ErrorMessage);
    }

    private static async Task<ApiAnswer> GetAsync(RunCreationTests.World world, Guid release, Action<HttpRequestHeaders> caller)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/releases/{release:D}/decision");
        caller(request.Headers);
        using var response = await world.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = System.Text.Json.JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    /// <summary>Row counts a read must leave alone: aggregates, tasks and outbox rows of the world's org.</summary>
    private async Task<(long Decisions, long Tasks, long Outbox)> WriteCountsAsync(RunCreationTests.World world) =>
    (
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.release_decision where org_id = '{world.Org}'"),
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.task where org_id = '{world.Org}'"),
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.outbox where org_id = '{world.Org}'")
    );

    private async Task<Guid> PolicyAsync(RunCreationTests.World world)
    {
        var id = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, created_by)
            values ('{id}', '{world.Org}', '{world.Project}', 1, 'sha256:{new string('c', 64)}', '{Empty}'::jsonb, '{Guid.CreateVersion7()}')
            """);

        return id;
    }

    private Task EvaluateAsync(RunCreationTests.World world, Guid run, Guid policy) =>
        _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.evaluation
              (id, org_id, run_id, evidence_revision, policy_revision_id, policy_hash, manifest_hash, completeness,
               missing, outcome, decision, reasons, evaluator_version, mode, exceptions_hash, exceptions_as_of)
            select '{Guid.CreateVersion7()}', '{world.Org}', '{run}', 1, p.id, p.hash, 'sha256:{new string('e', 64)}',
                   'complete', '{Empty}', 'all_passed', 'PASS', '{Empty}', '1.0.0', 'live', 'sha256:{new string('f', 64)}', now()
              from public.policy_revision p where p.org_id = '{world.Org}' and p.id = '{policy}'
            """);

    /// <summary>Runs the production aggregation, so the GET is checked against what the task really stores.</summary>
    private static async Task AggregateAsync(RunCreationTests.World world, Guid release)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await world.Api.Services.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, cancellationToken: cancellationToken);
        await using var services = world.Api.Services.CreateAsyncScope();
        await services.ServiceProvider.GetRequiredService<AggregateReleaseDecisionCommandHandler>()
            .Handle(new AggregateReleaseDecisionCommand(release), cancellationToken);
        await scope.CommitAsync(cancellationToken);
    }
}
