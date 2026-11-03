using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Persistence;

/// <summary>
/// 02.3.1-T1.4 / 02.9.1 §8 T1: the privilege posture of the platform identity tables after migration 0042 —
/// the app role still holds NOTHING on <c>user</c>, <c>user_identity</c> and <c>session</c> (no read, no
/// write), and its only path in is EXECUTE on the two <c>evidgate_resolve</c> functions the login uses.
/// </summary>
/// <remarks>
/// Both halves matter and can regress independently: a stray <c>grant</c> (or a lost <c>revoke</c>, which is
/// what 0001's default privileges would re-create) would let any tenant-scoped request read a human's
/// identity, and a missing EXECUTE would make every login a 500. The refusal must be
/// <c>permission denied</c> rather than zero rows — an empty result would look like a tenant scope that
/// simply matched nothing.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class ResolverTests : SessionTestBase
{
    private static readonly string[] PlatformTables = ["user", "user_identity", "session"];

    public ResolverTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// Done-when of 02.3.1-T1.4: as <c>evidgate_app</c>, SELECT, INSERT and UPDATE on each of the three tables
    /// fail with <c>permission denied</c>, while EXECUTE on both functions works and writes the rows the
    /// login needs.
    /// </summary>
    [Fact]
    public async Task AppRoleCannotSelectPlatformTables()
    {
        var userId = await SeedUserAsync(Unique.Email("denied"));
        var subject = Unique.Text("subject");
        await OwnerExecuteAsync(
            "insert into public.user_identity (id, user_id, provider, subject) "
            + "values (@id, @user_id, 'github', @subject)",
            ("id", Guid.CreateVersion7()),
            ("user_id", userId),
            ("subject", subject));
        await OwnerExecuteAsync(
            "insert into public.session (id, user_id, cookie_hmac, pepper_version, expires_at, absolute_expires_at) "
            + "values (@id, @user_id, @cookie_hmac, 1, now() + interval '1 hour', now() + interval '2 hours')",
            ("id", Guid.CreateVersion7()),
            ("user_id", userId),
            ("cookie_hmac", ExpectedHmac(Unique.Text("seed"))));

        foreach (var table in PlatformTables)
        {
            var select = await AppRefusalAsync($"select count(*) from public.\"{table}\"");
            select.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, $"{table}: evidgate_app holds no SELECT");

            var insert = await AppRefusalAsync(
                $"insert into public.\"{table}\" (id) values (@id)",
                ("id", Guid.CreateVersion7()));
            insert.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, $"{table}: evidgate_app holds no INSERT");

            var update = await AppRefusalAsync(
                $"update public.\"{table}\" set id = @id where false",
                ("id", Guid.CreateVersion7()));
            update.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, $"{table}: evidgate_app holds no UPDATE");
        }
    }

    /// <summary>
    /// The other side of that contract: EXECUTE on the two functions is the app role's ONLY way in, and it
    /// works for both — a first login creates the user and its identity, and a session row comes back with
    /// status <c>ok</c>. Run as <c>evidgate_app</c> on the real server, never as the owner.
    /// </summary>
    [Fact]
    public async Task AppRoleCanExecuteTheLoginFunctions()
    {
        var userId = Guid.CreateVersion7();

        await using (var login = AppDataSource.CreateCommand(
            "select user_id, status from evidgate_resolve.login_upsert_identity("
            + "@provider, @subject, @email, @display_name, @user_id, @identity_id)"))
        {
            login.Parameters.AddWithValue("provider", "google");
            login.Parameters.AddWithValue("subject", Unique.Text("sub"));
            login.Parameters.AddWithValue("email", Unique.Email("resolver"));
            login.Parameters.AddWithValue("display_name", "Resolver");
            login.Parameters.AddWithValue("user_id", userId);
            login.Parameters.AddWithValue("identity_id", Guid.CreateVersion7());

            await using var reader = await login.ExecuteReaderAsync(Token);
            (await reader.ReadAsync(Token)).ShouldBeTrue();
            reader.GetGuid(0).ShouldBe(userId);
            reader.GetString(1).ShouldBe("active");
        }

        var sessionId = Guid.CreateVersion7();
        await using (var session = AppDataSource.CreateCommand(
            "select session_id, expires_at, status from evidgate_resolve.session_create("
            + "@session_id, @user_id, @cookie_hmac, @pepper_version, @ip, @user_agent, @expires_at, @absolute_expires_at)"))
        {
            session.Parameters.AddWithValue("session_id", sessionId);
            session.Parameters.AddWithValue("user_id", userId);
            session.Parameters.AddWithValue("cookie_hmac", ExpectedHmac(Unique.Text("resolver")));
            session.Parameters.AddWithValue("pepper_version", PepperVersion);
            session.Parameters.AddWithValue("ip", "203.0.113.9");
            session.Parameters.AddWithValue("user_agent", "xunit");
            session.Parameters.AddWithValue("expires_at", DateTimeOffset.UtcNow.AddHours(1));
            session.Parameters.AddWithValue("absolute_expires_at", DateTimeOffset.UtcNow.AddDays(7));

            await using var reader = await session.ExecuteReaderAsync(Token);
            (await reader.ReadAsync(Token)).ShouldBeTrue();
            reader.GetGuid(0).ShouldBe(sessionId);
            reader.GetString(2).ShouldBe("ok");
        }

        (await OwnerScalarAsync<long>(
            "select count(*) from public.user_identity where user_id = @user_id",
            ("user_id", userId))).ShouldBe(1);
        (await OwnerScalarAsync<long>(
            "select count(*) from public.session where id = @id and pepper_version = @pepper_version",
            ("id", sessionId),
            ("pepper_version", PepperVersion))).ShouldBe(1);
    }

    /// <summary>
    /// The session credential's shape (02.3.1 §8): the cookie hash is 32 bytes and unique, so one cookie can
    /// only ever resolve to one session and a hash cannot be stored that no HMAC could produce. Each bound is
    /// the server's rule, not a handler's.
    /// </summary>
    [Fact]
    public async Task SessionCredentialShapeIsEnforcedByTheDatabase()
    {
        var userId = await SeedUserAsync(Unique.Email("shape"));

        var shortHash = await OwnerRefusalAsync(
            "insert into public.session (id, user_id, cookie_hmac, pepper_version, expires_at, absolute_expires_at) "
            + "values (@id, @user_id, @cookie_hmac, 1, now(), now())",
            ("id", Guid.CreateVersion7()),
            ("user_id", userId),
            ("cookie_hmac", new byte[31]));
        shortHash.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        shortHash.ConstraintName.ShouldBe("session_cookie_hmac_length_check");

        var pepperVersion = await OwnerRefusalAsync(
            "insert into public.session (id, user_id, cookie_hmac, pepper_version, expires_at, absolute_expires_at) "
            + "values (@id, @user_id, @cookie_hmac, 0, now(), now())",
            ("id", Guid.CreateVersion7()),
            ("user_id", userId),
            ("cookie_hmac", ExpectedHmac(Unique.Text("zero"))));
        pepperVersion.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        pepperVersion.ConstraintName.ShouldBe("session_pepper_version_check");

        var nullVersion = await OwnerRefusalAsync(
            "insert into public.session (id, user_id, cookie_hmac, pepper_version, expires_at, absolute_expires_at) "
            + "values (@id, @user_id, @cookie_hmac, null, now(), now())",
            ("id", Guid.CreateVersion7()),
            ("user_id", userId),
            ("cookie_hmac", ExpectedHmac(Unique.Text("null-version"))));
        nullVersion.SqlState.ShouldBe(PostgresErrorCodes.NotNullViolation);
        nullVersion.ColumnName.ShouldBe("pepper_version");

        var hmac = ExpectedHmac(Unique.Text("duplicate"));
        await OwnerExecuteAsync(
            "insert into public.session (id, user_id, cookie_hmac, pepper_version, expires_at, absolute_expires_at) "
            + "values (@id, @user_id, @cookie_hmac, 1, now(), now())",
            ("id", Guid.CreateVersion7()),
            ("user_id", userId),
            ("cookie_hmac", hmac));

        var duplicate = await OwnerRefusalAsync(
            "insert into public.session (id, user_id, cookie_hmac, pepper_version, expires_at, absolute_expires_at) "
            + "values (@id, @user_id, @cookie_hmac, 1, now(), now())",
            ("id", Guid.CreateVersion7()),
            ("user_id", userId),
            ("cookie_hmac", hmac));
        duplicate.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        duplicate.ConstraintName.ShouldBe("session_cookie_hmac_key");

        var index = await OwnerScalarAsync<string>(
            "select indexdef from pg_indexes where schemaname = 'public' and indexname = 'session_cookie_hmac_key'");
        index.ShouldContain("unique", Case.Insensitive);
        index.ShouldContain("(cookie_hmac)", Case.Insensitive);
    }
}
