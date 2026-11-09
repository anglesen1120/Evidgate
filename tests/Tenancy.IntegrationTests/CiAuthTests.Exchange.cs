using System.Net;
using System.Net.Http.Headers;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>GitHub OIDC exchange cases of <see cref="CiAuthTests"/>.</summary>
public sealed partial class CiAuthTests
{
    // ---------------------------------------------------------------- GitHub exchange

    [Fact]
    public async Task Exchange_HappyPath_ActivatesThePendingBindingAndMintsAScopedProducer()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var challenge = (await CreateBindingAsync(client, user, org, project)).Body.GetProperty("binding_challenge").GetString()!;

        // T70: no challenge, a wrong challenge: refused, nothing minted, still pending.
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId))).ErrorCode.ShouldBe("binding_pending");
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId), "wrong-challenge")).ErrorCode.ShouldBe("binding_pending");
        (await _fixture.ScalarAsync<string>(_database!, "select status from public.ci_binding")).ShouldBe("pending_verification");

        var exchanged = await ExchangeAsync(client, api.Jwks.Mint(RepoId), challenge);
        exchanged.Status.ShouldBe(HttpStatusCode.OK);
        exchanged.Id("project_id").ShouldBe(project);
        exchanged.Body.GetProperty("permission_set").EnumerateArray().Select(e => e.GetString()).Order()
            .ShouldBe(["artifact:write", "decision:read", "event:write", "run:create"]);
        (await _fixture.ScalarAsync<string>(_database!, "select status from public.ci_binding")).ShouldBe("active");
        (await _fixture.ScalarAsync<bool>(_database!, "select binding_challenge_hash is null from public.ci_binding")).ShouldBeTrue();

        // AC6: the minted claims.
        var jwt = new JsonWebToken(exchanged.Text("token")!);
        jwt.Issuer.ShouldBe(CiTestKeys.Issuer);
        jwt.Audiences.ShouldContain(CiTestKeys.Audience);
        jwt.GetPayloadValue<string>("org_id").ShouldBe(org.ToString("D"));
        jwt.GetPayloadValue<string>("project_id").ShouldBe(project.ToString("D"));
        jwt.GetPayloadValue<long>("repository_id").ShouldBe(RepoId);
        jwt.GetPayloadValue<string>("ref").ShouldBe("refs/heads/main");
        jwt.GetPayloadValue<long>("run_id").ShouldBe(9001);
        jwt.GetPayloadValue<int>("run_attempt").ShouldBe(1);
        jwt.GetPayloadValue<string>("job_workflow_ref").ShouldBe(Workflow);
        jwt.GetPayloadValue<string>("jti").ShouldNotBeNullOrEmpty();
        (jwt.ValidTo - DateTime.UtcNow).ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(61));
        (jwt.ValidTo - DateTime.UtcNow).ShouldBeGreaterThan(TimeSpan.FromMinutes(55));

        // Once active, a later exchange needs no challenge.
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId))).Status.ShouldBe(HttpStatusCode.OK);

        // T88: the OIDC claims are recorded on ci_token_issue.
        (await _fixture.ScalarAsync<long>(_database!, "select count(*) from public.ci_token_issue where run_id = 9001 and repository_id = 4242 and provider = 'github_actions'"))
            .ShouldBe(2);
    }

    [Fact]
    public async Task Exchange_MintedProducer_CannotEscapeItsScope()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var other = await CreateProjectAsync(client, user, org, "other-project");
        var challenge = (await CreateBindingAsync(client, user, org, project)).Body.GetProperty("binding_challenge").GetString()!;
        var token = (await ExchangeAsync(client, api.Jwks.Mint(RepoId), challenge)).Text("token")!;

        Action<HttpRequestHeaders> producer = headers => headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        // Authenticated as a service caller bound to the org, yet nothing outside the ci_producer set is allowed.
        (await client.SendAsync(HttpMethod.Post, $"/v1/orgs/{org}/memberships", producer, new { email = "x@example.test", roles = Array.Empty<string>() }, NewKey()))
            .Status.ShouldBe(HttpStatusCode.Forbidden);
        (await client.SendAsync(HttpMethod.Get, $"/v1/projects/{other}", producer)).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{Guid.CreateVersion7()}", producer)).Status.ShouldBe(HttpStatusCode.NotFound);

        // A tampered or foreign-signed token authenticates nobody.
        var tampered = token[..^3] + (token[^3] == 'A' ? "BBB" : "AAA");
        (await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}", h => h.TryAddWithoutValidation("Authorization", "Bearer " + tampered)))
            .Status.ShouldBe(HttpStatusCode.Unauthorized);
        var githubToken = api.Jwks.Mint(RepoId);
        (await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}", h => h.TryAddWithoutValidation("Authorization", "Bearer " + githubToken)))
            .Status.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Exchange_ClaimMatrix_RefusesEveryInvalidToken()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var challenge = (await CreateBindingAsync(client, user, org, project)).Body.GetProperty("binding_challenge").GetString()!;
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId), challenge)).Status.ShouldBe(HttpStatusCode.OK);

        var cases = new (string Name, string Token)[]
        {
            ("wrong iss", api.Jwks.Mint(RepoId, (_, o) => o.Issuer = "https://evil.example")),
            ("wrong aud", api.Jwks.Mint(RepoId, (_, o) => o.Audience = "someone-else")),
            ("expired", api.Jwks.Mint(RepoId, (_, o) => { o.NotBeforeOffset = TimeSpan.FromMinutes(-20); o.ExpiresOffset = TimeSpan.FromMinutes(-10); })),
            ("nbf beyond the skew", api.Jwks.Mint(RepoId, (_, o) => o.NotBeforeOffset = TimeSpan.FromMinutes(5))),
            ("unknown kid", api.Jwks.Mint(RepoId, (_, o) => o.KeyId = "rotated-away")),
            ("HS256 confusion", api.Jwks.Mint(RepoId, (_, o) => o.Algorithm = SecurityAlgorithms.RsaSsaPssSha256)),
            ("missing repository_id", api.Jwks.Mint(RepoId, (c, _) => c.Remove("repository_id"))),
            ("non-numeric run_id", api.Jwks.Mint(RepoId, (c, _) => c["run_id"] = "abc")),
            ("pull_request_target", api.Jwks.Mint(RepoId, (c, _) => c["event_name"] = "pull_request_target")),
            ("workflow_run", api.Jwks.Mint(RepoId, (c, _) => c["event_name"] = "workflow_run")),
            ("missing event_name", api.Jwks.Mint(RepoId, (c, _) => c.Remove("event_name"))),
            ("empty event_name", api.Jwks.Mint(RepoId, (c, _) => c["event_name"] = string.Empty)),
            ("blank event_name", api.Jwks.Mint(RepoId, (c, _) => c["event_name"] = "  ")),
            ("jti beyond the column", api.Jwks.Mint(RepoId, (c, _) => c["jti"] = new string('j', 300))),
            ("garbage", "not.a.jwt"),
        };

        foreach (var (name, token) in cases)
        {
            var answer = await ExchangeAsync(client, token);
            answer.Status.ShouldBe(HttpStatusCode.Unauthorized, name);
            answer.ErrorCode.ShouldBe("token_invalid", name);
        }

        // Skew boundary: 59 s ahead is inside the 60 s allowance (elapsed time only moves it closer), and an exp 30 s in
        // the past still passes with room for the time the request takes; the exact edges are pinned against a fixed clock
        // in CiUnitTests.Lifetime_HonoursTheSkewOnBothEdges.
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId, (_, o) => o.NotBeforeOffset = TimeSpan.FromSeconds(59)))).Status.ShouldBe(HttpStatusCode.OK);
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId, (_, o) => { o.NotBeforeOffset = TimeSpan.FromMinutes(-5); o.ExpiresOffset = TimeSpan.FromSeconds(-30); })))
            .Status.ShouldBe(HttpStatusCode.OK);
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId, (_, o) => { o.NotBeforeOffset = TimeSpan.FromMinutes(-5); o.ExpiresOffset = TimeSpan.FromSeconds(-61); })))
            .ErrorCode.ShouldBe("token_invalid");

        // No credential at all.
        var bare = await client.SendAsync(HttpMethod.Post, "/v1/ci/token", _ => { });
        bare.Status.ShouldBe(HttpStatusCode.Unauthorized);

        // Both sub formats are accepted: the binding follows repository_id, never `sub` (F3, D5).
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId, (c, _) => c["sub"] = "repo:acme/web:ref:refs/heads/main"))).Status.ShouldBe(HttpStatusCode.OK);
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId, (c, _) => c["sub"] = $"repository_owner_id:1:repository_id:{RepoId}:ref:refs/heads/main"))).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("push")]
    [InlineData("workflow_dispatch")]
    [InlineData("merge_group")]
    [InlineData("schedule")]
    [InlineData("release")]
    public async Task Exchange_AllowedEvents_StillMint(string eventName)
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var challenge = (await CreateBindingAsync(client, user, org, project)).Body.GetProperty("binding_challenge").GetString()!;

        (await ExchangeAsync(client, api.Jwks.Mint(RepoId, (c, _) => c["event_name"] = eventName), challenge))
            .Status.ShouldBe(HttpStatusCode.OK, eventName);
    }

    /// <summary>The allowlist fails closed: an unlisted event, and any differently cased spelling, is refused.</summary>
    [Theory]
    [InlineData("pull_request")]
    [InlineData("issue_comment")]
    [InlineData("Workflow_Run")]
    [InlineData("WORKFLOW_RUN")]
    [InlineData("PUSH")]
    [InlineData("Push")]
    public async Task Exchange_UnlistedOrDifferentlyCasedEvent_IsRefused(string eventName)
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var challenge = (await CreateBindingAsync(client, user, org, project)).Body.GetProperty("binding_challenge").GetString()!;

        (await ExchangeAsync(client, api.Jwks.Mint(RepoId, (c, _) => c["event_name"] = eventName), challenge))
            .ErrorCode.ShouldBe("token_invalid", eventName);
    }

    [Fact]
    public async Task Exchange_EventNameThatIsNotOneJsonString_IsRefused()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var challenge = (await CreateBindingAsync(client, user, org, project)).Body.GetProperty("binding_challenge").GetString()!;
        const string Push = "\"event_name\":\"push\"";
        var cases = new (string Name, Action<Dictionary<string, object>, TestJwks.TokenOptions> Tweak)[]
        {
            ("number", (c, _) => c["event_name"] = 123),
            ("boolean", (c, _) => c["event_name"] = true),
            ("one-element array", (c, _) => c["event_name"] = new[] { "push" }),
            ("duplicate, denied first", (_, o) => o.RewritePayload = p => p.Replace(Push, "\"event_name\":\"workflow_run\"," + Push, StringComparison.Ordinal)),
            ("duplicate, denied last", (_, o) => o.RewritePayload = p => p.Replace(Push, Push + ",\"event_name\":\"workflow_run\"", StringComparison.Ordinal)),
        };

        foreach (var (name, tweak) in cases)
        {
            (await ExchangeAsync(client, api.Jwks.Mint(RepoId, (c, o) => tweak(c, o)), challenge)).ErrorCode.ShouldBe("token_invalid", name);
        }
    }

    [Fact]
    public async Task Exchange_ExplicitAllowedEventNamesOverride_ReplacesTheDefault()
    {
        _database = _dedicated!.Name;
        var overrides = new Dictionary<string, string?>(StringComparer.Ordinal) { ["EVIDGATE:Ci:AllowedEventNames"] = "schedule, pull_request, true" };
        var api = TenancyApiFactory.Ci(_fixture.AppConnectionString(_database), overrides);
        await using var _ = api;
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");
        var challenge = (await CreateBindingAsync(client, user, org, project)).Body.GetProperty("binding_challenge").GetString()!;

        (await ExchangeAsync(client, api.Jwks.Mint(RepoId, (c, _) => c["event_name"] = "push"), challenge))
            .ErrorCode.ShouldBe("token_invalid");
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId, (c, _) => c["event_name"] = "pull_request"), challenge))
            .Status.ShouldBe(HttpStatusCode.OK);
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId, (c, _) => c["event_name"] = "schedule"), challenge))
            .Status.ShouldBe(HttpStatusCode.OK);

        // A JSON boolean is refused even though its text form is on the list; only the JSON string "true" mints.
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId, (c, _) => c["event_name"] = true), challenge))
            .ErrorCode.ShouldBe("token_invalid");
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId, (c, _) => c["event_name"] = "true"), challenge))
            .Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Exchange_KeysUnavailable_Is503_NotAnInvalidToken()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        await CreateBindingAsync(client, user, org, project);

        api.Jwks.KeysUnavailable = true;
        var answer = await ExchangeAsync(client, api.Jwks.Mint(RepoId));
        answer.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        answer.ErrorCode.ShouldBe("unavailable");
    }

    [Fact]
    public async Task Exchange_BindingMatching_RefusesUnboundForkedAndUnlistedWorkflows_AndMintsNothing()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var challenge = (await CreateBindingAsync(client, user, org, project)).Body.GetProperty("binding_challenge").GetString()!;
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId), challenge)).Status.ShouldBe(HttpStatusCode.OK);
        var issued = await _fixture.ScalarAsync<long>(_database!, "select count(*) from public.ci_token_issue");

        // Unbound repository, and a fork PR (a fork has its own repository_id).
        (await ExchangeAsync(client, api.Jwks.Mint(999_999))).ErrorCode.ShouldBe("binding_mismatch");
        // The upstream repo id with a workflow nobody allowlisted (a fork-PR-shaped job_workflow_ref).
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId, (c, _) => c["job_workflow_ref"] = "evil/fork/.github/workflows/ci.yml@refs/pull/9/merge")))
            .ErrorCode.ShouldBe("binding_mismatch");
        // An unlisted ref.
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId, (c, _) => c["ref"] = "refs/heads/feature")))
            .ErrorCode.ShouldBe("binding_mismatch");

        (await _fixture.ScalarAsync<long>(_database!, "select count(*) from public.ci_token_issue")).ShouldBe(issued);
    }

    [Fact]
    public async Task Exchange_RenamedRepository_StillMints_AndRecordsTheNewName()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var challenge = (await CreateBindingAsync(client, user, org, project)).Body.GetProperty("binding_challenge").GetString()!;
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId), challenge)).Status.ShouldBe(HttpStatusCode.OK);

        var renamed = await ExchangeAsync(client, api.Jwks.Mint(RepoId, (c, _) => c["repository"] = "acme/web-renamed"));
        renamed.Status.ShouldBe(HttpStatusCode.OK);
        (await _fixture.ScalarAsync<string>(_database!, "select repository_full_name from public.ci_binding")).ShouldBe("acme/web-renamed");
        (await _fixture.ScalarAsync<long>(
            _database!,
            "select count(*) from public.audit_log where action = 'ci_token.exchange' and metadata ->> 'reason' = 'repository_renamed'"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Exchange_ReplayedJti_IsRefused_AndTheAttemptIsNotMinted()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var challenge = (await CreateBindingAsync(client, user, org, project)).Body.GetProperty("binding_challenge").GetString()!;
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId), challenge)).Status.ShouldBe(HttpStatusCode.OK);

        var jwt = api.Jwks.Mint(RepoId, (c, _) => c["jti"] = "jti-fixed-1");
        (await ExchangeAsync(client, jwt)).Status.ShouldBe(HttpStatusCode.OK);
        var replay = await ExchangeAsync(client, jwt);

        replay.Status.ShouldBe(HttpStatusCode.Unauthorized);
        replay.ErrorCode.ShouldBe("token_replayed");
        (await _fixture.ScalarAsync<long>(_database!, "select count(*) from public.ci_token_issue where jti = 'jti-fixed-1'")).ShouldBe(1);
    }

    [Fact]
    public async Task Exchange_RivalOrgCannotStealAnActiveRepository()
    {
        var (api, client, userA, orgA, projectA) = await StartAsync();
        await using var _ = api;
        var userB = Guid.CreateVersion7();
        var orgB = await CreateOrgAsync(client, userB, "rival-org");
        var projectB = await CreateProjectAsync(client, userB, orgB, "rival-project");

        var challengeA = (await CreateBindingAsync(client, userA, orgA, projectA)).Body.GetProperty("binding_challenge").GetString()!;
        var challengeB = (await CreateBindingAsync(client, userB, orgB, projectB)).Body.GetProperty("binding_challenge").GetString()!;

        // B holds a pending binding for the same repository; A proves control first.
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId), challengeA)).Id("project_id").ShouldBe(projectA);

        // From now on the repository resolves to A, whatever challenge is presented: B's binding stays pending.
        var viaB = await ExchangeAsync(client, api.Jwks.Mint(RepoId), challengeB);
        viaB.Id("project_id").ShouldBe(projectA);
        (await _fixture.ScalarAsync<string>(_database!, $"select status from public.ci_binding where org_id = '{orgB}'")).ShouldBe("pending_verification");
    }

    [Fact]
    public async Task Exchange_WhenCiAuthIsNotConfigured_Is503()
    {
        var database = _dedicated!.Name;
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();

        var answer = await client.SendAsync(HttpMethod.Post, "/v1/ci/token", _ => { }, new { ingest_token = "egt_abcdefghijkl_" + new string('a', 43) });
        answer.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Exchange_IsRateLimited_WithRetryAfter()
    {
        var database = _dedicated!.Name;
        await using var api = TenancyApiFactory.Ci(
            _fixture.AppConnectionString(database),
            new Dictionary<string, string?> { ["EVIDGATE:Ci:ExchangeBurstLimit"] = "3" });
        using var client = api.CreateClient();

        var answers = new List<ApiAnswer>();
        for (var i = 0; i < 5; i++)
        {
            answers.Add(await client.SendAsync(HttpMethod.Post, "/v1/ci/token", _ => { }));
        }

        answers.Take(3).ShouldAllBe(answer => answer.Status == HttpStatusCode.Unauthorized);
        var limited = answers[3];
        limited.Status.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.ErrorCode.ShouldBe("rate_limited");
        limited.Headers.RetryAfter.ShouldNotBeNull();
    }
}
