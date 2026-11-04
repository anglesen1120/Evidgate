using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed partial class AuthoringLinkBoundaryTests(PostgresTenancyFixture fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Link_TargetOutsideProject_RefusesWithoutLeakingOrWriting(bool sameOrg)
    {
        await using var world = await StartAsync();
        var other = await AuthoringLinkTestData.SeedTreeAsync(fixture, world.Database, sameOrg ? world.Tree.Org : null);
        var target = await AuthoringLinkTestData.SeedCatalogCaseAsync(fixture, world.Database, other);
        var answer = await PostAsync(world, target);

        AssertInvalid(answer, "/test_case_id", "case_not_found");
        answer.Body.GetRawText().ShouldNotContain(target.ToString("D"));
        (await AuthoringLinkTestData.LinkCountAsync(fixture, world.Database, world.Tree)).ShouldBe(0);
        (await PostAsync(world, world.Catalog)).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Link_ArchivedTarget_RefusesWithoutWriting()
    {
        await using var world = await StartAsync();
        await AuthoringLinkTestData.ArchiveAsync(fixture, world.Database, world.Catalog);

        AssertInvalid(await PostAsync(world, world.Catalog), "/test_case_id", "case_archived");
        (await AuthoringLinkTestData.LinkCountAsync(fixture, world.Database, world.Tree)).ShouldBe(0);
        var live = await AuthoringLinkTestData.SeedCatalogCaseAsync(fixture, world.Database, world.Tree);
        (await PostAsync(world, live)).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Link_DuplicateAndReplay_PreserveOneRowAndOneAudit()
    {
        await using var world = await StartAsync();
        var key = NewKey();
        var created = await PostAsync(world, world.Catalog, key);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var replay = await PostAsync(world, world.Catalog, key);
        replay.Status.ShouldBe(HttpStatusCode.Created);
        JsonElement.DeepEquals(created.Body, replay.Body).ShouldBeTrue();
        var duplicate = await PostAsync(world, world.Catalog);
        duplicate.Status.ShouldBe(HttpStatusCode.OK);
        duplicate.Body.GetProperty("changed").GetBoolean().ShouldBeFalse();
        (await AuthoringLinkTestData.LinkCountAsync(fixture, world.Database, world.Tree)).ShouldBe(1);
        (await AuditCountAsync(world, "authoring.manual_case.link")).ShouldBe(1);
        (await VersionCountAsync(world)).ShouldBe(0);

        var other = await AuthoringLinkTestData.SeedCatalogCaseAsync(fixture, world.Database, world.Tree);
        var conflict = await PostAsync(world, other, key);
        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("idempotency_conflict");
        (await PostAsync(world, other)).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Link_ConcurrentDuplicates_OnlyOneRowAndAuditAreCommitted()
    {
        await using var world = await StartAsync();
        var answers = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => PostAsync(world, world.Catalog)));
        answers.Count(answer => answer.Status == HttpStatusCode.Created).ShouldBe(1);
        answers.Count(answer => answer.Status == HttpStatusCode.OK).ShouldBe(7);
        answers.Count(answer => answer.Body.GetProperty("changed").GetBoolean()).ShouldBe(1);
        (await AuthoringLinkTestData.LinkCountAsync(fixture, world.Database, world.Tree)).ShouldBe(1);
        (await AuditCountAsync(world, "authoring.manual_case.link")).ShouldBe(1);
        (await VersionCountAsync(world)).ShouldBe(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unlink_AnotherProjectsPair_Returns404AndPreservesLink(bool sameOrg)
    {
        await using var world = await StartAsync();
        await using var foreign = await StartAsync();
        var target = sameOrg
            ? await AuthoringLinkTestData.SeedTreeAsync(fixture, world.Database, world.Tree.Org)
            : foreign.Tree;
        var caller = sameOrg ? world.Caller : foreign.Caller;
        var manual = await AuthoringLinkTestData.SeedManualCaseAsync(fixture, world.Database, target, 2);
        var catalog = await AuthoringLinkTestData.SeedCatalogCaseAsync(fixture, world.Database, target);
        var created = await world.Client.SendAsync(HttpMethod.Post, target.LinkPath(manual), caller,
            new { test_case_id = catalog }, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        var denied = await world.Client.SendAsync(HttpMethod.Delete,
            world.Tree.LinkPath(manual) + $"/{catalog:D}", world.Caller, null, NewKey());
        denied.Status.ShouldBe(HttpStatusCode.NotFound);
        denied.ErrorCode.ShouldBe("not_found");
        (await AuthoringLinkTestData.LinkCountAsync(fixture, world.Database, target)).ShouldBe(1);

        var removed = await world.Client.SendAsync(HttpMethod.Delete,
            target.LinkPath(manual) + $"/{catalog:D}", caller, null, NewKey());
        removed.Status.ShouldBe(HttpStatusCode.NoContent);
        (await AuthoringLinkTestData.LinkCountAsync(fixture, world.Database, target)).ShouldBe(0);
    }

    [Fact]
    public async Task Unlink_ArchivedCatalogTarget_RemovesOnlyTheLinkAndReplays()
    {
        await using var world = await StartAsync();
        (await PostAsync(world, world.Catalog)).Status.ShouldBe(HttpStatusCode.Created);
        await AuthoringLinkTestData.ArchiveAsync(fixture, world.Database, world.Catalog);
        var key = NewKey();
        var path = world.Tree.LinkPath(world.Manual) + $"/{world.Catalog:D}";
        var removed = await world.Client.SendAsync(HttpMethod.Delete, path, world.Caller, null, key);
        removed.Status.ShouldBe(HttpStatusCode.NoContent);
        var replay = await world.Client.SendAsync(HttpMethod.Delete, path, world.Caller, null, key);
        replay.Status.ShouldBe(HttpStatusCode.NoContent);
        (await AuditCountAsync(world, "authoring.manual_case.unlink")).ShouldBe(1);
        (await VersionCountAsync(world)).ShouldBe(0);
        (await fixture.ScalarAsync<int>(world.Database,
            $"select count(*)::int from public.manual_test_case where id = '{world.Manual}'")).ShouldBe(1);
        (await fixture.ScalarAsync<int>(world.Database,
            $"select count(*)::int from public.test_case where id = '{world.Catalog}' and status = 'archived'")).ShouldBe(1);
        (await world.Client.SendAsync(HttpMethod.Delete, path, world.Caller, null, NewKey()))
            .Status.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<World> StartAsync()
    {
        var database = await fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "boundary-" + Guid.CreateVersion7().ToString("N"));
        var tree = await AuthoringLinkTestData.SeedTreeAsync(fixture, database, org);
        var manual = await AuthoringLinkTestData.SeedManualCaseAsync(fixture, database, tree);
        var catalog = await AuthoringLinkTestData.SeedCatalogCaseAsync(fixture, database, tree);
        return new World(database, api, client, tree, manual, catalog,
            TenancyApiFactory.Resolved(user: user, orgHeader: org));
    }

    private static Task<ApiAnswer> PostAsync(World world, Guid target, string? key = null) =>
        world.Client.SendAsync(HttpMethod.Post, world.Tree.LinkPath(world.Manual), world.Caller,
            new { test_case_id = target }, key ?? NewKey());

    private Task<int> AuditCountAsync(World world, string action) => fixture.ScalarAsync<int>(world.Database,
        $"select count(*)::int from public.audit_log where org_id = '{world.Tree.Org}' and action = '{action}'");

    private Task<int> VersionCountAsync(World world) => fixture.ScalarAsync<int>(world.Database,
        $"select count(*)::int from public.authoring_version where org_id = '{world.Tree.Org}'");

    private static void AssertInvalid(ApiAnswer answer, string path, string reason)
    {
        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        var detail = answer.Body.GetProperty("error").GetProperty("details").EnumerateArray().Single();
        detail.GetProperty("path").GetString().ShouldBe(path);
        detail.GetProperty("reason").GetString().ShouldBe(reason);
    }

    private sealed record World(string Database, TenancyApiFactory Api, HttpClient Client,
        AuthoringLinkTree Tree, Guid Manual, Guid Catalog, Action<HttpRequestHeaders> Caller) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }
}
