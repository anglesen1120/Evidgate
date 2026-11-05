// The refusals of the fixture routes: the closed body, the raw cap, the pointer rules and the three gates in front of
// every write — permission, tenant and project scope. Each refusal asserts the rows it did not write.
#pragma warning disable CA1861
using System.Net;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class FixtureApiTests
{
    [Fact]
    public async Task Create_WithAClientSuppliedResourcePrefix_422UnknownField()
    {
        await using var world = await StartAsync();

        var answer = await world.Client.SendRawAsync(
            HttpMethod.Post,
            World.Fixtures(world.Project),
            world.Admin,
            """{"fixture_key":"orders","seed_version":"v1","body_ref":"fixtures/orders/seed.sql","resource_prefix":"fx_mine"}""");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        var details = answer.Body.GetProperty("error").GetProperty("details").EnumerateArray().ToList();
        details.ShouldContain(detail => detail.GetProperty("path").GetString() == "resource_prefix"
            && detail.GetProperty("reason").GetString() == "unknown_field");
        (await FixtureCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Append_WithAClientSuppliedRevisionNo_422UnknownField()
    {
        await using var world = await StartAsync();
        var fixtureId = await CreateFixtureAsync(world, "orders");

        var answer = await world.Client.SendRawAsync(
            HttpMethod.Post,
            World.Revisions(fixtureId),
            world.Admin,
            """{"seed_version":"v2","body_ref":"fixtures/orders/seed.sql","revision_no":7}""");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetProperty("path").GetString() ?? string.Empty)
            .ShouldContain("revision_no", "the revision number is the server's to allocate");
        (await RevisionCountAsync(world, fixtureId)).ShouldBe(1);
    }

    [Fact]
    public async Task Create_ABodyOverTheRouteCap_413PayloadTooLarge()
    {
        await using var world = await StartAsync();
        var oversize = $"{{\"fixture_key\":\"orders\",\"seed_version\":\"v1\",\"body_ref\":\"fixtures/orders/seed.sql\","
            + $"\"note\":\"{new string('x', 70 * 1024)}\"}}";

        var answer = await world.Client.SendRawAsync(HttpMethod.Post, World.Fixtures(world.Project), world.Admin, oversize);

        answer.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("payload_too_large");
        (await FixtureCountAsync(world)).ShouldBe(0);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("fixtures/../../etc/passwd")]
    [InlineData("../seed.sql")]
    [InlineData("insert into orders values (1)")]
    [InlineData("fixtures/orders/seed.sql\n")]
    [InlineData("fixtures\\orders\\seed.sql")]
    public async Task Create_ABodyPointerThatIsNotARepositoryPath_422(string bodyRef)
    {
        await using var world = await StartAsync();

        var answer = await CreateAsync(world, Body("orders", bodyRef: bodyRef));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        answer.Body.GetProperty("error").GetProperty("details").GetArrayLength().ShouldBeGreaterThan(0);
        (await FixtureCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_AsViewer_403Forbidden_AndTheReadStaysAllowed()
    {
        await using var world = await StartAsync();
        await CreateFixtureAsync(world, "orders");
        var viewer = await AddMemberAsync(_fixture, world.Client, world.Database, world.Org, world.Admin, "viewer");

        var refused = await CreateAsync(world, Body("users"), caller: world.CallerFor(viewer));

        refused.Status.ShouldBe(HttpStatusCode.Forbidden, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("forbidden");
        (await FixtureCountAsync(world)).ShouldBe(1);

        var list = await ListAsync(world, caller: world.CallerFor(viewer));

        list.Status.ShouldBe(HttpStatusCode.OK, "a viewer holds suite:read, which the fixture reads reuse");
        list.Body.GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Read_AsAMemberWithoutARole_403Forbidden()
    {
        await using var world = await StartAsync();
        var fixtureId = await CreateFixtureAsync(world, "orders");
        var outsider = await AddMemberAsync(_fixture, world.Client, world.Database, world.Org, world.Admin, roleCode: null);

        var list = await ListAsync(world, caller: world.CallerFor(outsider));
        var detail = await GetAsync(world, fixtureId, caller: world.CallerFor(outsider));

        list.Status.ShouldBe(HttpStatusCode.Forbidden, list.ErrorMessage);
        list.ErrorCode.ShouldBe("forbidden");
        detail.Status.ShouldBe(HttpStatusCode.Forbidden, detail.ErrorMessage);
        detail.ErrorCode.ShouldBe("forbidden");

        (await GetAsync(world, fixtureId)).Status.ShouldBe(HttpStatusCode.OK, "the founder's read is the positive control");
    }

    [Fact]
    public async Task CrossOrg_ReadsAndWritesAre404_AndTheForeignRowsStayUntouched()
    {
        await using var owner = await StartAsync(slug: "owner");
        await using var rival = await StartAsync(slug: "rival");
        var fixtureId = await CreateFixtureAsync(owner, "orders");

        var read = await GetAsync(owner, fixtureId, caller: rival.Admin);
        var unknown = await GetAsync(owner, Guid.CreateVersion7());

        read.Status.ShouldBe(HttpStatusCode.NotFound, read.ErrorMessage);
        read.ErrorCode.ShouldBe("not_found");
        unknown.Status.ShouldBe(read.Status);
        unknown.ErrorCode.ShouldBe(read.ErrorCode);

        var append = await AppendAsync(owner, fixtureId, RevisionBody(seedVersion: "v2"), caller: rival.Admin);

        append.Status.ShouldBe(HttpStatusCode.NotFound, append.ErrorMessage);
        append.ErrorCode.ShouldBe("not_found");
        (await RevisionCountAsync(owner, fixtureId)).ShouldBe(1, "a foreign tenant's write never lands");

        var create = await CreateAsync(owner, Body("users"), caller: rival.Admin);

        create.Status.ShouldBe(HttpStatusCode.NotFound, create.ErrorMessage);
        create.ErrorCode.ShouldBe("not_found");
        (await FixtureCountAsync(owner)).ShouldBe(1);
    }

    [Fact]
    public async Task FixtureOfASiblingProject_UnderAProjectScopedRequest_Is404()
    {
        await using var world = await StartAsync();
        var sibling = await AddProjectAsync(world, "sibling");
        var inSibling = await CreateAsync(world, Body("orders"), project: sibling);
        inSibling.Status.ShouldBe(HttpStatusCode.Created, inSibling.ErrorMessage);
        var siblingFixture = inSibling.Id("fixture_id");
        var ownFixture = await CreateFixtureAsync(world, "carts");

        await using var scoped = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database),
            world.Org,
            world.Project,
            RequestPrincipal.Member(world.User));
        using var client = scoped.CreateClient();

        var read = await GetAsync(world, siblingFixture, caller: NoHeaders, client: client);
        var append = await AppendAsync(world, siblingFixture, RevisionBody(seedVersion: "v2"), caller: NoHeaders, client: client);

        read.Status.ShouldBe(HttpStatusCode.NotFound, read.ErrorMessage);
        read.ErrorCode.ShouldBe("not_found");
        append.Status.ShouldBe(HttpStatusCode.NotFound, append.ErrorMessage);
        append.ErrorCode.ShouldBe("not_found");
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.fixture_revision where fixture_id = '{siblingFixture:D}'")).ShouldBe(1);

        var own = await GetAsync(world, ownFixture, caller: NoHeaders, client: client);

        own.Status.ShouldBe(HttpStatusCode.OK, "the same host reads its own project's fixture");
    }
}
