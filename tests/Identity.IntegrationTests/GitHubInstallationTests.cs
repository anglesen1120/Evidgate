using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Infrastructure.GitHub;
using Evidgate.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// The GitHub App installation routes over the real Api composition: the nonce lifecycle, the two-part ownership
/// check, the one-App-one-org uniqueness, the suspended read and the disconnect, with the two GitHub clients replaced
/// by fakes so no test touches the network. The row, the state digest and the tenant path are the production ones.
/// </summary>
[Trait("Category", "Docker")]
public sealed class GitHubInstallationTests(DatabaseFixture fixture) : TenantTest(fixture)
{
    private const long InstallationId = 424242;

    /// <summary>The numeric GitHub account id the fakes report; it is never the seeded membership id.</summary>
    private const long ProbeUserId = 900001;

    /// <summary>POST start, then the state the URL carries. The body is JSON, never a redirect.</summary>
    [Fact]
    public async Task Start_Callback_HappyPath()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var oauth = new FakeGitHubOAuth { UserId = ProbeUserId, Administers = true };
        oauth.Installations.Add(new GitHubUserInstallation(InstallationId, "acme", ProbeUserId, "User"));
        var app = new FakeGitHubAppInstallations
        {
            Installation = new GitHubAppInstallation(
                InstallationId,
                "acme",
                ProbeUserId,
                "User",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["contents"] = "read" }),
        };

        await using var host = Host(org, admin, oauth, app);
        using var client = host.CreateClient();

        using var start = await client.PostAsync("/v1/github/installations/start", content: null, Token);
        start.StatusCode.ShouldBe(HttpStatusCode.OK);
        var url = (await ReadJsonAsync(start)).RootElement.GetProperty("url").GetString();
        url.ShouldNotBeNull();
        url.ShouldContain("/apps/evidgate-app/installations/new?state=");

        var state = StateOf(url);

        using var callback = await client.GetAsync(
            $"/v1/github/installations/callback?installation_id={InstallationId}&code=probe-code&state={state}", Token);
        callback.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var body = await ReadJsonAsync(callback);
        body.RootElement.GetProperty("installation_id").GetInt64().ShouldBe(InstallationId);
        body.RootElement.GetProperty("account_login").GetString().ShouldBe("acme");
        body.RootElement.GetProperty("state").GetString().ShouldBe("active");
        body.RootElement.GetProperty("permissions").GetProperty("contents").GetString().ShouldBe("read");
        var rowId = body.RootElement.GetProperty("id").GetGuid();

        using var read = await client.GetAsync("/v1/github/installations", Token);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var readBody = await ReadJsonAsync(read);
        readBody.RootElement.GetProperty("id").GetGuid().ShouldBe(rowId);

        oauth.ExchangeCalls.ShouldBe(1);
        app.Lookups.ShouldBe(1);

        // Nothing secret reaches the answer, and the nonce is spent.
        var text = readBody.RootElement.GetRawText();
        text.ShouldNotContain(FakeGitHubOAuth.UserToken, Case.Sensitive);
        text.ShouldNotContain("probe-code", Case.Sensitive);
        (await ScalarAsync<string>(
            "select case when used_at is null then 'open' else 'used' end from public.github_install_state where org_id = @org",
            ("org", org))).ShouldBe("used");
    }

    /// <summary>The nonce is single-use: the same state answers 400 <c>state_invalid</c> the second time.</summary>
    [Fact]
    public async Task Callback_ReplayedState_400()
    {
        var (host, client, oauth, state) = await StartedAsync();
        await using var _ = host;

        using var first = await CallbackAsync(client, state);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var replay = await CallbackAsync(client, state);
        replay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeAsync(replay)).ShouldBe("state_invalid");
        oauth.ExchangeCalls.ShouldBe(1);
    }

    /// <summary>An expired nonce is refused before GitHub is called at all.</summary>
    [Fact]
    public async Task Callback_ExpiredState_400()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var oauth = new FakeGitHubOAuth { UserId = ProbeUserId, Administers = true };
        var app = new FakeGitHubAppInstallations
        {
            Installation = new GitHubAppInstallation(InstallationId, "acme", ProbeUserId, "User", new Dictionary<string, string>()),
        };

        const string expired = "expired-nonce-probe";
        await InsertStateAsync(org, admin, expired, DateTimeOffset.UtcNow.AddMinutes(-1));

        await using var host = Host(org, admin, oauth, app);
        using var client = host.CreateClient();

        using var callback = await client.GetAsync(
            $"/v1/github/installations/callback?installation_id={InstallationId}&code=probe-code&state={expired}", Token);
        callback.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeAsync(callback)).ShouldBe("state_invalid");
        oauth.ExchangeCalls.ShouldBe(0);
        app.Lookups.ShouldBe(0);
    }

    /// <summary>A nonce issued to another org is refused in that org, never consumed for this one.</summary>
    [Fact]
    public async Task Callback_ForeignOrgState_400()
    {
        var (hostA, clientA, _, state) = await StartedAsync();
        await using var a = hostA;

        var otherOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var otherAdmin = await IdentityTestData.SeedActiveMemberAsync(Fixture, otherOrg, "tenant_admin", cancellationToken: Token);
        var oauth = new FakeGitHubOAuth { UserId = ProbeUserId, Administers = true };
        var app = new FakeGitHubAppInstallations
        {
            Installation = new GitHubAppInstallation(InstallationId, "acme", ProbeUserId, "User", new Dictionary<string, string>()),
        };

        await using var hostB = Host(otherOrg, otherAdmin, oauth, app);
        using var clientB = hostB.CreateClient();

        using var callback = await CallbackAsync(clientB, state);
        callback.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeAsync(callback)).ShouldBe("state_invalid");

        // Org A's own nonce is still open: the refused attempt did not spend it.
        (await ScalarAsync<string>(
            "select case when used_at is null then 'open' else 'used' end from public.github_install_state where org_id = @org",
            ("org", otherOrg))).ShouldBeNull();
        clientA.Dispose();
    }

    /// <summary>A GitHub installation already bound to another org answers 409 <c>installation_bound</c>.</summary>
    [Fact]
    public async Task Callback_InstallationBoundElsewhere_409()
    {
        var otherOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await SeedInstallationAsync(otherOrg, InstallationId, "active");

        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        const string nonce = "bound-elsewhere-nonce";
        await InsertStateAsync(org, admin, nonce, DateTimeOffset.UtcNow.AddMinutes(5));

        var oauth = new FakeGitHubOAuth { UserId = ProbeUserId, Administers = true };
        oauth.Installations.Add(new GitHubUserInstallation(InstallationId, "acme", ProbeUserId, "User"));
        var app = new FakeGitHubAppInstallations
        {
            Installation = new GitHubAppInstallation(InstallationId, "acme", ProbeUserId, "User", new Dictionary<string, string>()),
        };

        await using var host = Host(org, admin, oauth, app);
        using var client = host.CreateClient();

        using var callback = await CallbackAsync(client, nonce);
        callback.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeAsync(callback)).ShouldBe("installation_bound");
        (await ScalarAsync<long>(
            "select count(*) from public.github_installation where org_id = @org",
            ("org", org))).ShouldBe(0);
    }

    /// <summary>A second live installation for the same org answers 409 <c>installation_bound</c>.</summary>
    [Fact]
    public async Task Callback_SecondActiveInstallation_409()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        await SeedInstallationAsync(org, 111, "active");

        const string nonce = "second-active-nonce";
        await InsertStateAsync(org, admin, nonce, DateTimeOffset.UtcNow.AddMinutes(5));

        var oauth = new FakeGitHubOAuth { UserId = ProbeUserId, Administers = true };
        oauth.Installations.Add(new GitHubUserInstallation(InstallationId, "acme", ProbeUserId, "User"));
        var app = new FakeGitHubAppInstallations
        {
            Installation = new GitHubAppInstallation(InstallationId, "acme", ProbeUserId, "User", new Dictionary<string, string>()),
        };

        await using var host = Host(org, admin, oauth, app);
        using var client = host.CreateClient();

        using var callback = await CallbackAsync(client, nonce);
        callback.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeAsync(callback)).ShouldBe("installation_bound");
        (await ScalarAsync<long>(
            "select count(*) from public.github_installation where org_id = @org and state <> 'deleted'",
            ("org", org))).ShouldBe(1);

        // And the start route refuses a second flow for an org that already has one.
        using var start = await client.PostAsync("/v1/github/installations/start", content: null, Token);
        start.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeAsync(start)).ShouldBe("already_installed");
    }

    /// <summary>
    /// The two halves of the ownership rule: an installation the OAuth user cannot reach is 403, and so is one the
    /// user reaches but does not administer.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Callback_InstallationNotOwned_403(bool reachable)
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        const string nonce = "not-owned-nonce";
        await InsertStateAsync(org, admin, nonce, DateTimeOffset.UtcNow.AddMinutes(5));

        // Administers stays false in both halves: the first lacks the installation in the list, the second has it
        // and fails the administer check, which is the two-part rule's other half.
        var oauth = new FakeGitHubOAuth { UserId = ProbeUserId, Administers = false };
        if (reachable)
        {
            oauth.Installations.Add(new GitHubUserInstallation(InstallationId, "acme", ProbeUserId, "Organization"));
        }

        var app = new FakeGitHubAppInstallations
        {
            Installation = new GitHubAppInstallation(InstallationId, "acme", ProbeUserId, "Organization", new Dictionary<string, string>()),
        };

        await using var host = Host(org, admin, oauth, app);
        using var client = host.CreateClient();

        using var callback = await CallbackAsync(client, nonce);
        callback.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeAsync(callback)).ShouldBe("installation_not_owned");
        (await ScalarAsync<long>(
            "select count(*) from public.github_installation where org_id = @org",
            ("org", org))).ShouldBe(0);
    }

    /// <summary>A suspended binding is a 409, not a healthy 200; an org with none is 404.</summary>
    [Fact]
    public async Task Get_WhenSuspended_409()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var other = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);

        await SeedInstallationAsync(org, InstallationId, "suspended");

        var oauth = new FakeGitHubOAuth { UserId = ProbeUserId };
        await using var host = Host(org, admin, oauth, new FakeGitHubAppInstallations());
        using var client = host.CreateClient();

        using var suspended = await client.GetAsync("/v1/github/installations", Token);
        suspended.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeAsync(suspended)).ShouldBe("installation_unavailable");

        await using var otherHost = Host(other, admin, oauth, new FakeGitHubAppInstallations());
        using var otherClient = otherHost.CreateClient();
        using var missing = await otherClient.GetAsync("/v1/github/installations", Token);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>Disconnect is terminal per row and idempotent in effect: 204, then the read is 404 and a replay 404.</summary>
    [Fact]
    public async Task Disconnect_204_Then_404()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var rowId = await SeedInstallationAsync(org, InstallationId, "active");

        var oauth = new FakeGitHubOAuth { UserId = ProbeUserId };
        await using var host = Host(org, admin, oauth, new FakeGitHubAppInstallations());
        using var client = host.CreateClient();

        using var deleted = await client.DeleteAsync($"/v1/github/installations/{rowId}", Token);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var read = await client.GetAsync("/v1/github/installations", Token);
        read.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var replay = await client.DeleteAsync($"/v1/github/installations/{rowId}", Token);
        replay.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await ScalarAsync<string>(
            "select state from public.github_installation where id = @id",
            ("id", rowId))).ShouldBe("deleted");

        // A disconnected org may start a fresh flow again.
        using var start = await client.PostAsync("/v1/github/installations/start", content: null, Token);
        start.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// AC4's database half: the two tables hold no OAuth code, user token or App key after a full flow, and the stored
    /// state is a 32-byte digest that is not the nonce itself.
    /// </summary>
    [Fact]
    public async Task Db_Scan_NoTokenOrKey()
    {
        var (host, client, _, state) = await StartedAsync();
        await using var _host = host;

        using var callback = await CallbackAsync(client, state);
        callback.StatusCode.ShouldBe(HttpStatusCode.OK);

        var dump = await DumpAsync(
            """
            select coalesce(string_agg(t.row_text, ' '), '') from (
              select github_installation::text as row_text from public.github_installation
              union all
              select github_install_state::text from public.github_install_state
            ) t
            """);

        dump.ShouldNotContain(FakeGitHubOAuth.UserToken, Case.Sensitive);
        dump.ShouldNotContain("probe-code", Case.Sensitive);
        dump.ShouldNotContain("ghs_", Case.Sensitive);
        dump.ShouldNotContain("BEGIN", Case.Sensitive);
        dump.ShouldNotContain(state, Case.Sensitive);

        var hashLength = await ScalarAsync<int>(
            "select coalesce(max(octet_length(state_hash)), 0) from public.github_install_state");
        hashLength.ShouldBe(32);
    }

    private async Task<(IdentityApiFactory Host, HttpClient Client, FakeGitHubOAuth OAuth, string State)> StartedAsync()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var oauth = new FakeGitHubOAuth { UserId = ProbeUserId, Administers = true };
        oauth.Installations.Add(new GitHubUserInstallation(InstallationId, "acme", ProbeUserId, "User"));
        var app = new FakeGitHubAppInstallations
        {
            Installation = new GitHubAppInstallation(
                InstallationId,
                "acme",
                ProbeUserId,
                "User",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["contents"] = "read" }),
        };

        var host = Host(org, admin, oauth, app);
        var client = host.CreateClient();

        using var start = await client.PostAsync("/v1/github/installations/start", content: null, Token);
        start.StatusCode.ShouldBe(HttpStatusCode.OK);
        var state = StateOf((await ReadJsonAsync(start)).RootElement.GetProperty("url").GetString()!);

        return (host, client, oauth, state);
    }

    private static Task<HttpResponseMessage> CallbackAsync(HttpClient client, string state) =>
        client.GetAsync($"/v1/github/installations/callback?installation_id={InstallationId}&code=probe-code&state={state}", Token);

    private IdentityApiFactory Host(Guid org, Guid user, FakeGitHubOAuth oauth, FakeGitHubAppInstallations app) =>
        IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(
                org,
                Principal: RequestPrincipal.Member(user),
                Evaluator: new GrantAllPermissionEvaluator(),
                Services: services =>
                {
                    services.AddSingleton(Options.Create(new GitHubOptions
                    {
                        AppId = "1",
                        ClientId = "Iv1.probe",
                        AppSlug = "evidgate-app",
                    }));
                    services.AddSingleton<IGitHubOAuthClient>(oauth);
                    services.AddSingleton<IGitHubAppInstallations>(app);
                }));

    private static string StateOf(string installUrl) =>
        new Uri(installUrl).Query.TrimStart('?')
            .Split('&')
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0] == "state")
            .Select(pair => Uri.UnescapeDataString(pair[1]))
            .Single();

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).RootElement.GetProperty("error").GetProperty("code").GetString();

    private async Task<Guid> SeedInstallationAsync(Guid org, long installationId, string state)
    {
        var id = Guid.CreateVersion7();
        await Fixture.SeedAsync(
            """
            insert into public.github_installation
                (id, org_id, installation_id, account_login, account_id, permissions, state, suspended_at)
            values (@id, @org, @installation_id, 'acme', 1, '{"metadata":"read"}'::jsonb, @state,
                    case when @state = 'suspended' then now() else null end)
            """,
            [("id", id), ("org", org), ("installation_id", installationId), ("state", state)],
            Token);

        return id;
    }

    private async Task InsertStateAsync(Guid org, Guid user, string nonce, DateTimeOffset expiresAt) =>
        await Fixture.SeedAsync(
            """
            insert into public.github_install_state (state_hash, org_id, user_id, expires_at)
            values (@hash, @org, @user, @expires_at)
            """,
            [
                ("hash", SHA256.HashData(Encoding.ASCII.GetBytes(nonce))),
                ("org", org),
                ("user", user),
                ("expires_at", expiresAt),
            ],
            Token);

    private async Task<T?> ScalarAsync<T>(string sql, params (string Name, object Value)[] values)
    {
        var rows = await QueryAsync(sql, values);

        return rows.Count == 0 || rows[0] is not T value ? default : value;
    }

    private async Task<List<object?>> QueryAsync(string sql, params (string Name, object Value)[] values)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<object?>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            rows.Add(await reader.IsDBNullAsync(0, Token) ? null : reader.GetValue(0));
        }

        return rows;
    }

    private async Task<string> DumpAsync(string sql) =>
        await ScalarAsync<string>(sql) ?? string.Empty;
}

/// <summary>
/// The OAuth half of the callback, faked: the code always exchanges, the user is the seeded admin, and whether the
/// user administers the installation's account is the case's own decision.
/// </summary>
internal sealed class FakeGitHubOAuth : IGitHubOAuthClient
{
    /// <summary>A token-shaped value that must never appear in a response, a log or the database.</summary>
    public const string UserToken = "ghu_installation-probe-secret";

    public required long UserId { get; init; }

    public List<GitHubUserInstallation> Installations { get; } = [];

    public bool Administers { get; init; }

    public int ExchangeCalls { get; private set; }

    public Task<Result<string>> ExchangeCodeAsync(string code, CancellationToken cancellationToken)
    {
        ExchangeCalls++;

        return Task.FromResult(Result.Success(UserToken));
    }

    public Task<Result<GitHubUser>> GetUserAsync(string userToken, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(new GitHubUser(UserId, "probe-user")));

    public Task<Result<IReadOnlyList<GitHubUserInstallation>>> ListInstallationsAsync(
        string userToken,
        CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success<IReadOnlyList<GitHubUserInstallation>>(Installations));

    public Task<Result<bool>> AdministersAsync(
        string userToken,
        GitHubUser user,
        GitHubUserInstallation installation,
        CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(Administers));
}

/// <summary>The App-authenticated lookup, faked: one installation or a 404, and a count of the calls.</summary>
internal sealed class FakeGitHubAppInstallations : IGitHubAppInstallations
{
    public GitHubAppInstallation? Installation { get; init; }

    public int Lookups { get; private set; }

    public Task<Result<GitHubAppInstallation>> GetAsync(long installationId, CancellationToken cancellationToken)
    {
        Lookups++;

        return Task.FromResult(
            Installation is { } found && found.Id == installationId
                ? Result.Success(found)
                : Result.Failure<GitHubAppInstallation>(new Error(GitHubErrorCodes.NotFound, "probe", 404)));
    }
}
