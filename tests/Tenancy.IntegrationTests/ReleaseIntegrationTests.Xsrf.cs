// The cookie-session path for release create: the CSRF gate in front of the route, plus the same create through the
// pair. The dev sign-in route is the only way to obtain a real browser session in a test, so the host is the real Api
// composition in Development with that route mapped.
#pragma warning disable CA1861
using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class ReleaseIntegrationTests
{
    [Fact]
    public async Task CreateRelease_CookieSession_WithoutXsrfPair_403CsrfFailed_AndWithThePair_201()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project, revision) = await SeedSessionTenantAsync(database);
        await using var host = CookieSessionHost.Start(_fixture.AppConnectionString(database));
        using var client = host.CreateSessionClient();
        var pair = await host.SignInAsync(client);
        var body = Body("release-cookie-session", [new("web", Digest('a'), revision)]);

        using var refused = await PostAsCookieAsync(client, pair.SessionOnly(), org, project, NewKey(), body, xsrf: null);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using (var document = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)))
        {
            document.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("csrf_failed");
        }

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.release where org_id = '{org:D}'")).ShouldBe(0);

        using var accepted = await PostAsCookieAsync(client, pair.Both(), org, project, NewKey(), body, pair.Xsrf);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Created, await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using (var document = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)))
        {
            var releaseId = document.RootElement.GetProperty("release_id").GetGuid();
            (await _fixture.ScalarAsync<long>(
                database,
                $"select count(*) from public.release where org_id = '{org:D}' and id = '{releaseId:D}'")).ShouldBe(1);
        }
    }

    /// <summary>One release create as the browser: the session cookie pair, the org header and an idempotency key.</summary>
    private static async Task<HttpResponseMessage> PostAsCookieAsync(
        HttpClient client,
        string cookies,
        Guid org,
        Guid project,
        string idempotencyKey,
        string body,
        string? xsrf)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/projects/{project:D}/releases");
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

    /// <summary>Seeds a tenant the dev account can sign in to, with a project and one environment revision.</summary>
    private async Task<(Guid Org, Guid Project, Guid Revision)> SeedSessionTenantAsync(string database)
    {
        var (org, project) = await CookieSessionHost.SeedTenantAsync(_fixture, database);
        var environment = Guid.CreateVersion7();
        var revision = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.environment (id, org_id, project_id, environment_key, name)
            values ('{environment:D}', '{org:D}', '{project:D}', 'staging', 'Staging');
            insert into public.environment_revision
                (id, org_id, project_id, environment_id, revision_no, base_urls, dependency_versions, revision_hash, created_by)
            values ('{revision:D}', '{org:D}', '{project:D}', '{environment:D}', 1, jsonb_build_object(), jsonb_build_object(),
                    'sha256:{new string('a', 64)}', 'probe');
            """).ConfigureAwait(false);

        return (org, project, revision);
    }
}
