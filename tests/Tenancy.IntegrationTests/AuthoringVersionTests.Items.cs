using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The route half of AC5 for checklist items: a <c>PATCH</c> through route 9 appends exactly one <c>authoring_version</c>
/// row, and a PATCH whose result equals the stored snapshot appends none — the same <c>AuthoringVersionWriter</c> call
/// the empty-directory path exercises, now asserted over HTTP so the route's <c>If-Match</c> and no-op answers are the
/// ones under test.
/// </summary>
public sealed partial class AuthoringVersionTests
{
    [Fact]
    public async Task ItemPatch_WritesOneVersion()
    {
        await using var world = await StartAsync();
        var (_, _, _, item) = await SeedLadderAsync(world);

        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(world.Database));
        using var client = api.CreateClient();
        var caller = TenancyApiFactory.Resolved(
            serviceOrg: world.Org,
            servicePermissions: ["authoring:write", "authoring:read"]);

        var changed = await client.SendAsync(
            HttpMethod.Patch,
            $"/v1/projects/{world.Project}/authoring/checklist-items/{item}",
            caller,
            new { title = "Pay by card" },
            NewKey(),
            "\"1\"");

        changed.Status.ShouldBe(HttpStatusCode.OK);
        changed.Body.GetProperty("changed").GetBoolean().ShouldBeTrue();
        changed.Body.GetProperty("version").GetInt32().ShouldBe(2);
        changed.ETag.ShouldBe("\"2\"");
        (await VersionCountAsync(world, item)).ShouldBe(2);

        var changeKind = await _fixture.ScalarAsync<string>(
            world.Database,
            $"select change_kind from public.authoring_version where entity_kind = 'checklist_item' and entity_id = '{item:D}' order by version_no desc limit 1");
        changeKind.ShouldBe("update");

        // AC5's no-op: writing the title the item already holds is a 200 with changed:false and no version row.
        var unchanged = await client.SendAsync(
            HttpMethod.Patch,
            $"/v1/projects/{world.Project}/authoring/checklist-items/{item}",
            caller,
            new { title = "Pay by card" },
            NewKey(),
            "\"2\"");

        unchanged.Status.ShouldBe(HttpStatusCode.OK);
        unchanged.Body.GetProperty("changed").GetBoolean().ShouldBeFalse();
        unchanged.Body.GetProperty("version").GetInt32().ShouldBe(2);
        (await VersionCountAsync(world, item)).ShouldBe(2);
    }

    /// <summary>The number of <c>authoring_version</c> rows of one checklist item.</summary>
    private async Task<int> VersionCountAsync(World world, Guid item) => await _fixture.ScalarAsync<int>(
        world.Database,
        $"select count(*)::integer from public.authoring_version where entity_kind = 'checklist_item' and entity_id = '{item:D}'");
}
