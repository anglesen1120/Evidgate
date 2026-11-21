using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The template registry reads against the real host and PostgreSQL: a keyset page over active rows, a closed kind
/// filter, one read-only statement per page, and the RLS answer that makes an inactive row, another org's row and an
/// unknown id indistinguishable.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class TemplateTests : IClassFixture<TenancyHostPool>
{
    private static readonly string BodyRef = "sha256:" + new string('a', 64);

    private readonly PostgresTenancyFixture _fixture;

    private readonly TenancyHostPool _hosts;

    public TemplateTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

    /// <summary>One org, its founder and the host the tests call.</summary>
    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org)
        : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public Action<HttpRequestHeaders> Caller(Guid user) => TenancyApiFactory.Resolved(user: user, orgHeader: Org);

        public static Action<HttpRequestHeaders> CallerIn(Guid user, Guid org) => TenancyApiFactory.Resolved(user: user, orgHeader: org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync(Action<IServiceCollection>? services = null)
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = services is null
            ? _hosts.RealResolver(database)
            : _hosts.RealResolver(database, new Dictionary<string, string?>(), services);
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("template"));

        // The registry ships no seed row and a built-in is visible to every org, so this suite owns the table:
        // clearing it keeps one test's built-ins out of the next test's page.
        await _fixture.ExecuteAsync(database, "delete from public.template");

        return new World(api, client, database, user, org);
    }

    /// <summary>Inserts one row as the owner role through the owning connection, at an explicit instant.</summary>
    private async Task<Guid> SeedAsync(
        World world,
        string kind = "web",
        bool active = true,
        bool builtIn = false,
        Guid? org = null,
        string? name = null,
        DateTimeOffset? createdAt = null,
        string rules = """{"suite":"checkout"}""")
    {
        var id = Guid.CreateVersion7();

        await using var connection = new NpgsqlConnection(_fixture.OwnerConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into public.template (id, org_id, kind, name, version, body_ref, conformance_rules, active, created_at)
            values (@id, @org, @kind, @name, 1, @body_ref, @rules::jsonb, @active, @created_at)
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("org", (object?)org ?? (builtIn ? DBNull.Value : world.Org));
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("name", name ?? "t-" + id.ToString("N"));
        command.Parameters.AddWithValue("body_ref", BodyRef);
        command.Parameters.AddWithValue("rules", rules);
        command.Parameters.AddWithValue("active", active);
        command.Parameters.AddWithValue("created_at", createdAt ?? DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        return id;
    }

    /// <summary>A second org with its own founder, for the cross-org probes.</summary>
    private static async Task<(Guid User, Guid Org)> AddOrgAsync(World world)
    {
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(world.Client, user, Unique.Slug("template-b"));
        return (user, org);
    }

    private static Task<ApiAnswer> ListAsync(World world, string query = "", Action<HttpRequestHeaders>? caller = null) =>
        world.Client.SendAsync(HttpMethod.Get, "/v1/templates" + query, caller ?? world.Admin);

    private static Task<ApiAnswer> GetAsync(World world, Guid id, Action<HttpRequestHeaders>? caller = null) =>
        world.Client.SendAsync(HttpMethod.Get, $"/v1/templates/{id}", caller ?? world.Admin);

    private static List<Guid> Ids(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())];

    private static void AssertInvalid(ApiAnswer answer, string path, string reason)
    {
        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        var detail = answer.Body.GetProperty("error").GetProperty("details")[0];
        detail.GetProperty("path").GetString().ShouldBe(path);
        detail.GetProperty("reason").GetString().ShouldBe(reason);
    }
}
