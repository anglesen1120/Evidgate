// The cookie-session path for release create: the CSRF gate in front of the route, plus the same create through the
// pair. The dev sign-in route is the only way to obtain a real browser session in a test, so the host is the real Api
// composition in Development with that route mapped.
#pragma warning disable CA1861
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Dev;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class ReleaseIntegrationTests
{
    private const string SessionCookie = "__Host-evidgate_session";
    private const string XsrfCookie = "XSRF-TOKEN";
    private const string AntiforgeryCookie = "__Host-evidgate_af";
    private const string XsrfHeader = "X-XSRF-TOKEN";

    [Fact]
    public async Task CreateRelease_CookieSession_WithoutXsrfPair_403CsrfFailed_AndWithThePair_201()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project, revision) = await SeedSessionTenantAsync(database);
        await using var host = ReleaseSessionHost.Start(_fixture.AppConnectionString(database));
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
            (await _fixture.ScalarAsync<int>(
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
            request.Headers.Add(XsrfHeader, xsrf);
        }

        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        return await client.SendAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>Seeds a tenant the dev account can sign in to, with a project and one environment revision.</summary>
    private async Task<(Guid Org, Guid Project, Guid Revision)> SeedSessionTenantAsync(string database)
    {
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var environment = Guid.CreateVersion7();
        var revision = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public."user" (id, email) values ('{KnownDevUser.UserId:D}', 'dev@example.test')
              on conflict (id) do update set email = excluded.email;
            insert into public.organization (id, name, slug) values ('{org:D}', 'Session', 'session-{org:N}');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values (gen_random_uuid(), '{org:D}', '{KnownDevUser.UserId:D}', 'dev@example.test', 'active');
            insert into public.membership_role (id, org_id, membership_id, role_id)
            select gen_random_uuid(), '{org:D}', m.id, r.id
              from public.membership m, public.role r
             where m.org_id = '{org:D}' and m.user_id = '{KnownDevUser.UserId:D}'
               and r.code = 'owner' and r.org_id is null;
            insert into public.project (id, org_id, name, slug) values ('{project:D}', '{org:D}', 'Session', 'session-{project:N}');
            insert into public.environment (id, org_id, project_id, environment_key, name)
            values ('{environment:D}', '{org:D}', '{project:D}', 'staging', 'Staging');
            insert into public.environment_revision
                (id, org_id, project_id, environment_id, revision_no, base_urls, dependency_versions, revision_hash, created_by)
            values ('{revision:D}', '{org:D}', '{project:D}', '{environment:D}', 1, jsonb_build_object(), jsonb_build_object(),
                    'sha256:{new string('a', 64)}', 'probe');
            """).ConfigureAwait(false);

        return (org, project, revision);
    }

    /// <summary>The cookie values a dev sign-in set, assembled per request as a browser would send them.</summary>
    private sealed record SessionCookies(string Session, string Xsrf, string Antiforgery)
    {
        internal string SessionOnly() => $"{SessionCookie}={Session}";

        internal string Both() => $"{SessionCookie}={Session}; {XsrfCookie}={Xsrf}; {AntiforgeryCookie}={Antiforgery}";
    }

    /// <summary>The real Api composition in Development, with the dev sign-in route mapped over the harness database.</summary>
    private sealed class ReleaseSessionHost : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;

        private ReleaseSessionHost(string connectionString)
        {
            _connectionString = connectionString;
            Secret = Guid.NewGuid().ToString("N");
        }

        internal string Secret { get; }

        internal static ReleaseSessionHost Start(string connectionString) => new(connectionString);

        internal HttpClient CreateSessionClient() =>
            CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false,
                HandleCookies = false,
            });

        /// <summary>Signs the seeded dev account in and returns the cookie values the response set.</summary>
        internal async Task<SessionCookies> SignInAsync(HttpClient client)
        {
            using var response = await client.PostAsJsonAsync(
                "/v1/dev/session",
                new { secret = Secret },
                TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

            var cookies = response.Headers.GetValues("Set-Cookie")
                .Select(value => value.Split(';', 2)[0])
                .Select(value => value.Split('=', 2))
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

            return new SessionCookies(cookies[SessionCookie], cookies[XsrfCookie], cookies[AntiforgeryCookie]);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["EVIDGATE:Database:ConnectionString"] = _connectionString,
                    ["EVIDGATE:Runs:Cancel:SoftStopDelay"] = "00:00:30",
                    ["EVIDGATE:Runs:Cancel:KillDelay"] = "00:02:00",
                    ["EVIDGATE:Isolation:SelfCheck"] = "Disabled",
                    ["EVIDGATE:Session:Pepper"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                    ["EVIDGATE:DevLogin:Enabled"] = "true",
                    ["EVIDGATE:DevLogin:Secret"] = Secret,
                }));
        }
    }
}
