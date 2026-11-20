using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;
using World = Tenancy.IntegrationTests.RunCreationTests.World;

namespace Tenancy.IntegrationTests;

public sealed partial class CatalogUnstorableBodyTests
{
    private const string FixtureCreateBody =
        """{"fixture_key":"orders-zq7","seed_version":"v1","body_ref":"fixtures/orders/seed.sql"}""";

    private const string FixtureUnicodeBody =
        """{"fixture_key":"orders-zq7","seed_version":"é\ud83d\ude00","body_ref":"fixtures/orders/seed.sql"}""";

    private const string FixtureRevisionBody =
        """{"seed_version":"é\ud83d\ude00","body_ref":"fixtures/orders/seed.sql"}""";

    [Fact]
    public async Task FixtureCreate_LoneSurrogateName_RefusesWithoutWritesOrRecord()
    {
        await using var world = await StartAsync();
        var target = FixtureCreateTarget(world);

        await AssertRefusedAndRecoverAsync(world, target,
            """{"fixture_key":"orders-zq7","seed_version":"v1","body_ref":"fixtures/orders/seed.sql","\ud800":1,"marker-zq7":"SECRETMARK"}""");
    }

    [Fact]
    public async Task FixtureCreate_LoneSurrogateValue_RefusesWithoutWritesOrRecord()
    {
        await using var world = await StartAsync();
        var target = FixtureCreateTarget(world);

        await AssertRefusedAndRecoverAsync(world, target,
            """{"fixture_key":"orders-zq7","seed_version":"\ud800","body_ref":"fixtures/orders/seed.sql","marker-zq7":"SECRETMARK"}""");
    }

    [Fact]
    public async Task FixtureCreate_NulValue_RefusesWithoutWritesOrRecord()
    {
        await using var world = await StartAsync();
        var target = FixtureCreateTarget(world);

        await AssertRefusedAndRecoverAsync(world, target,
            """{"fixture_key":"orders-zq7","seed_version":"\u0000","body_ref":"fixtures/orders/seed.sql","marker-zq7":"SECRETMARK"}""");
    }

    [Fact]
    public async Task FixtureRevision_LoneSurrogateName_RefusesWithoutWritesOrRecord()
    {
        await using var world = await StartAsync();
        var target = await FixtureRevisionTargetAsync(world);

        await AssertRefusedAndRecoverAsync(world, target,
            """{"seed_version":"v1","body_ref":"fixtures/orders/seed.sql","\ud800":1,"marker-zq7":"SECRETMARK"}""");
    }

    [Fact]
    public async Task FixtureCreate_ValidBmpAndPairedSurrogate_ReplaysWithoutChangingTheHash()
    {
        await using var world = await StartAsync();

        await AssertUnicodeReplayAsync(world, FixtureCreateTarget(world, FixtureUnicodeBody));
    }

    [Fact]
    public async Task FixtureRevision_ValidBmpAndPairedSurrogate_ReplaysWithoutChangingTheHash()
    {
        await using var world = await StartAsync();

        await AssertUnicodeReplayAsync(world, await FixtureRevisionTargetAsync(world));
    }

    [Fact]
    public async Task GitRepoBinding_LoneSurrogateName_RefusesWithoutWritesOrRecord()
    {
        await using var world = await StartAsync();
        var target = new Target(
            HttpMethod.Post,
            $"/v1/projects/{world.Project}/git-repo-bindings",
            """{"installation_id":1,"repository_id":5001}""",
            "git_repo_binding",
            HttpStatusCode.Created,
            Recover: false);

        await AssertRefusedAndRecoverAsync(world, target,
            """{"installation_id":1,"repository_id":5001,"\ud800":1,"marker-zq7":"SECRETMARK"}""");
    }

    private static Target FixtureCreateTarget(World world, string validBody = FixtureCreateBody) =>
        new(
            HttpMethod.Post,
            $"/v1/projects/{world.Project}/fixtures",
            validBody,
            "fixture",
            HttpStatusCode.Created);

    private async Task<Target> FixtureRevisionTargetAsync(World world)
    {
        var fixtureId = await CreateFixtureAsync(world);

        return new Target(
            HttpMethod.Post,
            $"/v1/fixtures/{fixtureId}/revisions",
            FixtureRevisionBody,
            "fixture_revision",
            HttpStatusCode.Created);
    }

    private async Task<Guid> CreateFixtureAsync(World world)
    {
        var target = FixtureCreateTarget(world);
        var answer = await SendAsync(world, target, FixtureCreateBody, NewKey());

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.Body.GetRawText());

        return answer.Id("fixture_id");
    }
}
