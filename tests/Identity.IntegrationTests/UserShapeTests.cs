using System.Security.Cryptography;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// 02.3.1-T1.1 / 02.3.1 §4: the shape of <c>user</c>, <c>user_identity</c> and
/// <c>session</c> on the migrated database — the reviewed column set (AC7: no
/// password material of any kind), the <c>(provider, subject)</c> binding, the
/// check constraints the command layer relies on, ENABLE + FORCE row level
/// security with the owner-only policy of D12, and the plain
/// <c>lower(email)</c> index of D3/T6.
/// </summary>
/// <remarks>
/// <para>
/// The platform identity tables are not tenant tables (D12): no <c>org_id</c>, no
/// <c>evidgate_app</c> privilege at all, and the only sanctioned path is an
/// owner-owned SECURITY DEFINER function. This suite therefore asserts the two
/// halves that matter and can regress independently — the row shape the login
/// command writes, and the privilege posture that keeps the app role out (the
/// same contract 02.9.1-T3's <c>ResolverTests</c> enforces from the other side).
/// </para>
/// <para>
/// Catalogue reads and row arrangement go through the owner connection
/// (<see cref="DatabaseFixture.SeedAsync"/> is the harness's one seeding seam);
/// every refusal assertion goes through the real server, never a memento of the
/// DDL text.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class UserShapeTests : CatalogueTestBase
{
    /// <summary>
    /// The exact column list of <c>public."user"</c> in ordinal order. A column
    /// added without review fails here on purpose: every column of an identity
    /// table is a privacy decision (02.3.1 §8), so the list is the contract, not
    /// a "contains" check.
    /// </summary>
    private static readonly string[] UserColumns =
        ["id", "email", "display_name", "status", "last_login_at", "created_at", "updated_at"];

    /// <summary>The exact column list of <c>user_identity</c> (02.3.1 §4, T6).</summary>
    private static readonly string[] UserIdentityColumns =
        ["id", "user_id", "provider", "subject", "email_at_provider", "linked_at"];

    /// <summary>
    /// The exact column list of <c>session</c>. It ships here so login can insert
    /// (02.3.1 §3); <c>cookie_hmac</c>/<c>pepper_version</c> are 02.3.1-T1.4's
    /// credential columns (migration 0042), <c>rotation_required_at</c> is
    /// 02.3.2-T1's rotation flag (migration 0045, AC7) and <c>expired_at</c> is
    /// 02.3.2-T2's sweep stamp of the <c>expired</c> state (migration 0048,
    /// D-S102-Q1): the <c>session-cleanup</c> sweep writes it once on a dead row,
    /// and the two deadlines stay the truth of the state machine. Each is additive
    /// and nullable, so no reviewed column left and none of the credential posture
    /// changed.
    /// </summary>
    private static readonly string[] SessionColumns =
    [
        "id", "user_id", "created_at", "last_seen_at", "expires_at", "absolute_expires_at",
        "ip", "user_agent", "revoked_at", "rotated_from", "cookie_hmac", "pepper_version",
        "rotation_required_at", "expired_at",
    ];

    /// <summary>
    /// AC7: no identity table may store a password, secret or reusable token. <c>session.cookie_hmac</c> is the
    /// one deliberate exception and is not one of these: it holds HMAC-SHA256 of a value the browser keeps, so
    /// the row verifies a cookie without being usable as one (0042).
    /// </summary>
    private static readonly string[] CredentialNameFragments =
        ["password", "passwd", "secret", "hash", "token"];

    /// <summary>A fixed instant for the session expiries, so the assertions never depend on the clock.</summary>
    private static readonly DateTime AbsoluteExpiry = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public UserShapeTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// AC7 and the privacy minimum of 02.3.1 §4/§8: each table carries exactly the
    /// reviewed columns — no more (an unreviewed column is an unreviewed data
    /// decision) and no fewer (the login command writes them) — and none of them
    /// names a password, secret, hash or token.
    /// </summary>
    [Fact]
    public async Task Tables_Carry_Exactly_The_Reviewed_Columns_And_No_Credential_Column()
    {
        (await ColumnsAsync("user")).ShouldBe(UserColumns);
        (await ColumnsAsync("user_identity")).ShouldBe(UserIdentityColumns);
        (await ColumnsAsync("session")).ShouldBe(SessionColumns);

        foreach (var table in new[] { "user", "user_identity", "session" })
        {
            var columns = await ColumnsAsync(table);
            foreach (var fragment in CredentialNameFragments)
            {
                columns
                    .Where(column => column.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    .ShouldBeEmpty(
                        $"{table} must store no credential: a column naming '{fragment}' means a password, "
                        + "hash or token arrived on an identity table (AC7, FR-02-3)");
            }
        }

        // The binding key of FR-02-4 and the status machine of §6 exist as
        // constraints, not merely as columns: a later "fix" that drops one shows
        // up as a missing name here.
        (await ConstraintNamesAsync("user_identity")).ShouldContain("user_identity_provider_subject_key");
        (await ConstraintNamesAsync("user")).ShouldContain("user_status_check");
    }

    /// <summary>
    /// FR-02-4/AC6: the identity key is <c>(provider, subject)</c>. The same
    /// subject at a second provider is a different identity (the two rows AC6
    /// keeps distinct); the same pair twice is refused by the server, so a second
    /// login can never create a duplicate binding.
    /// </summary>
    [Fact]
    public async Task Identity_Binding_Is_Unique_Per_Provider_And_Subject()
    {
        var userId = await SeedUserAsync("bound@example.com");
        var subject = Unique.Text("subject");

        await SeedIdentityAsync(userId, "github", subject);

        var duplicate = await RefusalAsync(
            "insert into public.user_identity (id, user_id, provider, subject) values (@id, @user_id, 'github', @subject)",
            ("id", Guid.CreateVersion7()),
            ("user_id", userId),
            ("subject", subject));

        duplicate.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        duplicate.ConstraintName.ShouldBe("user_identity_provider_subject_key");

        // Non-vacuity of the boundary: the same subject at the other provider is a
        // different human as far as V1 knows (AC6), so it must be accepted.
        await SeedIdentityAsync(userId, "google", subject);

        (await OwnerScalarAsync<long>(
            "select count(*) from public.user_identity where user_id = @user_id",
            ("user_id", userId))).ShouldBe(2);
    }

    /// <summary>
    /// The check constraints the login and session paths depend on: an unknown
    /// provider, an unknown user status, an idle deadline that outlives the
    /// absolute one, and an unbounded user agent are all refused by the server
    /// rather than by a handler someone can forget.
    /// </summary>
    [Fact]
    public async Task Check_Constraints_Refuse_Unreviewed_Values()
    {
        var userId = await SeedUserAsync("checks@example.com");
        var sessionId = await SeedSessionAsync(userId);

        var provider = await RefusalAsync(
            "insert into public.user_identity (id, user_id, provider, subject) values (@id, @user_id, 'saml', '1')",
            ("id", Guid.CreateVersion7()),
            ("user_id", userId));
        provider.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        provider.ConstraintName.ShouldBe("user_identity_provider_check");

        var status = await RefusalAsync(
            "insert into public.\"user\" (id, email, status) values (@id, @email, 'suspended')",
            ("id", Guid.CreateVersion7()),
            ("email", "suspended@example.com"));
        status.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        status.ConstraintName.ShouldBe("user_status_check");

        // The idle deadline may be shorter than the absolute one, never longer.
        var expiries = await RefusalAsync(
            "update public.session set expires_at = @expires_at where id = @id",
            ("expires_at", AbsoluteExpiry.AddMinutes(1)),
            ("id", sessionId));
        expiries.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        expiries.ConstraintName.ShouldBe("session_expiry_check");

        // Boundary: 512 characters is the cap this contract sets on the column
        // (`session_user_agent_length_check`), 513 is out.
        var userAgent = await RefusalAsync(
            "update public.session set user_agent = @user_agent where id = @id",
            ("user_agent", new string('a', 513)),
            ("id", sessionId));
        userAgent.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        userAgent.ConstraintName.ShouldBe("session_user_agent_length_check");

        await OwnerExecuteAsync(
            "update public.session set user_agent = @user_agent where id = @id",
            ("user_agent", new string('a', 512)),
            ("id", sessionId));
    }

    /// <summary>
    /// D12 and DECISIONS F5: all three tables run under ENABLE + FORCE row level
    /// security, carry the owner policy an expand/contract backfill needs, and
    /// carry no policy that reaches <c>evidgate_app</c> — the app role's access is
    /// the resolver path, not a direct read.
    /// </summary>
    [Fact]
    public async Task Row_Level_Security_Is_Enabled_Forced_And_Owner_Only()
    {
        foreach (var table in new[] { "user", "user_identity", "session" })
        {
            var flags = await OwnerRowAsync(
                """
                select c.relrowsecurity, c.relforcerowsecurity
                  from pg_class c
                  join pg_namespace n on n.oid = c.relnamespace
                 where n.nspname = 'public' and c.relname = @table
                """,
                ("table", table));

            flags[0].ShouldBe(true, $"{table}.relrowsecurity (DECISIONS F5)");
            flags[1].ShouldBe(true, $"{table}.relforcerowsecurity — the owner would otherwise bypass its policy");

            (await OwnerScalarAsync<long>(
                "select count(*) from pg_policies where schemaname = 'public' and tablename = @table and policyname = @policy",
                ("table", table),
                ("policy", $"{table}_owner_backfill")))
                .ShouldBe(1, $"{table} carries the owner policy its backfill needs (0004/0011 form)");

            (await OwnerScalarAsync<long>(
                """
                select count(*)
                  from pg_policies
                 where schemaname = 'public' and tablename = @table
                   and ('evidgate_app' = any(roles) or 'public' = any(roles))
                """,
                ("table", table)))
                .ShouldBe(0, $"{table} must reach the app role through evidgate_resolve only — no direct-read policy");
        }
    }

    /// <summary>
    /// 02.9.1 §8 T1 / 00 §5.4.3: the platform identity tables grant the runtime
    /// roles nothing. A read fails with <c>permission denied</c> rather than
    /// returning zero rows — the difference matters, because a zero-row read would
    /// look like a tenant scope that simply matched nothing.
    /// </summary>
    [Fact]
    public async Task App_Role_Cannot_Read_Or_Write_The_Platform_Tables()
    {
        var userId = await SeedUserAsync("denied@example.com");
        await SeedSessionAsync(userId);

        foreach (var table in new[] { "user", "user_identity", "session" })
        {
            var read = await AppRefusalAsync($"select count(*) from public.\"{table}\"");
            read.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, $"{table}: evidgate_app must hold no SELECT");

            var write = await AppRefusalAsync(
                table switch
                {
                    "user" => "insert into public.\"user\" (id, email) values (@id, 'sneaky@example.com')",
                    "user_identity" => "insert into public.user_identity (id, user_id, provider, subject) "
                        + "values (@id, @user_id, 'github', '9')",
                    _ => "insert into public.session (id, user_id, expires_at, absolute_expires_at, cookie_hmac, pepper_version) "
                        + "values (@id, @user_id, now(), now(), @cookie_hmac, 1)",
                },
                ("id", Guid.CreateVersion7()),
                ("user_id", userId),
                ("cookie_hmac", NewCookieHmac()));
            write.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, $"{table}: evidgate_app must hold no write");
        }
    }

    /// <summary>
    /// The other side of the privilege contract: the migration role owns the
    /// tables and can arrange and read rows through its own policy, which is what
    /// the harness's seeding seam and every later backfill rely on.
    /// </summary>
    [Fact]
    public async Task Owner_Can_Read_And_Write_All_Three_Tables()
    {
        var userId = await SeedUserAsync("owner@example.com");
        await SeedIdentityAsync(userId, "google", Unique.Text("subject"));
        await SeedSessionAsync(userId);

        (await OwnerScalarAsync<string>(
            "select email from public.\"user\" where id = @id",
            ("id", userId))).ShouldBe("owner@example.com");

        (await OwnerScalarAsync<long>(
            "select count(*) from public.user_identity where user_id = @user_id",
            ("user_id", userId))).ShouldBe(1);

        (await OwnerScalarAsync<long>(
            "select count(*) from public.session where user_id = @user_id",
            ("user_id", userId))).ShouldBe(1);
    }

    /// <summary>
    /// D3/T6: the address index is plain and NON-unique. Two users with the same
    /// address are allowed — AC6 records the same address at two providers as two
    /// identities, and a merge is an explicit 09 flow, never a unique violation in
    /// the login path.
    /// </summary>
    [Fact]
    public async Task Email_Index_Is_Plain_And_Non_Unique()
    {
        var email = Unique.Email("shared");
        await SeedUserAsync(email);
        await SeedUserAsync(email.ToUpperInvariant());

        (await OwnerScalarAsync<long>(
            "select count(*) from public.\"user\" where lower(email) = @email",
            ("email", email)))
            .ShouldBe(2, "the lower(email) index must not be unique (D3/T6: email is not an identity key)");

        var definition = await OwnerScalarAsync<string>(
            "select indexdef from pg_indexes where schemaname = 'public' and indexname = 'user_email_lower_idx'");

        definition.ShouldContain("lower(email)", Case.Insensitive, "the login lookup is on the normalised address");
        definition.ShouldNotContain("unique", Case.Insensitive);

        (await OwnerScalarAsync<long>(
            """
            select count(*)
              from pg_index i
              join pg_class c on c.oid = i.indrelid
             where c.relname = 'user' and i.indisunique and i.indisvalid
               and pg_get_indexdef(i.indexrelid) ilike '%lower(email)%'
            """)).ShouldBe(0, "no unique index may cover the normalised address");
    }

    /// <summary>
    /// The rotation chain of 02.3.2-T1: <c>session.rotated_from</c> is a self
    /// reference, and the successor lookup goes through it, so the reference is
    /// indexed here — partial, because a first login's row has no predecessor —
    /// and enforced, so a successor always names a session that exists. The
    /// sliding-expiry index shape is 02.3.2-T1's own migration (it names
    /// <c>session_user_revoked_expires_idx</c>); this assertion only pins what 0041
    /// ships, so that task can add its indexes without silently changing this one.
    /// </summary>
    [Fact]
    public async Task Session_Rotation_Index_Covers_The_Self_Reference()
    {
        var definition = await OwnerScalarAsync<string>(
            "select indexdef from pg_indexes where schemaname = 'public' and indexname = 'session_rotated_from_idx'");

        definition.ShouldContain("on public.session", Case.Insensitive, "the chain lives on the session table");
        definition.ShouldContain("(rotated_from)", Case.Insensitive, "the successor lookup is on rotated_from");
        definition.ShouldContain("is not null", Case.Insensitive, "the index is partial: a first login has no predecessor");

        // The reference it covers exists as the named constraint, so a dropped key
        // fails here instead of leaving an index over a column nothing enforces.
        (await ConstraintNamesAsync("session")).ShouldContain("session_rotated_from_fk");

        // …and it is enforced, which is what makes the index load-bearing: a
        // successor naming a session that does not exist is refused by the server,
        // a successor naming a real predecessor is accepted.
        var userId = await SeedUserAsync("rotated@example.com");
        var predecessor = await SeedSessionAsync(userId);
        await SeedRotatedSessionAsync(userId, predecessor);

        var orphan = await RefusalAsync(
            "insert into public.session (id, user_id, expires_at, absolute_expires_at, rotated_from, cookie_hmac, pepper_version) "
            + "values (@id, @user_id, @expires_at, @absolute_expires_at, @rotated_from, @cookie_hmac, 1)",
            ("id", Guid.CreateVersion7()),
            ("user_id", userId),
            ("expires_at", AbsoluteExpiry.AddHours(-1)),
            ("absolute_expires_at", AbsoluteExpiry),
            ("rotated_from", Guid.CreateVersion7()),
            ("cookie_hmac", NewCookieHmac()));

        orphan.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        orphan.ConstraintName.ShouldBe("session_rotated_from_fk");
    }

    /// <summary>Seeds one user through the owner seam and returns its app-generated id.</summary>
    private async Task<Guid> SeedUserAsync(string email)
    {
        var id = Guid.CreateVersion7();
        await Fixture.SeedAsync(
            "insert into public.\"user\" (id, email, display_name) values (@id, @email, @display_name)",
            [("id", id), ("email", email), ("display_name", "Test User")],
            Token);
        return id;
    }

    /// <summary>Seeds one provider binding for a user.</summary>
    private async Task SeedIdentityAsync(Guid userId, string provider, string subject) =>
        await Fixture.SeedAsync(
            "insert into public.user_identity (id, user_id, provider, subject, email_at_provider) "
            + "values (@id, @user_id, @provider, @subject, @email)",
            [
                ("id", Guid.CreateVersion7()),
                ("user_id", userId),
                ("provider", provider),
                ("subject", subject),
                ("email", "linked@example.com"),
            ],
            Token);

    /// <summary>
    /// Seeds one live session: the idle deadline one hour before the absolute one,
    /// which is the only shape <c>session_expiry_check</c> accepts.
    /// </summary>
    private async Task<Guid> SeedSessionAsync(Guid userId)
    {
        var id = Guid.CreateVersion7();
        await Fixture.SeedAsync(
            "insert into public.session (id, user_id, expires_at, absolute_expires_at, ip, user_agent, cookie_hmac, pepper_version) "
            + "values (@id, @user_id, @expires_at, @absolute_expires_at, '203.0.113.7', 'xunit', @cookie_hmac, 1)",
            [
                ("id", id),
                ("user_id", userId),
                ("expires_at", AbsoluteExpiry.AddHours(-1)),
                ("absolute_expires_at", AbsoluteExpiry),
                ("cookie_hmac", NewCookieHmac()),
            ],
            Token);
        return id;
    }

    /// <summary>
    /// Seeds the successor of a session: <c>rotated_from</c> names its predecessor,
    /// which is the only kind of row the partial index holds.
    /// </summary>
    private async Task SeedRotatedSessionAsync(Guid userId, Guid predecessor) =>
        await Fixture.SeedAsync(
            "insert into public.session (id, user_id, expires_at, absolute_expires_at, ip, user_agent, rotated_from, cookie_hmac, pepper_version) "
            + "values (@id, @user_id, @expires_at, @absolute_expires_at, '203.0.113.7', 'xunit', @rotated_from, @cookie_hmac, 1)",
            [
                ("id", Guid.CreateVersion7()),
                ("user_id", userId),
                ("expires_at", AbsoluteExpiry.AddHours(-1)),
                ("absolute_expires_at", AbsoluteExpiry),
                ("rotated_from", predecessor),
                ("cookie_hmac", NewCookieHmac()),
            ],
            Token);

    /// <summary>
    /// A fresh 32-byte cookie hash for a seeded session: <c>session_cookie_hmac_length_check</c> fixes the
    /// length and <c>session_cookie_hmac_key</c> is unique, so a fixed literal would collide at the second row.
    /// </summary>
    private static byte[] NewCookieHmac() => SHA256.HashData(Guid.CreateVersion7().ToByteArray());

    /// <summary>The table's columns in ordinal order, as <c>information_schema</c> reports them.</summary>
    private async Task<string[]> ColumnsAsync(string table) =>
        await OwnerScalarAsync<string[]>(
            """
            select array_agg(column_name::text order by ordinal_position)
              from information_schema.columns
             where table_schema = 'public' and table_name = @table
            """,
            ("table", table));

    /// <summary>The table's constraint names, so a dropped key or check fails by name.</summary>
    private async Task<string[]> ConstraintNamesAsync(string table) =>
        await OwnerScalarAsync<string[]>(
            """
            select array_agg(con.conname::text order by con.conname::text)
              from pg_constraint con
              join pg_class c on c.oid = con.conrelid
              join pg_namespace n on n.oid = c.relnamespace
             where n.nspname = 'public' and c.relname = @table
            """,
            ("table", table));

    /// <summary>Runs one statement as the owner and returns the refusal it must raise.</summary>
    private async Task<PostgresException> RefusalAsync(
        string sql,
        params (string Name, object Value)[] values) =>
        await Should.ThrowAsync<PostgresException>(() => Fixture.SeedAsync(sql, values, Token));

    /// <summary>Runs one statement as the app role and returns the refusal it must raise.</summary>
    private async Task<PostgresException> AppRefusalAsync(
        string sql,
        params (string Name, object Value)[] values)
    {
        await using var command = Fixture.AppDataSource.CreateCommand(sql);
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Token));
    }

    /// <summary>One scalar on the owner connection (catalogue reads and row arrangement).</summary>
    private async Task<T> OwnerScalarAsync<T>(string sql, params (string Name, object Value)[] values)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(Token);
        return (T)result!;
    }

    /// <summary>One row of scalars on the owner connection.</summary>
    private async Task<object?[]> OwnerRowAsync(string sql, params (string Name, object Value)[] values)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).ShouldBeTrue();

        var row = new object?[reader.FieldCount];
        for (var index = 0; index < reader.FieldCount; index++)
        {
            row[index] = await reader.IsDBNullAsync(index, Token) ? null : reader.GetValue(index);
        }

        return row;
    }

    /// <summary>One statement on the owner connection, for the accepting side of a boundary.</summary>
    private async Task OwnerExecuteAsync(string sql, params (string Name, object Value)[] values)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Token);
    }
}
