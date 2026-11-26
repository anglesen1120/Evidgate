using System.Net;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 02.11.1 run provenance through the real host and a real PostgreSQL: a run created by an ingest-token caller keeps
/// exactly the ref, sha and attempt metadata the producer declared and is stored and reported as declared
/// (<c>unverified_ref = true</c>). No credential can make a run verified, so each run in this class is read back from
/// the stored row, the outbox event and the run read model. A fork identity declared the same way stays advisory-only.
/// </summary>
[Trait("Category", "Docker")]
public sealed class CreateRunProvenanceTests : IClassFixture<TenancyHostPool>
{
    private static readonly string Sha = new('a', 40);

    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;

    public CreateRunProvenanceTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) =>
        (_fixture, _hosts) = (fixture, hosts);

    /// <summary>An ingest-token caller: the credential id is the token id, and nothing in it names a ref.</summary>
    private static RequestPrincipal IngestToken(Guid project) =>
        RequestPrincipal.Service(project, ["run:create"], credentialId: Guid.CreateVersion7().ToString("D"));

    private static string Body(RunCreationTests.World world, string refJson) =>
        $$$"""{"manifest_id":"{{{world.ManifestId}}}","ref":{{{refJson}}}}""";

    private Task<T> RunColumnAsync<T>(RunCreationTests.World world, string expression) =>
        _fixture.ScalarAsync<T>(world.Database, $"select {expression} from public.run where org_id = '{world.Org:D}'");

    private static async Task<ApiAnswer> GetRunAsync(RunCreationTests.World world, Guid runId) =>
        await world.Client.SendAsync(HttpMethod.Get, $"/v1/runs/{runId:D}", world.Admin);

    [Fact]
    public async Task Provenance_IngestTokenRunWithDeclaredRefShaAndAttempt_IsStoredAndReadBackAsDeclared()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var body = Body(
            world,
            $$$"""{"kind":"branch","name":"main","head_sha":"{{{Sha}}}","trigger_event":"push"}""");

        var result = await RunCreationSeams.SendDirectAsync(world.Api, world.Org, world.Project, IngestToken(world.Project), body);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.UnverifiedRef.ShouldBeTrue("a producer's ref is declared, never verified");
        (await RunColumnAsync<string>(world, "ref_name")).ShouldBe("main");
        (await RunColumnAsync<string>(world, "head_sha")).ShouldBe(Sha);
        (await RunColumnAsync<string>(world, "unverified_ref::text")).ShouldBe("true");
        (await _fixture.ScalarAsync<string>(
            world.Database, $"select payload -> 'unverified_ref' from public.outbox where org_id = '{world.Org:D}'")).ShouldBe("true");

        var read = await GetRunAsync(world, result.Value.RunId);
        read.Status.ShouldBe(HttpStatusCode.OK, read.ErrorMessage);
        read.Body.GetProperty("unverified_ref").GetBoolean().ShouldBeTrue();
        read.Body.GetProperty("ref").GetProperty("head_sha").GetString().ShouldBe(Sha);
    }

    [Fact]
    public async Task Provenance_IngestTokenRunWithoutRef_DeclaresNothing()
    {
        // Positive control for the rule above: with no ref member there is no identity to declare.
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        var result = await RunCreationSeams.SendDirectAsync(
            world.Api, world.Org, world.Project, IngestToken(world.Project), RunCreationTests.RunBody(world.ManifestId));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.UnverifiedRef.ShouldBeFalse();
        (await RunColumnAsync<string>(world, "ref_name")).ShouldBeNull();
        (await RunColumnAsync<string>(world, "unverified_ref::text")).ShouldBe("false");
    }

    [Fact]
    public async Task Provenance_BodyCannotClaimAVerifiedRun_422AndNoRun()
    {
        await using var world = await RunCreationTests.StartRefusalWorldAsync(_fixture, hosts: _hosts);

        // The label is the server's: a body that tries to set it is an unknown ref member, not an override.
        var forged = await RunCreationTests.CreateRunAsync(
            world, Body(world, """{"kind":"branch","name":"main","unverified_ref":false}"""));

        forged.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, forged.ErrorMessage);
        forged.ErrorCode.ShouldBe("request_invalid");
        await RunCreationTests.AssertNoRunRowsAsync(_fixture, world);

        // Positive control: the same ref without the forged member is accepted and labelled declared.
        var control = await RunCreationTests.CreateRunAsync(world, Body(world, """{"kind":"branch","name":"main"}"""));
        control.Status.ShouldBe(HttpStatusCode.Created, control.ErrorMessage);
        control.Body.GetProperty("unverified_ref").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Provenance_ForkIdentityFromIngestTokenWithDefaultBranchLabel_IsDeclaredAndAdvisoryOnly()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        var result = await RunCreationSeams.SendDirectAsync(
            world.Api,
            world.Org,
            world.Project,
            IngestToken(world.Project),
            Body(world, """{"kind":"branch","name":"main","is_fork":true}"""));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.UnverifiedRef.ShouldBeTrue();
        (await RunColumnAsync<string>(world, "is_fork::text")).ShouldBe("true");

        // The default-branch name does not make a fork run bindable: the read model labels it advisory-only, and the
        // bind refusals (fork_run_not_bindable) are pinned by the release bind tests on the same stored flag.
        var read = await GetRunAsync(world, result.Value.RunId);
        read.Body.GetProperty("advisory_only").GetBoolean().ShouldBeTrue();
        read.Body.GetProperty("ref").GetProperty("name").GetString().ShouldBe("main");
    }
}
