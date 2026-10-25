using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// Shared arrangement and call seams of the invite-flow schema suite (02.1.2-T2, W1):
/// seeding rows as the owner (00 §5.5 C3 — arranging rows is the one job a test may not
/// do through the tenant path) and calling the two <c>evidgate_resolve</c> lookups and
/// the <c>evidgate</c> sweep through the roles that are allowed to call them.
/// </summary>
/// <remarks>
/// The resolver calls go through <see cref="DatabaseFixture.AppDataSource"/> with no
/// tenant setting at all, which is the pre-tenant situation the functions exist for: the
/// app role must be able to resolve an invite without being able to read the matching
/// <c>public.membership</c> row directly (T75, 00 §5.4.8). The sweep is called through a
/// superuser connection switched to <c>evidgate_worker_sched</c>, because the harness has
/// no password for that role and the point of the test is the real grant, not a
/// substitute one.
/// </remarks>
public abstract class InviteTestBase : CatalogueTestBase
{
    protected InviteTestBase(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>A 12-character public prefix, the shape 02.1.2-T2's mint produces.</summary>
    protected static string Prefix() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>A stand-in digest of the requested length (the real one is an HMAC-SHA256).</summary>
    protected static byte[] Hash(int length = 32)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
        {
            bytes[index] = (byte)(index + 1);
        }

        return bytes;
    }

    /// <summary>Seeds an org with the AC8 opt-in and the status the resolver filters on.</summary>
    protected async Task<Guid> SeedOrgAsync(bool autobindVerifiedDomain, string status = "active")
    {
        var id = Guid.CreateVersion7();

        await Fixture.SeedAsync(
            """
            insert into public.organization (id, name, slug, status, invite_autobind_verified_domain)
            values (@id, @name, @slug, @status, @autobind)
            """,
            [
                ("id", id),
                ("name", "Seeded invite org"),
                ("slug", "inv-" + Guid.NewGuid().ToString("N")[..12]),
                ("status", status),
                ("autobind", autobindVerifiedDomain),
            ],
            Token);

        return id;
    }

    /// <summary>
    /// Seeds one membership row with the invite columns the caller asks for. A <c>null</c>
    /// prefix means "no token at all", so the hash, pepper version and expiry must then be
    /// <c>null</c> too — 0043's all-or-none constraint is what this helper exercises.
    /// </summary>
    protected async Task<Guid> SeedMembershipAsync(
        Guid orgId,
        string email,
        string status = "pending",
        string? prefix = null,
        byte[]? hash = null,
        int? pepperVersion = null,
        DateTimeOffset? expiresAt = null)
    {
        var id = Guid.CreateVersion7();

        await Fixture.SeedAsync(
            """
            insert into public.membership (
              id, org_id, invited_email, status,
              invite_secret_prefix, invite_token_hash, invite_pepper_version, invite_expires_at)
            values (
              @id, @org_id, @email, @status,
              @prefix::text, @hash::bytea, @version::integer, @expires_at::timestamptz)
            """,
            [
                ("id", id),
                ("org_id", orgId),
                ("email", email),
                ("status", status),
                ("prefix", (object?)prefix ?? DBNull.Value),
                ("hash", (object?)hash ?? DBNull.Value),
                ("version", (object?)pepperVersion ?? DBNull.Value),
                ("expires_at", (object?)expiresAt ?? DBNull.Value),
            ],
            Token);

        return id;
    }

    /// <summary>The status of one membership row, read as the owner (arrangement bookkeeping).</summary>
    protected async Task<string> StatusAsync(Guid membershipId) =>
        await ScalarOwnerAsync<string>(
            "select status from public.membership where id = @id",
            ("id", membershipId));

    /// <summary>One scalar on the owner connection.</summary>
    protected async Task<T> ScalarOwnerAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (parameterName, parameterValue) in parameters)
        {
            command.Parameters.AddWithValue(parameterName, parameterValue);
        }

        var value = await command.ExecuteScalarAsync(Token);
        return value is null or DBNull ? default! : (T)value;
    }

    /// <summary>One row of scalars on the owner connection (catalogue reads).</summary>
    protected async Task<string?[]> RowOwnerAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).ShouldBeTrue();

        var values = new string?[reader.FieldCount];
        for (var index = 0; index < reader.FieldCount; index++)
        {
            values[index] = await reader.IsDBNullAsync(index, Token) ? null : reader.GetValue(index)?.ToString();
        }

        return values;
    }

    /// <summary>
    /// <c>evidgate_resolve.invite_by_secret(prefix)</c> as the app role with no tenant
    /// setting: the pair list the pre-tenant login path would see.
    /// </summary>
    protected async Task<IReadOnlyList<(Guid OrgId, Guid MembershipId)>> InviteBySecretAsync(string prefix)
    {
        await using var command = Fixture.AppDataSource.CreateCommand(
            "select org_id, membership_id from evidgate_resolve.invite_by_secret(@prefix::text)");
        command.Parameters.AddWithValue("prefix", prefix);

        var rows = new List<(Guid OrgId, Guid MembershipId)>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            rows.Add((reader.GetGuid(0), reader.GetGuid(1)));
        }

        return rows;
    }

    /// <summary>
    /// <c>evidgate_resolve.invite_autobind_candidates(email)</c> as the app role with no
    /// tenant setting: the orgs the opt-in path would try to bind.
    /// </summary>
    protected async Task<IReadOnlyList<(Guid OrgId, Guid MembershipId)>> AutobindCandidatesAsync(string email)
    {
        await using var command = Fixture.AppDataSource.CreateCommand(
            "select org_id, membership_id from evidgate_resolve.invite_autobind_candidates(@email::text)");
        command.Parameters.AddWithValue("email", email);

        var rows = new List<(Guid OrgId, Guid MembershipId)>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            rows.Add((reader.GetGuid(0), reader.GetGuid(1)));
        }

        return rows;
    }

    /// <summary>Opens a connection switched to <c>evidgate_worker_sched</c> (the T80 sweep role).</summary>
    protected async Task<NpgsqlConnection> OpenSweepConnectionAsync()
    {
        var database = new NpgsqlConnectionStringBuilder(Fixture.AppConnectionString).Database!;
        var connection = await Fixture.OpenSuperuserAsync(database, Token);

        await using var switchRole = connection.CreateCommand();
        switchRole.CommandText = "set role evidgate_worker_sched";
        await switchRole.ExecuteNonQueryAsync(Token);

        return connection;
    }

    /// <summary>Calls <c>evidgate.expire_pending_invites(limit)</c> and returns the rows it swept.</summary>
    protected static async Task<int> SweepAsync(NpgsqlConnection connection, int? limit)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select evidgate.expire_pending_invites(@limit::integer)";
        command.Parameters.AddWithValue("limit", (object?)limit ?? DBNull.Value);

        return (int)(await command.ExecuteScalarAsync(Token))!;
    }
}

/// <summary>
/// 02.1.2-T2 (W1) / 02.1.2 §4, §6, §7: the schema the invite flow rests on — the pepper
/// version column, the all-or-none rule that makes a half-minted invite impossible, the
/// AC8 opt-in column on <c>organization</c>, and the bounded expiry sweep of 02.1.2 §7
/// with its T80 privilege scoping.
/// </summary>
/// <remarks>
/// <c>pending → expired</c> needs no new state: 0011's <c>membership_status_check</c>
/// already admits <c>expired</c> and declares no transition trigger, so the sweep writes
/// the transition directly. These tests therefore pin behaviour, not DDL that does not
/// exist.
/// </remarks>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class InviteSchemaTests : InviteTestBase
{
    public InviteSchemaTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// 0043 adds <c>membership.invite_pepper_version</c> (T5: the version travels with the
    /// hash) and <c>organization.invite_autobind_verified_domain</c> (AC8's opt-in, NOT NULL
    /// and defaulting to off so no existing org silently opts in).
    /// </summary>
    [Fact]
    public async Task Invite_Columns_Are_Declared_With_Their_Defaults()
    {
        var pepper = await RowOwnerAsync(
            """
            select data_type, is_nullable, column_default
              from information_schema.columns
             where table_schema = 'public' and table_name = 'membership' and column_name = 'invite_pepper_version'
            """);

        pepper[0].ShouldBe("integer");
        pepper[1].ShouldBe("YES", "a row without a minted token has no pepper version (all-or-none)");

        var autobind = await RowOwnerAsync(
            """
            select data_type, is_nullable, column_default
              from information_schema.columns
             where table_schema = 'public' and table_name = 'organization'
               and column_name = 'invite_autobind_verified_domain'
            """);

        autobind[0].ShouldBe("boolean");
        autobind[1].ShouldBe("NO");
        autobind[2].ShouldBe("false", "AC8 is an opt-in: off by default");
    }

    /// <summary>
    /// The all-or-none rule: the three token columns move together, the expiry the resolver
    /// filters on is mandatory with them, and the digest length is a real HMAC-SHA256.
    /// </summary>
    [Fact]
    public async Task Half_Set_Invite_Token_Is_Rejected()
    {
        var orgId = await SeedOrgAsync(autobindVerifiedDomain: false);
        var live = DateTimeOffset.UtcNow.AddDays(1);

        var definition = await ScalarOwnerAsync<string>(
            """
            select pg_get_constraintdef(con.oid)
              from pg_constraint con
              join pg_class c on c.oid = con.conrelid
             where c.relname = 'membership' and con.conname = 'membership_invite_token_all_or_none'
            """);

        foreach (var column in new[]
                 {
                     "invite_secret_prefix", "invite_token_hash", "invite_pepper_version", "invite_expires_at",
                 })
        {
            definition.ShouldContain(column, Case.Insensitive);
        }

        // A prefix with no hash is a lookup key that can never verify.
        var prefixOnly = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await SeedMembershipAsync(orgId, "prefix-only@example.com", prefix: Prefix());
        });
        prefixOnly.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        prefixOnly.ConstraintName.ShouldBe("membership_invite_token_all_or_none");

        // A token without an expiry is a permanent credential.
        var noExpiry = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await SeedMembershipAsync(
                orgId, "no-expiry@example.com", prefix: Prefix(), hash: Hash(), pepperVersion: 1);
        });
        noExpiry.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        noExpiry.ConstraintName.ShouldBe("membership_invite_token_all_or_none");

        // A 31-byte digest is not HMAC-SHA256.
        var shortHash = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await SeedMembershipAsync(
                orgId, "short-hash@example.com", prefix: Prefix(), hash: Hash(31), pepperVersion: 1, expiresAt: live);
        });
        shortHash.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        shortHash.ConstraintName.ShouldBe("membership_invite_token_all_or_none");

        // A pepper version is a lookup key into the configured peppers, so 0 is not one.
        var badVersion = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await SeedMembershipAsync(
                orgId, "zero-version@example.com", prefix: Prefix(), hash: Hash(), pepperVersion: 0, expiresAt: live);
        });
        badVersion.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        badVersion.ConstraintName.ShouldBe("membership_invite_pepper_version_check");

        // Both legal shapes: a complete token, and the untouched pending row of 0011.
        await SeedMembershipAsync(
            orgId, "minted@example.com", prefix: Prefix(), hash: Hash(), pepperVersion: 1, expiresAt: live);

        await SeedMembershipAsync(orgId, "unminted@example.com");

        (await ScalarOwnerAsync<long>(
            "select count(*) from public.membership where org_id = @org_id",
            ("org_id", orgId))).ShouldBe(2, "only the two legal shapes were written");
    }

    /// <summary>
    /// 02.1.2 §7: the sweep expires exactly the pending invites whose expiry has passed —
    /// not live ones, not an <c>active</c> row that happens to carry an expiry, and not a
    /// pending row that never had a token.
    /// </summary>
    [Fact]
    public async Task Expire_Pending_Invites_Expires_Only_Past_Due_Pending_Rows()
    {
        var pastDue = DateTimeOffset.UtcNow.AddHours(-1);
        var live = DateTimeOffset.UtcNow.AddHours(1);

        var first = await SeedMembershipAsync(
            await SeedOrgAsync(autobindVerifiedDomain: false),
            "first@example.com", prefix: Prefix(), hash: Hash(), pepperVersion: 1, expiresAt: pastDue);
        var second = await SeedMembershipAsync(
            await SeedOrgAsync(autobindVerifiedDomain: false),
            "second@example.com", prefix: Prefix(), hash: Hash(), pepperVersion: 1, expiresAt: pastDue);
        var livePending = await SeedMembershipAsync(
            await SeedOrgAsync(autobindVerifiedDomain: false),
            "live@example.com", prefix: Prefix(), hash: Hash(), pepperVersion: 1, expiresAt: live);
        var active = await SeedMembershipAsync(
            await SeedOrgAsync(autobindVerifiedDomain: false),
            "active@example.com", status: "active", prefix: Prefix(), hash: Hash(), pepperVersion: 1,
            expiresAt: pastDue);
        var unminted = await SeedMembershipAsync(
            await SeedOrgAsync(autobindVerifiedDomain: false), "unminted@example.com");

        await using var sweep = await OpenSweepConnectionAsync();
        (await SweepAsync(sweep, 100)).ShouldBe(2, "only the two past-due pending rows");

        (await StatusAsync(first)).ShouldBe("expired");
        (await StatusAsync(second)).ShouldBe("expired");
        (await StatusAsync(livePending)).ShouldBe("pending");
        (await StatusAsync(active)).ShouldBe("active", "an active membership is not a pending invite");
        (await StatusAsync(unminted)).ShouldBe("pending", "a row without an expiry is never swept");
    }

    /// <summary>
    /// The batch bound lives in the function, not at the caller: a schedule task loops until
    /// a short batch comes back, so a large backlog drains in short transactions. A NULL or
    /// non-positive limit sweeps nothing rather than everything (fail-closed), and a second
    /// call after the backlog is gone returns 0 (the task's idempotency).
    /// </summary>
    [Fact]
    public async Task Expire_Pending_Invites_Respects_The_Batch_Limit_And_Is_Idempotent()
    {
        var pastDue = DateTimeOffset.UtcNow.AddMinutes(-5);
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);

        for (var index = 0; index < 3; index++)
        {
            await SeedMembershipAsync(
                org,
                $"batch-{index}@example.com",
                prefix: Prefix(),
                hash: Hash(),
                pepperVersion: 1,
                expiresAt: pastDue.AddMinutes(index));
        }

        await using var sweep = await OpenSweepConnectionAsync();

        (await SweepAsync(sweep, null)).ShouldBe(0, "an absent batch size must not become an unbounded update");
        (await SweepAsync(sweep, 0)).ShouldBe(0, "a non-positive batch asks for nothing");
        (await SweepAsync(sweep, 2)).ShouldBe(2);
        (await SweepAsync(sweep, 2)).ShouldBe(1, "the remainder, in a second short transaction");
        (await SweepAsync(sweep, 2)).ShouldBe(0, "the sweep is idempotent once the backlog is gone");
    }

    /// <summary>
    /// TL T80: the sweep is an owner <c>SECURITY DEFINER</c> function with an empty
    /// <c>search_path</c>, its EXECUTE grant is scoped to <c>evidgate_worker_sched</c>, and
    /// neither the app role nor PUBLIC may call it.
    /// </summary>
    [Fact]
    public async Task Expire_Pending_Invites_Is_Executable_Only_By_The_Sweep_Role()
    {
        var definition = await ScalarOwnerAsync<string>(
            """
            select p.prosecdef::text || '|' || pg_catalog.pg_get_userbyid(p.proowner) || '|'
                   || coalesce(array_to_string(p.proconfig, ','), '')
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate' and p.proname = 'expire_pending_invites'
            """);

        definition.ShouldBe("true|evidgate_owner|search_path=\"\"");

        (await ScalarOwnerAsync<bool>(
            "select pg_catalog.has_function_privilege('evidgate_worker_sched', 'evidgate.expire_pending_invites(integer)', 'execute')"))
            .ShouldBeTrue();

        foreach (var role in new[] { "evidgate_app", "evidgate_operator" })
        {
            (await ScalarOwnerAsync<bool>(
                $"select pg_catalog.has_function_privilege('{role}', 'evidgate.expire_pending_invites(integer)', 'execute')"))
                .ShouldBeFalse($"{role} must not run a status sweep (TL T80)");
        }

        (await ScalarOwnerAsync<long>(
            """
            select count(*)
              from pg_proc p
              cross join lateral aclexplode(p.proacl) a
             where p.proname = 'expire_pending_invites' and a.grantee = 0
            """)).ShouldBe(0, "PUBLIC holds no EXECUTE on a definer function");

        await using var app = Fixture.AppDataSource.CreateCommand(
            "select evidgate.expire_pending_invites(1)");

        var denied = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await app.ExecuteScalarAsync(Token);
        });
        denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }
}

/// <summary>
/// T75 / 02.1.2 §8: the two pre-tenant invite lookups. Each returns only the
/// <c>(org_id, membership_id)</c> pair, only for a row that can still bind, and works with
/// no tenant setting while the same app role cannot read the row directly.
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class InviteBySecretTests : InviteTestBase
{
    public InviteBySecretTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// The happy path of the secret lookup: a pending, unexpired invite resolves to its pair
    /// and to nothing else — the tuple is the contract (00 §5.4.8), never a column of the
    /// table the function reads.
    /// </summary>
    [Fact]
    public async Task Returns_Only_The_Tenant_Tuple_For_A_Pending_Unexpired_Prefix()
    {
        var orgId = await SeedOrgAsync(autobindVerifiedDomain: false);
        var prefix = Prefix();
        var membershipId = await SeedMembershipAsync(
            orgId,
            "invited@example.com",
            prefix: prefix,
            hash: Hash(),
            pepperVersion: 1,
            expiresAt: DateTimeOffset.UtcNow.AddDays(7));

        await using var command = Fixture.AppDataSource.CreateCommand(
            "select * from evidgate_resolve.invite_by_secret(@prefix::text)");
        command.Parameters.AddWithValue("prefix", prefix);

        await using var reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).ShouldBeTrue();

        reader.FieldCount.ShouldBe(2, "a resolver returns the tenant tuple and nothing else");
        reader.GetName(0).ShouldBe("org_id");
        reader.GetName(1).ShouldBe("membership_id");
        reader.GetGuid(0).ShouldBe(orgId);
        reader.GetGuid(1).ShouldBe(membershipId);
        (await reader.ReadAsync(Token)).ShouldBeFalse("the prefix is unique (0011)");

        var resolved = await InviteBySecretAsync(prefix);
        resolved.Count.ShouldBe(1);
        resolved[0].ShouldBe((orgId, membershipId));
    }

    /// <summary>
    /// Every state that must not bind returns no row at all: an unknown prefix, a cancelled
    /// (<c>revoked</c>) invite, an <c>expired</c> one, a consumed (<c>active</c>) one whose
    /// token columns are still on the row for audit, and a pending invite past its expiry
    /// that the sweep has not reached yet (the resolver filters on time, not only on status).
    /// </summary>
    [Fact]
    public async Task Returns_Nothing_For_Unknown_Revoked_Expired_Or_Active_Rows()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var live = DateTimeOffset.UtcNow.AddDays(7);
        var past = DateTimeOffset.UtcNow.AddMinutes(-5);

        var unknown = Prefix();
        var revoked = Prefix();
        var expired = Prefix();
        var consumed = Prefix();
        var staleButPending = Prefix();

        await SeedMembershipAsync(org, "revoked@example.com", status: "revoked", prefix: revoked, hash: Hash(),
            pepperVersion: 1, expiresAt: live);
        await SeedMembershipAsync(org, "expired@example.com", status: "expired", prefix: expired, hash: Hash(),
            pepperVersion: 1, expiresAt: past);
        await SeedMembershipAsync(org, "consumed@example.com", status: "active", prefix: consumed, hash: Hash(),
            pepperVersion: 1, expiresAt: live);
        await SeedMembershipAsync(org, "stale@example.com", prefix: staleButPending, hash: Hash(),
            pepperVersion: 1, expiresAt: past);

        (await InviteBySecretAsync(unknown)).ShouldBeEmpty("an unknown prefix resolves to nothing");
        (await InviteBySecretAsync(revoked)).ShouldBeEmpty("a cancelled invite can never bind");
        (await InviteBySecretAsync(expired)).ShouldBeEmpty();
        (await InviteBySecretAsync(consumed)).ShouldBeEmpty("a consumed invite is not pending");
        (await InviteBySecretAsync(staleButPending)).ShouldBeEmpty("past-due pending rows are not bindable");
        (await InviteBySecretAsync(string.Empty)).ShouldBeEmpty();
    }

    /// <summary>
    /// The pre-tenant boundary: inside a tenant scope the app role cannot select another
    /// org's invite columns at all (RLS on <c>membership</c>), while the resolver — with no
    /// tenant setting — returns that same row's pair. The function is the only way across.
    /// </summary>
    [Fact]
    public async Task Tenant_Scope_Cannot_Read_Another_Orgs_Invite_Columns()
    {
        var orgA = await SeedOrgAsync(autobindVerifiedDomain: false);
        var orgB = await SeedOrgAsync(autobindVerifiedDomain: false);

        await SeedMembershipAsync(orgA, "a@example.com");
        var prefixB = Prefix();
        var membershipB = await SeedMembershipAsync(
            orgB, "b@example.com", prefix: prefixB, hash: Hash(), pepperVersion: 1,
            expiresAt: DateTimeOffset.UtcNow.AddDays(7));

        await using (var scope = await Fixture.BeginTenantScopeAsync(orgA, readOnlyTransaction: true, cancellationToken: Token))
        {
            (await scope.Db.ScalarAsync<long>(
                "select count(*) from public.membership where org_id = @org_id",
                new { org_id = orgB },
                Token)).ShouldBe(0, "org B's membership row is not visible from org A");

            (await scope.Db.ScalarAsync<long>(
                "select count(*) from public.membership where invite_secret_prefix is not null",
                cancellationToken: Token)).ShouldBe(0, "no invite lookup key leaks across the tenant boundary");
        }

        var resolved = await InviteBySecretAsync(prefixB);
        resolved.Count.ShouldBe(1);
        resolved[0].ShouldBe((orgB, membershipB));

        (await ScalarOwnerAsync<long>(
            "select count(*) from public.membership where id = @id and status = 'pending'",
            ("id", membershipB))).ShouldBe(1, "the resolver returned a real pending row");
    }

    /// <summary>
    /// AC8 / T75: the email-only path exists only for an org that opted in and is still
    /// active. The same address in an org that did not opt in, or that is suspended, yields
    /// no candidate — and a past-due pending row is not a candidate either.
    /// </summary>
    [Fact]
    public async Task Autobind_Candidates_Require_The_Opt_In_And_An_Active_Org()
    {
        const string Email = "auto@example.com";
        var live = DateTimeOffset.UtcNow.AddDays(7);

        var optedIn = await SeedOrgAsync(autobindVerifiedDomain: true);
        var optedOut = await SeedOrgAsync(autobindVerifiedDomain: false);
        var suspended = await SeedOrgAsync(autobindVerifiedDomain: true, status: "suspended");

        var expected = await SeedMembershipAsync(
            optedIn, Email, prefix: Prefix(), hash: Hash(), pepperVersion: 1, expiresAt: live);
        await SeedMembershipAsync(optedOut, Email, prefix: Prefix(), hash: Hash(), pepperVersion: 1, expiresAt: live);
        await SeedMembershipAsync(suspended, Email, prefix: Prefix(), hash: Hash(), pepperVersion: 1, expiresAt: live);
        await SeedMembershipAsync(
            optedIn, Email + ".other", prefix: Prefix(), hash: Hash(), pepperVersion: 1, expiresAt: live);
        await SeedMembershipAsync(
            optedIn, "past-due@example.com", prefix: Prefix(), hash: Hash(), pepperVersion: 1,
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        await SeedMembershipAsync(optedIn, "revoked@example.com", status: "revoked", prefix: Prefix(), hash: Hash(),
            pepperVersion: 1, expiresAt: live);

        var candidates = await AutobindCandidatesAsync(Email);
        candidates.Count.ShouldBe(1, "exactly the opted-in, active org's pending invite");
        candidates[0].ShouldBe((optedIn, expected));
    }

    /// <summary>
    /// The address comparison is exact and the caller normalises: an unnormalised argument
    /// must match nothing rather than half-match a stored address (02.1.2 §5: emails are
    /// compared <c>lower(trim())</c>, so the value handed in is that form).
    /// </summary>
    [Fact]
    public async Task Autobind_Candidates_Match_The_Exact_Normalised_Address()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: true);
        var membershipId = await SeedMembershipAsync(
            org, "auto@example.com", prefix: Prefix(), hash: Hash(), pepperVersion: 1,
            expiresAt: DateTimeOffset.UtcNow.AddDays(7));

        var candidates = await AutobindCandidatesAsync("auto@example.com");
        candidates.Count.ShouldBe(1);
        candidates[0].ShouldBe((org, membershipId));
        (await AutobindCandidatesAsync("AUTO@example.com")).ShouldBeEmpty("the function does not lowercase");
        (await AutobindCandidatesAsync(" auto@example.com")).ShouldBeEmpty("the function does not trim");
        (await AutobindCandidatesAsync("auto@example.com ")).ShouldBeEmpty();
        (await AutobindCandidatesAsync("other@example.com")).ShouldBeEmpty();
        (await AutobindCandidatesAsync(string.Empty)).ShouldBeEmpty();
    }
}
