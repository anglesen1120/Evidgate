using System.Net;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Two revisions of one case in the same suite and variant plan the same execution: upload accepts them (it compares
/// case revisions, not cases) and run creation refuses the manifest with 422 before it writes anything.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RunCreationExecutionKeyConflictTests(PostgresTenancyFixture fixture, TenancyHostPool hosts)
    : IClassFixture<TenancyHostPool>
{
    private readonly PostgresTenancyFixture _fixture = fixture;
    private readonly TenancyHostPool _hosts = hosts;

    [Fact]
    public async Task CreateRun_CollidingExecutionKey_422_NoRowsWritten()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, cases: 1, hosts: _hosts);
        var manifestId = await UploadCollidingManifestAsync(world);

        var answer = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(manifestId));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("execution_key_conflict");
        var detail = answer.Body.GetProperty("error").GetProperty("details").EnumerateArray().Single();
        detail.GetProperty("path").GetString().ShouldBe("/planned_executions/1", "the second plan row repeats the first key");
        detail.GetProperty("reason").GetString().ShouldBe("execution_key_conflict");
        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);
    }

    /// <summary>
    /// Fault-injection pin, not a race test: run ids are fresh per create, so only a trigger in this test's own database
    /// (never the shared one) can raise the unique violation the catch answers. The same request then succeeds.
    /// </summary>
    [Fact]
    public async Task CreateRun_UniqueViolationFromInsert_MapsTo422_NothingWritten()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await RunCreationTests.StartAsync(_fixture, cases: 1, database: owned.Name);
        await _fixture.ExecuteAsync(
            world.Database,
            "create function public.fault_execution_key() returns trigger language plpgsql as $$ begin "
            + "raise exception 'injected' using errcode = '23505', constraint = 'planned_execution_run_execution_key'; "
            + "end $$; "
            + "create trigger fault_execution_key before insert on public.planned_execution "
            + "for each row execute function public.fault_execution_key()");

        var key = NewKey();
        var body = RunCreationTests.RunBody(world.ManifestId);

        var answer = await RunCreationTests.CreateRunAsync(world, body, idempotencyKey: key);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("execution_key_conflict", "the catch maps the violation of the execution-key index");
        var detail = answer.Body.GetProperty("error").GetProperty("details").EnumerateArray().Single();
        detail.GetProperty("path").GetString().ShouldBe("/planned_executions");
        detail.GetProperty("reason").GetString().ShouldBe("execution_key_conflict");
        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);

        await _fixture.ExecuteAsync(world.Database, "drop trigger fault_execution_key on public.planned_execution");
        var retry = await RunCreationTests.CreateRunAsync(world, body, idempotencyKey: key);
        retry.Status.ShouldBe(HttpStatusCode.Created, "the same request and key succeed once the fault is gone: nothing was claimed or left behind");
    }

    [Fact]
    public async Task CreateRun_DistinctCases_SameVariant_NoConflict()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, cases: 2, hosts: _hosts);

        var answer = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(distinct execution_key) from public.planned_execution where org_id = '{world.Org}'"))
            .ShouldBe(4, "the same environment and variants over different cases keep distinct keys");
    }

    /// <summary>A manifest over the world's case and a second revision of that same case, one variant, one environment.</summary>
    private async Task<Guid> UploadCollidingManifestAsync(RunCreationTests.World world)
    {
        var second = await RegisterSecondRevisionAsync(world);
        var suite = await world.Client.SendAsync(
            HttpMethod.Post, $"/v1/projects/{world.Project}/suites", world.Admin, new { name = "Twice", slug = "twice" }, NewKey());
        suite.Status.ShouldBe(HttpStatusCode.Created, suite.ErrorMessage);
        Guid[] revisions = [world.Cases[0], second];
        var suiteRevision = await world.Client.SendAsync(
            HttpMethod.Post,
            $"/v1/suites/{suite.Id()}/revisions",
            world.Admin,
            new { members = revisions.Select(id => new { case_revision_id = id }).ToArray() },
            NewKey());
        suiteRevision.Status.ShouldBe(HttpStatusCode.Created, suiteRevision.ErrorMessage);

        var manifest = ManifestUnitTests.Build(
            suiteRevision.Id("suite_revision_id"), revisions, [new() { ["browser"] = "b0" }], ["staging"]);
        manifest["environment_revision_id"] = world.EnvironmentRevision.ToString();

        return await RunCreationTests.UploadAsync(world.Client, world.Admin, world.Project, manifest);
    }

    /// <summary>Re-registers the first case's file with changed source, which adds a revision under the same case id.</summary>
    private async Task<Guid> RegisterSecondRevisionAsync(RunCreationTests.World world)
    {
        var changed = new SourceFile(
            "m/0.spec.ts",
            "// evidgate.id: 018f2f0e-8a52-7c4b-9d3e-000000000000\ntest('0', () => { expect(a).toBe(100); });",
            "playwright",
            "typescript");
        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var outcome = await services.ServiceProvider.GetRequiredService<ICaseIdentityService>()
            .RegisterDiscoveredAsync(world.Project, "ccccccc3333333", [changed], TestContext.Current.CancellationToken);
        outcome.Succeeded.ShouldBeTrue(outcome.Failure);
        await scope.CommitAsync(TestContext.Current.CancellationToken);

        return await _fixture.ScalarAsync<Guid>(
            world.Database,
            "select r.id from public.test_revision r join public.test_case c on c.org_id = r.org_id and c.id = r.case_id "
            + $"where c.project_id = '{world.Project}' and c.file_path = 'm/0.spec.ts' and r.id <> '{world.Cases[0]}'");
    }
}
