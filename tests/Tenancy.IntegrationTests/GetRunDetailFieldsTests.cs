// Request bodies are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// F44 (S143) through the real host and a real PostgreSQL: <c>GET /v1/runs/{r}</c> reports the release inputs a run
/// locked at creation — the pinned manifest's hash, the suite and policy revisions, the environment revision and the
/// producer slots it registered — every member taken from its stored column (the contract's 00 §9.3 row: no member
/// without a producer, and the producers named <c>shard-&lt;n&gt;</c> in <c>shard_index</c> order). The whole detail is
/// one statement, so N shards cost the read exactly what none does, and the three not-visible branches (unknown id,
/// another tenant, a credential scoped to a sibling project) stay the one <c>404 not_found</c> answer.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class GetRunDetailFieldsTests
{
    /// <summary>A body <c>policy-revision.v1</c> accepts, both timing values unset (the shape 07.1.1's suite uses).</summary>
    private const string PolicyBody = """{"required_suites":["smoke"],"flaky_budget":3}""";

    private readonly PostgresTenancyFixture _fixture;

    public GetRunDetailFieldsTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// F44: every member of the detail block is the stored value — the manifest's own hash, the run's suite and
    /// (still unpinned) policy revision, the environment revision's identity, and one producer per manifest shard in
    /// <c>shard_index</c> order.
    /// </summary>
    [Fact]
    public async Task GetRun_DetailFields_ComeFromTheStoredRows()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, cases: 2);
        // 2 cases × 3 variants × 1 environment = 6 planned executions, and the builder sizes shards at
        // ceil(6 / 3) = 2, so `shards: 3` really declares three of them (the shape 05.2.1's slot test uses).
        var manifest = RunCreationTests.Manifest(world.SuiteRevision, world.Cases, variants: 3, shards: 3);
        manifest["environment_revision_id"] = world.EnvironmentRevision.ToString();
        var manifestId = await RunCreationTests.UploadAsync(world.Client, world.Admin, world.Project, manifest);
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(manifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var runId = created.Id("run_id");

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        // The manifest hash is the pinned manifest's own value (`manifest.manifest_hash`), joined through the run.
        answer.Text("manifest_hash").ShouldBe(
            await _fixture.ScalarAsync<string>(world.Database, $"select manifest_hash from public.manifest where id = '{manifestId:D}'"));

        // The suite revision is the run's own locked column, not a re-read of the manifest.
        answer.Body.GetProperty("suite_revision_id").GetGuid().ShouldBe(world.SuiteRevision);
        answer.Body.GetProperty("suite_revision_id").GetGuid().ShouldBe(
            await _fixture.ScalarAsync<Guid>(world.Database, $"select suite_revision_id from public.run where id = '{runId:D}'"));

        // The world's project has no active policy revision, so the run pinned nothing: null, never a placeholder.
        answer.Body.GetProperty("policy_revision_id").ValueKind.ShouldBe(JsonValueKind.Null);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.run where id = '{runId:D}' and policy_revision_id is null")).ShouldBe(1);

        // The environment block is the pinned revision's identity plus the environment's own key.
        var environment = answer.Body.GetProperty("environment");
        environment.GetProperty("revision_id").GetGuid().ShouldBe(world.EnvironmentRevision);
        environment.GetProperty("environment_id").GetGuid().ShouldBe(
            await _fixture.ScalarAsync<Guid>(world.Database, $"select environment_id from public.environment_revision where id = '{world.EnvironmentRevision:D}'"));
        environment.GetProperty("environment_key").GetString().ShouldBe(
            await _fixture.ScalarAsync<string>(
                world.Database,
                $"select e.environment_key from public.environment_revision r join public.environment e on e.org_id = r.org_id and e.id = r.environment_id where r.id = '{world.EnvironmentRevision:D}'"));
        environment.GetProperty("revision_no").GetInt32().ShouldBe(
            await _fixture.ScalarAsync<int>(world.Database, $"select revision_no from public.environment_revision where id = '{world.EnvironmentRevision:D}'"));
        environment.GetProperty("revision_hash").GetString().ShouldBe(
            await _fixture.ScalarAsync<string>(world.Database, $"select revision_hash from public.environment_revision where id = '{world.EnvironmentRevision:D}'"));
        environment.GetProperty("revision_hash").GetString().ShouldStartWith("sha256:");

        // One slot per manifest shard, reported as the count the run registered and the ids the shards declare.
        answer.Body.GetProperty("shard_count").GetInt32().ShouldBe(3);
        answer.Body.GetProperty("shard_count").GetInt32().ShouldBe((int)await _fixture.ScalarAsync<long>(
            world.Database, $"select count(*) from public.producer_slot where run_id = '{runId:D}'"));
        answer.Body.GetProperty("shard_count").GetInt32().ShouldBe((int)await _fixture.ScalarAsync<long>(
            world.Database,
            $"select jsonb_array_length(document -> 'shard_plan' -> 'shards')::bigint from public.manifest where id = '{manifestId:D}'"));
        Producers(answer).ShouldBe(["shard-1", "shard-2", "shard-3"]);
        Producers(answer).ShouldBe(await _fixture.ColumnAsync<string>(
            world.Database, $"select producer_id from public.producer_slot where run_id = '{runId:D}' order by shard_index"));
    }

    /// <summary>
    /// F44: <c>policy_revision_id</c> is the run's own column — null while the project has no active revision, and the
    /// pinned id once one is (07.1.1-T4.2 owns the pin, this read only reports it).
    /// </summary>
    [Fact]
    public async Task GetRun_PolicyRevision_NullUntilTheColumnIsPinned()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var runId = created.Id("run_id");

        (await GetAsync(world, runId)).Body.GetProperty("policy_revision_id").ValueKind.ShouldBe(JsonValueKind.Null);

        // A policy revision created through the served route, then pinned onto this run: the read must report the
        // column, so a null here would be a hard-coded value rather than the stored one.
        var revision = await CreatePolicyAsync(world);
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set policy_revision_id = '{revision:D}' where id = '{runId:D}'");

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("policy_revision_id").GetGuid().ShouldBe(revision);
    }

    /// <summary>
    /// F44: <c>declared_producers</c> follows <c>shard_index</c>, not the order the slots were written in and not the
    /// ids' own collation — so the pair is permuted to make the three orders disagree and the body still reports the
    /// shard order. <c>producers</c> is the same list with each slot's state, so it must follow that order too.
    /// </summary>
    [Fact]
    public async Task GetRun_DeclaredProducers_FollowShardIndex()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var runId = created.Id("run_id");

        // Shard 1 of the manifest now names `shard-z` and shard 2 names `shard-a`: index order is the reverse of the
        // ids' collation, and a read that forgot the ORDER BY would have to be right by luck.
        var slots = $"where run_id = '{runId:D}'";
        await _fixture.ExecuteAsync(world.Database, $"update public.producer_slot set shard_index = shard_index + 10 {slots}");
        await _fixture.ExecuteAsync(world.Database, $"update public.producer_slot set shard_index = 13 - shard_index {slots}");
        await _fixture.ExecuteAsync(world.Database, $"update public.producer_slot set producer_id = 'x' || producer_id {slots}");
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.producer_slot set producer_id = case shard_index when 1 then 'shard-z' else 'shard-a' end {slots}");

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("shard_count").GetInt32().ShouldBe(2);
        Producers(answer).ShouldBe(["shard-z", "shard-a"]);
        Producers(answer).ShouldBe(await _fixture.ColumnAsync<string>(
            world.Database, $"select producer_id from public.producer_slot {slots} order by shard_index"));

        // The same order in the slot list, whose items carry the state `declared_producers` only names.
        SlotFacts(answer).Select(item => item.GetProperty("producer_id").GetString()!).ShouldBe(Producers(answer));
    }

    /// <summary>
    /// F44 with 00 §5.4: a run the caller may not see is the one <c>404 not_found</c> — for an unknown id, for another
    /// tenant's run under RLS, and for a credential scoped to a sibling project, whose handler-level scope mismatch is
    /// mapped to the same answer (never <c>403 project_scope_mismatch</c> and never a different message). A bound slot
    /// is seeded with a probe token id, so the refusal must leak neither the slot list nor that id.
    /// </summary>
    [Fact]
    public async Task GetRun_NotFound_IsTheSameAnswerForUnknownCrossTenantAndSiblingProject()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var runId = created.Id("run_id");
        await _fixture.ExecuteAsync(world.Database, $"""
            update public.producer_slot set bound_token_jti = '{SlotJti}'
             where run_id = '{runId:D}' and producer_id = 'shard-1'
            """);

        var unknown = await GetAsync(world, Guid.CreateVersion7());
        unknown.Status.ShouldBe(HttpStatusCode.NotFound);
        unknown.ErrorCode.ShouldBe("not_found");

        // Another org's run is invisible under the RLS policy, which is FORCEd on `run` (0031).
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));
        var foreign = await GetAsync(world, runId, TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg));
        foreign.Status.ShouldBe(HttpStatusCode.NotFound);
        foreign.ErrorCode.ShouldBe(unknown.ErrorCode);
        foreign.ErrorMessage.ShouldBe(unknown.ErrorMessage, "an existence oracle is a leak even when only the code matches");

        // A project-scoped credential is refused at the permission stage with 403 when it cannot hold `run:read`
        // (GetRunTests.GetRun_SiblingProjectCredential_Forbidden). This host grants every permission, so the request
        // reaches the handler's OrchestratorScope.Project branch — the defence-in-depth check under test here.
        await using var scoped = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(world.Database));
        using var client = scoped.CreateClient();
        var caller = TenancyApiFactory.Caller(org: world.Org, project: world.Project, serviceScope: world.Project, servicePermissions: ["run:read"]);

        // The control: the same credential scoped to the run's own project reads it, so the 404 below is the scope.
        var ownProject = await client.SendAsync(HttpMethod.Get, $"/v1/runs/{runId}", caller);
        ownProject.Status.ShouldBe(HttpStatusCode.OK, ownProject.ErrorMessage);

        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, "sibling");
        var siblingScope = await client.SendAsync(
            HttpMethod.Get,
            $"/v1/runs/{runId}",
            TenancyApiFactory.Caller(org: world.Org, project: sibling, serviceScope: sibling, servicePermissions: ["run:read"]));

        siblingScope.Status.ShouldBe(HttpStatusCode.NotFound);
        siblingScope.ErrorCode.ShouldBe(unknown.ErrorCode);
        siblingScope.ErrorMessage.ShouldBe(unknown.ErrorMessage, "the scope mismatch must not become a second answer");
        siblingScope.Body.TryGetProperty("producers", out _).ShouldBeFalse();
        siblingScope.Body.GetRawText().ShouldNotContain(SlotJti, Case.Sensitive);
    }

    /// <summary>Creates one policy revision through <c>POST /v1/projects/{p}/policies</c> (07.1.1-T3); returns its id.</summary>
    private static async Task<Guid> CreatePolicyAsync(RunCreationTests.World world)
    {
        var answer = await world.Client.SendAsync(
            HttpMethod.Post,
            $"/v1/projects/{world.Project}/policies",
            world.Admin,
            JsonNode.Parse(PolicyBody));

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);

        return answer.Id();
    }

    /// <summary>The <c>declared_producers</c> member of a detail body, in the order the read reported it.</summary>
    private static IReadOnlyList<string> Producers(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("declared_producers").EnumerateArray().Select(value => value.GetString()!)];

    private static Task<ApiAnswer> GetAsync(
        RunCreationTests.World world,
        Guid runId,
        Action<HttpRequestHeaders>? caller = null) =>
        world.Client.SendAsync(HttpMethod.Get, $"/v1/runs/{runId}", caller ?? world.Admin);
}
