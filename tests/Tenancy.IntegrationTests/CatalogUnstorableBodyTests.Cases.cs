using System.Net;
using Shouldly;
using Xunit;
using World = Tenancy.IntegrationTests.RunCreationTests.World;

namespace Tenancy.IntegrationTests;

public sealed partial class CatalogUnstorableBodyTests
{
    [Fact]
    public async Task RequirementUpdate_LoneSurrogateName_RefusesAtTheEmptyPath()
    {
        await using var world = await StartAsync();
        var target = await RequirementTargetAsync(world);

        await AssertRefusedAndRecoverAsync(world, target,
            """{"requirement_ref":null,"\ud800":1,"marker-zq7":"SECRETMARK"}""");
    }

    [Fact]
    public async Task RequirementUpdate_LoneSurrogateValue_RefusesAtTheRequirementPath()
    {
        await using var world = await StartAsync();
        var target = await RequirementTargetAsync(world);

        await AssertRefusedAndRecoverAsync(world, target,
            """{"requirement_ref":"\ud800"}""",
            path: "requirement_ref");
    }

    [Fact]
    public async Task RequirementUpdate_NulValue_RefusesAtTheRequirementPath()
    {
        await using var world = await StartAsync();
        var target = await RequirementTargetAsync(world);

        await AssertRefusedAndRecoverAsync(world, target,
            """{"requirement_ref":"\u0000"}""",
            path: "requirement_ref");
    }

    [Fact]
    public async Task RequirementUpdate_UnstorableValueInAnotherMember_RefusesTheMemberByName()
    {
        await using var world = await StartAsync();
        var target = await RequirementTargetAsync(world);

        await AssertRefusedAndRecoverAsync(world, target,
            """{"requirement_ref":null,"x":"\ud800"}""",
            path: "x",
            reason: "unknown_field");
    }

    [Fact]
    public async Task RequirementUpdate_NestedUnstorableName_RefusesAtTheEmptyPath()
    {
        await using var world = await StartAsync();
        var target = await RequirementTargetAsync(world);

        await AssertRefusedAndRecoverAsync(world, target,
            """{"requirement_ref":null,"x":{"\ud800":1},"marker-zq7":"SECRETMARK"}""");
    }

    [Fact]
    public async Task CaseUpdate_LoneSurrogateName_RefusesWithoutWritesOrRecord()
    {
        await using var world = await StartAsync();
        var target = await CaseTargetAsync(world);

        await AssertRefusedAndRecoverAsync(world, target,
            """{"name":"renamed-zq7","\ud800":1,"marker-zq7":"SECRETMARK"}""");
    }

    private async Task<Target> RequirementTargetAsync(World world)
    {
        var caseId = await CaseIdAsync(world);

        return new Target(
            HttpMethod.Patch,
            $"/v1/projects/{world.Project}/cases/{caseId}",
            """{"requirement_ref":"REQ-1042"}""",
            "test_case",
            HttpStatusCode.OK,
            CreatesRow: false);
    }

    private async Task<Target> CaseTargetAsync(World world)
    {
        var caseId = await CaseIdAsync(world);
        var etag = await CaseETagAsync(world, caseId);

        return new Target(
            HttpMethod.Patch,
            $"/v1/cases/{caseId}",
            """{"name":"renamed-zq7"}""",
            "test_case",
            HttpStatusCode.OK,
            CreatesRow: false,
            IfMatch: etag);
    }

    private Task<Guid> CaseIdAsync(World world) =>
        fixture.ScalarAsync<Guid>(world.Database,
            $"select case_id from public.test_revision where org_id = '{world.Org}' and id = '{world.Cases[0]}'");

    private static async Task<string> CaseETagAsync(World world, Guid caseId)
    {
        var answer = await world.Client.SendAsync(HttpMethod.Get, $"/v1/cases/{caseId}", world.Admin);
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.ETag.ShouldNotBeNullOrWhiteSpace();

        return answer.ETag ?? throw new InvalidOperationException("the case read carried no ETag");
    }
}
