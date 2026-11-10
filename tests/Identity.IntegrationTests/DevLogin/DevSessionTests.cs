using System.Net;
using System.Net.Http.Json;
using Evidgate.BuildingBlocks.SharedKernel.Dev;
using Evidgate.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.DevLogin;

/// <summary>
/// <c>POST /v1/dev/session</c> over the real Api composition: the three mapping guards, the constant-time secret
/// check, and the session it issues for the seeded dev user through the login's own audit and session machinery.
/// </summary>
/// <remarks>
/// <para>
/// Each guard has a case of its own that fails when that guard is dropped: a Production host, a Development host
/// with the flag off, and a Development host with no secret each answer 404 (the route is not mapped), which no
/// other case would notice if the guard disappeared — the wrong-secret case proves the route IS mapped in the
/// one configuration where it should be.
/// </para>
/// <para>
/// The successful cases run on the harness's migrated database, because a session is a row: the seeded dev
/// account and its active membership are arranged as the owner, and what the route wrote is read back through the
/// same owner connection (the app role holds no privilege on the identity tables).
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed partial class DevSessionTests : CatalogueTestBase
{
    /// <summary>The constructor the harness's assembly fixture flows into.</summary>
    /// <param name="fixture">The shared container; this class gets a private database dropped after each test.</param>
    public DevSessionTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    /// <summary>A Production host maps nothing, even with the flag on and a secret configured.</summary>
    [Fact]
    public async Task ProductionHost_WithFlagAndSecret_Answers_404()
    {
        using var host = DevSessionTestHost.Start(Fixture, DevSessionTestHost.Production, enabled: true);
        using var client = host.CreateDevClient();

        using var response = await client.PostAsJsonAsync(
            DevSessionTestHost.Route,
            new { secret = host.Secret },
            DevSessionTestHost.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await AssertNoSessionAsync();
    }

    /// <summary>A Development host with the flag off maps nothing.</summary>
    [Fact]
    public async Task FlagOff_Answers_404()
    {
        using var host = DevSessionTestHost.Start(Fixture, DevSessionTestHost.Development, enabled: false);
        using var client = host.CreateDevClient();

        using var response = await client.PostAsJsonAsync(
            DevSessionTestHost.Route,
            new { secret = host.Secret },
            DevSessionTestHost.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await AssertNoSessionAsync();
    }

    /// <summary>A Development host with the flag on but no secret configured maps nothing.</summary>
    [Fact]
    public async Task MissingSecret_Answers_404()
    {
        using var host = DevSessionTestHost.StartWithoutSecret(Fixture);
        using var client = host.CreateDevClient();

        using var response = await client.PostAsJsonAsync(
            DevSessionTestHost.Route,
            new { secret = "any-value-at-all" },
            DevSessionTestHost.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await AssertNoSessionAsync();
    }

    /// <summary>A wrong secret is the login's own 401 <c>token_invalid</c>, with no session row and no cookie.</summary>
    [Fact]
    public async Task WrongSecret_Answers_401_With_No_Session_And_No_Cookie()
    {
        await SeedDevAccountAsync(withMembership: true);

        using var host = DevSessionTestHost.Start(Fixture, DevSessionTestHost.Development);
        using var client = host.CreateDevClient();

        using var response = await client.PostAsJsonAsync(
            DevSessionTestHost.Route,
            new { secret = host.Secret + "x" },
            DevSessionTestHost.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await DevSessionTestHost.ErrorCodeAsync(response)).ShouldBe("token_invalid");
        DevSessionTestHost.SessionCookies(response).ShouldBeEmpty();
        await AssertNoSessionAsync();
        (await DevSessionTestHost.LoginAuditRowsAsync(Fixture)).ShouldBe(0, "a refused sign-in is not audited");
    }

    /// <summary>An absent <c>secret</c> member is refused exactly like a wrong one: same code, no session.</summary>
    [Fact]
    public async Task MissingSecretMember_Answers_401()
    {
        await SeedDevAccountAsync(withMembership: true);

        using var host = DevSessionTestHost.Start(Fixture, DevSessionTestHost.Development);
        using var client = host.CreateDevClient();

        using var response = await client.PostAsJsonAsync(
            DevSessionTestHost.Route,
            new { },
            DevSessionTestHost.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await DevSessionTestHost.ErrorCodeAsync(response)).ShouldBe("token_invalid");
        await AssertNoSessionAsync();
    }

    /// <summary>
    /// The right secret signs the seeded dev user in: 204, the <c>__Host-</c> session cookie and the XSRF pair,
    /// exactly one session row for that account, and the <c>auth.login</c> row written before it.
    /// </summary>
    [Fact]
    public async Task RightSecret_Issues_The_Seeded_Dev_Users_Session()
    {
        var orgId = await SeedDevAccountAsync(withMembership: true);

        using var host = DevSessionTestHost.Start(Fixture, DevSessionTestHost.Development);
        using var client = host.CreateDevClient();

        using var response = await client.PostAsJsonAsync(
            DevSessionTestHost.Route,
            new { secret = host.Secret },
            DevSessionTestHost.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Content.Headers.ContentLength.ShouldBe(0, "204 carries no body");

        var cookie = DevSessionTestHost.SessionCookieValue(response);
        cookie.ShouldNotBeNullOrEmpty("the sign-in sets the session cookie");
        DevSessionTestHost.SessionCookies(response).Single()
            .ShouldContain("secure", Case.Insensitive);
        DevSessionTestHost.CookieNames(response).ShouldContain("XSRF-TOKEN");

        (await DevSessionTestHost.CountAsync(Fixture, "select count(*) from public.session")).ShouldBe(1);
        (await DevSessionTestHost.CountAsync(Fixture, "select count(*) from public.\"user\"")).ShouldBe(1);
        (await DevSessionTestHost.SessionUserIdAsync(Fixture)).ShouldBe(
            KnownDevUser.UserId,
            "the session belongs to the seeded dev account and to no one else");
        (await DevSessionTestHost.LoginAuditRowsAsync(Fixture, orgId)).ShouldBe(
            1,
            "the login audit row of the dev user's org is written for the sign-in");
    }

    /// <summary>
    /// A dev account without an active membership is refused with 403 <c>no_membership</c> and writes no session:
    /// the same access rule a federated login runs, because the route reuses that step.
    /// </summary>
    [Fact]
    public async Task NoMembership_Answers_403_And_Writes_No_Session()
    {
        await SeedDevAccountAsync(withMembership: false);

        using var host = DevSessionTestHost.Start(Fixture, DevSessionTestHost.Development);
        using var client = host.CreateDevClient();

        using var response = await client.PostAsJsonAsync(
            DevSessionTestHost.Route,
            new { secret = host.Secret },
            DevSessionTestHost.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await DevSessionTestHost.ErrorCodeAsync(response)).ShouldBe("no_membership");
        await AssertNoSessionAsync();
    }

    /// <summary>Fails closed: an audit write that throws leaves no session behind (503 <c>unavailable</c>).</summary>
    [Fact]
    public async Task AuditFailure_Leaves_No_Session()
    {
        await SeedDevAccountAsync(withMembership: true);

        using var host = DevSessionTestHost.Start(
            Fixture,
            DevSessionTestHost.Development,
            auditFails: true);
        using var client = host.CreateDevClient();

        using var response = await client.PostAsJsonAsync(
            DevSessionTestHost.Route,
            new { secret = host.Secret },
            DevSessionTestHost.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        DevSessionTestHost.SessionCookies(response).ShouldBeEmpty();
        await AssertNoSessionAsync();
    }

    /// <summary>
    /// A plaintext request is refused before the secret is judged and before anything is written: the
    /// antiforgery pair this route must issue is Secure-only, so a sign-in over http would throw after the
    /// session row was committed and leave a live session no browser can use. A wrong and the right secret get
    /// the same 503 <c>unavailable</c> — the status is not an oracle for the deployment's secret — and no row
    /// exists afterwards.
    /// </summary>
    [Fact]
    public async Task PlaintextRequest_Answers_503_For_Any_Secret_And_Writes_No_Session()
    {
        await SeedDevAccountAsync(withMembership: true);

        using var host = DevSessionTestHost.Start(Fixture, DevSessionTestHost.Development);
        using var client = host.CreatePlaintextClient();

        foreach (var presented in new[] { host.Secret + "x", host.Secret })
        {
            using var response = await client.PostAsJsonAsync(
                DevSessionTestHost.Route,
                new { secret = presented },
                DevSessionTestHost.Token);

            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await DevSessionTestHost.ErrorCodeAsync(response)).ShouldBe("unavailable");
            DevSessionTestHost.SessionCookies(response).ShouldBeEmpty();
        }

        await AssertNoSessionAsync();
    }

    /// <summary>Seeds the account the seed-dev migrator writes: the user row, optionally with an active membership.</summary>
    private async Task<Guid> SeedDevAccountAsync(bool withMembership)
    {
        var orgId = await Fixture.SeedOrgAsync();
        await Fixture.SeedAsync(
            """insert into public."user" (id, email) values (@id, 'dev@example.test')""",
            [("id", KnownDevUser.UserId)],
            Token);

        if (withMembership)
        {
            await Fixture.SeedAsync(
                """
                insert into public.membership
                    (id, org_id, user_id, invited_email, status, created_at, updated_at, version)
                values (@id, @org_id, @user_id, 'dev@example.test', 'active', now(), now(), 1)
                """,
                [("id", Guid.CreateVersion7()), ("org_id", orgId), ("user_id", KnownDevUser.UserId)],
                Token);
        }

        return orgId;
    }

    /// <summary>No session row exists: the assertion every refusal case shares.</summary>
    private async Task AssertNoSessionAsync() =>
        (await DevSessionTestHost.CountAsync(Fixture, "select count(*) from public.session")).ShouldBe(0);
}

/// <summary>
/// The host the development sign-in cases drive: the real Api composition over the harness database, with the
/// host environment, the <c>EVIDGATE:DevLogin</c> switch and the secret of the case.
/// </summary>
/// <remarks>
/// The secret reaches the route the way a deployment provides it — through the options post-configurer and
/// <c>ISecretProvider</c>, which reads the host configuration — so no case binds the option property directly.
/// A fresh random value per host, never a literal in the tree.
/// </remarks>
internal sealed class DevSessionTestHost : WebApplicationFactory<Program>
{
    /// <summary>The route under test.</summary>
    internal const string Route = "/v1/dev/session";

    /// <summary>A deployed host: the environment that must never map the route.</summary>
    internal const string Production = "Production";

    /// <summary>The only environment that may map it.</summary>
    internal const string Development = "Development";

    private const int SessionPepperBytes = 32;

    private readonly string _connectionString;
    private readonly string _environment;
    private readonly bool _enabled;
    private readonly bool _auditFails;
    private readonly string? _configuredSecret;

    private DevSessionTestHost(
        string connectionString,
        string environment,
        bool enabled,
        bool auditFails,
        string? configuredSecret)
    {
        _connectionString = connectionString;
        _environment = environment;
        _enabled = enabled;
        _auditFails = auditFails;
        Secret = Guid.NewGuid().ToString("N");
        _configuredSecret = configuredSecret;
    }

    /// <summary>The secret this host configured, so a case can present the right and the wrong one.</summary>
    internal string Secret { get; }

    /// <summary>The running test's cancellation token.</summary>
    internal static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A host in <paramref name="environment"/> with the switch at <paramref name="enabled"/> and a secret.</summary>
    internal static DevSessionTestHost Start(
        DatabaseFixture fixture,
        string environment,
        bool enabled = true,
        bool auditFails = false)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        return new DevSessionTestHost(fixture.AppConnectionString, environment, enabled, auditFails, configuredSecret: null);
    }

    /// <summary>
    /// A Development host with the switch on and no secret configured: the route must not exist. The empty string is
    /// what an unset variable looks like to <c>ISecretProvider</c>, which answers null for it.
    /// </summary>
    internal static DevSessionTestHost StartWithoutSecret(DatabaseFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        return new DevSessionTestHost(
            fixture.AppConnectionString,
            Development,
            enabled: true,
            auditFails: false,
            configuredSecret: string.Empty);
    }

    /// <summary>A client that neither follows redirects nor keeps cookies, over TLS as the deployment is.</summary>
    internal HttpClient CreateDevClient() =>
        CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

    /// <summary>
    /// A client over plain HTTP, which is what a misconfigured deployment or a proxy that terminates TLS without
    /// forwarded headers looks like: the Secure-only cookie pair cannot be minted there.
    /// </summary>
    internal HttpClient CreatePlaintextClient() =>
        CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

    /// <summary>Every <c>Set-Cookie</c> the response carries for the session cookie.</summary>
    internal static IReadOnlyList<string> SessionCookies(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? [.. values.Where(value => value.StartsWith("__Host-evidgate_session=", StringComparison.Ordinal))]
            : [];
    }

    /// <summary>The cookie names the response sets.</summary>
    internal static IReadOnlyList<string> CookieNames(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? [.. values.Select(value => value.Split('=', 2)[0])]
            : [];
    }

    /// <summary>The session cookie's value, or null when the response sets none.</summary>
    internal static string? SessionCookieValue(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var cookies = SessionCookies(response);

        return cookies.Count == 0
            ? null
            : cookies[0].Split(';', 2)[0]["__Host-evidgate_session=".Length..];
    }

    /// <summary>The 00 §9.2 <c>error.code</c> of a response body, or null.</summary>
    internal static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var body = await response.Content.ReadAsStringAsync(Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        using var document = System.Text.Json.JsonDocument.Parse(body);

        return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
            && document.RootElement.TryGetProperty("error", out var error)
            && error.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;
    }

    /// <summary>How many rows <paramref name="sql"/> counts, read as the owner.</summary>
    internal static async Task<int> CountAsync(DatabaseFixture fixture, string sql) =>
        (int)await OwnerScalarAsync<long>(fixture, sql, []).ConfigureAwait(false);

    /// <summary>The <c>auth.login</c> rows of <paramref name="orgId"/>'s audit chain.</summary>
    internal static async Task<int> LoginAuditRowsAsync(DatabaseFixture fixture, Guid orgId) =>
        (int)await OwnerScalarAsync<long>(
            fixture,
            "select count(*) from public.audit_log where org_id = @org_id and action = 'auth.login'",
            [("org_id", orgId)]).ConfigureAwait(false);

    /// <summary>Every <c>auth.login</c> row of the database, for the cases that expect none.</summary>
    internal static async Task<int> LoginAuditRowsAsync(DatabaseFixture fixture) =>
        (int)await OwnerScalarAsync<long>(
            fixture,
            "select count(*) from public.audit_log where action = 'auth.login'",
            []).ConfigureAwait(false);

    /// <summary>The <c>user_id</c> of the one session row.</summary>
    internal static Task<Guid> SessionUserIdAsync(DatabaseFixture fixture) =>
        OwnerScalarAsync<Guid>(fixture, "select user_id from public.session", []);

    /// <summary>One scalar read as the owner connection.</summary>
    internal static async Task<T> OwnerScalarAsync<T>(
        DatabaseFixture fixture,
        string sql,
        IReadOnlyList<(string Name, object Value)> parameters)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var connection = new Npgsql.NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync(Token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(Token).ConfigureAwait(false);

        return (T)result!;
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Database:ConnectionString"] = _connectionString,
                ["EVIDGATE:Runs:Cancel:SoftStopDelay"] = "00:00:30",
                ["EVIDGATE:Runs:Cancel:KillDelay"] = "00:02:00",
                ["EVIDGATE:Isolation:SelfCheck"] = "Disabled",
                ["EVIDGATE:Session:Pepper"] = Convert.ToBase64String(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(SessionPepperBytes)),
                ["EVIDGATE:DevLogin:Enabled"] = _enabled ? "true" : "false",
                ["EVIDGATE:DevLogin:Secret"] = _configuredSecret ?? Secret,
            }));

        if (_auditFails)
        {
            builder.ConfigureTestServices(services =>
                services.AddSingleton<Evidgate.Modules.Identity.Login.Audit.IIdentityAuditWriter>(
                    new ThrowingAuditWriter()));
        }
    }
}

/// <summary>An audit writer whose every write fails, so the fail-closed rule of a sign-in can be observed.</summary>
internal sealed class ThrowingAuditWriter : Evidgate.Modules.Identity.Login.Audit.IIdentityAuditWriter
{
    /// <inheritdoc />
    public ValueTask WriteAsync(
        Evidgate.Modules.Identity.Login.Audit.IdentityAuditEvent auditEvent,
        IReadOnlyCollection<Guid> orgIds,
        CancellationToken cancellationToken) =>
        throw new Evidgate.BuildingBlocks.Persistence.Audit.AuditBusyException(
            new TimeoutException("the audit chain of the org is busy (test)"));
}
