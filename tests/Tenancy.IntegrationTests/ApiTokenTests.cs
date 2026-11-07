using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Project API tokens through the real host and a real database: the one-time plaintext, the mandatory bounded
/// expiry, the deny set and the creator's own set, the metadata-only list, revocation and its audit row. The token
/// string is checked against the table on every write, because the point of the design is that a dump holds nothing
/// usable.
/// </summary>
[Collection(ExclusiveTenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class ApiTokenTests : IAsyncLifetime
{
    private static readonly string[] ReadOnly = ["run:read"];
    private static readonly string[] DeniedSet = ["run:read", "api_token:write"];
    private static readonly string[] UnknownSet = ["nope:read"];
    private static readonly string[] EscalatingSet = ["run:read", "healing:triage"];
    private static readonly string[] TwoAllowed = ["run:read", "decision:read"];
    private static readonly string[] NoPermissions = [];
    private static readonly DateTimeOffset[] InvalidExpiries = [DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow];

    private readonly PostgresTenancyFixture _fixture;
    private DedicatedDatabase? _dedicated;

    public ApiTokenTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // The class asserts on unfiltered api_token rows and the idempotency table, so each test owns a migrated database.
    public async ValueTask InitializeAsync() => _dedicated = await _fixture.CreateDedicatedDatabaseAsync();

    public async ValueTask DisposeAsync()
    {
        if (_dedicated is not null)
        {
            await _dedicated.DisposeAsync();
        }
    }

    [Fact]
    public async Task ApiToken_ShowsThePlaintextOnce_AndStoresOnlyPrefixAndHmac()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;

        var created = await CreateAsync(client, user, org, project, new { name = "dashboard", permissions = ReadOnly, expires_at = In(30) });
        created.Status.ShouldBe(HttpStatusCode.OK);
        var secret = created.Text("token")!;
        secret.ShouldStartWith("evg_");
        var prefix = created.Text("prefix")!;
        prefix.Length.ShouldBe(12);
        secret.ShouldBe($"evg_{prefix}_{secret[^43..]}");
        created.Body.GetProperty("permissions").EnumerateArray().Select(entry => entry.GetString()).ShouldBe(["run:read"]);

        var list = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/api-tokens", Admin(user, org));
        list.Status.ShouldBe(HttpStatusCode.OK);
        list.Body.GetRawText().ShouldNotContain(secret);
        list.Body.GetRawText().ShouldNotContain(secret[^43..]);
        var item = list.Body.GetProperty("items")[0];
        item.GetProperty("status").GetString().ShouldBe("active");
        item.GetProperty("prefix").GetString().ShouldBe(prefix);

        // `created_by` is the creator's MEMBERSHIP, not the platform user: the row's `api_token_created_by_fk` is
        // (org_id, created_by) → membership (org_id, id), and a founder's membership id is its own value. The founder
        // here comes from the real POST /v1/orgs path, so the two ids differ and this assertion is not vacuous.
        var membership = await MembershipIdAsync(user, org);
        membership.ShouldNotBe(
            user,
            "the founder's membership must have its own id, or asserting the membership id proves nothing");
        item.GetProperty("created_by").GetGuid().ShouldBe(membership);
        (await _fixture.ScalarAsync<Guid>(_database!, "select created_by from public.api_token")).ShouldBe(membership);
        item.GetProperty("last_used_at").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);

        // Nothing of the secret reaches the row: not the plaintext, not its prefix columns, not the label.
        (await _fixture.ScalarAsync<long>(_database!, $"select count(*) from public.api_token where position('{secret[^43..]}' in name || prefix) > 0"))
            .ShouldBe(0);
        (await _fixture.ScalarAsync<int>(_database!, "select octet_length(token_hmac) from public.api_token")).ShouldBe(32);
        (await _fixture.ScalarAsync<string>(_database!, "select array_to_string(permissions, ',') from public.api_token")).ShouldBe("run:read");
        (await _fixture.ScalarAsync<int>(_database!, "select pepper_version from public.api_token")).ShouldBe(2);
        (await _fixture.ScalarAsync<bool>(_database!, "select expires_at > now() + interval '29 days' from public.api_token")).ShouldBeTrue();
    }

    [Fact]
    public async Task ApiToken_CreateIsNotIdempotent_TwoCreatesMintTwoSecrets()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var body = new { name = "same body", permissions = ReadOnly, expires_at = In(30) };

        // The setup's org and project creates are idempotent commands and claim their own keys, so the baseline is
        // whatever they left behind; the API-token create must not add to it.
        var baseline = await IdempotencyRowsAsync(org);
        var first = await CreateAsync(client, user, org, project, body);
        var second = await CreateAsync(client, user, org, project, body);

        first.Status.ShouldBe(HttpStatusCode.OK);
        second.Status.ShouldBe(HttpStatusCode.OK);
        first.Id().ShouldNotBe(second.Id());
        first.Text("prefix").ShouldNotBe(second.Text("prefix"));
        first.Text("token").ShouldNotBe(second.Text("token"));
        (await _fixture.ScalarAsync<long>(_database!, "select count(*) from public.api_token")).ShouldBe(2);

        // A retried create mints a second token; nothing remembers the secret, so no idempotency row is written.
        (await IdempotencyRowsAsync(org)).ShouldBe(baseline);
    }

    [Fact]
    public async Task ApiToken_Expiry_IsMandatoryInTheFutureAndWithinTheMaximum()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;

        var missing = await CreateAsync(client, user, org, project, new { name = "no expiry", permissions = ReadOnly });
        missing.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        missing.ErrorCode.ShouldBe("schema_invalid");
        missing.Body.GetProperty("error").GetProperty("details")[0].GetProperty("path").GetString().ShouldBe("expires_at");

        foreach (var expiresAt in InvalidExpiries)
        {
            var past = await CreateAsync(client, user, org, project, new { name = "past", permissions = ReadOnly, expires_at = expiresAt });
            past.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
            past.ErrorCode.ShouldBe("schema_invalid");
        }

        // The default maximum is 365 days; one day past it is refused, one day inside it is accepted.
        var beyond = await CreateAsync(client, user, org, project, new { name = "too long", permissions = ReadOnly, expires_at = DateTimeOffset.UtcNow.AddDays(366) });
        beyond.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        beyond.ErrorCode.ShouldBe("schema_invalid");
        beyond.Body.GetProperty("error").GetProperty("details")[0].GetProperty("reason").GetString().ShouldBe("out_of_range");

        (await CreateAsync(client, user, org, project, new { name = "at the edge", permissions = ReadOnly, expires_at = In(364) }))
            .Status.ShouldBe(HttpStatusCode.OK);

        // The bound is configuration, not a constant in the handler.
        await using var narrow = TenancyApiFactory.Ci(
            _fixture.AppConnectionString(_database!),
            new Dictionary<string, string?> { ["EVIDGATE:ApiToken:MaxLifetimeDays"] = "7" });
        using var narrowClient = narrow.CreateClient();
        (await CreateAsync(narrowClient, user, org, project, new { name = "narrow", permissions = ReadOnly, expires_at = In(30) }))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);

        (await _fixture.ScalarAsync<long>(_database!, "select count(*) from public.api_token")).ShouldBe(1);
    }

    [Fact]
    public async Task ApiToken_Create_RefusesDeniedUnknownAndEscalatingPermissions()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;

        var denied = await CreateAsync(client, user, org, project, new { name = "denied", permissions = DeniedSet, expires_at = In(30) });
        denied.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        denied.ErrorCode.ShouldBe("forbidden_token_permission");
        denied.Body.GetProperty("error").GetProperty("details")[0].GetProperty("path").GetString().ShouldBe("permissions");

        var unknown = await CreateAsync(client, user, org, project, new { name = "unknown", permissions = UnknownSet, expires_at = In(30) });
        unknown.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        unknown.ErrorCode.ShouldBe("unknown_permission");

        var empty = await CreateAsync(client, user, org, project, new { name = "empty", permissions = NoPermissions, expires_at = In(30) });
        empty.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        empty.ErrorCode.ShouldBe("schema_invalid");

        // The creator is a tenant admin; healing:triage is assignable, outside the deny set and not held by that role,
        // so this is the self-escalation path and not the deny set.
        var escalation = await CreateAsync(client, user, org, project, new { name = "escalating", permissions = EscalatingSet, expires_at = In(30) });
        escalation.Status.ShouldBe(HttpStatusCode.Forbidden);
        escalation.ErrorCode.ShouldBe("permission_escalation");

        var allowed = await CreateAsync(client, user, org, project, new { name = "allowed", permissions = TwoAllowed, expires_at = In(30) });
        allowed.Status.ShouldBe(HttpStatusCode.OK);
        (await _fixture.ScalarAsync<long>(_database!, "select count(*) from public.api_token")).ShouldBe(1);
    }

    [Fact]
    public async Task ApiToken_List_NeedsTheReadPermission_AndNeverCrossesTheTenant()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        (await CreateAsync(client, user, org, project, new { name = "listed", permissions = ReadOnly, expires_at = In(30) }))
            .Status.ShouldBe(HttpStatusCode.OK);

        var member = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            _database!,
            $"""
            insert into public."user" (id, email) values ('{member}', 'member@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status) values (gen_random_uuid(), '{org}', '{member}', 'member@example.test', 'active')
            """);
        var forbidden = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/api-tokens", Admin(member, org));
        forbidden.Status.ShouldBe(HttpStatusCode.Forbidden);
        forbidden.ErrorCode.ShouldBe("forbidden");

        var userB = Guid.CreateVersion7();
        var orgB = await CreateOrgAsync(client, userB, "other-org");
        var foreign = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/api-tokens", Admin(userB, orgB));
        foreign.Status.ShouldBe(HttpStatusCode.OK);
        foreign.Body.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task ApiToken_Revoke_IsAuditedOnceAndIdempotent()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var created = await CreateAsync(client, user, org, project, new { name = "short lived", permissions = ReadOnly, expires_at = In(30) });
        var id = created.Id();

        (await client.SendAsync(HttpMethod.Delete, $"/v1/api-tokens/{id}", Admin(user, org))).Status.ShouldBe(HttpStatusCode.NoContent);
        (await _fixture.ScalarAsync<long>(_database!, $"select count(*) from public.audit_log where action = 'api_token.revoke' and actor_type = 'user'"))
            .ShouldBe(1);
        var list = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/api-tokens", Admin(user, org));
        list.Body.GetProperty("items")[0].GetProperty("status").GetString().ShouldBe("revoked");

        // A second revoke changes nothing, so it writes no second audit row; an unknown id is a plain 404.
        (await client.SendAsync(HttpMethod.Delete, $"/v1/api-tokens/{id}", Admin(user, org))).Status.ShouldBe(HttpStatusCode.NoContent);
        (await _fixture.ScalarAsync<long>(_database!, "select count(*) from public.audit_log where action = 'api_token.revoke'")).ShouldBe(1);
        (await client.SendAsync(HttpMethod.Delete, $"/v1/api-tokens/{Guid.CreateVersion7()}", Admin(user, org))).Status.ShouldBe(HttpStatusCode.NotFound);

        // A revoked token is never resurrected by the plaintext: the row keeps revoked_at and the verifier is T2's.
        (await _fixture.ScalarAsync<bool>(_database!, $"select revoked_at is not null from public.api_token where id = '{id}'")).ShouldBeTrue();

        // Revoked in the same breath it was minted: `created_at` is the database's clock and this request's clock
        // answered earlier, so the stored instant is clamped to the row's creation instead of tripping
        // `api_token_revoked_after_created_check`.
        (await _fixture.ScalarAsync<bool>(
                _database!,
                $"select revoked_at >= created_at from public.api_token where id = '{id}'"))
            .ShouldBeTrue("a revocation can never precede the creation it ends");
    }

    [Fact]
    public async Task ApiToken_CrossTenantRevoke_IsNotFound_AndCreateNamesAnUnknownProject()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var id = (await CreateAsync(client, user, org, project, new { name = "mine", permissions = ReadOnly, expires_at = In(30) })).Id();

        var userB = Guid.CreateVersion7();
        var orgB = await CreateOrgAsync(client, userB, "rival-org");
        (await client.SendAsync(HttpMethod.Delete, $"/v1/api-tokens/{id}", Admin(userB, orgB))).Status.ShouldBe(HttpStatusCode.NotFound);

        (await CreateAsync(client, user, org, Guid.CreateVersion7(), new { name = "ghost", permissions = ReadOnly, expires_at = In(30) }))
            .Status.ShouldBe(HttpStatusCode.NotFound);
        (await _fixture.ScalarAsync<bool>(_database!, $"select revoked_at is null from public.api_token where id = '{id}'")).ShouldBeTrue();
    }

    [Fact]
    public async Task ApiToken_Create_RefusesABodyOverTheRouteCap()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;

        // The route caps the create body at 16 KiB and refuses it before parsing: 32 codes of at most 64 characters
        // plus a 120-character name is a few KiB, so this is a request no legal create could send.
        var oversize = await client.SendRawAsync(
            HttpMethod.Post,
            $"/v1/projects/{project}/api-tokens",
            Admin(user, org),
            $$"""{"name":"{{new string('x', 20 * 1024)}}","permissions":["run:read"],"expires_at":"{{In(30):O}}"}""");

        oversize.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        oversize.ErrorCode.ShouldBe("payload_too_large");
        (await _fixture.ScalarAsync<long>(_database!, "select count(*) from public.api_token")).ShouldBe(0);

        // The control: the same route accepts the same body under the cap, so the refusal is about its size.
        (await CreateAsync(client, user, org, project, new { name = "within the cap", permissions = ReadOnly, expires_at = In(30) }))
            .Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ApiToken_Create_IsAuditedWithoutTheSecret()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var secret = (await CreateAsync(client, user, org, project, new { name = "audited", permissions = ReadOnly, expires_at = In(30) })).Text("token")!;

        (await _fixture.ScalarAsync<string>(
                _database!,
                $"select action || '|' || actor_type from public.audit_log where action = 'api_token.create'"))
            .ShouldBe("api_token.create|user");
        (await _fixture.ScalarAsync<long>(
                _database!,
                $"select count(*) from public.audit_log where metadata::text like '%{secret[^43..]}%' or metadata::text like '%{secret}%'"))
            .ShouldBe(0);
    }

    // ---------------------------------------------------------------- helpers

    private string? _database;

    /// <summary>The id of the membership a user holds in an org — the row a `created_by` column names.</summary>
    /// <param name="user">The account.</param>
    /// <param name="org">The tenant.</param>
    private Task<Guid> MembershipIdAsync(Guid user, Guid org) =>
        _fixture.ScalarAsync<Guid>(
            _database!,
            $"select id from public.membership where org_id = '{org}' and user_id = '{user}'");

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

    private static DateTimeOffset In(int days) => DateTimeOffset.UtcNow.AddDays(days);

    private Task<long> IdempotencyRowsAsync(Guid org) =>
        _fixture.ScalarAsync<long>(_database!, $"select count(*) from public.idempotency_record where org_id = '{org}'");

    private static Action<System.Net.Http.Headers.HttpRequestHeaders> Admin(Guid user, Guid org) =>
        TenancyApiFactory.Resolved(user: user, orgHeader: org);

    private static Task<ApiAnswer> CreateAsync(HttpClient client, Guid user, Guid org, Guid project, object body) =>
        client.SendAsync(HttpMethod.Post, $"/v1/projects/{project}/api-tokens", Admin(user, org), body);
}
