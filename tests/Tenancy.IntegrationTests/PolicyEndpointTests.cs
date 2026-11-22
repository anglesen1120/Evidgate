using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 07.1.1 AC1/AC3/AC5 through the real host and a real PostgreSQL: the append-only policy-revision write path
/// (content-addressed dedupe, Corvus schema rejection before any row), the read path with its project-scope rule,
/// the cursor-paginated revision list with <c>If-Match</c> on the cached draft, and the P4 index plan.
/// </summary>
/// <remarks>
/// The routes, status codes, wire names, error codes and headers are the 07.1.1-T3 contract (s9-plan); the tests
/// drive the HTTP surface only, so they do not depend on the module's internal type names.
/// </remarks>
[Trait("Category", "Docker")]
public sealed partial class PolicyEndpointTests
{
    /// <summary>A body the <c>policy-revision.v1</c> schema accepts; two keys so a reorder is observable.</summary>
    private const string BodyA = """{"flaky_budget":3,"required_suites":["smoke","regression"]}""";

    /// <summary>The same body as <see cref="BodyA"/> with reordered keys and added whitespace.</summary>
    private const string BodyAReordered = """
        {
          "required_suites" : [ "smoke" , "regression" ],
          "flaky_budget" : 3
        }
        """;

    /// <summary>A different body (one value changed) — a new revision, never a conflict.</summary>
    private const string BodyB = """{"flaky_budget":4,"required_suites":["smoke","regression"]}""";

    private const string BodyC = """{"flaky_budget":5,"required_suites":["smoke","regression"]}""";

    private const string BodyD = """{"flaky_budget":6,"required_suites":["smoke"]}""";

    private readonly PostgresTenancyFixture _fixture;

    public PolicyEndpointTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project,
        DedicatedDatabase Dedicated) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try
            {
                await Api.DisposeAsync();
            }
            finally
            {
                // The database is this world's to drop even when the host's own disposal throws.
                await Dedicated.DisposeAsync();
            }
        }
    }

    // Private database: this class asserts unfiltered policy_revision counts across the whole database.
    private Task<World> StartAsync() =>
        _fixture.StartDedicatedAsync(async dedicated =>
        {
            var database = dedicated.Name;
            var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
            var client = api.CreateClient();
            var user = Guid.CreateVersion7();
            var org = await CreateOrgAsync(client, user, "acme");
            var project = await CreateProjectAsync(client, user, org, "checkout");

            return new World(api, client, database, user, org, project, dedicated);
        });

    private static Task<ApiAnswer> CreateAsync(
        World world,
        string body,
        Guid? project = null,
        Action<HttpRequestHeaders>? caller = null,
        string? ifMatch = null) =>
        SendRawAsync(world.Client, HttpMethod.Post, $"/v1/projects/{project ?? world.Project}/policies", caller ?? world.Admin, body, ifMatch);

    /// <summary>Sends a raw body verbatim (the JSON-body helper of the registry suite cannot express whitespace).</summary>
    private static async Task<ApiAnswer> SendRawAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        Action<HttpRequestHeaders> caller,
        string raw,
        string? ifMatch = null,
        string contentType = "application/json",
        bool chunked = false)
    {
        using var request = new HttpRequestMessage(method, url);
        caller(request.Headers);

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (chunked)
        {
            // No Content-Length: the endpoint must cap the streamed body itself (ReadBodyAsync).
            request.Headers.TransferEncodingChunked = true;
        }

        request.Content = new StringContent(raw, Encoding.UTF8, contentType);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    private static IReadOnlyList<JsonElement> Items(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("items").EnumerateArray()];

    private static JsonElement Detail(ApiAnswer answer, int index = 0) =>
        answer.Body.GetProperty("error").GetProperty("details")[index];

    private async Task<long> RevisionCountAsync(World world) =>
        await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.policy_revision");

    /// <summary>Adds an active member of <paramref name="roleCode"/> to the org and returns her user id.</summary>
    private async Task<Guid> AddMemberAsync(World world, string roleCode)
    {
        var user = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public."user" (id, email) values ('{user}', '{roleCode}@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values (gen_random_uuid(), '{world.Org}', '{user}', '{roleCode}@example.test', 'active');
            insert into public.membership_role (id, org_id, membership_id, role_id)
            select gen_random_uuid(), '{world.Org}', m.id, r.id
              from public.membership m, public.role r
             where m.org_id = '{world.Org}' and m.user_id = '{user}'
               and r.code = '{roleCode}' and r.org_id is null;
            """);
        await AssertRoleSeededAsync(_fixture, world.Database, world.Org, user, roleCode);

        return user;
    }

    /// <summary>Compares two JSON values ignoring object key order (the <c>jsonb</c> column reorders keys).</summary>
    private static bool JsonSemanticallyEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                var leftProperties = left.EnumerateObject().ToList();
                var rightProperties = right.EnumerateObject().ToList();
                return leftProperties.Count == rightProperties.Count
                    && leftProperties.All(property =>
                        right.TryGetProperty(property.Name, out var other)
                        && JsonSemanticallyEqual(property.Value, other));
            case JsonValueKind.Array:
                var leftItems = left.EnumerateArray().ToList();
                var rightItems = right.EnumerateArray().ToList();
                return leftItems.Count == rightItems.Count
                    && leftItems.Zip(rightItems).All(pair => JsonSemanticallyEqual(pair.First, pair.Second));
            case JsonValueKind.Number:
                return left.GetDouble() == right.GetDouble();
            case JsonValueKind.String:
                return left.GetString() == right.GetString();
            default:
                return true;
        }
    }
}
