// The cookie-session path of the real Api composition: a browser session is the only way a test can hold the
// antiforgery pair, so the host is Development with the dev sign-in route mapped over the harness database.
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Evidgate.BuildingBlocks.SharedKernel.Dev;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The cookie values a dev sign-in set, assembled per request as a browser would send them.</summary>
internal sealed record CookieSessionCookies(string Session, string Xsrf, string Antiforgery)
{
    internal string SessionOnly() => $"{CookieSessionHost.SessionCookie}={Session}";

    internal string Both() =>
        $"{CookieSessionHost.SessionCookie}={Session}; {CookieSessionHost.XsrfCookie}={Xsrf}; "
        + $"{CookieSessionHost.AntiforgeryCookie}={Antiforgery}";
}

/// <summary>
/// The real Api composition in Development, with the dev sign-in route mapped over the harness database. Shared by
/// every suite that proves the CSRF gate in front of a write route: the pair, the org header and the session cookie
/// are the browser's, and the route under test is the suite's.
/// </summary>
internal sealed class CookieSessionHost : WebApplicationFactory<Program>
{
    internal const string SessionCookie = "__Host-evidgate_session";
    internal const string XsrfCookie = "XSRF-TOKEN";
    internal const string AntiforgeryCookie = "__Host-evidgate_af";
    internal const string XsrfHeader = "X-XSRF-TOKEN";

    private readonly string _connectionString;

    private CookieSessionHost(string connectionString)
    {
        _connectionString = connectionString;
        Secret = Guid.NewGuid().ToString("N");
    }

    internal string Secret { get; }

    internal static CookieSessionHost Start(string connectionString) => new(connectionString);

    internal HttpClient CreateSessionClient() =>
        CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

    /// <summary>Signs the seeded dev account in and returns the cookie values the response set.</summary>
    internal async Task<CookieSessionCookies> SignInAsync(HttpClient client)
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

        return new CookieSessionCookies(cookies[SessionCookie], cookies[XsrfCookie], cookies[AntiforgeryCookie]);
    }

    /// <summary>
    /// An org with one project the seeded dev account administers. The session belongs to that one known user, so a
    /// suite that needs a signed-in administrator seeds the rows the sign-in route resolves instead of minting a token.
    /// </summary>
    /// <param name="fixture">The harness fixture (shared database, app connection string).</param>
    /// <param name="database">The database the host runs over.</param>
    internal static async Task<(Guid Org, Guid Project)> SeedTenantAsync(PostgresTenancyFixture fixture, string database)
    {
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();

        await fixture.ExecuteAsync(
            database,
            $"""
            insert into public."user" (id, email) values ('{KnownDevUser.UserId:D}', 'dev@example.test')
              on conflict (id) do update set email = excluded.email;
            insert into public.organization (id, name, slug) values ('{org:D}', 'Session', 'session-{org:N}');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values (gen_random_uuid(), '{org:D}', '{KnownDevUser.UserId:D}', 'dev@example.test', 'active');
            -- The catalogue has no `owner` role code; `tenant_admin` is the member role that holds the catalog
            -- writes a session needs, so the browser session is a real member the evaluator allows.
            insert into public.membership_role (id, org_id, membership_id, role_id)
            select gen_random_uuid(), '{org:D}', m.id, r.id
              from public.membership m, public.role r
             where m.org_id = '{org:D}' and m.user_id = '{KnownDevUser.UserId:D}'
               and r.code = 'tenant_admin' and r.org_id is null;
            insert into public.project (id, org_id, name, slug) values ('{project:D}', '{org:D}', 'Session', 'session-{project:N}');
            """).ConfigureAwait(false);

        await RegistryApi.AssertRoleSeededAsync(fixture, database, org, KnownDevUser.UserId, "tenant_admin")
            .ConfigureAwait(false);

        return (org, project);
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
