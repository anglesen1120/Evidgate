using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

[Collection(ExclusiveTenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class ApiTokenBoundaryTests(PostgresTenancyFixture fixture) : IAsyncLifetime
{
    private static readonly string[] ReadOnly = ["run:read"];
    private DedicatedDatabase? _database;
    private string Database => _database?.Name ?? throw new InvalidOperationException("Test database is not initialized");

    public async ValueTask InitializeAsync() => _database = await fixture.CreateDedicatedDatabaseAsync();

    public async ValueTask DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("healing:triage", HttpStatusCode.Forbidden, "permission_escalation")]
    [InlineData("api_token:read", HttpStatusCode.UnprocessableEntity, "forbidden_token_permission")]
    public async Task Create_UnheldOrDeniedPermission_RefusesWithoutMinting(
        string permission, HttpStatusCode status, string code)
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        using var __ = client;
        var denied = await CreateAsync(client, user, org, project,
            new { name = "restricted", permissions = new[] { "run:read", permission }, expires_at = Future() });
        denied.Status.ShouldBe(status);
        denied.ErrorCode.ShouldBe(code);
        (await CountAsync()).ShouldBe(0);
        (await CreateAsync(client, user, org, project,
            new { name = "restricted", permissions = ReadOnly, expires_at = Future() })).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Create_ExpiryBounds_RejectsMissingPastAndOverMaximum_AcceptsExactMaximum()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        using var __ = client;
        var clock = new FakeTimeProvider(TimeProvider.System.GetUtcNow().AddMinutes(1));
        await using var frozen = api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));
        using var frozenClient = frozen.CreateClient();
        var missing = await CreateAsync(frozenClient, user, org, project, new { name = "expiry", permissions = ReadOnly });
        missing.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        missing.ErrorCode.ShouldBe("schema_invalid");
        foreach (var expiry in new[] { clock.GetUtcNow().AddTicks(-1), clock.GetUtcNow(), clock.GetUtcNow().AddDays(365).AddTicks(1) })
        {
            var refused = await CreateAsync(frozenClient, user, org, project,
                new { name = "expiry", permissions = ReadOnly, expires_at = expiry });
            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
            refused.ErrorCode.ShouldBe("schema_invalid");
        }

        (await CountAsync()).ShouldBe(0);
        var maximum = clock.GetUtcNow().AddDays(365);
        var accepted = await CreateAsync(frozenClient, user, org, project,
            new { name = "expiry", permissions = ReadOnly, expires_at = maximum });
        accepted.Status.ShouldBe(HttpStatusCode.OK);
        accepted.Body.GetProperty("expires_at").GetDateTimeOffset().ShouldBe(maximum);
        (await CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Create_IdenticalBody_MintsTwoIndependentTokensWithoutReplayStorage()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        using var __ = client;
        var body = new { name = "retry", permissions = ReadOnly, expires_at = Future() };
        var baseline = await IdempotencyCountAsync(org);
        var first = await CreateAsync(client, user, org, project, body);
        var second = await CreateAsync(client, user, org, project, body);
        first.Status.ShouldBe(HttpStatusCode.OK);
        second.Status.ShouldBe(HttpStatusCode.OK);
        first.Id().ShouldNotBe(second.Id());
        first.Text("token").ShouldNotBe(second.Text("token"));
        (await CountAsync()).ShouldBe(2);
        (await IdempotencyCountAsync(org)).ShouldBe(baseline);
    }

    [Theory]
    [InlineData("00000000000000000000000000000001")]
    [InlineData("not-a-valid-idempotency-key")]
    [InlineData("")]
    public async Task Create_SameIdempotencyHeaderWithDifferentBody_MintsNewTokenInsteadOfReplayOrConflict(string key)
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        using var __ = client;
        var baseline = await IdempotencyCountAsync(org);
        var first = await client.SendAsync(HttpMethod.Post, $"/v1/projects/{project}/api-tokens", Admin(user, org),
            new { name = "first", permissions = ReadOnly, expires_at = Future() }, key);
        var second = await client.SendAsync(HttpMethod.Post, $"/v1/projects/{project}/api-tokens", Admin(user, org),
            new { name = "different", permissions = ReadOnly, expires_at = Future() }, key);
        first.Status.ShouldBe(HttpStatusCode.OK);
        second.Status.ShouldBe(HttpStatusCode.OK);
        second.Text("name").ShouldBe("different");
        first.Id().ShouldNotBe(second.Id());
        first.Text("token").ShouldNotBe(second.Text("token"));
        (await CountAsync()).ShouldBe(2);
        (await IdempotencyCountAsync(org)).ShouldBe(baseline);
    }

    [Fact]
    public async Task List_MemberWithoutReadPermission_IsForbiddenWhileAdminCanRead()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        using var __ = client;
        var seeded = await SeedTokenAsync(user, org, project);
        var member = await AddMemberAsync(fixture, client, Database, org, Admin(user, org), roleCode: null);
        var denied = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/api-tokens", Admin(member, org));
        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        denied.ErrorCode.ShouldBe("forbidden");
        var allowed = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/api-tokens", Admin(user, org));
        allowed.Status.ShouldBe(HttpStatusCode.OK);
        allowed.Body.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldBe(seeded.Id);
    }

    [Fact]
    public async Task ListAndRevoke_ForeignTenant_ReturnsEmptyPageAndNotFoundWithoutMutation()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        using var __ = client;
        var seeded = await SeedTokenAsync(user, org, project);
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(client, otherUser, "other-tenant");
        var foreign = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/api-tokens", Admin(otherUser, otherOrg));
        foreign.Status.ShouldBe(HttpStatusCode.OK);
        foreign.Body.GetProperty("items").GetArrayLength().ShouldBe(0);
        foreign.Body.GetProperty("next_cursor").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        (await client.SendAsync(HttpMethod.Delete, $"/v1/api-tokens/{seeded.Id}", Admin(otherUser, otherOrg)))
            .Status.ShouldBe(HttpStatusCode.NotFound);
        (await fixture.ScalarAsync<bool>(Database, $"select revoked_at is null from public.api_token where id = '{seeded.Id}'"))
            .ShouldBeTrue();
        (await client.SendAsync(HttpMethod.Delete, $"/v1/api-tokens/{seeded.Id}", Admin(user, org)))
            .Status.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Revoke_ResolverNextReadAtSameDatabaseInstant_ReportsRevokedWithoutWaiting()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        using var __ = client;
        var seeded = await SeedTokenAsync(user, org, project);
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString(Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var before = await ResolveAsync(connection, seeded.Prefix);
        before.Status.ShouldBe("active");
        (await client.SendAsync(HttpMethod.Delete, $"/v1/api-tokens/{seeded.Id}", Admin(user, org)))
            .Status.ShouldBe(HttpStatusCode.NoContent);
        var after = await ResolveAsync(connection, seeded.Prefix);
        after.Status.ShouldBe("revoked");
        after.DatabaseInstant.ShouldBe(before.DatabaseInstant);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        (await client.SendAsync(HttpMethod.Delete, $"/v1/api-tokens/{seeded.Id}", Admin(user, org)))
            .Status.ShouldBe(HttpStatusCode.NoContent);
        (await fixture.ScalarAsync<long>(Database,
            $"select count(*) from public.audit_log where action = 'api_token.revoke' and object_id = '{seeded.Id}'"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Resolver_ExpiredRow_ReportsExpiredAndActiveControlRemainsActive()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        using var __ = client;
        var expired = await SeedTokenAsync(user, org, project, expired: true);
        var active = await SeedTokenAsync(user, org, project);
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString(Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        (await ResolveAsync(connection, expired.Prefix)).Status.ShouldBe("expired");
        (await ResolveAsync(connection, active.Prefix)).Status.ShouldBe("active");
    }

    [Fact]
    public async Task Resolver_ProjectAToken_NeverResolvesToProjectB()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        using var __ = client;
        var otherProject = await CreateProjectAsync(client, user, org, "other-project");
        var first = await SeedTokenAsync(user, org, project);
        var second = await SeedTokenAsync(user, org, otherProject);
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString(Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var resolvedFirst = await ResolveAsync(connection, first.Prefix);
        var resolvedSecond = await ResolveAsync(connection, second.Prefix);
        resolvedFirst.Project.ShouldBe(project);
        resolvedFirst.Project.ShouldNotBe(otherProject);
        resolvedSecond.Project.ShouldBe(otherProject);
        resolvedFirst.Org.ShouldBe(org);
        resolvedFirst.Token.ShouldBe(first.Id);
    }

    [Fact]
    public async Task CreateAndRevoke_ListAndAuditContainNeitherPlaintextNorEncodedHmac()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        using var __ = client;
        var created = await CreateAsync(client, user, org, project,
            new { name = "secret-free", permissions = ReadOnly, expires_at = Future() });
        created.Status.ShouldBe(HttpStatusCode.OK);
        created.Headers.CacheControl.ShouldNotBeNull();
        created.Headers.CacheControl.NoStore.ShouldBeTrue();
        var id = created.Id();
        var secret = created.Text("token") ?? throw new InvalidOperationException("Token missing from create response");
        var hmacHex = await fixture.ScalarAsync<string>(Database,
            $"select encode(token_hmac, 'hex') from public.api_token where id = '{id}'");
        var hmacBase64 = Convert.ToBase64String(Convert.FromHexString(hmacHex));
        foreach (var revoke in new[] { false, true })
        {
            if (revoke)
            {
                (await client.SendAsync(HttpMethod.Delete, $"/v1/api-tokens/{id}", Admin(user, org)))
                    .Status.ShouldBe(HttpStatusCode.NoContent);
            }

            var listed = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/api-tokens", Admin(user, org));
            listed.Status.ShouldBe(HttpStatusCode.OK);
            var audit = await fixture.ScalarAsync<string>(Database,
                $"select jsonb_agg(to_jsonb(a))::text from public.audit_log a where action in ('api_token.create','api_token.revoke') and org_id = '{org}'");
            foreach (var text in new[] { listed.Body.GetRawText(), audit })
            {
                text.ShouldNotContain(secret);
                text.ShouldNotContain(secret[^43..]);
                text.ShouldNotContain(hmacHex);
                text.ShouldNotContain(hmacBase64);
                text.ShouldNotContain("token_hmac");
            }

            (await fixture.ScalarAsync<long>(Database,
                $"select count(*) from public.audit_log where org_id = '{org}' and action in ('api_token.create','api_token.revoke')"))
                .ShouldBe(revoke ? 2 : 1);
        }
    }

    [Fact]
    public async Task Create_NameAt120Characters_AcceptsWhile121Refuses()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        using var __ = client;
        var refused = await CreateAsync(client, user, org, project,
            new { name = new string('x', 121), permissions = ReadOnly, expires_at = Future() });
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("request_invalid");
        (await CountAsync()).ShouldBe(0);
        var accepted = await CreateAsync(client, user, org, project,
            new { name = new string('x', 120), permissions = ReadOnly, expires_at = Future() });
        accepted.Status.ShouldBe(HttpStatusCode.OK);
        accepted.Text("name").ShouldBe(new string('x', 120));
    }

    [Fact]
    public async Task Create_ZeroOr65Permissions_RefusesWhileOnePermissionAccepts()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        using var __ = client;
        foreach (var permissions in new[] { Array.Empty<string>(), Enumerable.Range(0, 65).Select(index => $"permission{index}:read").ToArray() })
        {
            var refused = await CreateAsync(client, user, org, project,
                new { name = "permission-count", permissions, expires_at = Future() });
            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
            refused.ErrorCode.ShouldBe(permissions.Length == 0 ? "schema_invalid" : "request_invalid");
        }

        (await CountAsync()).ShouldBe(0);
        (await CreateAsync(client, user, org, project,
            new { name = "permission-count", permissions = ReadOnly, expires_at = Future() })).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task List_ValidCursorPagesExactlyOnce_MalformedAndOverflowCursorsRefuse()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        using var __ = client;
        var first = await SeedTokenAsync(user, org, project);
        var second = await SeedTokenAsync(user, org, project);
        var page = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/api-tokens?limit=1", Admin(user, org));
        page.Status.ShouldBe(HttpStatusCode.OK);
        var cursor = page.Text("next_cursor");
        cursor.ShouldNotBeNullOrEmpty();
        var next = await client.SendAsync(HttpMethod.Get,
            $"/v1/projects/{project}/api-tokens?limit=1&cursor={Uri.EscapeDataString(cursor)}", Admin(user, org));
        next.Status.ShouldBe(HttpStatusCode.OK);
        next.Body.GetProperty("items").GetArrayLength().ShouldBe(1);
        var ids = new[] { page.Body.GetProperty("items")[0].GetProperty("id").GetGuid(), next.Body.GetProperty("items")[0].GetProperty("id").GetGuid() };
        ids.Distinct().Count().ShouldBe(2);
        ids.Order().ShouldBe(new[] { first.Id, second.Id }.Order());
        foreach (var tampered in new[] { cursor + "!", "ffffffffffffffff-" + first.Id, "';select pg_sleep(1)--" })
        {
            var refused = await client.SendAsync(HttpMethod.Get,
                $"/v1/projects/{project}/api-tokens?cursor={Uri.EscapeDataString(tampered)}", Admin(user, org));
            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
            refused.ErrorCode.ShouldBe("request_invalid");
        }
    }

    [Fact]
    public async Task Create_CreatorUserAndMembershipHaveDifferentIds_StoresTheMembershipReference()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        using var __ = client;
        var membership = await fixture.ScalarAsync<Guid>(Database,
            $"select id from public.membership where org_id = '{org}' and user_id = '{user}'");
        membership.ShouldNotBe(user);
        var created = await CreateAsync(client, user, org, project,
            new { name = "creator-reference", permissions = ReadOnly, expires_at = Future() });
        created.Status.ShouldBe(HttpStatusCode.OK);
        (await fixture.ScalarAsync<Guid>(Database,
            $"select created_by from public.api_token where id = '{created.Id()}'")).ShouldBe(membership);
        var listed = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/api-tokens", Admin(user, org));
        listed.Status.ShouldBe(HttpStatusCode.OK);
        listed.Body.GetProperty("items")[0].GetProperty("created_by").GetGuid().ShouldBe(membership);
    }

    [Fact]
    public async Task Revoke_ApplicationClockBehindStoredCreation_StillRevokesAndAudits()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        using var __ = client;
        var created = await CreateAsync(client, user, org, project,
            new { name = "clock-skew", permissions = ReadOnly, expires_at = Future() });
        created.Status.ShouldBe(HttpStatusCode.OK);
        var id = created.Id();
        var createdAt = await fixture.ScalarAsync<DateTime>(Database,
            $"select created_at from public.api_token where id = '{id}'");
        var clock = new FakeTimeProvider(new DateTimeOffset(createdAt).AddSeconds(-1));
        await using var skewed = api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));
        using var skewedClient = skewed.CreateClient();
        (await skewedClient.SendAsync(HttpMethod.Delete, $"/v1/api-tokens/{id}", Admin(user, org)))
            .Status.ShouldBe(HttpStatusCode.NoContent);
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString(Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        (await ResolveAsync(connection, created.Text("prefix")!)).Status.ShouldBe("revoked");
        (await fixture.ScalarAsync<long>(Database,
            $"select count(*) from public.audit_log where action = 'api_token.revoke' and object_id = '{id}'"))
            .ShouldBe(1);
    }

    private async Task<(TenancyApiFactory Api, HttpClient Client, Guid User, Guid Org, Guid Project)> StartAsync()
    {
        var api = TenancyApiFactory.Ci(fixture.AppConnectionString(Database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "boundary-org");
        var project = await CreateProjectAsync(client, user, org, "boundary-project");
        return (api, client, user, org, project);
    }

    private async Task<(Guid Id, string Prefix)> SeedTokenAsync(Guid user, Guid org, Guid project, bool expired = false)
    {
        var id = Guid.CreateVersion7();
        var prefix = Guid.NewGuid().ToString("N")[..12];
        var window = expired ? "interval '-1 day'" : "interval '30 days'";
        await fixture.ExecuteAsync(Database, $"""
            insert into public.api_token
              (id, org_id, project_id, name, prefix, token_hmac, pepper_version, permissions, expires_at, created_by, created_at)
            select '{id}', '{org}', '{project}', 'seeded', '{prefix}', decode(repeat('00', 32), 'hex'), 1,
                   array['run:read'], now() + {window}, m.id, now() - interval '2 days'
              from public.membership m where m.org_id = '{org}' and m.user_id = '{user}';
            """);
        return (id, prefix);
    }

    private static async Task<(Guid Org, Guid Project, Guid Token, string Status, DateTime DatabaseInstant)> ResolveAsync(
        NpgsqlConnection connection, string prefix)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select org_id, project_id, token_id, status, now() from evidgate_resolve.resolve_api_token(@prefix)";
        command.Parameters.AddWithValue("prefix", prefix);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        var resolved = (reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetDateTime(4));
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
        return resolved;
    }

    private Task<long> CountAsync() => fixture.ScalarAsync<long>(Database, "select count(*) from public.api_token");

    private Task<long> IdempotencyCountAsync(Guid org) => fixture.ScalarAsync<long>(Database,
        $"select count(*) from public.idempotency_record where org_id = '{org}'");

    private static DateTimeOffset Future() => TimeProvider.System.GetUtcNow().AddDays(30);

    private static Action<HttpRequestHeaders> Admin(Guid user, Guid org) => TenancyApiFactory.Resolved(user: user, orgHeader: org);

    private static Task<ApiAnswer> CreateAsync(HttpClient client, Guid user, Guid org, Guid project, object body) =>
        client.SendAsync(HttpMethod.Post, $"/v1/projects/{project}/api-tokens", Admin(user, org), body);
}
