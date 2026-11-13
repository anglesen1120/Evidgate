using System.Net;
using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// The audited half of the install routes: one create row per binding, one delete row per disconnect, no row for a
/// refused call, and no secret anywhere in the four tables a full flow writes.
/// </summary>
public sealed partial class GitHubInstallationTests
{
    /// <summary>
    /// The start route stores the digest of the nonce, and the callback consumes with the digest of the same bytes:
    /// the state it hands over is <c>&lt;org&gt;.&lt;nonce&gt;</c>, so the two must agree or every callback is 400.
    /// </summary>
    [Fact]
    public async Task Start_StoresTheDigestOfTheNonceInsideTheState()
    {
        var (host, _, _, _, state, org) = await StartedAsync();
        await using var _host = host;

        var stored = await ScalarAsync<byte[]>(
            "select state_hash from public.github_install_state where org_id = @org",
            ("org", org));
        var expected = SHA256.HashData(Encoding.ASCII.GetBytes(NonceOf(state)));

        stored.ShouldNotBeNull();
        Convert.ToHexString(stored).ShouldBe(Convert.ToHexString(expected));
    }

    /// <summary>One create audit row, with the caller as its actor and the new row as its object.</summary>
    [Fact]
    public async Task Create_WritesExactlyOneAuditRow()
    {
        var (host, client, _, _, state, org) = await StartedAsync();
        await using var _host = host;
        var admin = await UserIdOfAsync(org);

        using var callback = await CallbackAsync(client, state);
        callback.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await ReadJsonAsync(callback);
        var rowId = body.RootElement.GetProperty("id").GetGuid();

        (await ScalarAsync<long>(
            "select count(*) from public.audit_log where org_id = @org and action = 'github_installation.create'",
            ("org", org))).ShouldBe(1, "one binding, one entry");

        (await ScalarAsync<string>(
            "select actor_type from public.audit_log where org_id = @org and action = 'github_installation.create'",
            ("org", org))).ShouldBe("user");

        (await ScalarAsync<string>(
            "select actor_id from public.audit_log where org_id = @org and action = 'github_installation.create'",
            ("org", org))).ShouldBe(admin.ToString("D"));

        (await ScalarAsync<string>(
            "select object_type from public.audit_log where org_id = @org and action = 'github_installation.create'",
            ("org", org))).ShouldBe("github_installation");

        (await ScalarAsync<string>(
            "select object_id from public.audit_log where org_id = @org and action = 'github_installation.create'",
            ("org", org))).ShouldBe(rowId.ToString("D"));
    }

    /// <summary>A refused callback writes no audit row, and the delete is audited exactly once.</summary>
    [Fact]
    public async Task Disconnect_WritesExactlyOneDeleteAuditRow_AndNoRowForTheReplay()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var rowId = await SeedInstallationAsync(org, InstallationId, "active");

        await using var host = Host(org, admin, new FakeGitHubOAuth { UserId = ProbeUserId }, new FakeGitHubAppInstallations());
        using var client = host.CreateClient();

        using var deleted = await SendAsync(
            client, HttpMethod.Delete, $"/v1/github/installations/{rowId}", org, "disconnect-audit-1");
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ScalarAsync<long>(
            "select count(*) from public.audit_log where org_id = @org and action = 'github_installation.delete'",
            ("org", org))).ShouldBe(1);

        (await ScalarAsync<string>(
            "select object_id from public.audit_log where org_id = @org and action = 'github_installation.delete'",
            ("org", org))).ShouldBe(rowId.ToString("D"));

        (await ScalarAsync<string>(
            "select actor_id from public.audit_log where org_id = @org and action = 'github_installation.delete'",
            ("org", org))).ShouldBe(admin.ToString("D"));

        // The replay is a 404 and its rolled-back unit leaves the chain where it was.
        using var replay = await SendAsync(
            client, HttpMethod.Delete, $"/v1/github/installations/{rowId}", org, "disconnect-audit-2");
        replay.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await ScalarAsync<long>(
            "select count(*) from public.audit_log where org_id = @org and action = 'github_installation.delete'",
            ("org", org))).ShouldBe(1, "a refused disconnect writes nothing");
        (await ScalarAsync<long>(
            "select count(*) from public.audit_log where org_id = @org and action = 'github_installation.create'",
            ("org", org))).ShouldBe(0);
    }

    /// <summary>
    /// AC4 over every sink a flow writes: the two installation tables, the audit chain and the outbox hold neither the
    /// authorization code, the user token, an App key, nor the state the browser carried.
    /// </summary>
    [Fact]
    public async Task FullFlow_WritesNoSecretToAnyTable()
    {
        var (host, client, _, _, state, org) = await StartedAsync();
        await using var _host = host;

        using var callback = await CallbackAsync(client, state);
        callback.StatusCode.ShouldBe(HttpStatusCode.OK);

        var dump = await DumpAsync(
            """
            select coalesce(string_agg(t.row_text, ' '), '') from (
              select github_installation::text as row_text from public.github_installation where org_id = @org
              union all
              select github_install_state::text from public.github_install_state where org_id = @org
              union all
              select to_jsonb(a)::text from public.audit_log a where a.org_id = @org
              union all
              select to_jsonb(o)::text from public.outbox o where o.org_id = @org
            ) t
            """,
            ("org", org));

        dump.ShouldNotContain(FakeGitHubOAuth.UserToken, Case.Sensitive);
        dump.ShouldNotContain("probe-code", Case.Sensitive);
        dump.ShouldNotContain("ghs_", Case.Sensitive);
        dump.ShouldNotContain("BEGIN", Case.Sensitive);
        dump.ShouldNotContain(NonceOf(state), Case.Sensitive);

        // The stored digest is a 32-byte hash: it is neither the nonce nor a truncated one.
        (await ScalarAsync<int>(
            "select coalesce(max(octet_length(state_hash)), 0) from public.github_install_state where org_id = @org",
            ("org", org))).ShouldBe(32);
    }

    /// <summary>The start route stops opening unbounded live states: the 21st answers 429 rather than adding a row.</summary>
    [Fact]
    public async Task Start_AtLiveStateCap_429()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);

        for (var index = 0; index < 20; index++)
        {
            await InsertStateAsync(org, admin, $"cap-probe-{index}", DateTimeOffset.UtcNow.AddMinutes(5));
        }

        await using var host = Host(org, admin, new FakeGitHubOAuth { UserId = ProbeUserId }, new FakeGitHubAppInstallations());
        using var client = host.CreateClient();

        using var start = await SendAsync(client, HttpMethod.Post, "/v1/github/installations/start", org);
        start.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await CodeAsync(start)).ShouldBe("install_flow_limited");

        (await ScalarAsync<long>(
            "select count(*) from public.github_install_state where org_id = @org and used_at is null",
            ("org", org))).ShouldBe(20, "the refused start added no state");
    }

    /// <summary>A flow that ends on a spent state drops its own expiry only through the purge, not the routes.</summary>
    [Fact]
    public async Task CompletedFlow_KeepsOneSpentStateRow()
    {
        var (host, client, _, _, state, org) = await StartedAsync();
        await using var _host = host;

        using var callback = await CallbackAsync(client, state);
        callback.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await ScalarAsync<long>(
            "select count(*) from public.github_install_state where org_id = @org",
            ("org", org))).ShouldBe(1);
        (await ScalarAsync<int>(
            "select case when used_at is null then 0 else 1 end from public.github_install_state where org_id = @org",
            ("org", org))).ShouldBe(1);
    }
}
