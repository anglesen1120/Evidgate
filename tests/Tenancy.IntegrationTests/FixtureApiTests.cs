// The fixture routes of the catalog against the real host and a real PostgreSQL: a fixture is an immutable identity
// and its revisions are append-only, so each case reads the committed rows back instead of trusting the response.
#pragma warning disable CA1861
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// A fixture is a project-scoped identity and its revisions are immutable, content-hashed versions of one body pointer.
/// These cases cover the two write routes, the two read routes and the per-run data a suite revision pins.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class FixtureApiTests
{
    /// <summary>A request that publishes its own tenant; a project-scoped host needs no header at all.</summary>
    private static readonly Action<HttpRequestHeaders> NoHeaders = _ => { };

    private readonly PostgresTenancyFixture _fixture;

    public FixtureApiTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>One project of one org, its founder and the client the fixture routes are called on.</summary>
    internal sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project) : IAsyncDisposable
    {
        /// <summary>The founder's headers: the <c>tenant_admin</c> that holds <c>fixture:write</c> and <c>suite:read</c>.</summary>
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        /// <summary>The headers of one member of the org.</summary>
        public Action<HttpRequestHeaders> CallerFor(Guid user) => TenancyApiFactory.Resolved(user: user, orgHeader: Org);

        public static string Fixtures(Guid project) => $"/v1/projects/{project:D}/fixtures";

        public static string Fixture(Guid fixture) => $"/v1/fixtures/{fixture:D}";

        public static string Revisions(Guid fixture) => $"{Fixture(fixture)}/revisions";

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>A fresh org with one project, as its founder.</summary>
    private async Task<World> StartAsync(string slug = "acme")
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug(slug));
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    /// <summary>A second project of the same org, as the founder.</summary>
    private static async Task<Guid> AddProjectAsync(World world, string slug) =>
        await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug(slug));

    /// <summary>A fixture create body: the key, the seed version and the pointer, with the optional members only when asked.</summary>
    private static Dictionary<string, object?> Body(
        string key,
        string seedVersion = "v1",
        string bodyRef = "fixtures/orders/seed.sql",
        int? timeoutS = null,
        string? cleanupPolicy = null)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["fixture_key"] = key,
            ["seed_version"] = seedVersion,
            ["body_ref"] = bodyRef,
        };

        if (timeoutS is not null)
        {
            body["timeout_s"] = timeoutS;
        }

        if (cleanupPolicy is not null)
        {
            body["cleanup_policy"] = cleanupPolicy;
        }

        return body;
    }

    /// <summary>A revision body: the same shape as create, without the key the fixture already holds.</summary>
    private static Dictionary<string, object?> RevisionBody(
        string seedVersion = "v1",
        string bodyRef = "fixtures/orders/seed.sql",
        int? timeoutS = null,
        string? cleanupPolicy = null)
    {
        var body = Body("ignored", seedVersion, bodyRef, timeoutS, cleanupPolicy);
        body.Remove("fixture_key");

        return body;
    }

    private static Task<ApiAnswer> CreateAsync(
        World world,
        object body,
        Action<HttpRequestHeaders>? caller = null,
        Guid? project = null,
        string? idempotencyKey = null,
        HttpClient? client = null) =>
        (client ?? world.Client).SendAsync(
            HttpMethod.Post,
            World.Fixtures(project ?? world.Project),
            caller ?? world.Admin,
            body,
            idempotencyKey ?? NewKey());

    private static Task<ApiAnswer> AppendAsync(
        World world,
        Guid fixture,
        object body,
        Action<HttpRequestHeaders>? caller = null,
        string? idempotencyKey = null,
        HttpClient? client = null) =>
        (client ?? world.Client).SendAsync(
            HttpMethod.Post,
            World.Revisions(fixture),
            caller ?? world.Admin,
            body,
            idempotencyKey ?? NewKey());

    private static Task<ApiAnswer> GetAsync(
        World world,
        Guid fixture,
        Action<HttpRequestHeaders>? caller = null,
        HttpClient? client = null) =>
        (client ?? world.Client).SendAsync(HttpMethod.Get, World.Fixture(fixture), caller ?? world.Admin);

    private static Task<ApiAnswer> ListAsync(
        World world,
        string query = "",
        Action<HttpRequestHeaders>? caller = null) =>
        world.Client.SendAsync(HttpMethod.Get, World.Fixtures(world.Project) + query, caller ?? world.Admin);

    /// <summary>A created fixture, asserted 201 and its head revision.</summary>
    private static async Task<Guid> CreateFixtureAsync(World world, string key, string bodyRef = "fixtures/orders/seed.sql")
    {
        var answer = await CreateAsync(world, Body(key, bodyRef: bodyRef));
        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        answer.Body.GetProperty("revision_no").GetInt32().ShouldBe(1);
        answer.Body.GetProperty("created").GetBoolean().ShouldBeTrue();

        return answer.Id("fixture_id");
    }

    private Task<long> FixtureCountAsync(World world) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.fixture where org_id = '{world.Org:D}' and project_id = '{world.Project:D}'");

    private Task<long> RevisionCountAsync(World world, Guid fixture) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.fixture_revision where org_id = '{world.Org:D}' and fixture_id = '{fixture:D}'");

    /// <summary>The head revision as the rows hold it, read as the migration role so the assertion is about the store.</summary>
    private async Task<JsonElement> StoredHeadAsync(World world, Guid fixture)
    {
        var json = await _fixture.ScalarAsync<string>(
            world.Database,
            $"""
            select jsonb_build_object(
                       'revision_no', r.revision_no,
                       'revision_hash', r.revision_hash,
                       'resource_prefix', r.resource_prefix,
                       'body_ref', r.body_ref,
                       'seed_version', r.seed_version,
                       'timeout_s', r.timeout_s,
                       'cleanup_policy', r.cleanup_policy)::text
              from public.fixture_revision r
             where r.fixture_id = '{fixture:D}'
             order by r.revision_no desc
             limit 1
            """);

        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }

    [Fact]
    public async Task Create_StoresRevisionOne_WithAServerDerivedResourcePrefix()
    {
        await using var world = await StartAsync();

        var answer = await CreateAsync(world, Body("orders"));

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        answer.Text("fixture_key").ShouldBe("orders");
        answer.Body.GetProperty("revision_no").GetInt32().ShouldBe(1);
        answer.Body.GetProperty("created").GetBoolean().ShouldBeTrue();
        var fixtureId = answer.Id("fixture_id");
        var hash = answer.Text("revision_hash").ShouldNotBeNull();

        (await FixtureCountAsync(world)).ShouldBe(1);
        (await RevisionCountAsync(world, fixtureId)).ShouldBe(1);

        var head = await StoredHeadAsync(world, fixtureId);
        head.GetProperty("revision_hash").GetString().ShouldBe(hash);
        head.GetProperty("body_ref").GetString().ShouldBe("fixtures/orders/seed.sql");
        head.GetProperty("seed_version").GetString().ShouldBe("v1");
        head.GetProperty("timeout_s").GetInt32().ShouldBe(60, "an absent timeout takes the default");
        head.GetProperty("cleanup_policy").GetString().ShouldBe("drop", "an absent policy takes the default");

        // Derived from the fixture id and the content hash, so a caller can never name the prefix its cleanup uses.
        hash.ShouldStartWith("sha256:");
        head.GetProperty("resource_prefix").GetString().ShouldBe(
            "fx_" + fixtureId.ToString("N")[^8..] + hash["sha256:".Length..][..8]);
    }

    [Fact]
    public async Task Get_ReturnsTheHeadAsAPointer_AndThePageCarriesNoPointer()
    {
        await using var world = await StartAsync();
        var fixtureId = await CreateFixtureAsync(world, "orders");

        var detail = await GetAsync(world, fixtureId);

        detail.Status.ShouldBe(HttpStatusCode.OK, detail.ErrorMessage);
        detail.Text("fixture_key").ShouldBe("orders");
        var head = detail.Body.GetProperty("head");
        head.GetProperty("revision_no").GetInt32().ShouldBe(1);
        head.GetProperty("body_ref").GetString().ShouldBe("fixtures/orders/seed.sql");
        head.GetProperty("resource_prefix").GetString().ShouldStartWith("fx_");
        var headHash = head.GetProperty("revision_hash").GetString().ShouldNotBeNull();
        detail.ETag.ShouldBe("\"" + headHash + "\"", "the head hash is the identity an immutable answer carries");

        var list = await ListAsync(world);

        list.Status.ShouldBe(HttpStatusCode.OK, list.ErrorMessage);
        var item = list.Body.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("id").GetGuid().ShouldBe(fixtureId);
        item.GetProperty("head_revision_no").GetInt32().ShouldBe(1);
        item.TryGetProperty("body_ref", out _).ShouldBeFalse("a page carries the identity, never the body pointer");
        list.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Append_IsRevisionTwo_AndTheSameContentAgain_AnswersTheSameRevision()
    {
        await using var world = await StartAsync();
        var fixtureId = await CreateFixtureAsync(world, "orders");
        var firstHash = (await StoredHeadAsync(world, fixtureId)).GetProperty("revision_hash").GetString();

        var second = await AppendAsync(world, fixtureId, RevisionBody(seedVersion: "v2"));

        second.Status.ShouldBe(HttpStatusCode.Created, second.ErrorMessage);
        second.Body.GetProperty("revision_no").GetInt32().ShouldBe(2);
        second.Body.GetProperty("created").GetBoolean().ShouldBeTrue();
        second.Text("revision_hash").ShouldNotBe(firstHash);
        (await RevisionCountAsync(world, fixtureId)).ShouldBe(2);

        var again = await AppendAsync(world, fixtureId, RevisionBody(seedVersion: "v2"));

        again.Status.ShouldBe(HttpStatusCode.OK, again.ErrorMessage);
        again.Body.GetProperty("revision_no").GetInt32().ShouldBe(2);
        again.Body.GetProperty("created").GetBoolean().ShouldBeFalse();
        again.Id("revision_id").ShouldBe(second.Id("revision_id"));
        again.Text("revision_hash").ShouldBe(second.Text("revision_hash"));
        (await RevisionCountAsync(world, fixtureId)).ShouldBe(2, "identical content answers the stored revision");

        var detail = await GetAsync(world, fixtureId);
        detail.Body.GetProperty("head").GetProperty("revision_no").GetInt32().ShouldBe(2);
        detail.Body.GetProperty("head").GetProperty("seed_version").GetString().ShouldBe("v2");
    }

    [Fact]
    public async Task Create_AnExistingFixtureKey_409FixtureKeyTaken_AndTheRowsStayOne()
    {
        await using var world = await StartAsync();
        var fixtureId = await CreateFixtureAsync(world, "orders");

        var duplicate = await CreateAsync(world, Body("orders"), idempotencyKey: NewKey());

        duplicate.Status.ShouldBe(HttpStatusCode.Conflict, duplicate.ErrorMessage);
        duplicate.ErrorCode.ShouldBe("fixture_key_taken");
        (await FixtureCountAsync(world)).ShouldBe(1, "a key names one fixture for good; versions are appended to it");
        (await RevisionCountAsync(world, fixtureId)).ShouldBe(1);
    }

    [Fact]
    public async Task Create_ReplayingTheSameKeyAndBody_AnswersTheStoredResponse()
    {
        await using var world = await StartAsync();
        var key = NewKey();
        var body = Body("orders");

        var first = await CreateAsync(world, body, idempotencyKey: key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);

        var replay = await CreateAsync(world, body, idempotencyKey: key);

        replay.Status.ShouldBe(HttpStatusCode.Created, replay.ErrorMessage);
        replay.Id("fixture_id").ShouldBe(first.Id("fixture_id"));
        replay.Id("revision_id").ShouldBe(first.Id("revision_id"));
        replay.Text("revision_hash").ShouldBe(first.Text("revision_hash"));
        (await FixtureCountAsync(world)).ShouldBe(1);
        (await RevisionCountAsync(world, replay.Id("fixture_id"))).ShouldBe(1);
    }

    [Fact]
    public async Task Create_ReusingTheKeyForAnotherBody_409IdempotencyConflict()
    {
        await using var world = await StartAsync();
        var key = NewKey();

        var first = await CreateAsync(world, Body("orders"), idempotencyKey: key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);

        var conflicting = await CreateAsync(world, Body("orders", seedVersion: "v2"), idempotencyKey: key);

        conflicting.Status.ShouldBe(HttpStatusCode.Conflict, conflicting.ErrorMessage);
        conflicting.ErrorCode.ShouldBe("idempotency_conflict");
        (await FixtureCountAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task Create_WithoutAnIdempotencyKey_400RequestInvalid_AndNoRow()
    {
        await using var world = await StartAsync();

        var answer = await world.Client.SendRawAsync(
            HttpMethod.Post,
            World.Fixtures(world.Project),
            world.Admin,
            """{"fixture_key":"orders","seed_version":"v1","body_ref":"fixtures/orders/seed.sql"}""");

        answer.Status.ShouldBe(HttpStatusCode.BadRequest, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        (await FixtureCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task List_CursorPagesInCreationOrder_AndTheLastPageCarriesNoCursor()
    {
        await using var world = await StartAsync();
        foreach (var key in new[] { "orders", "users", "carts" })
        {
            await CreateFixtureAsync(world, key);
        }

        var first = await ListAsync(world, "?limit=2");
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        first.Body.GetProperty("items").GetArrayLength().ShouldBe(2);
        var cursor = first.Body.GetProperty("next_cursor").GetString().ShouldNotBeNull(
            "a page that is not the last carries a cursor");
        cursor.ShouldNotBeNullOrEmpty();

        var second = await ListAsync(world, $"?limit=2&cursor={Uri.EscapeDataString(cursor)}");
        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
        second.Body.GetProperty("items").GetArrayLength().ShouldBe(1);
        second.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);

        var listed = first.Body.GetProperty("items").EnumerateArray()
            .Concat(second.Body.GetProperty("items").EnumerateArray())
            .Select(item => item.GetProperty("fixture_key").GetString())
            .ToList();

        listed.ShouldBe(["orders", "users", "carts"], "the page is the project's fixtures in creation order, once each");
    }
}
