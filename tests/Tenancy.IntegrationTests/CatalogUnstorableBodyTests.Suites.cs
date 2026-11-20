using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;
using World = Tenancy.IntegrationTests.RunCreationTests.World;

namespace Tenancy.IntegrationTests;

public sealed partial class CatalogUnstorableBodyTests
{
    // The bad name sits on the request object the endpoint deserialises, not inside the params JsonElement.
    private const string RequestNameBody = """{"\ud800":1,"marker-zq7":"SECRETMARK"}""";

    [Fact]
    public async Task SuiteRevision_LoneSurrogateName_RefusesWithoutWritesOrRecord()
    {
        await using var world = await StartAsync();
        var target = await SuiteTargetAsync(world, dryRun: false);

        await AssertRefusedAndRecoverAsync(world, target, RequestNameBody);
    }

    [Fact]
    public async Task SuiteRevision_LoneSurrogateValue_RefusesWithoutWritesOrRecord()
    {
        await using var world = await StartAsync();
        var target = await SuiteTargetAsync(world, dryRun: false);

        await AssertRefusedAndRecoverAsync(world, target,
            RevisionBody(world, """{"k":"\ud800","marker-zq7":"SECRETMARK"}"""));
    }

    [Fact]
    public async Task SuiteRevision_NulValue_RefusesWithoutWritesOrRecord()
    {
        await using var world = await StartAsync();
        var target = await SuiteTargetAsync(world, dryRun: false);

        await AssertRefusedAndRecoverAsync(world, target,
            RevisionBody(world, """{"k":"\u0000","marker-zq7":"SECRETMARK"}"""));
    }

    [Fact]
    public async Task SuiteRevisionDryRun_LoneSurrogateName_RefusesWithoutWritesOrRecord()
    {
        await using var world = await StartAsync();
        var target = await SuiteTargetAsync(world, dryRun: true);

        await AssertRefusedAndRecoverAsync(world, target, RequestNameBody);
    }

    [Fact]
    public async Task SuiteRevision_UnstorableEnvironmentRequirementName_RefusesWithoutWritesOrRecord()
    {
        await using var world = await StartAsync();
        var target = await SuiteTargetAsync(world, dryRun: false);

        await AssertRefusedAndRecoverAsync(world, target, EnvironmentNameBody(world));
    }

    [Fact]
    public async Task SuiteRevisionDryRun_UnstorableEnvironmentRequirementName_RefusesWithoutWritesOrRecord()
    {
        await using var world = await StartAsync();
        var target = await SuiteTargetAsync(world, dryRun: true);

        await AssertRefusedAndRecoverAsync(world, target, EnvironmentNameBody(world));
    }

    [Fact]
    public async Task SuiteRevision_UnstorableShardStrategyName_RefusesWithoutWritesOrRecord()
    {
        await using var world = await StartAsync();
        var target = await SuiteTargetAsync(world, dryRun: false);

        await AssertRefusedAndRecoverAsync(world, target, ShardStrategyNameBody(world));
    }

    [Fact]
    public async Task SuiteRevision_ValidBmpAndPairedSurrogate_ReplaysWithoutChangingTheHash()
    {
        await using var world = await StartAsync();
        var target = await SuiteTargetAsync(world, dryRun: false);

        await AssertUnicodeReplayAsync(world, target);
    }

    private static string EnvironmentNameBody(World world) =>
        $$$"""{"members":[{"case_revision_id":"{{{world.Cases[0]}}}"}],"environment_requirements":{"\ud800":1,"marker-zq7":"SECRETMARK"}}""";

    private static string ShardStrategyNameBody(World world) =>
        $$$"""{"members":[{"case_revision_id":"{{{world.Cases[0]}}}"}],"shard_strategy":{"\ud800":1,"marker-zq7":"SECRETMARK"}}""";

    private async Task<Target> SuiteTargetAsync(World world, bool dryRun)
    {
        var suite = await fixture.ScalarAsync<Guid>(world.Database,
            $"select suite_id from public.suite_revision where org_id = '{world.Org}' and id = '{world.SuiteRevision}'");
        var route = $"/v1/suites/{suite}/revisions" + (dryRun ? "?dry_run=true" : string.Empty);

        return new Target(
            HttpMethod.Post,
            route,
            RevisionBody(world, """{"k":"ok","_meta":{"é":1,"\ud83d\ude00":2}}"""),
            "suite_revision",
            dryRun ? HttpStatusCode.OK : HttpStatusCode.Created,
            CreatesRow: !dryRun,
            DryRun: dryRun);
    }

    private static string RevisionBody(World world, string parameters) =>
        $$$"""{"members":[{"case_revision_id":"{{{world.Cases[0]}}}","params":{{{parameters}}}}]}""";
}
