using System.Net;
using System.Net.Http.Headers;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 02.4.1-T2/T3 and 02.4.2 through the real host: GitHub OIDC exchange against a local JWKS, binding challenge,
/// replay arbiter, minted <c>ci_producer</c> scope, and ingest-token create/exchange/rotate/revoke.
/// </summary>
[Collection(ExclusiveTenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class CiAuthTests : IAsyncLifetime
{
    private const long RepoId = 4242;
    private static readonly string[] EventOnly = ["event:write"];
    private static readonly string[] MainAndTags = ["refs/heads/main", "refs/tags/v*"];
    private static readonly string[] TagsOnly = ["refs/tags/v*"];
    private const string Workflow = "acme/web/.github/workflows/ci.yml@refs/heads/main";

    private readonly PostgresTenancyFixture _fixture;
    private DedicatedDatabase? _dedicated;

    public CiAuthTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // This class asserts on unfiltered CI tables, so each test owns a migrated database.
    public async ValueTask InitializeAsync() => _dedicated = await _fixture.CreateDedicatedDatabaseAsync();

    public async ValueTask DisposeAsync()
    {
        if (_dedicated is not null)
        {
            await _dedicated.DisposeAsync();
        }
    }

    // ---------------------------------------------------------------- bindings (admin)

    [Fact]
    public async Task CreateBinding_ReturnsTheChallengeOnce_AndStoresOnlyAnHmac()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;

        var created = await CreateBindingAsync(client, user, org, project);
        created.Status.ShouldBe(HttpStatusCode.Created);
        var challenge = created.Body.GetProperty("binding_challenge").GetString()!;
        challenge.Length.ShouldBeGreaterThanOrEqualTo(43);
        created.Body.GetProperty("binding").GetProperty("status").GetString().ShouldBe("pending_verification");

        var list = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/ci-bindings", Admin(user, org));
        list.Status.ShouldBe(HttpStatusCode.OK);
        list.Body.GetRawText().ShouldNotContain(challenge);
        list.Body.GetProperty("items").GetArrayLength().ShouldBe(1);

        var stored = await _fixture.ScalarAsync<string>(_database!, "select encode(binding_challenge_hash, 'hex') from public.ci_binding");
        stored.Length.ShouldBe(64);
        (await _fixture.ScalarAsync<long>(_database!, $"select count(*) from public.ci_binding where position('{challenge}' in repository_full_name || array_to_string(allowed_ref_patterns, '')) > 0"))
            .ShouldBe(0);
        (await _fixture.ScalarAsync<int>(_database!, "select challenge_pepper_version from public.ci_binding")).ShouldBe(2);

        (await CreateBindingAsync(client, user, org, project)).Status.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateBinding_RefusesBadShapes_UnknownProjects_AndCallersWithoutThePermission()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;

        foreach (var body in new object[]
                 {
                     new { provider = "gitlab", repository_id = RepoId, repository_full_name = "acme/web", allowed_ref_patterns = new[] { "refs/heads/main" }, allowed_workflow_refs = new[] { Workflow } },
                     new { provider = "github_actions", repository_id = 0, repository_full_name = "acme/web", allowed_ref_patterns = new[] { "refs/heads/main" }, allowed_workflow_refs = new[] { Workflow } },
                     new { provider = "github_actions", repository_id = RepoId, repository_full_name = "not a repo", allowed_ref_patterns = new[] { "refs/heads/main" }, allowed_workflow_refs = new[] { Workflow } },
                     new { provider = "github_actions", repository_id = RepoId, repository_full_name = "acme/web", allowed_ref_patterns = new[] { "main" }, allowed_workflow_refs = new[] { Workflow } },
                     new { provider = "github_actions", repository_id = RepoId, repository_full_name = "acme/web", allowed_ref_patterns = Array.Empty<string>(), allowed_workflow_refs = new[] { Workflow } },
                     new { provider = "github_actions", repository_id = RepoId, repository_full_name = "acme/web", allowed_ref_patterns = new[] { "refs/heads/main" }, allowed_workflow_refs = new[] { "ci.yml" } },
                 })
        {
            (await client.SendAsync(HttpMethod.Post, $"/v1/projects/{project}/ci-bindings", Admin(user, org), body))
                .Status.ShouldBe(HttpStatusCode.UnprocessableEntity, body.ToString());
        }

        (await CreateBindingAsync(client, user, org, Guid.CreateVersion7())).Status.ShouldBe(HttpStatusCode.NotFound);

        var bare = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            _database!,
            $"""
            insert into public."user" (id, email) values ('{bare}', 'bare@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status) values (gen_random_uuid(), '{org}', '{bare}', 'bare@example.test', 'active')
            """);
        (await CreateBindingAsync(client, bare, org, project)).Status.ShouldBe(HttpStatusCode.Forbidden);

        (await _fixture.ScalarAsync<long>(_database!, "select count(*) from public.ci_binding")).ShouldBe(0);
    }

    [Fact]
    public async Task UpdateBinding_NeedsIfMatch_Disables_AndReEnablesOnlyAfterActivation()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var created = await CreateBindingAsync(client, user, org, project);
        var binding = created.Body.GetProperty("binding").GetProperty("id").GetGuid();
        var challenge = created.Body.GetProperty("binding_challenge").GetString()!;

        (await client.SendAsync(HttpMethod.Patch, $"/v1/ci-bindings/{binding}", Admin(user, org), new { status = "disabled" }, NewKey()))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await client.SendAsync(HttpMethod.Patch, $"/v1/ci-bindings/{binding}", Admin(user, org), new { status = "disabled" }, NewKey(), "\"7\""))
            .Status.ShouldBe(HttpStatusCode.PreconditionFailed);

        // A binding that never proved control cannot be "re-enabled".
        (await client.SendAsync(HttpMethod.Patch, $"/v1/ci-bindings/{binding}", Admin(user, org), new { status = "active" }, NewKey(), "\"1\""))
            .Status.ShouldBe(HttpStatusCode.Conflict);

        (await ExchangeAsync(client, api.Jwks.Mint(RepoId), challenge)).Status.ShouldBe(HttpStatusCode.OK);

        var narrowed = await client.SendAsync(
            HttpMethod.Patch, $"/v1/ci-bindings/{binding}", Admin(user, org), new { allowed_ref_patterns = TagsOnly }, NewKey(), await VersionAsync(binding));
        narrowed.Status.ShouldBe(HttpStatusCode.OK);
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId))).ErrorCode.ShouldBe("binding_mismatch");

        (await client.SendAsync(HttpMethod.Delete, $"/v1/ci-bindings/{binding}", Admin(user, org), null, NewKey())).Status.ShouldBe(HttpStatusCode.NoContent);
        (await client.SendAsync(HttpMethod.Delete, $"/v1/ci-bindings/{binding}", Admin(user, org), null, NewKey())).Status.ShouldBe(HttpStatusCode.NoContent);
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId))).ErrorCode.ShouldBe("binding_disabled");

        (await client.SendAsync(HttpMethod.Patch, $"/v1/ci-bindings/{binding}", Admin(user, org), new { status = "active" }, NewKey(), await VersionAsync(binding)))
            .Status.ShouldBe(HttpStatusCode.OK);

        (await client.SendAsync(HttpMethod.Patch, $"/v1/ci-bindings/{Guid.CreateVersion7()}", Admin(user, org), new { status = "disabled" }, NewKey(), "\"1\""))
            .Status.ShouldBe(HttpStatusCode.NotFound);
    }

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
            ("nbf 61s ahead", api.Jwks.Mint(RepoId, (_, o) => o.NotBeforeOffset = TimeSpan.FromSeconds(61))),
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

        // Skew boundary: 59 s ahead is inside the 60 s allowance, and an exp 59 s in the past still passes.
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId, (_, o) => o.NotBeforeOffset = TimeSpan.FromSeconds(59)))).Status.ShouldBe(HttpStatusCode.OK);
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId, (_, o) => { o.NotBeforeOffset = TimeSpan.FromMinutes(-5); o.ExpiresOffset = TimeSpan.FromSeconds(-59); })))
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

    // ---------------------------------------------------------------- ingest tokens

    [Fact]
    public async Task IngestToken_ShowsThePlaintextOnce_AndStoresOnlyPrefixAndHmac()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;

        var created = await CreateTokenAsync(client, user, org, project);
        created.Status.ShouldBe(HttpStatusCode.Created);
        var secret = created.Text("secret")!;
        secret.ShouldStartWith("egt_");
        var prefix = created.Body.GetProperty("token").GetProperty("prefix").GetString()!;
        secret.ShouldContain(prefix);

        var list = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/ingest-tokens", Admin(user, org));
        list.Body.GetRawText().ShouldNotContain(secret);
        list.Body.GetRawText().ShouldNotContain(secret[^43..]);
        list.Body.GetProperty("items")[0].GetProperty("status").GetString().ShouldBe("active");

        (await _fixture.ScalarAsync<long>(_database!, $"select count(*) from public.ingest_token where position('{secret[^43..]}' in name || prefix) > 0")).ShouldBe(0);
        (await _fixture.ScalarAsync<int>(_database!, "select octet_length(token_hmac) from public.ingest_token")).ShouldBe(32);
        (await _fixture.ScalarAsync<int>(_database!, "select pepper_version from public.ingest_token")).ShouldBe(2);
    }

    [Fact]
    public async Task IngestToken_Exchange_ReturnsAProducerScopedToTheProject_AndUniformFailures()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var secret = (await CreateTokenAsync(client, user, org, project)).Text("secret")!;

        var ok = await ExchangeIngestAsync(client, secret);
        ok.Status.ShouldBe(HttpStatusCode.OK);
        ok.Id("project_id").ShouldBe(project);
        new JsonWebToken(ok.Text("token")!).GetPayloadValue<string>("org_id").ShouldBe(org.ToString("D"));

        var wrongSecret = await ExchangeIngestAsync(client, secret[..^43] + new string('A', 43));
        var unknownPrefix = await ExchangeIngestAsync(client, "egt_ZZZZZZZZZZZZ_" + new string('A', 43));
        var malformed = await ExchangeIngestAsync(client, "hunter2");
        foreach (var answer in new[] { wrongSecret, unknownPrefix, malformed })
        {
            answer.Status.ShouldBe(HttpStatusCode.Unauthorized);
            answer.ErrorCode.ShouldBe("token_unknown");
        }

        wrongSecret.ErrorMessage.ShouldBe(unknownPrefix.ErrorMessage);
    }

    [Fact]
    public async Task IngestToken_LastUsedAt_IsThrottled()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var secret = (await CreateTokenAsync(client, user, org, project)).Text("secret")!;

        (await ExchangeIngestAsync(client, secret)).Status.ShouldBe(HttpStatusCode.OK);
        var version = await _fixture.ScalarAsync<int>(_database!, "select version from public.ingest_token");
        var first = await _fixture.ScalarAsync<DateTime>(_database!, "select last_used_at from public.ingest_token");
        (await ExchangeIngestAsync(client, secret)).Status.ShouldBe(HttpStatusCode.OK);
        (await _fixture.ScalarAsync<DateTime>(_database!, "select last_used_at from public.ingest_token")).ShouldBe(first);

        await _fixture.ExecuteAsync(_database!, "update public.ingest_token set last_used_at = now() - interval '2 minutes'");
        (await ExchangeIngestAsync(client, secret)).Status.ShouldBe(HttpStatusCode.OK);
        (await _fixture.ScalarAsync<DateTime>(_database!, "select last_used_at from public.ingest_token")).ShouldBeGreaterThan(DateTime.UtcNow.AddMinutes(-1));

        // Bookkeeping is not an edit: a concurrent rotate/revoke with If-Match must not see a spurious version bump.
        (await _fixture.ScalarAsync<int>(_database!, "select version from public.ingest_token")).ShouldBe(version);
    }

    [Fact]
    public async Task IngestToken_Revoke_IsImmediate()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var created = await CreateTokenAsync(client, user, org, project);
        var id = created.Body.GetProperty("token").GetProperty("id").GetGuid();
        var secret = created.Text("secret")!;
        (await ExchangeIngestAsync(client, secret)).Status.ShouldBe(HttpStatusCode.OK);

        (await client.SendAsync(HttpMethod.Delete, $"/v1/ingest-tokens/{id}", Admin(user, org), null, NewKey())).Status.ShouldBe(HttpStatusCode.NoContent);

        var after = await ExchangeIngestAsync(client, secret);
        after.Status.ShouldBe(HttpStatusCode.Unauthorized);
        after.ErrorCode.ShouldBe("token_revoked");

        // Only the holder of the secret learns the state: a wrong secret on a revoked token looks like any unknown token.
        (await ExchangeIngestAsync(client, secret[..^43] + new string('A', 43))).ErrorCode.ShouldBe("token_unknown");
        (await client.SendAsync(HttpMethod.Post, $"/v1/ingest-tokens/{id}/rotate", Admin(user, org))).ErrorCode.ShouldBe("token_revoked");
        (await client.SendAsync(HttpMethod.Delete, $"/v1/ingest-tokens/{Guid.CreateVersion7()}", Admin(user, org), null, NewKey())).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task IngestToken_Rotate_BothVerifyDuringTheOverlap_ThenTheOldOneExpires()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var created = await CreateTokenAsync(client, user, org, project);
        var oldId = created.Body.GetProperty("token").GetProperty("id").GetGuid();
        var oldSecret = created.Text("secret")!;

        var rotated = await client.SendAsync(HttpMethod.Post, $"/v1/ingest-tokens/{oldId}/rotate", Admin(user, org));
        rotated.Status.ShouldBe(HttpStatusCode.OK);
        var newSecret = rotated.Text("secret")!;
        newSecret.ShouldNotBe(oldSecret);
        var overlapEnds = rotated.Body.GetProperty("overlap_ends_at").GetDateTimeOffset();
        (overlapEnds - DateTimeOffset.UtcNow).ShouldBeInRange(TimeSpan.FromHours(23.9), TimeSpan.FromHours(24.1));
        (await _fixture.ScalarAsync<Guid>(_database!, $"select rotated_to_id from public.ingest_token where id = '{oldId}'"))
            .ShouldBe(rotated.Body.GetProperty("token").GetProperty("id").GetGuid());

        (await ExchangeIngestAsync(client, oldSecret)).Status.ShouldBe(HttpStatusCode.OK);
        (await ExchangeIngestAsync(client, newSecret)).Status.ShouldBe(HttpStatusCode.OK);
        (await client.SendAsync(HttpMethod.Post, $"/v1/ingest-tokens/{oldId}/rotate", Admin(user, org))).Status.ShouldBe(HttpStatusCode.Conflict);

        await _fixture.ExecuteAsync(_database!, $"update public.ingest_token set overlap_ends_at = now() - interval '1 minute' where id = '{oldId}'");
        var expired = await ExchangeIngestAsync(client, oldSecret);
        expired.Status.ShouldBe(HttpStatusCode.Unauthorized);
        expired.ErrorCode.ShouldBe("token_expired");
        (await ExchangeIngestAsync(client, newSecret)).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task IngestToken_RetiredPepperVersion_FailsLikeAnUnknownToken()
    {
        var database = _dedicated!.Name;
        string secret;
        Guid org;
        Guid project;
        await using (var first = TenancyApiFactory.Ci(_fixture.AppConnectionString(database)))
        {
            using var client = first.CreateClient();
            var user = Guid.CreateVersion7();
            org = await CreateOrgAsync(client, user, "acme");
            project = await CreateProjectAsync(client, user, org, "checkout");
            secret = (await CreateTokenAsync(client, user, org, project)).Text("secret")!;
            (await ExchangeIngestAsync(client, secret)).Status.ShouldBe(HttpStatusCode.OK);
        }

        // The pepper moved on to version 3 and the old one (version 2) was dropped: the token no longer verifies.
        var rotatedKeys = new Dictionary<string, string?>
        {
            ["EVIDGATE:Ci:Pepper"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            ["EVIDGATE:Ci:PepperVersion"] = "3",
        };
        await using (var retired = TenancyApiFactory.Ci(_fixture.AppConnectionString(database), rotatedKeys))
        {
            using var client = retired.CreateClient();
            (await ExchangeIngestAsync(client, secret)).ErrorCode.ShouldBe("token_unknown");
        }

        // While the previous pepper is still configured (the overlap), the same token verifies.
        var overlap = new Dictionary<string, string?>(rotatedKeys)
        {
            ["EVIDGATE:Ci:PreviousPepper"] = Convert.ToBase64String(TenancyApiFactory.Keys.Pepper),
            ["EVIDGATE:Ci:PreviousPepperVersion"] = "2",
        };
        await using (var during = TenancyApiFactory.Ci(_fixture.AppConnectionString(database), overlap))
        {
            using var client = during.CreateClient();
            (await ExchangeIngestAsync(client, secret)).Status.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task IngestToken_Create_ValidatesInput_AndNeedsThePermission()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;

        foreach (var body in new object[]
                 {
                     new { name = "" },
                     new { name = new string('x', 129) },
                     new { name = "ok", scopes = new[] { "membership:write" } },
                     new { name = "ok", scopes = new[] { "run:create", "run:create" } },
                     new { name = "ok", expires_at = DateTimeOffset.UtcNow.AddDays(-1) },
                 })
        {
            (await client.SendAsync(HttpMethod.Post, $"/v1/projects/{project}/ingest-tokens", Admin(user, org), body))
                .Status.ShouldBe(HttpStatusCode.UnprocessableEntity, body.ToString());
        }

        var narrow = await client.SendAsync(
            HttpMethod.Post, $"/v1/projects/{project}/ingest-tokens", Admin(user, org), new { name = "narrow", scopes = EventOnly });
        narrow.Status.ShouldBe(HttpStatusCode.Created);
        var exchanged = await ExchangeIngestAsync(client, narrow.Text("secret")!);
        exchanged.Body.GetProperty("permission_set").EnumerateArray().Select(e => e.GetString()).ShouldBe(["event:write"]);

        var bare = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            _database!,
            $"""
            insert into public."user" (id, email) values ('{bare}', 'bare@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status) values (gen_random_uuid(), '{org}', '{bare}', 'bare@example.test', 'active')
            """);
        (await CreateTokenAsync(client, bare, org, project)).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/ingest-tokens", Admin(bare, org))).Status.ShouldBe(HttpStatusCode.Forbidden);

        (await CreateTokenAsync(client, user, org, Guid.CreateVersion7())).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------- audit and tenancy of the CI tables

    [Fact]
    public async Task CiMutationsAndExchanges_WriteOneAuditRowEach_WithTheRightActor()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var created = await CreateBindingAsync(client, user, org, project);
        var challenge = created.Body.GetProperty("binding_challenge").GetString()!;
        var token = await CreateTokenAsync(client, user, org, project);
        var exchanged = await ExchangeAsync(client, api.Jwks.Mint(RepoId), challenge);
        await ExchangeIngestAsync(client, token.Text("secret")!);

        var rows = await _fixture.ColumnAsync<string>(
            _database!,
            $"select action || '|' || actor_type from public.audit_log where org_id = '{org}' and action in ('ci_binding.create', 'ingest_token.create', 'ci_token.exchange', 'ingest_token.exchange') order by seq");
        rows.ShouldBe(["ci_binding.create|user", "ingest_token.create|user", "ci_token.exchange|ci_producer", "ingest_token.exchange|ci_producer"]);

        // No secret reaches the trail.
        (await _fixture.ScalarAsync<long>(_database!, $"select count(*) from public.audit_log where metadata::text like '%{challenge}%' or metadata::text like '%{token.Text("secret")![^43..]}%'"))
            .ShouldBe(0);
        exchanged.Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OtherOrgsBindingsAndTokens_AreNotFound()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var userB = Guid.CreateVersion7();
        var orgB = await CreateOrgAsync(client, userB, "other-org");
        var binding = (await CreateBindingAsync(client, user, org, project)).Body.GetProperty("binding").GetProperty("id").GetGuid();
        var token = (await CreateTokenAsync(client, user, org, project)).Body.GetProperty("token").GetProperty("id").GetGuid();

        (await client.SendAsync(HttpMethod.Delete, $"/v1/ci-bindings/{binding}", Admin(userB, orgB), null, NewKey())).Status.ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(HttpMethod.Delete, $"/v1/ingest-tokens/{token}", Admin(userB, orgB), null, NewKey())).Status.ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(HttpMethod.Post, $"/v1/ingest-tokens/{token}/rotate", Admin(userB, orgB))).Status.ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/ci-bindings", Admin(userB, orgB))).Body.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    // ---------------------------------------------------------------- helpers

    private string? _database;

    private async Task<(TenancyApiFactory Api, HttpClient Client, Guid User, Guid Org, Guid Project)> StartAsync()
    {
        _database = _dedicated!.Name;
        var api = TenancyApiFactory.Ci(_fixture.AppConnectionString(_database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return (api, client, user, org, project);
    }

    private static Action<HttpRequestHeaders> Admin(Guid user, Guid org) => TenancyApiFactory.Resolved(user: user, orgHeader: org);

    private static Task<ApiAnswer> CreateBindingAsync(HttpClient client, Guid user, Guid org, Guid project) =>
        client.SendAsync(
            HttpMethod.Post,
            $"/v1/projects/{project}/ci-bindings",
            Admin(user, org),
            new
            {
                provider = "github_actions",
                repository_id = RepoId,
                repository_full_name = "acme/web",
                allowed_ref_patterns = MainAndTags,
                allowed_workflow_refs = new[] { Workflow },
            });

    private static Task<ApiAnswer> CreateTokenAsync(HttpClient client, Guid user, Guid org, Guid project) =>
        client.SendAsync(HttpMethod.Post, $"/v1/projects/{project}/ingest-tokens", Admin(user, org), new { name = "gitlab" });

    private static Task<ApiAnswer> ExchangeAsync(HttpClient client, string jwt, string? challenge = null) =>
        client.SendAsync(
            HttpMethod.Post,
            "/v1/ci/token",
            headers => headers.TryAddWithoutValidation("Authorization", "Bearer " + jwt),
            challenge is null ? null : new { binding_challenge = challenge });

    private static Task<ApiAnswer> ExchangeIngestAsync(HttpClient client, string token) =>
        client.SendAsync(HttpMethod.Post, "/v1/ci/token", _ => { }, new { ingest_token = token });

    private async Task<string> VersionAsync(Guid binding) =>
        "\"" + await _fixture.ScalarAsync<int>(_database!, $"select version from public.ci_binding where id = '{binding}'") + "\"";

    private static async Task<Guid> CreateOrgAsync(HttpClient client, Guid user, string slug)
    {
        var created = await client.SendAsync(
            HttpMethod.Post, "/v1/orgs", TenancyApiFactory.Resolved(user: user, email: $"{slug}@example.test"), new { name = slug, slug }, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created);

        return created.Id();
    }

    private static async Task<Guid> CreateProjectAsync(HttpClient client, Guid user, Guid org, string slug)
    {
        var created = await client.SendAsync(
            HttpMethod.Post, $"/v1/orgs/{org}/projects", TenancyApiFactory.Resolved(user: user), new { name = slug, slug }, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created);

        return created.Id();
    }
}
