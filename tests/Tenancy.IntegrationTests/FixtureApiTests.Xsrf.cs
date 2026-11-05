// The fixture write through a browser session: the CSRF gate sits in front of the route, so a cookie-only request is
// refused and the same body with the antiforgery pair is the 201. The dev sign-in route is the only way a test can
// hold a real session, so the host is the real Api composition in Development.
#pragma warning disable CA1861
using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class FixtureApiTests
{
    [Fact]
    public async Task Create_CookieSession_WithoutTheXsrfPair_403CsrfFailed_AndWithIt_201()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await CookieSessionHost.SeedTenantAsync(_fixture, database);
        await using var host = CookieSessionHost.Start(_fixture.AppConnectionString(database));
        using var client = host.CreateSessionClient();
        var pair = await host.SignInAsync(client);
        var body = """{"fixture_key":"orders","seed_version":"v1","body_ref":"fixtures/orders/seed.sql"}""";

        using var refused = await PostFixtureAsCookieAsync(client, pair.SessionOnly(), org, project, NewKey(), body, xsrf: null);
        var refusal = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, refusal);
        using (var document = JsonDocument.Parse(refusal))
        {
            document.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("csrf_failed");
        }

        (await _fixture.ScalarAsync<long>(database, $"select count(*) from public.fixture where org_id = '{org:D}'"))
            .ShouldBe(0, "a refused write writes no row");

        using var accepted = await PostFixtureAsCookieAsync(client, pair.Both(), org, project, NewKey(), body, pair.Xsrf);
        var acceptedBody = await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Created, acceptedBody);
        using (var document = JsonDocument.Parse(acceptedBody))
        {
            var fixtureId = document.RootElement.GetProperty("fixture_id").GetGuid();
            (await _fixture.ScalarAsync<long>(
                database,
                $"select count(*) from public.fixture where org_id = '{org:D}' and id = '{fixtureId:D}'")).ShouldBe(1);
        }
    }

    /// <summary>One fixture create as the browser: the cookies, the org header and an idempotency key.</summary>
    private static async Task<HttpResponseMessage> PostFixtureAsCookieAsync(
        HttpClient client,
        string cookies,
        Guid org,
        Guid project,
        string idempotencyKey,
        string body,
        string? xsrf)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/projects/{project:D}/fixtures");
        request.Headers.Add("Cookie", cookies);
        request.Headers.Add("X-Evidgate-Org", org.ToString("D"));
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (xsrf is not null)
        {
            request.Headers.Add(CookieSessionHost.XsrfHeader, xsrf);
        }

        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        return await client.SendAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(false);
    }
}
