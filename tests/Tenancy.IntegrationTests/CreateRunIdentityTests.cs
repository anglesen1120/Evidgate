using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.2.2-T1 AC1–AC4 through the real host and a real PostgreSQL: the identity a run stores and reports. Every ref
/// member comes from the producer's body, so a stored identity is always declared (<c>unverified_ref</c>).
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class CreateRunIdentityTests : IClassFixture<TenancyHostPool>
{
    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;
    public CreateRunIdentityTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

    private static readonly string Sha = new('a', 40);
    private static readonly string OtherSha = new('b', 40);

    /// <summary>A create body whose <c>ref</c> member is <paramref name="refJson"/> verbatim (an object, or a bare value).</summary>
    private static string BodyWithRef(RunCreationTests.World world, string refJson) =>
        $$$"""{"manifest_id":"{{{world.ManifestId}}}","ref":{{{refJson}}}}""";

    /// <summary>One identity column of the world's run, as text (null when the column is null).</summary>
    private async Task<string?> ColumnAsync(RunCreationTests.World world, string column) =>
        await _fixture.ScalarAsync<string>(
            world.Database, $"select {column}::text from public.run where org_id = '{world.Org:D}'");

    /// <summary>One expression of the world's own outbox rows; the shared database also holds other tests' rows.</summary>
    private Task<T> OutboxAsync<T>(RunCreationTests.World world, string expression) =>
        _fixture.ScalarAsync<T>(world.Database, $"select {expression} from public.outbox where org_id = '{world.Org:D}'");

    private static async Task<ApiAnswer> GetRunAsync(RunCreationTests.World world, Guid runId) =>
        await world.Client.SendAsync(HttpMethod.Get, $"/v1/runs/{runId:D}", world.Admin);

    private static string DeclaredBranchBody(RunCreationTests.World world) =>
        BodyWithRef(world, $$$"""{"kind":"branch","name":"main","head_sha":"{{{Sha}}}"}""");

    // ------------------------------------------------------------------ declared identity is stored and reported

    [Fact]
    public async Task CreateRun_DeclaredBranchRef_OutboxCarriesTheIdentityBlock()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var principal = RequestPrincipal.Service(world.Project, ["run:create"], credentialId: Guid.CreateVersion7().ToString("D"));

        var result = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, principal, DeclaredBranchBody(world));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        // The three pre-existing members are unchanged, so a consumer that reads only them keeps working.
        (await OutboxAsync<string>(world, "payload ->> 'run_id'")).ShouldBe(result.Value.RunId.ToString("D"));
        (await OutboxAsync<string>(world, "payload ->> 'project_id'")).ShouldBe(world.Project.ToString("D"));
        (await OutboxAsync<string>(world, "payload ->> 'manifest_id'")).ShouldBe(world.ManifestId.ToString("D"));

        // The additive block: all seven members, the declared values, and the label beside it.
        (await OutboxAsync<long>(world, "(select count(*) from jsonb_object_keys(payload -> 'ref'))")).ShouldBe(7);
        (await OutboxAsync<string>(world, "payload -> 'ref' ->> 'kind'")).ShouldBe("branch");
        (await OutboxAsync<string>(world, "payload -> 'ref' ->> 'name'")).ShouldBe("main");
        (await OutboxAsync<string>(world, "payload -> 'ref' ->> 'head_sha'")).ShouldBe(Sha);
        (await OutboxAsync<string>(world, "payload -> 'ref' ->> 'trigger_event'")).ShouldBeNull();
        (await OutboxAsync<string>(world, "payload -> 'unverified_ref'")).ShouldBe("true");
    }

    [Fact]
    public async Task CreateRun_DeclaredBranchRef_GetRunReportsTheIdentityBlock()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var principal = RequestPrincipal.Service(world.Project, ["run:create"], credentialId: Guid.CreateVersion7().ToString("D"));

        var result = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, principal, DeclaredBranchBody(world));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        var answer = await GetRunAsync(world, result.Value.RunId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("unverified_ref").GetBoolean().ShouldBeTrue();
        var identity = answer.Body.GetProperty("ref");

        // Every member of the block is present, nulls included (05.2.2 §5), so the wire shape is stable for a client.
        identity.EnumerateObject().Select(member => member.Name)
            .ShouldBe(["kind", "name", "pr_number", "head_sha", "base_sha", "trigger_event", "is_fork"]);
        identity.GetProperty("kind").GetString().ShouldBe("branch");
        identity.GetProperty("name").GetString().ShouldBe("main");
        identity.GetProperty("head_sha").GetString().ShouldBe(Sha);
        identity.GetProperty("pr_number").ValueKind.ShouldBe(JsonValueKind.Null);
        identity.GetProperty("base_sha").ValueKind.ShouldBe(JsonValueKind.Null);
        identity.GetProperty("trigger_event").ValueKind.ShouldBe(JsonValueKind.Null);
        identity.GetProperty("is_fork").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task CreateRun_GetRun_RunWithoutIdentity_AllMembersNull()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        var answer = await GetRunAsync(world, created.Id("run_id"));

        var identity = answer.Body.GetProperty("ref");
        identity.GetProperty("kind").ValueKind.ShouldBe(JsonValueKind.Null);
        identity.GetProperty("name").ValueKind.ShouldBe(JsonValueKind.Null);
        identity.GetProperty("is_fork").GetBoolean().ShouldBeFalse();
        answer.Body.GetProperty("unverified_ref").GetBoolean().ShouldBeFalse("a run with no identity has nothing declared");
    }

    // ------------------------------------------------------------------ AC2: body-only identity

    [Fact]
    public async Task CreateRun_DeclaredRef_StoresEveryMemberAsUnverified()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var principal = RequestPrincipal.Service(world.Project, ["run:create"], credentialId: "token-inbound");
        var body = BodyWithRef(
            world,
            $$$"""{"kind":"branch","name":"feature/05.2.2-T1","head_sha":"{{{Sha}}}","base_sha":"{{{OtherSha}}}","trigger_event":"dispatch","is_fork":false}""");

        var result = await RunCreationSeams.SendDirectAsync(world.Api, world.Org, world.Project, principal, body);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.UnverifiedRef.ShouldBeTrue("every stored value came from the body");
        (await ColumnAsync(world, "ref_kind")).ShouldBe("branch");
        (await ColumnAsync(world, "ref_name")).ShouldBe("feature/05.2.2-T1");
        (await ColumnAsync(world, "head_sha")).ShouldBe(Sha);
        (await ColumnAsync(world, "base_sha")).ShouldBe(OtherSha);
        (await ColumnAsync(world, "trigger_event")).ShouldBe("dispatch");
        (await ColumnAsync(world, "is_fork")).ShouldBe("false");
        (await ColumnAsync(world, "unverified_ref")).ShouldBe("true");
    }

    [Fact]
    public async Task CreateRun_NoRef_StoresNoIdentityAndIsNotUnverified()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var principal = RequestPrincipal.Service(world.Project, ["run:create"], credentialId: "token-empty");

        var result = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, principal, RunCreationTests.RunBody(world.ManifestId));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.UnverifiedRef.ShouldBeFalse();
        (await ColumnAsync(world, "ref_kind")).ShouldBeNull();
        (await ColumnAsync(world, "ref_name")).ShouldBeNull();
        (await ColumnAsync(world, "head_sha")).ShouldBeNull();
        (await ColumnAsync(world, "unverified_ref")).ShouldBe("false");
    }

    [Fact]
    public async Task CreateRun_ForkBodyValue_IsStoredAndUnverified()
    {
        // Only the body can set is_fork, and it is declared like every other member.
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var principal = RequestPrincipal.Service(world.Project, ["run:create"], credentialId: "token-fork");

        var result = await RunCreationSeams.SendDirectAsync(
            world.Api,
            world.Org,
            world.Project,
            principal,
            BodyWithRef(world, """{"kind":"pull_request","name":"7/merge","pr_number":7,"is_fork":true}"""));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        (await ColumnAsync(world, "is_fork")).ShouldBe("true");
        (await ColumnAsync(world, "pr_number")).ShouldBe("7");
        result.Value.UnverifiedRef.ShouldBeTrue();
    }
}
