// Request bodies are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.3.1-T1.4 AC7 through the real host and a real PostgreSQL: <c>GET /v1/runs/{r}</c> reports the run's state
/// machine position, the evidence revision its verdict was frozen from, its optimistic-concurrency version and its
/// purpose. The read is one Dapper statement over the ambient tenant scope, so another tenant's run is the same 404
/// <c>not_found</c> as an unknown id (00 §5.4: no existence oracle).
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class GetRunTests
{
    private readonly PostgresTenancyFixture _fixture;

    public GetRunTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>AC7: the body carries run id, state, evidence pointer, version and purpose, and matches the stored row.</summary>
    [Fact]
    public async Task GetRun_ReturnsStateAndEvidenceRevision()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateRunAsync(world);

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Text("run_id").ShouldBe(runId.ToString());
        answer.Text("state").ShouldBe("queued");
        answer.Text("purpose").ShouldBe("regular");

        // The pointer exists in the body and is null until 05.3.2 freezes a revision from it (the column is nullable).
        answer.Body.GetProperty("current_evidence_revision_id").ValueKind.ShouldBe(JsonValueKind.Null);

        // The version is the token a transition echoes (05.3.1 §5, C4); it must be the row's own value, not a constant.
        answer.Body.GetProperty("version").GetInt32().ShouldBe(
            await _fixture.ScalarAsync<int>(world.Database, $"select version from public.run where id = '{runId:D}'"));

        // Purpose is exposed because 05.2.1-T5 labels a replay run for regression-count exclusion; the read path is
        // the only place a client can see the label, and the row is the one that decides (seeded here as the owner,
        // since nothing served creates a replay run until the replay story).
        await _fixture.ExecuteAsync(world.Database, $"update public.run set purpose = 'replay' where id = '{runId:D}'");

        (await GetAsync(world, runId)).Text("purpose").ShouldBe("replay");
    }

    /// <summary>AC7: an unknown id is 404 <c>not_found</c>, the same answer another tenant's run gets.</summary>
    [Fact]
    public async Task GetRun_UnknownId_404()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answer = await GetAsync(world, Guid.CreateVersion7());

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
    }

    /// <summary>AC7, 00 §5.4: another org's run is invisible under RLS, and the answer is byte-for-byte the unknown-id one.</summary>
    [Fact]
    public async Task GetRun_CrossTenant_404()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateRunAsync(world);

        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");
        var foreign = await GetAsync(
            world,
            runId,
            TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg));

        foreign.Status.ShouldBe(HttpStatusCode.NotFound);
        var unknown = await GetAsync(world, Guid.CreateVersion7());

        foreign.ErrorCode.ShouldBe(unknown.ErrorCode);
        foreign.ErrorMessage.ShouldBe(unknown.ErrorMessage, "an existence oracle is a leak even when only the code matches");
    }

    /// <summary>T14/§5: <c>run:read</c> gates the route; a credential that does not hold it is refused 403.</summary>
    [Fact]
    public async Task GetRun_NoPermission_403()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateRunAsync(world);

        var answer = await GetAsync(
            world,
            runId,
            TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: world.Project, servicePermissions: ["run:create"]));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>§5: a project-scoped credential reads its own project only; the sibling project's run is refused 403.</summary>
    [Fact]
    public async Task GetRun_ProjectScopeMismatch_403()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateRunAsync(world);
        var other = await CreateProjectAsync(world.Client, world.User, world.Org, "other");

        var answer = await GetAsync(
            world,
            runId,
            TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: other, servicePermissions: ["run:read"]));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe("project_scope_mismatch");
    }

    /// <summary>Creates one run of the world's manifest through the served route; returns its id.</summary>
    private static async Task<Guid> CreateRunAsync(RunCreationTests.World world)
    {
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        return created.Id("run_id");
    }

    private static Task<ApiAnswer> GetAsync(
        RunCreationTests.World world,
        Guid runId,
        Action<HttpRequestHeaders>? caller = null) =>
        world.Client.SendAsync(HttpMethod.Get, $"/v1/runs/{runId}", caller ?? world.Admin);
}
