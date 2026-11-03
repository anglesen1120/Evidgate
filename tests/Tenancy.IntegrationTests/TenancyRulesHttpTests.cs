using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The converted tenancy validators through the real host and a migrated PostgreSQL: a hostile
/// slug, case prefix and email domain each answer the documented 422 <c>request_invalid</c> with
/// the field path, never a 500, and nothing is written.
/// </summary>
[Trait("Category", "Docker")]
public sealed class TenancyRulesHttpTests
{
    private static readonly string[] HostileSlugs =
    [
        new string('a', 100_000),
        new string('a', 99_999) + "-",
        "ab\n",
        "ab\0",
        "\u0430b",
        "\uFF41b",
        "ab-",
        "AB",
    ];

    private static readonly string[] HostileCasePrefixes =
    [
        new string('A', 100_000),
        new string('A', 99_999) + "\n",
        "AB\n",
        "\u0410B",
        "\uFF21B",
        "TOOLONGPREFIX",
        "ab",
    ];

    private static readonly string[][] HostileDomains =
    [
        [new string('a', 100_000)],
        [new string('a', 254)],
        ["acme..example"],
        ["acme.example\n"],
        ["\u0430cme.example"],
        ["\uFF41cme.example"],
        ["-acme.example"],
        ["acme.example."],
    ];

    private readonly PostgresTenancyFixture _fixture;

    public TenancyRulesHttpTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static readonly string[] AcmeDomains = ["acme.example"];

    [Fact]
    public async Task CreateProject_HostileSlug_Is422WithSlugPath()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var org = await SeedOrgAsync(database, "acme");
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());

        var control = await client.SendAsync(
            HttpMethod.Post, $"/v1/orgs/{org}/projects", caller, new { name = "Web", slug = "web" }, NewKey());
        control.Status.ShouldBe(HttpStatusCode.Created);

        foreach (var slug in HostileSlugs)
        {
            var refused = await client.SendAsync(
                HttpMethod.Post, $"/v1/orgs/{org}/projects", caller, new { name = "Web", slug }, NewKey());

            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
            refused.ErrorCode.ShouldBe("request_invalid");
            Details(refused).ShouldContain(detail => Mentions(detail, "slug"));
        }

        (await _fixture.ScalarAsync<long>(database, $"select count(*) from public.project where org_id = '{org}'"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task UpdateProject_HostileCasePrefix_Is422WithCasePrefixPath()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var org = await SeedOrgAsync(database, "acme");
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());
        var project = await CreateProjectAsync(client, org, "shop");

        var read = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}", caller);
        read.Status.ShouldBe(HttpStatusCode.OK);
        var before = read.Text("case_prefix");

        foreach (var prefix in HostileCasePrefixes)
        {
            var refused = await client.SendAsync(
                HttpMethod.Patch,
                $"/v1/projects/{project}",
                caller,
                new { case_prefix = prefix },
                NewKey(),
                read.ETag);

            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
            refused.ErrorCode.ShouldBe("request_invalid");
            Details(refused).ShouldContain(detail =>
                detail.GetProperty("path").GetString() == "/case_prefix"
                && detail.GetProperty("reason").GetString() == "value_invalid");
        }

        (await _fixture.ScalarAsync<string>(database, $"select case_prefix from public.project where id = '{project}'"))
            .ShouldBe(before);

        var accepted = await client.SendAsync(
            HttpMethod.Patch, $"/v1/projects/{project}", caller, new { case_prefix = "AB" }, NewKey(), read.ETag);
        accepted.Status.ShouldBe(HttpStatusCode.OK);
        accepted.Text("case_prefix").ShouldBe("AB");
    }

    [Fact]
    public async Task UpdateOrganization_HostileEmailDomain_Is422WithDomainPath()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var org = await SeedOrgAsync(database, "acme");
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());

        var read = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}", caller);
        read.Status.ShouldBe(HttpStatusCode.OK);

        foreach (var domains in HostileDomains)
        {
            var refused = await client.SendAsync(
                HttpMethod.Patch,
                $"/v1/orgs/{org}",
                caller,
                new { allowed_email_domains = domains },
                NewKey(),
                read.ETag);

            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
            refused.ErrorCode.ShouldBe("request_invalid");
            Details(refused).ShouldContain(detail => Mentions(detail, "emaildomains"));
        }

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.organization where id = '{org}' and allowed_email_domains is null"))
            .ShouldBe(1);

        var accepted = await client.SendAsync(
            HttpMethod.Patch,
            $"/v1/orgs/{org}",
            caller,
            new { allowed_email_domains = AcmeDomains },
            NewKey(),
            read.ETag);
        accepted.Status.ShouldBe(HttpStatusCode.OK);
        accepted.Body.GetProperty("allowed_email_domains")[0].GetString().ShouldBe("acme.example");
    }

    /// <summary>The 00 §9.2 <c>details</c> rows of a refused answer.</summary>
    private static JsonElement[] Details(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()];

    /// <summary>True when one detail names the request member, whatever its exact spelling.</summary>
    private static bool Mentions(JsonElement detail, string member) =>
        detail.GetProperty("path").GetString() is { Length: > 0 } path
        && path.Replace("_", string.Empty, StringComparison.Ordinal)
            .Contains(member, StringComparison.OrdinalIgnoreCase);

    /// <summary>An org row as the owner, the way the registry suites seed one.</summary>
    private async Task<Guid> SeedOrgAsync(string database, string slug)
    {
        var id = Guid.CreateVersion7();
        var unique = Unique.Slug(slug);
        await _fixture.ExecuteAsync(
            database,
            $"insert into public.organization (id, name, slug) values ('{id}', '{unique} org', '{unique}')");

        return id;
    }

    /// <summary>A project created through the real route by a caller of <paramref name="org"/> (permissions granted).</summary>
    private static async Task<Guid> CreateProjectAsync(HttpClient client, Guid org, string slug)
    {
        var created = await client.SendAsync(
            HttpMethod.Post,
            $"/v1/orgs/{org}/projects",
            TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7()),
            new { name = slug + " project", slug },
            NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created);

        return created.Id();
    }
}
