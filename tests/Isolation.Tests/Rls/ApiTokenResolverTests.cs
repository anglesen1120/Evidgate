using System.Globalization;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// The pre-tenant resolver of a project API token, asserted against the migrated schema: it answers the
/// documented tuple (never a column of the table it reads), derives the three states from the row, is
/// executable by <c>evidgate_app</c> alone with PUBLIC closed, and opens no direct read of its own — the
/// table stays org-scoped for the app role, so an unset tenant setting yields zero rows while the
/// resolver still resolves the prefix it was given.
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class ApiTokenResolverTests : HarnessTest
{
    private const string ResolverSignature = "evidgate_resolve.resolve_api_token(text)";

    /// <summary>Binds the test to the shared harness fixture (container, migrations, Respawn).</summary>
    public ApiTokenResolverTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// The active half: one row per prefix, exactly the four documented columns, the tenant tuple of the
    /// token and its derived status — and no second row, so the prefix is the key the bearer handler can
    /// trust.
    /// </summary>
    [Fact]
    public async Task Resolver_ActiveToken_ReturnsTheDocumentedTuple()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var token = await SeedTokenAsync(org, project, Past, Future, revokedAt: null);

        await using var connection = await Fixture.AppDataSource.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "select * from evidgate_resolve.resolve_api_token(@prefix)";
        command.Parameters.AddWithValue("prefix", token.Prefix);
        await using var reader = await command.ExecuteReaderAsync(Token);

        (await reader.ReadAsync(Token)).ShouldBeTrue($"the resolver returned no row for prefix {token.Prefix}");
        reader.FieldCount.ShouldBe(4);
        reader.GetName(0).ShouldBe("org_id");
        reader.GetName(1).ShouldBe("project_id");
        reader.GetName(2).ShouldBe("token_id");
        reader.GetName(3).ShouldBe("status");
        reader.GetGuid(0).ShouldBe(org);
        reader.GetGuid(1).ShouldBe(project);
        reader.GetGuid(2).ShouldBe(token.Id);
        reader.GetString(3).ShouldBe("active");
        (await reader.ReadAsync(Token)).ShouldBeFalse(
            "the prefix is unique platform-wide: a second row means the lookup key is not");
    }

    /// <summary>
    /// The derived states: <c>revoked_at</c> wins over an expired window, because a revoked token answers
    /// <c>token_revoked</c> however long ago it expired — the handler maps the status it is given.
    /// </summary>
    [Fact]
    public async Task Resolver_DerivesExpiredAndRevokedFromTheRow()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);

        var active = await SeedTokenAsync(org, project, Past, Future, revokedAt: null);
        var expired = await SeedTokenAsync(org, project, Past, Past.AddDays(1), revokedAt: null);
        var revoked = await SeedTokenAsync(org, project, Past, Future, Past.AddDays(2));
        var revokedAndExpired = await SeedTokenAsync(org, project, Past, Past.AddDays(1), Past.AddDays(2));

        (await StatusAsync(active.Prefix)).ShouldBe("active");
        (await StatusAsync(expired.Prefix)).ShouldBe("expired");
        (await StatusAsync(revoked.Prefix)).ShouldBe("revoked");
        (await StatusAsync(revokedAndExpired.Prefix)).ShouldBe(
            "revoked",
            "a revoked token reports revoked even once its window has also passed");
    }

    /// <summary>An unknown prefix resolves nothing — the handler answers <c>token_unknown</c> without a table read.</summary>
    [Fact]
    public async Task Resolver_UnknownPrefix_ReturnsNoRow()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        await SeedTokenAsync(org, project, Past, Future, revokedAt: null);

        await using var connection = await Fixture.AppDataSource.OpenConnectionAsync(Token);
        (await ScalarAsync<long>(
            connection,
            "select count(*) from evidgate_resolve.resolve_api_token(@prefix)",
            ("prefix", NewPrefix()))).ShouldBe(0L);
    }

    /// <summary>
    /// TL T1/T80: the resolver is the app role's alone. EXECUTE for <c>evidgate_app</c>, nothing for the
    /// operator or the scheduler role, and no PUBLIC grantee at all — a definer that a role outside the
    /// runtime path could call would be a second way across the tenant boundary.
    /// </summary>
    [Fact]
    public async Task Resolver_IsAppRoleExecuteOnly()
    {
        await using var connection = await Fixture.AppDataSource.OpenConnectionAsync(Token);

        (await ScalarAsync<long>(
            connection,
            "select count(*) from pg_catalog.pg_proc p cross join lateral "
            + "aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a "
            + "where p.oid = @signature::regprocedure and a.grantee = 0 and a.privilege_type = 'EXECUTE'",
            ("signature", ResolverSignature)))
            .ShouldBe(0L, "PUBLIC still has EXECUTE on the resolver");

        (await ScalarAsync<bool>(
            connection,
            "select has_function_privilege('evidgate_app', @signature::regprocedure, 'execute')",
            ("signature", ResolverSignature)))
            .ShouldBeTrue("evidgate_app must be able to resolve a prefix");

        foreach (var role in new[] { "evidgate_operator", "evidgate_worker_sched" })
        {
            (await ScalarAsync<bool>(
                connection,
                $"select has_function_privilege('{role}', @signature::regprocedure, 'execute')",
                ("signature", ResolverSignature)))
                .ShouldBeFalse($"{role} must not call the resolver");
        }
    }

    /// <summary>
    /// The resolver does not widen a direct read. With no tenant setting the app role sees zero rows of the
    /// table, while the resolver answered the row above; under one org's scope it sees that org's token and
    /// never the other org's.
    /// </summary>
    [Fact]
    public async Task Table_UnsetTenantSetting_ZeroRows_AndScopedReadSeesItsOwnToken()
    {
        var (firstOrg, firstProject) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var (secondOrg, secondProject) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var first = await SeedTokenAsync(firstOrg, firstProject, Past, Future, revokedAt: null);
        var second = await SeedTokenAsync(secondOrg, secondProject, Past, Future, revokedAt: null);

        await using (var connection = await Fixture.AppDataSource.OpenConnectionAsync(Token))
        {
            (await ScalarAsync<long>(connection, "select count(*) from public.api_token"))
                .ShouldBe(0L, "an unset tenant setting must yield zero rows, never every tenant's tokens");
        }

        await AssertScopedAsync(firstOrg, first.Id);
        await AssertScopedAsync(secondOrg, second.Id);
    }

    /// <summary>
    /// The write guard of the table: the credential and its grant are written once, so a later expiry
    /// extension or permission widening is refused (23514) instead of silently escalating the token, and a
    /// revocation is terminal — no un-revoke, no re-revoke. The rename is the positive control: the same
    /// statement path succeeds when only a display field changes.
    /// </summary>
    [Fact]
    public async Task Guard_RefusesCredentialRewrite_And_Unrewrite()
    {
        var (org, project) = await Fixture.SeedProjectAsync(cancellationToken: Token);
        var token = await SeedTokenAsync(org, project, Past, Future, revokedAt: null);

        await using (var owner = await TenantScope.BeginAsync(Fixture.OwnerConnectionString))
        {
            (await owner.ExecuteAsync("update public.api_token set name = 'renamed' where id = @id", ("id", token.Id)))
                .ShouldBe(1, "renaming a token is the positive control: the guard must not refuse every update");
            await owner.CommitAsync();
        }

        await AssertRefusedAsync(
            "update public.api_token set permissions = array['run:write'] where id = @id",
            token.Id,
            "widening the stored grant");
        await AssertRefusedAsync(
            "update public.api_token set expires_at = @later where id = @id",
            token.Id,
            "extending the expiry past the lifetime the create command validated",
            ("later", Future.AddYears(5)));

        await using (var owner = await TenantScope.BeginAsync(Fixture.OwnerConnectionString))
        {
            (await owner.ExecuteAsync("update public.api_token set revoked_at = now() where id = @id", ("id", token.Id)))
                .ShouldBe(1, "the first revocation must succeed");
            await owner.CommitAsync();
        }

        await AssertRefusedAsync(
            "update public.api_token set revoked_at = null where id = @id",
            token.Id,
            "un-revoking a token");
        await AssertRefusedAsync(
            "update public.api_token set revoked_at = @later where id = @id",
            token.Id,
            "rewriting a revocation",
            ("later", Past));
    }

    /// <summary>When a seeded token's window ended: later than <see cref="Past"/>, as the expiry CHECK demands.</summary>
    private static DateTimeOffset Past => new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A lifetime past any create-time maximum, so a seeded active token is never expired by the clock.</summary>
    private static DateTimeOffset Future => DateTimeOffset.UtcNow.AddDays(30);

    private static string NewPrefix() =>
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];

    /// <summary>
    /// Seeds the creator's membership: <c>created_by</c> names the tenant-scoped actor, so the composite key
    /// <c>(org_id, created_by) -> membership (org_id, id)</c> can only match a member of the token's own org.
    /// </summary>
    private async Task<Guid> SeedMembershipAsync(Guid org)
    {
        var id = Guid.CreateVersion7();
        await Fixture.SeedAsync(
            "insert into public.membership (id, org_id, invited_email, status) values (@id, @org_id, @email, 'active')",
            [("id", id), ("org_id", org), ("email", $"member+{id:N}@example.test")],
            Token);
        return id;
    }

    /// <summary>Seeds one token row of the given state, as the owner (arranging rows is the harness seam).</summary>
    private async Task<SeededToken> SeedTokenAsync(
        Guid org,
        Guid project,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        DateTimeOffset? revokedAt)
    {
        var token = new SeededToken(Guid.CreateVersion7(), NewPrefix());
        await Fixture.SeedAsync(
            """
            insert into public.api_token
              (id, org_id, project_id, name, prefix, token_hmac, pepper_version, permissions,
               expires_at, revoked_at, created_by, created_at)
            values
              (@id, @org_id, @project_id, @name, @prefix, @hmac, 1, array['run:read'],
               @expires_at::timestamptz, @revoked_at::timestamptz, @created_by, @created_at::timestamptz)
            """,
            [
                ("id", token.Id),
                ("org_id", org),
                ("project_id", project),
                ("name", "probe " + token.Prefix),
                ("prefix", token.Prefix),
                ("hmac", new byte[32]),
                ("expires_at", expiresAt),
                ("revoked_at", (object?)revokedAt ?? DBNull.Value),
                ("created_by", await SeedMembershipAsync(org)),
                ("created_at", createdAt),
            ],
            Token);
        return token;
    }

    /// <summary>Calls the resolver for one prefix and returns the status it answers.</summary>
    private async Task<string> StatusAsync(string prefix)
    {
        await using var connection = await Fixture.AppDataSource.OpenConnectionAsync(Token);
        return await ScalarAsync<string>(
            connection,
            "select status from evidgate_resolve.resolve_api_token(@prefix)",
            ("prefix", prefix));
    }

    /// <summary>Asserts that one org's scope reads exactly its own token and nothing of the other org's.</summary>
    private async Task AssertScopedAsync(Guid org, Guid expectedToken)
    {
        await using var scope = await TenantScope.BeginAsync(Fixture.AppConnectionString, org);
        (await scope.ScalarAsync<long>("select count(*) from public.api_token")).ShouldBe(1L);
        (await scope.ScalarAsync<Guid>("select id from public.api_token")).ShouldBe(expectedToken);
    }

    /// <summary>Runs one update the write guard must refuse, in a transaction of its own (an error aborts it).</summary>
    private async Task AssertRefusedAsync(
        string sql,
        Guid token,
        string because,
        params (string Name, object Value)[] extra)
    {
        await using var owner = await TenantScope.BeginAsync(Fixture.OwnerConnectionString);
        var error = await owner.CaptureErrorAsync(sql, [("id", token), .. extra]);
        error.SqlState.ShouldBe("23514", $"the write guard must refuse {because}: {error.MessageText}");
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] values)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private sealed record SeededToken(Guid Id, string Prefix);
}
