using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The draft routes against the real host and PostgreSQL: an ETag over <c>version</c>, a state machine the database
/// enforces, idempotent creates and the author gate. Every assertion reads the committed row or the audit trail.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class DraftTests : IClassFixture<TenancyHostPool>
{
    private static readonly string Sha = "sha256:" + new string('a', 64);

    private readonly PostgresTenancyFixture _fixture;

    private readonly TenancyHostPool _hosts;

    public DraftTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

    /// <summary>One org, its project and the founder the tests act as.</summary>
    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project)
        : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public Action<HttpRequestHeaders> Caller(Guid user) => TenancyApiFactory.Resolved(user: user, orgHeader: Org);

        public string Collection => $"/v1/projects/{Project}/drafts";

        public static string Draft(Guid id) => $"/v1/drafts/{id}";

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = _hosts.RealResolver(database);
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("draft"));
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    private static JsonObject File(string path = "tests/checkout.spec.ts", string? sha = null, string bodyRef = "drafts/checkout.ts") => new()
    {
        ["path"] = path,
        ["body_sha"] = sha ?? Sha,
        ["body_ref"] = bodyRef,
    };

    private static JsonObject Content(params JsonNode[] files) => new()
    {
        ["files"] = new JsonArray(files),
    };

    private static JsonObject Body(string path = "tests/checkout.spec.ts", string? sha = null, string bodyRef = "drafts/checkout.ts") => new()
    {
        ["kind"] = "template",
        ["base_commit"] = "abcdef1234567",
        ["content"] = Content(File(path, sha, bodyRef)),
    };

    private static JsonObject UpdateBody(string path = "tests/changed.spec.ts") => new()
    {
        ["content"] = Content(File(path)),
    };

    private static Task<ApiAnswer> CreateAsync(
        World world,
        JsonObject? body = null,
        string? key = null,
        Action<HttpRequestHeaders>? caller = null) =>
        world.Client.SendAsync(HttpMethod.Post, world.Collection, caller ?? world.Admin, body ?? Body(), key ?? NewKey());

    private static Task<ApiAnswer> PatchAsync(
        World world,
        Guid id,
        string? etag = "\"1\"",
        JsonObject? body = null,
        string? key = null,
        Action<HttpRequestHeaders>? caller = null) =>
        world.Client.SendAsync(
            HttpMethod.Patch, World.Draft(id), caller ?? world.Admin, body ?? UpdateBody(), key ?? NewKey(), etag);

    private static Task<ApiAnswer> GetAsync(World world, Guid id, Action<HttpRequestHeaders>? caller = null) =>
        world.Client.SendAsync(HttpMethod.Get, World.Draft(id), caller ?? world.Admin);

    private static async Task<Guid> CreateDraftAsync(World world, string path = "tests/checkout.spec.ts")
    {
        var created = await CreateAsync(world, Body(path));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id();
    }

    private static IReadOnlyList<(string? Path, string Reason)> Details(ApiAnswer answer) =>
    [
        .. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(detail => (detail.GetProperty("path").GetString(), detail.GetProperty("reason").GetString()!)),
    ];

    /// <summary>
    /// A 412 over the draft's version: the status the contract fixes, and the current version in <c>details</c> so a
    /// client can re-read and retry. The error code is pinned to the catalog's stale spelling, with the short
    /// <c>conflict</c> alias accepted while the two modules name the same refusal.
    /// </summary>
    private static void ShouldBeStale(ApiAnswer answer, string scenario)
    {
        answer.Status.ShouldBe(HttpStatusCode.PreconditionFailed, scenario + ": " + answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("precondition_failed", scenario);
        Details(answer).ShouldContain(
            detail => detail.Path == "version",
            $"{scenario}: the 412 carries the draft's current version: {string.Join(", ", Details(answer))}");
    }

    private static void ShouldNameField(ApiAnswer answer, string field, string scenario)
    {
        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, $"{scenario}: {answer.ErrorMessage}");
        answer.ErrorCode.ShouldBe("draft_invalid", scenario);
        Details(answer).ShouldContain(
            detail => (detail.Path ?? string.Empty).Contains(field, StringComparison.OrdinalIgnoreCase),
            $"{scenario}: details must name '{field}', got {string.Join(", ", Details(answer))}");
    }

    private Task<Guid> MembershipAsync(World world, Guid? user = null) =>
        _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.membership where org_id = '{world.Org}' and user_id = '{user ?? world.User}' and status = 'active'");

    private Task<T> StoredAsync<T>(World world, Guid id, string expression) =>
        _fixture.ScalarAsync<T>(
            world.Database,
            $"select {expression} from public.draft where id = '{id}' and org_id = '{world.Org}'");

    private Task<long> DraftCountAsync(World world) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.draft where org_id = '{world.Org}' and project_id = '{world.Project}'");

    private Task<long> AuditCountAsync(World world, Guid id, string action) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"""
             select count(*) from public.audit_log
              where org_id = '{world.Org}' and object_type = 'draft' and object_id = '{id}' and action = '{action}'
             """);

    /// <summary>
    /// Files one draft as the owner because the test needs a state the create route cannot reach (the states after
    /// <c>draft</c> are the dry-run and PR tasks' to write). The row is asserted to exist before a test relies on it.
    /// </summary>
    private async Task<Guid> SeedAsync(World world, string status = "draft", Guid? project = null)
    {
        var id = Guid.CreateVersion7();
        var content = $"{{\"files\":[{{\"path\":\"test.ts\",\"body_sha\":\"{Sha}\",\"body_ref\":\"drafts/test.ts\"}}]}}";

        await _fixture.ExecuteAsync(
            world.Database,
            $"""
             insert into public.draft (id, org_id, project_id, author_membership_id, kind, base_commit, status, content)
             select '{id}', '{world.Org}', '{project ?? world.Project}', m.id, 'template', 'abcdef1234567', '{status}', '{content}'::jsonb
               from public.membership m
              where m.org_id = '{world.Org}' and m.user_id = '{world.User}' and m.status = 'active';
             """);

        (await StoredAsync<string>(world, id, "status")).ShouldBe(status);
        return id;
    }

    /// <summary>
    /// A second org with its own project and founder, created through the tenantless org route on the same host: the
    /// tenant a draft read must not cross.
    /// </summary>
    private static async Task<(Guid Org, Guid Project, Guid User)> OtherTenantAsync(World world)
    {
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(world.Client, user, Unique.Slug("other"));
        var project = await CreateProjectAsync(world.Client, user, org, "other");

        return (org, project, user);
    }
}
