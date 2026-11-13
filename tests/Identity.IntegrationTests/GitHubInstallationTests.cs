using System.Net;
using Evidgate.Modules.Identity.Infrastructure.GitHub;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// The GitHub App installation routes over the real Api composition: the nonce lifecycle, the two-part ownership
/// check, the one-App-one-org uniqueness, the suspended read and the disconnect, with the two GitHub clients replaced
/// by fakes so no test touches the network. The tenant is resolved by the host's own filter — the org of a callback
/// comes only from its one-time state — and the row, the state digest and the tenant path are the production ones.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class GitHubInstallationTests(DatabaseFixture fixture) : TenantTest(fixture)
{
    /// <summary>
    /// A distinct GitHub installation id per test instance. The suite shares one database, and
    /// <c>github_installation.installation_id</c> is unique across every org (a deleted row keeps its id), so a
    /// constant here would make the second happy path a 409 and a seeded row a unique-key violation.
    /// </summary>
    private static long _nextInstallation = 4_242_420_000;

    private long InstallationId { get; } = NextInstallationId();

    /// <summary>The next unused GitHub installation id of this process; the shared database keeps every row.</summary>
    private static long NextInstallationId() => Interlocked.Increment(ref _nextInstallation);

    /// <summary>The numeric GitHub account id the fakes report; it is never the seeded membership id.</summary>
    private const long ProbeUserId = 900001;

    /// <summary>The header a member uses to pick the org on the routes that carry no <c>{o}</c>.</summary>
    private const string OrgHeader = "X-Evidgate-Org";

    /// <summary>The header a mutation carries so a replay is answerable.</summary>
    private const string IdempotencyHeader = "Idempotency-Key";

    /// <summary>POST start, then the state the URL carries. The body is JSON, never a redirect.</summary>
    [Fact]
    public async Task Start_Callback_HappyPath()
    {
        var (host, client, oauth, app, state, org) = await StartedAsync();
        await using var _ = host;

        state.ShouldStartWith(org.ToString("D") + ".");

        using var callback = await CallbackAsync(client, state);
        callback.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var body = await ReadJsonAsync(callback);
        body.RootElement.GetProperty("installation_id").GetInt64().ShouldBe(InstallationId);
        body.RootElement.GetProperty("account_login").GetString().ShouldBe("acme");
        body.RootElement.GetProperty("state").GetString().ShouldBe("active");
        body.RootElement.GetProperty("permissions").GetProperty("contents").GetString().ShouldBe("read");
        var rowId = body.RootElement.GetProperty("id").GetGuid();

        using var read = await SendAsync(client, HttpMethod.Get, "/v1/github/installations", org);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var readBody = await ReadJsonAsync(read);
        readBody.RootElement.GetProperty("id").GetGuid().ShouldBe(rowId);

        oauth.ExchangeCalls.ShouldBe(1);
        app.Lookups.ShouldBe(1);
        oauth.RevokeCalls.ShouldBe(1, "the callback revokes the user token it authorized with");

        // Nothing secret reaches the answer, and the nonce is spent.
        var text = readBody.RootElement.GetRawText();
        text.ShouldNotContain(FakeGitHubOAuth.UserToken, Case.Sensitive);
        text.ShouldNotContain("probe-code", Case.Sensitive);
        (await ScalarAsync<string>(
            "select case when used_at is null then 'open' else 'used' end from public.github_install_state where org_id = @org",
            ("org", org))).ShouldBe("used");
    }

    /// <summary>Another member's org may not borrow the flow: the org in the state is the org resolved.</summary>
    [Fact]
    public async Task Callback_StateOfAnotherOrg_400_ThenOwnStateCompletes()
    {
        var (host, client, oauth, _, state, org) = await StartedAsync();
        await using var _host = host;

        // The same user is an active member of a second org, so tenant resolution accepts it and the refusal is
        // the state binding's own — the case a header-based path could not distinguish.
        var otherOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var userId = await UserIdOfAsync(org);
        await SeedMembershipAsync(otherOrg, userId);

        var borrowed = otherOrg.ToString("D") + "." + NonceOf(state);

        using var refused = await CallbackAsync(client, borrowed);
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeAsync(refused)).ShouldBe("state_invalid");
        oauth.ExchangeCalls.ShouldBe(0, "a state the org never issued makes no GitHub call");

        // The org that issued the state still has its nonce: the refused attempt spent nothing there.
        (await ScalarAsync<string>(
            "select case when used_at is null then 'open' else 'used' end from public.github_install_state where org_id = @org and used_at is null",
            ("org", org))).ShouldBe("open");

        using var callback = await CallbackAsync(client, state);
        callback.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ScalarAsync<string>(
            "select state from public.github_installation where org_id = @org",
            ("org", org))).ShouldBe("active");
        (await ScalarAsync<long>(
            "select count(*) from public.github_installation where org_id = @org",
            ("org", otherOrg))).ShouldBe(0);
    }

    /// <summary>A state naming an org the caller does not belong to is refused before any nonce is touched.</summary>
    [Fact]
    public async Task Callback_StateOfOrgWithoutMembership_403_ThenOwnStateCompletes()
    {
        var (host, client, _, _, state, org) = await StartedAsync();
        await using var _host = host;

        var foreignOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);

        using var refused = await CallbackAsync(client, foreignOrg.ToString("D") + "." + NonceOf(state));
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeAsync(refused)).ShouldNotBeNullOrWhiteSpace("a refusal carries its 00 §9.2 code");

        (await ScalarAsync<string>(
            "select case when used_at is null then 'open' else 'used' end from public.github_install_state where org_id = @org and used_at is null",
            ("org", org))).ShouldBe("open");

        using var callback = await CallbackAsync(client, state);
        callback.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>The nonce is single-use: the same state answers 400 <c>state_invalid</c> the second time.</summary>
    [Fact]
    public async Task Callback_ReplayedState_400()
    {
        var (host, client, oauth, _, state, _) = await StartedAsync();
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
        var app = UsableApp();

        const string expired = "expired-nonce-probe";
        await InsertStateAsync(org, admin, expired, DateTimeOffset.UtcNow.AddMinutes(-1));

        await using var host = Host(org, admin, oauth, app);
        using var client = host.CreateClient();

        using var callback = await CallbackAsync(client, org.ToString("D") + "." + expired);
        callback.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeAsync(callback)).ShouldBe("state_invalid");
        oauth.ExchangeCalls.ShouldBe(0);
        app.Lookups.ShouldBe(0);
    }

    /// <summary>
    /// A refused ownership check still spends the nonce, so the same callback cannot be retried: the second attempt
    /// answers 400 <c>state_invalid</c> and makes no GitHub call at all.
    /// </summary>
    [Fact]
    public async Task Callback_OwnershipRefused_SpendsTheState_ThenReplay_400()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var oauth = new FakeGitHubOAuth { UserId = ProbeUserId, Administers = false };
        oauth.Installations.Add(new GitHubUserInstallation(InstallationId, "acme", ProbeUserId, "Organization"));
        var app = UsableApp();

        const string nonce = "ownership-refused-nonce";
        await InsertStateAsync(org, admin, nonce, DateTimeOffset.UtcNow.AddMinutes(5));

        await using var host = Host(org, admin, oauth, app);
        using var client = host.CreateClient();
        var state = org.ToString("D") + "." + nonce;

        using var refused = await CallbackAsync(client, state);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeAsync(refused)).ShouldBe("installation_not_owned");
        oauth.ExchangeCalls.ShouldBe(1);
        app.Lookups.ShouldBe(1);

        using var replay = await CallbackAsync(client, state);
        replay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeAsync(replay)).ShouldBe("state_invalid");
        oauth.ExchangeCalls.ShouldBe(1, "the refused attempt spent the nonce, so the replay never reaches GitHub");
        app.Lookups.ShouldBe(1);

        (await ScalarAsync<long>(
            "select count(*) from public.github_installation where org_id = @org",
            ("org", org))).ShouldBe(0);
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
        var app = UsableApp();

        await using var host = Host(org, admin, oauth, app);
        using var client = host.CreateClient();

        using var callback = await CallbackAsync(client, org.ToString("D") + "." + nonce);
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
        await SeedInstallationAsync(org, NextInstallationId(), "active");

        const string nonce = "second-active-nonce";
        await InsertStateAsync(org, admin, nonce, DateTimeOffset.UtcNow.AddMinutes(5));

        var oauth = new FakeGitHubOAuth { UserId = ProbeUserId, Administers = true };
        oauth.Installations.Add(new GitHubUserInstallation(InstallationId, "acme", ProbeUserId, "User"));
        var app = UsableApp();

        await using var host = Host(org, admin, oauth, app);
        using var client = host.CreateClient();

        using var callback = await CallbackAsync(client, org.ToString("D") + "." + nonce);
        callback.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeAsync(callback)).ShouldBe("installation_bound");
        (await ScalarAsync<long>(
            "select count(*) from public.github_installation where org_id = @org and state <> 'deleted'",
            ("org", org))).ShouldBe(1);

        // And the start route refuses a second flow for an org that already has one.
        using var start = await SendAsync(client, HttpMethod.Post, "/v1/github/installations/start", org);
        start.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeAsync(start)).ShouldBe("already_installed");
    }

    /// <summary>
    /// The two halves of the ownership rule, with the both-halves case as the positive control: an installation the
    /// OAuth user cannot reach is 403, one the user reaches but does not administer is 403, and one satisfying both
    /// binds.
    /// </summary>
    [Theory]
    [InlineData(false, true, HttpStatusCode.Forbidden)]
    [InlineData(true, false, HttpStatusCode.Forbidden)]
    [InlineData(true, true, HttpStatusCode.OK)]
    public async Task Callback_OwnershipRule_AnswersTheTwoHalves(bool reachable, bool administers, HttpStatusCode expected)
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var nonce = $"not-owned-nonce-{reachable}-{administers}";
        await InsertStateAsync(org, admin, nonce, DateTimeOffset.UtcNow.AddMinutes(5));

        var oauth = new FakeGitHubOAuth { UserId = ProbeUserId, Administers = administers };
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

        using var callback = await CallbackAsync(client, org.ToString("D") + "." + nonce);
        callback.StatusCode.ShouldBe(expected);

        if (expected == HttpStatusCode.Forbidden)
        {
            (await CodeAsync(callback)).ShouldBe("installation_not_owned");
            (await ScalarAsync<long>(
                "select count(*) from public.github_installation where org_id = @org",
                ("org", org))).ShouldBe(0);
        }
        else
        {
            (await ScalarAsync<long>(
                "select count(*) from public.github_installation where org_id = @org",
                ("org", org))).ShouldBe(1);
        }
    }

    /// <summary>A list that disagrees with the App's own view of the installation is not this user's either.</summary>
    [Fact]
    public async Task Callback_AccountDisagreesWithTheApp_403()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        const string nonce = "account-mismatch-nonce";
        await InsertStateAsync(org, admin, nonce, DateTimeOffset.UtcNow.AddMinutes(5));

        // The installation id matches; the account behind it does not, so the two views are not one installation.
        var oauth = new FakeGitHubOAuth { UserId = ProbeUserId, Administers = true };
        oauth.Installations.Add(new GitHubUserInstallation(InstallationId, "impostor", 1, "Organization"));
        var app = UsableApp();

        await using var host = Host(org, admin, oauth, app);
        using var client = host.CreateClient();

        using var callback = await CallbackAsync(client, org.ToString("D") + "." + nonce);
        callback.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeAsync(callback)).ShouldBe("installation_not_owned");
    }

    /// <summary>
    /// A suspended installation is recorded as suspended, never as active: the row keeps the suspension time the App
    /// reports and the read route refuses it, which the state guard requires of any suspended row.
    /// </summary>
    [Fact]
    public async Task Callback_SuspendedInstallation_BindsAsSuspended()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        const string nonce = "suspended-nonce";
        await InsertStateAsync(org, admin, nonce, DateTimeOffset.UtcNow.AddMinutes(5));

        var oauth = new FakeGitHubOAuth { UserId = ProbeUserId, Administers = true };
        oauth.Installations.Add(new GitHubUserInstallation(InstallationId, "acme", ProbeUserId, "User"));
        var app = new FakeGitHubAppInstallations
        {
            Installation = new GitHubAppInstallation(InstallationId, "acme", ProbeUserId, "User", new Dictionary<string, string>())
            {
                SuspendedAt = new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero),
            },
        };

        await using var host = Host(org, admin, oauth, app);
        using var client = host.CreateClient();

        using var callback = await CallbackAsync(client, org.ToString("D") + "." + nonce);
        callback.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await ReadJsonAsync(callback);
        body.RootElement.GetProperty("state").GetString().ShouldBe("suspended");

        (await ScalarAsync<string>(
            "select state from public.github_installation where org_id = @org",
            ("org", org))).ShouldBe("suspended");
        (await ScalarAsync<int>(
            "select case when suspended_at is null then 0 else 1 end from public.github_installation where org_id = @org",
            ("org", org))).ShouldBe(1);

        using var read = await SendAsync(client, HttpMethod.Get, "/v1/github/installations", org);
        read.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeAsync(read)).ShouldBe("installation_unavailable");
    }

    /// <summary>A suspended binding is a 409, not a healthy 200; an org with none is 404.</summary>
    [Fact]
    public async Task Get_WhenSuspended_409()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var other = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var otherAdmin = await IdentityTestData.SeedActiveMemberAsync(Fixture, other, "tenant_admin", cancellationToken: Token);

        await SeedInstallationAsync(org, InstallationId, "suspended");

        var oauth = new FakeGitHubOAuth { UserId = ProbeUserId };
        await using var host = Host(org, admin, oauth, new FakeGitHubAppInstallations());
        using var client = host.CreateClient();

        using var suspended = await SendAsync(client, HttpMethod.Get, "/v1/github/installations", org);
        suspended.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeAsync(suspended)).ShouldBe("installation_unavailable");

        await using var otherHost = Host(other, otherAdmin, oauth, new FakeGitHubAppInstallations());
        using var otherClient = otherHost.CreateClient();
        using var missing = await SendAsync(otherClient, HttpMethod.Get, "/v1/github/installations", other);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>Disconnect is terminal per row: 204, then the read is 404 and a replay 404.</summary>
    [Fact]
    public async Task Disconnect_204_Then_404()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var rowId = await SeedInstallationAsync(org, InstallationId, "active");

        var oauth = new FakeGitHubOAuth { UserId = ProbeUserId };
        await using var host = Host(org, admin, oauth, new FakeGitHubAppInstallations());
        using var client = host.CreateClient();

        using var deleted = await SendAsync(
            client, HttpMethod.Delete, $"/v1/github/installations/{rowId}", org, "disconnect-probe-1");
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var read = await SendAsync(client, HttpMethod.Get, "/v1/github/installations", org);
        read.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var replay = await SendAsync(
            client, HttpMethod.Delete, $"/v1/github/installations/{rowId}", org, "disconnect-probe-2");
        replay.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await ScalarAsync<string>(
            "select state from public.github_installation where id = @id",
            ("id", rowId))).ShouldBe("deleted");

        // A disconnected org may start a fresh flow again.
        using var start = await SendAsync(client, HttpMethod.Post, "/v1/github/installations/start", org);
        start.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
