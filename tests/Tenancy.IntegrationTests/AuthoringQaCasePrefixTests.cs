using System.Net;
using System.Net.Http.Headers;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Independent QA of the <c>case_prefix</c> member 13.1.1-T2 exposes through <c>PATCH /v1/projects/{p}</c> (AC4,
/// OI-13-101): the grammar edges the migration's <c>^[A-Z][A-Z0-9]{1,7}$</c> and the app rule share — a lowercase letter,
/// a one-character value, nine characters, a trailing newline (the <c>\z</c> anchor) and a leading digit — are each
/// <c>422 request_invalid</c> on <c>/case_prefix</c> with the stored value unchanged, the two-character minimum is
/// accepted, another project of the same org holding the prefix is <c>409 case_prefix_taken</c>, and a member without
/// <c>project:write</c> is refused before the write.
/// </summary>
[Trait("Category", "Docker")]
public sealed class AuthoringQaCasePrefixTests
{
    private readonly PostgresTenancyFixture _fixture;

    /// <summary>Wires the fixture.</summary>
    /// <param name="fixture">The shared PostgreSQL fixture.</param>
    public AuthoringQaCasePrefixTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>The grammar-checked values: lowercase, too short, too long, a trailing newline, a leading digit and a space.</summary>
    public static TheoryData<string> RefusedPrefixes => new()
    {
        "shop",
        "S",
        "SHOP12345",
        "SHOP\n",
        "1SHOP",
        "SHO P",
    };

    /// <summary>Reads the project's current prefix and ETag as the founder.</summary>
    private static async Task<(string Prefix, string ETag)> ReadAsync(
        HttpClient client,
        Action<HttpRequestHeaders> caller,
        Guid project)
    {
        var read = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}", caller);
        read.Status.ShouldBe(HttpStatusCode.OK, read.ErrorMessage);

        return (read.Text("case_prefix").ShouldNotBeNull(), read.ETag.ShouldNotBeNull());
    }

    /// <summary>
    /// AC4/OI-13-101: every value outside <c>^[A-Z][A-Z0-9]{1,7}\z</c> is a 422 that names <c>/case_prefix</c> and leaves
    /// the column untouched, while the two-character minimum is inside the grammar.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedPrefixes))]
    public async Task Patch_RefusesEveryValueOutsideTheGrammar(string prefix)
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var founder = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, founder, Unique.Slug("qa-prefix"));
        var project = await CreateProjectAsync(client, founder, org, "shop");
        var asFounder = TenancyApiFactory.Resolved(user: founder, orgHeader: org);

        var (before, etag) = await ReadAsync(client, asFounder, project);

        var refused = await client.SendAsync(
            HttpMethod.Patch, $"/v1/projects/{project}", asFounder, new { case_prefix = prefix }, NewKey(), etag);

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, prefix);
        refused.ErrorCode.ShouldBe("request_invalid", prefix);
        refused.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetProperty("path").GetString())
            .ShouldContain(path => path == "/case_prefix", prefix);

        (await _fixture.ScalarAsync<string>(database, $"select case_prefix from public.project where id = '{project}'"))
            .ShouldBe(before, $"{prefix} must not be stored");
    }

    /// <summary>
    /// The accepted edges of the grammar (two characters and the eight-character maximum), the same-org uniqueness
    /// (<c>project_org_case_prefix_uq</c>) as <c>409 case_prefix_taken</c>, and the read-back the DTO guarantees.
    /// </summary>
    [Fact]
    public async Task Patch_AcceptsTheGrammarEdges_AndRefusesADuplicate()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var founder = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, founder, Unique.Slug("qa-prefix-edges"));
        var first = await CreateProjectAsync(client, founder, org, "shop");
        var second = await CreateProjectAsync(client, founder, org, "warehouse");
        var asFounder = TenancyApiFactory.Resolved(user: founder, orgHeader: org);

        var (_, etag) = await ReadAsync(client, asFounder, first);
        var shortest = await client.SendAsync(
            HttpMethod.Patch, $"/v1/projects/{first}", asFounder, new { case_prefix = "SH" }, NewKey(), etag);
        shortest.Status.ShouldBe(HttpStatusCode.OK, shortest.ErrorMessage);
        shortest.Text("case_prefix").ShouldBe("SH");

        var longest = await client.SendAsync(
            HttpMethod.Patch, $"/v1/projects/{first}", asFounder, new { case_prefix = "SHOP9999" }, NewKey(), "\"2\"");
        longest.Status.ShouldBe(HttpStatusCode.OK, longest.ErrorMessage);
        longest.Text("case_prefix").ShouldBe("SHOP9999");
        longest.ETag.ShouldBe("\"3\"");

        var (_, secondTag) = await ReadAsync(client, asFounder, second);
        var duplicate = await client.SendAsync(
            HttpMethod.Patch, $"/v1/projects/{second}", asFounder, new { case_prefix = "SHOP9999" }, NewKey(), secondTag);
        duplicate.Status.ShouldBe(HttpStatusCode.Conflict);
        duplicate.ErrorCode.ShouldBe("case_prefix_taken");

        (await ReadAsync(client, asFounder, first)).Prefix.ShouldBe("SHOP9999", "a refused write changes nothing");
    }

    /// <summary>
    /// A member without <c>project:write</c> (the <c>viewer</c> role carries <c>authoring:read</c> only) is
    /// <c>403 forbidden</c> and the stored prefix is unchanged (AC17's route-permission rule at the tenancy route).
    /// </summary>
    [Fact]
    public async Task Patch_RefusesAViewer()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var founder = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, founder, Unique.Slug("qa-prefix-viewer"));
        var project = await CreateProjectAsync(client, founder, org, "shop");
        var asFounder = TenancyApiFactory.Resolved(user: founder, orgHeader: org);
        var (before, etag) = await ReadAsync(client, asFounder, project);

        var viewer = Guid.CreateVersion7();
        var membership = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public."user" (id, email) values ('{viewer}', 'viewer-{viewer:N}@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{membership}', '{org}', '{viewer}', 'viewer-{viewer:N}@example.test', 'active')
            """);
        var role = await _fixture.ScalarAsync<Guid>(database, "select id from public.role where code = 'viewer' and org_id is null");
        (await client.SendAsync(
            HttpMethod.Post, $"/v1/orgs/{org}/memberships/{membership}/roles", asFounder, new { role_id = role }, NewKey()))
            .Status.ShouldBe(HttpStatusCode.OK);

        var refused = await client.SendAsync(
            HttpMethod.Patch, $"/v1/projects/{project}", TenancyApiFactory.Resolved(user: viewer, orgHeader: org),
            new { case_prefix = "NOPE" }, NewKey(), etag);

        refused.Status.ShouldBe(HttpStatusCode.Forbidden);
        refused.ErrorCode.ShouldBe("forbidden");
        (await _fixture.ScalarAsync<string>(database, $"select case_prefix from public.project where id = '{project}'"))
            .ShouldBe(before);
    }
}
