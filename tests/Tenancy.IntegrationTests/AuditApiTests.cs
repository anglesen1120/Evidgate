using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>02.7.1-T3.1 (AC4, AC5): the audit list and export through the real pipeline.</summary>
[Collection(ExclusiveTenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class AuditApiTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AuditApiTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetAuditPage_Keyset_NoDuplicatesAcrossPages_AndRowsCarryTheChain()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await SeedAsync(client, user, 5);
        var caller = TenancyApiFactory.Resolved(user: user);

        var seen = new List<long>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"/v1/orgs/{org}/audit?limit=2" + (cursor is null ? string.Empty : $"&cursor={cursor}");
            var page = await client.SendAsync(HttpMethod.Get, url, caller);
            page.Status.ShouldBe(HttpStatusCode.OK);
            seen.AddRange(page.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("seq").GetInt64()));
            cursor = page.Body.GetProperty("next_cursor").GetString();
            pages++;
        }
        while (cursor is not null);

        seen.Count.ShouldBe(6);
        seen.Distinct().Count().ShouldBe(6);
        seen.ShouldBe(seen.OrderByDescending(seq => seq));
        pages.ShouldBe(3);

        var first = (await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/audit?limit=1&action=organization.create", caller))
            .Body.GetProperty("items")[0];
        first.GetProperty("seq").GetInt64().ShouldBe(1);
        first.GetProperty("prev_hash").GetString().ShouldBe(new string('0', 64));
        first.GetProperty("entry_hash").GetString()!.Length.ShouldBe(64);
        first.GetProperty("actor_type").GetString().ShouldBe("user");
    }

    [Fact]
    public async Task GetAuditPage_Filters_Narrow_AndBadInputIs422()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await SeedAsync(client, user, 3);
        var caller = TenancyApiFactory.Resolved(user: user);

        var projects = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/audit?action=project.create", caller);
        projects.Body.GetProperty("items").GetArrayLength().ShouldBe(3);

        var none = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/audit?actor_type=system", caller);
        none.Body.GetProperty("items").GetArrayLength().ShouldBe(0);

        var future = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/audit?from={DateTime.UtcNow.AddDays(1):O}", caller);
        future.Body.GetProperty("items").GetArrayLength().ShouldBe(0);

        (await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/audit?from=not-a-date", caller))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/audit?cursor=garbage", caller))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task GetAuditPage_WithoutAuditRead_Is403_AndAnotherOrgIs403Not404Leak()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var founder = Guid.CreateVersion7();
        var org = await SeedAsync(client, founder, 1);
        var other = await SeedAsync(client, Guid.CreateVersion7(), 0, "other-org");

        // An active member with no role holds no permission.
        var bare = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public."user" (id, email) values ('{bare}', 'bare@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status) values (gen_random_uuid(), '{org}', '{bare}', 'bare@example.test', 'active')
            """);

        var denied = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/audit", TenancyApiFactory.Resolved(user: bare));
        denied.Status.ShouldBe(HttpStatusCode.Forbidden);

        // The founder asks for another org's trail: refused before any row is read.
        (await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{other}/audit", TenancyApiFactory.Resolved(user: founder)))
            .Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ExportAudit_Jsonl_MatchesList_AndWritesItsOwnRow()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await SeedAsync(client, user, 2);

        var listed = (await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/audit?limit=200", TenancyApiFactory.Resolved(user: user)))
            .Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).Reverse().ToList();

        var (status, body, contentType) = await RawAsync(client, $"/v1/orgs/{org}/audit/export", user);
        status.ShouldBe(HttpStatusCode.OK);
        contentType.ShouldBe("application/x-ndjson");

        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("id").GetGuid()).ShouldBe(listed);

        // The export is itself audited, once, and the next export sees it.
        (await _fixture.ScalarAsync<long>(database, $"select count(*) from public.audit_log where org_id = '{org}' and action = 'audit.export'"))
            .ShouldBe(1);
        var second = await RawAsync(client, $"/v1/orgs/{org}/audit/export", user);
        second.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(lines.Length + 1);
    }

    [Fact]
    public async Task ExportAudit_Csv_HasHeaderAndRows_AndRefusesBadFormatAndWideRange()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await SeedAsync(client, user, 1);

        var csv = await RawAsync(client, $"/v1/orgs/{org}/audit/export?format=csv", user);
        csv.Status.ShouldBe(HttpStatusCode.OK);
        csv.ContentType.ShouldBe("text/csv");
        var lines = csv.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].ShouldStartWith("seq,created_at,actor_type");
        lines.Length.ShouldBe(3);

        var caller = TenancyApiFactory.Resolved(user: user);
        (await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/audit/export?format=xml", caller))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var wide = await client.SendAsync(
            HttpMethod.Get, $"/v1/orgs/{org}/audit/export?from={DateTime.UtcNow.AddDays(-93):O}&to={DateTime.UtcNow:O}", caller);
        wide.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        wide.ErrorCode.ShouldBe("export_range_too_large");

        var now = DateTime.UtcNow;
        var exactly = await RawAsync(client, $"/v1/orgs/{org}/audit/export?from={now.AddDays(-92):O}&to={now:O}", user);
        exactly.Status.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@x", "'@x")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("plain", "plain")]
    [InlineData(null, "")]
    public void CsvCell_NeutralisesFormulasAndQuotes(string? input, string expected) =>
        Evidgate.Modules.Identity.Endpoints.AuditEndpoints.CsvCell(input).ShouldBe(expected);

    private static async Task<(HttpStatusCode Status, string Body, string? ContentType)> RawAsync(HttpClient client, string url, Guid user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        TenancyApiFactory.Resolved(user: user)(request.Headers);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<Guid> SeedAsync(HttpClient client, Guid user, int projects, string slug = "acme")
    {
        var created = await client.SendAsync(
            HttpMethod.Post, "/v1/orgs", TenancyApiFactory.Resolved(user: user, email: $"{slug}@example.test"), new { name = slug, slug }, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created);
        var org = created.Id();

        for (var i = 0; i < projects; i++)
        {
            (await client.SendAsync(
                HttpMethod.Post, $"/v1/orgs/{org}/projects", TenancyApiFactory.Resolved(user: user), new { name = $"P{i}", slug = $"proj-{i}" }, NewKey()))
                .Status.ShouldBe(HttpStatusCode.Created);
        }

        return org;
    }
}
