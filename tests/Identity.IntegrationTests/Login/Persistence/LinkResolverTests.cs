using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Persistence;

/// <summary>
/// 02.3.1-T2 / 02.3.1 §4, AC1, AC3, AC6 (contract C6): the two <c>evidgate_resolve</c> functions of
/// migration 0044 — the read-only session lookup a cookie-authenticated request needs, and the only
/// writer of a second-provider link.
/// </summary>
/// <remarks>
/// Both are called as <c>evidgate_app</c> on the real server, with no tenant setting: that is the
/// pre-tenant situation they exist for, and the app role holds no privilege on <c>user</c>,
/// <c>user_identity</c> or <c>session</c> (0041), so every assertion about a stored row reads it back
/// through the owner seam. The link cases pin the "never auto-merge" rule of AC6 at the database: an
/// identity is taken from another account only when that account has no membership and no live
/// session, and a refusal changes nothing.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class LinkResolverTests : SessionTestBase
{
    private const string Provider = "github";

    private static readonly string[] PlatformTables = ["session", "user_identity", "user"];

    // Own database: one case drops 0041's session_expiry_check to arrange a state only the absolute
    // deadline can produce, and the whole-table counts below must see no other case's rows.
    public LinkResolverTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    // ------------------------------------------------------------------ session liveness (AC1/AC3)

    /// <summary>
    /// A revoked session is dead the moment the column is stamped, without waiting for the row to expire. The
    /// removed <c>session_user</c> lookup had no caller left, so the link function judges liveness itself: a
    /// revoked cookie answers <c>unauthenticated</c> and nothing is written.
    /// </summary>
    [Fact]
    public async Task Link_AnswersUnauthenticated_ForARevokedSession()
    {
        var userId = await SeedUserAsync("revoked-session@example.com");
        var subject = $"revoked-{Guid.NewGuid():N}";
        var cookie = ExpectedHmac("revoked");

        await SeedSessionAsync(userId, cookie, revokedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        (await LinkAsync([cookie], Provider, subject, "revoked@example.test")).Outcome.ShouldBe("unauthenticated");
        (await OwnerScalarAsync<long>("select count(*) from public.user_identity")).ShouldBe(0);
    }

    /// <summary>Idle expiry alone refuses the cookie as <c>unauthenticated</c>; the absolute deadline is a separate clock (02.3.2 §4).</summary>
    [Fact]
    public async Task Link_AnswersUnauthenticated_ForAnIdleExpiredSession()
    {
        var userId = await SeedUserAsync("idle-expired@example.com");
        var subject = $"idle-{Guid.NewGuid():N}";
        var cookie = ExpectedHmac("idle-expired");

        await SeedSessionAsync(
            userId,
            cookie,
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            absoluteExpiresAt: DateTimeOffset.UtcNow.AddDays(7));

        (await LinkAsync([cookie], Provider, subject, "idle-expired@example.test")).Outcome.ShouldBe("unauthenticated");
        (await OwnerScalarAsync<long>("select count(*) from public.user_identity")).ShouldBe(0);
    }

    /// <summary>
    /// A session past its absolute deadline is dead even when its idle clock is still ahead — the state a
    /// sliding write leaves behind, and the one only the absolute predicate can end.
    /// </summary>
    /// <remarks>
    /// 0041's <c>session_expiry_check</c> (<c>expires_at &lt;= absolute_expires_at</c>) makes that pair
    /// unwritable, so the case drops the check for its own row and restores it in the <c>finally</c> (the
    /// 0031/FK precedent of <c>MembershipUserFkTests</c>). Without the drop the idle clock would already
    /// refuse the session and the case would pass with the absolute predicate removed.
    /// </remarks>
    [Fact]
    public async Task Link_AnswersUnauthenticated_ForAnAbsoluteExpiredSession()
    {
        var userId = await SeedUserAsync("absolute-expired@example.com");
        var subject = $"absolute-{Guid.NewGuid():N}";
        var cookie = ExpectedHmac("absolute-expired");

        await OwnerExecuteAsync("alter table public.session drop constraint session_expiry_check");

        try
        {
            await SeedSessionAsync(
                userId,
                cookie,
                expiresAt: DateTimeOffset.UtcNow.AddHours(2),
                absoluteExpiresAt: DateTimeOffset.UtcNow.AddHours(-1));

            (await LinkAsync([cookie], Provider, subject, "absolute-expired@example.test")).Outcome.ShouldBe(
                "unauthenticated",
                "the idle deadline is two hours away, so only absolute_expires_at ends this session");
        }
        finally
        {
            // The database is this test's own: the row that violates the check goes before the check
            // comes back, or the re-add would refuse to validate.
            await OwnerExecuteAsync(
                "delete from public.session where cookie_hmac = @cookie_hmac",
                ("cookie_hmac", cookie));
            await OwnerExecuteAsync(
                "alter table public.session add constraint session_expiry_check check (expires_at <= absolute_expires_at)");
        }

        (await OwnerScalarAsync<bool>(
            "select exists (select 1 from pg_catalog.pg_constraint "
            + "where conrelid = 'public.session'::regclass and conname = 'session_expiry_check')"))
            .ShouldBeTrue("the check is back before the next case runs");
    }

    /// <summary>
    /// The privilege posture of 0044, the 02.9.1 §8 T1 rule the login resolvers follow: EXECUTE is the
    /// app role's only way in. A direct read of <c>session</c> is <c>permission denied</c> (not zero
    /// rows), and a runtime role that was not granted the functions cannot call them either.
    /// </summary>
    [Fact]
    public async Task Link_IsTheAppRolesOnlyWayIn()
    {
        var userId = await SeedUserAsync("privileges@example.com");
        var cookie = ExpectedHmac("privileges");
        await SeedSessionAsync(userId, cookie);

        (await LinkAsync([cookie], Provider, $"privileges-{Guid.NewGuid():N}", "privileges@example.test"))
            .Outcome.ShouldBe("linked");

        foreach (var table in PlatformTables)
        {
            var tableRefused = await AppRefusalAsync($"select count(*) from public.\"{table}\"");
            tableRefused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, $"{table}: evidgate_app holds no SELECT");
        }

        await using var asOperator = await OpenAsRoleAsync("evidgate_operator");

        await using var command = asOperator.CreateCommand();
        command.CommandText =
            "select outcome, moved_from_user_id from evidgate_resolve.login_link_identity("
            + "@cookie_hmacs, @now, @provider, @subject, @email, @identity_id)";
        command.Parameters.AddWithValue("cookie_hmacs", new[] { cookie });
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("provider", Provider);
        command.Parameters.AddWithValue("subject", $"operator-{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("email", "operator@example.test");
        command.Parameters.AddWithValue("identity_id", Guid.CreateVersion7());

        var operatorRefusal = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Token));
        operatorRefusal.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, "login_link_identity");
    }

    // ------------------------------------------------------------------ login_link_identity (AC6)

    /// <summary>A free <c>(provider, subject)</c> is inserted on the target user, with the address the provider reported.</summary>
    [Fact]
    public async Task Link_InsertsTheIdentity()
    {
        var userId = await SeedUserAsync("first-provider@example.com");
        var subject = $"first-{Guid.NewGuid():N}";

        var cookie = await SignedInAsync(userId, "first");
        var (outcome, movedFrom) = await LinkAsync(cookie, Provider, subject, "first-provider@example.com");

        outcome.ShouldBe("linked");
        movedFrom.ShouldBeNull();

        var row = await IdentityRowAsync(Provider, subject);
        row[0].ShouldBe(userId);
        row[1].ShouldBe("first-provider@example.com");
    }

    /// <summary>A second explicit confirm of the same identity on the same account writes nothing and says so.</summary>
    [Fact]
    public async Task Link_AnswersAlreadyLinked_OnTheSameUser()
    {
        var userId = await SeedUserAsync("second-confirm@example.com");
        var subject = $"repeat-{Guid.NewGuid():N}";

        var cookie = await SignedInAsync(userId, "second-confirm");
        (await LinkAsync(cookie, Provider, subject, "first@example.test")).Outcome.ShouldBe("linked");

        var (outcome, movedFrom) = await LinkAsync(cookie, Provider, subject, "changed@example.test");

        outcome.ShouldBe("already_linked");
        movedFrom.ShouldBeNull();

        // The row is not rewritten by a repeated confirm: `email_at_provider` is what the provider
        // reported when the identity was linked, and the login upsert (0042) is the writer that refreshes
        // it on a real login.
        (await IdentityRowAsync(Provider, subject))[1].ShouldBe("first@example.test");
    }

    /// <summary>
    /// A link never renames the account and has no argument to try with: <c>user.display_name</c> belongs to the
    /// login upsert of 0042, and the function's signature carries no profile name (F3 of the 02.3.1-T2 review
    /// removed the parameter that was accepted and never written).
    /// </summary>
    [Fact]
    public async Task Link_NeitherRenamesTheAccountNorTakesAProfileName()
    {
        var userId = await SeedUserAsync("no-rename@example.com", displayName: "First Name");
        var subject = $"rename-{Guid.NewGuid():N}";

        var cookie = await SignedInAsync(userId, "no-rename");
        (await LinkAsync(cookie, Provider, subject, "no-rename@example.com")).Outcome.ShouldBe("linked");

        (await OwnerScalarAsync<string>("select display_name from public.\"user\" where id = @id", ("id", userId)))
            .ShouldBe("First Name");

        // The removed shape is not a function at all: a caller that still passes a profile name gets 42883 rather
        // than an argument that is silently ignored. The hash value is irrelevant — the call never resolves a
        // function — but it must be typed as the `bytea[]` the surviving signature takes, so the refusal comes
        // from the server's resolution and not from binding a string array to that parameter.
        var refused = await AppRefusalAsync(
            "select outcome, moved_from_user_id from evidgate_resolve.login_link_identity("
            + "@cookie_hmacs, @now, @provider, @subject, @email, @display_name, @identity_id)",
            ("cookie_hmacs", new[] { new byte[32] }),
            ("now", DateTimeOffset.UtcNow),
            ("provider", Provider),
            ("subject", $"{subject}-extra"),
            ("email", "no-rename@example.com"),
            ("display_name", "Second Name"),
            ("identity_id", Guid.CreateVersion7()));

        refused.SqlState.ShouldBe(PostgresErrorCodes.UndefinedFunction);
    }

    /// <summary>
    /// The move AC6 allows: the identity sits on an account with no membership (any status) and no live
    /// session, so it can be attached to the confirming user, which is told where it came from.
    /// </summary>
    [Fact]
    public async Task Link_Moves_FromAnEmptyUser()
    {
        var userId = await SeedUserAsync("move-target@example.com");
        var emptyUserId = await SeedUserAsync("move-source@example.com");
        var subject = $"move-{Guid.NewGuid():N}";

        await SeedIdentityAsync(emptyUserId, Provider, subject, "source@example.test");

        var cookie = await SignedInAsync(userId, "move-target");
        var (outcome, movedFrom) = await LinkAsync(cookie, Provider, subject, "source@example.test");

        outcome.ShouldBe("moved");
        movedFrom.ShouldBe(emptyUserId);
        (await IdentityRowAsync(Provider, subject))[0].ShouldBe(userId);
    }

    /// <summary>
    /// A membership on the other account — even a `pending` invite or a `removed` record — is a person
    /// with a foothold in an org, so the identity is not taken and nothing changes.
    /// </summary>
    [Fact]
    public async Task Link_RefusesToMove_WhenTheOtherUserHasAMembership()
    {
        var userId = await SeedUserAsync("member-target@example.com");
        var memberId = await SeedUserAsync("member-source@example.com");
        var subject = $"member-{Guid.NewGuid():N}";

        await SeedIdentityAsync(memberId, Provider, subject, "member@example.test");
        await SeedMembershipAsync(await Fixture.SeedOrgAsync(cancellationToken: Token), memberId, "pending");

        var cookie = await SignedInAsync(userId, "member-target");
        var (outcome, movedFrom) = await LinkAsync(cookie, Provider, subject, "member@example.test");

        outcome.ShouldBe("conflict");
        movedFrom.ShouldBeNull();
        (await IdentityRowAsync(Provider, subject))[0].ShouldBe(memberId);
    }

    /// <summary>The same refusal for a live session: an account someone is signed in to is in use.</summary>
    [Fact]
    public async Task Link_RefusesToMove_WhenTheOtherUserHasALiveSession()
    {
        var userId = await SeedUserAsync("session-target@example.com");
        var signedInId = await SeedUserAsync("session-source@example.com");
        var subject = $"session-{Guid.NewGuid():N}";

        await SeedIdentityAsync(signedInId, Provider, subject, "signed-in@example.test");
        await SeedSessionAsync(signedInId, ExpectedHmac("signed-in"));

        var cookie = await SignedInAsync(userId, "session-target");
        var (outcome, movedFrom) = await LinkAsync(cookie, Provider, subject, "signed-in@example.test");

        outcome.ShouldBe("conflict");
        movedFrom.ShouldBeNull();
        (await IdentityRowAsync(Provider, subject))[0].ShouldBe(signedInId);
    }

    /// <summary>
    /// The boundary of that rule, both ways: a revoked session and an expired one are not "live", so
    /// the identity is moved — otherwise a user who cleared their sessions could never re-link.
    /// </summary>
    [Fact]
    public async Task Link_Moves_WhenTheOtherUsersSessionsAreRevokedOrExpired()
    {
        var userId = await SeedUserAsync("stale-target@example.com");
        var staleId = await SeedUserAsync("stale-source@example.com");
        var subject = $"stale-{Guid.NewGuid():N}";

        await SeedIdentityAsync(staleId, Provider, subject, "stale@example.test");
        await SeedSessionAsync(staleId, ExpectedHmac("stale-revoked"), revokedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        await SeedSessionAsync(
            staleId,
            ExpectedHmac("stale-expired"),
            expiresAt: DateTimeOffset.UtcNow.AddHours(-2),
            absoluteExpiresAt: DateTimeOffset.UtcNow.AddHours(-1));

        var cookie = await SignedInAsync(userId, "stale-target");
        var (outcome, movedFrom) = await LinkAsync(cookie, Provider, subject, "stale@example.test");

        outcome.ShouldBe("moved");
        movedFrom.ShouldBe(staleId);
        (await IdentityRowAsync(Provider, subject))[0].ShouldBe(userId);
    }

    /// <summary>An unknown cookie is a value, never an exception: it answers <c>unauthenticated</c> and no identity row appears.</summary>
    [Fact]
    public async Task Link_AnswersUnauthenticated_ForAnUnknownCookie_AndWritesNothing()
    {
        var subject = $"unknown-{Guid.NewGuid():N}";

        var (outcome, movedFrom) = await LinkAsync([ExpectedHmac("never-minted")], Provider, subject, "ghost@example.test");

        outcome.ShouldBe("unauthenticated");
        movedFrom.ShouldBeNull();
        (await OwnerScalarAsync<long>("select count(*) from public.user_identity")).ShouldBe(0);
    }

    /// <summary>A disabled account cannot be linked to (`active -&gt; disabled` is one-way, 02.3.1 §6).</summary>
    [Fact]
    public async Task Link_AnswersDisabled_AndWritesNothing()
    {
        var userId = await SeedDisabledUserAsync("disabled-link@example.com");
        var subject = $"disabled-{Guid.NewGuid():N}";
        var cookie = await SignedInAsync(userId, "disabled-link");

        var (outcome, movedFrom) = await LinkAsync(cookie, Provider, subject, "disabled-link@example.com");

        outcome.ShouldBe("disabled");
        movedFrom.ShouldBeNull();
        (await OwnerScalarAsync<long>("select count(*) from public.user_identity")).ShouldBe(0);
    }

    /// <summary>
    /// Two confirms of one identity on one account, at once: the user row lock serialises them and the
    /// unique key is the backstop, so the end state is one identity row and one <c>linked</c>.
    /// </summary>
    [Fact]
    public async Task ConcurrentDoubleLink_EndsWithOneIdentityRow()
    {
        var userId = await SeedUserAsync("concurrent@example.com");
        var subject = $"concurrent-{Guid.NewGuid():N}";

        var cookie = await SignedInAsync(userId, "concurrent");

        var results = await Task.WhenAll(
            LinkAsync(cookie, Provider, subject, "concurrent@example.test"),
            LinkAsync(cookie, Provider, subject, "concurrent@example.test"));

        var outcomes = results
            .Select(result => result.Outcome)
            .OrderBy(outcome => outcome, StringComparer.Ordinal)
            .ToArray();

        string[] expected = ["already_linked", "linked"];
        outcomes.ShouldBe(expected);
        (await OwnerScalarAsync<long>(
            "select count(*) from public.user_identity where provider = @provider and subject = @subject",
            ("provider", Provider),
            ("subject", subject))).ShouldBe(1);
    }

    // ------------------------------------------------------------------ arrangement

    /// <summary>A disabled account, seeded through the owner seam (the status column has no writer yet).</summary>
    private async Task<Guid> SeedDisabledUserAsync(string email)
    {
        var id = Guid.CreateVersion7();

        await Fixture.SeedAsync(
            "insert into public.\"user\" (id, email, status) values (@id, @email, 'disabled')",
            [("id", id), ("email", email)],
            Token);

        return id;
    }

    /// <summary>
    /// One session row, live by default. The two expiries respect 0041's
    /// <c>session_expiry_check</c> (<c>expires_at &lt;= absolute_expires_at</c>) in every case.
    /// </summary>
    private Task<int> SeedSessionAsync(
        Guid userId,
        byte[] cookieHmac,
        int pepperVersion = PepperVersion,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? absoluteExpiresAt = null,
        DateTimeOffset? revokedAt = null)
    {
        var now = DateTimeOffset.UtcNow;

        return Fixture.SeedAsync(
            """
            insert into public.session
                (id, user_id, cookie_hmac, pepper_version, expires_at, absolute_expires_at, revoked_at)
            values
                (@id, @user_id, @cookie_hmac, @pepper_version, @expires_at, @absolute_expires_at, @revoked_at)
            """,
            [
                ("id", Guid.CreateVersion7()),
                ("user_id", userId),
                ("cookie_hmac", cookieHmac),
                ("pepper_version", pepperVersion),
                ("expires_at", expiresAt ?? now.AddHours(1)),
                ("absolute_expires_at", absoluteExpiresAt ?? now.AddDays(7)),
                ("revoked_at", (object?)revokedAt ?? DBNull.Value),
            ],
            Token);
    }

    /// <summary>One identity row on a user, as 0042's login upsert would have written it.</summary>
    private Task<int> SeedIdentityAsync(Guid userId, string provider, string subject, string? email) =>
        Fixture.SeedAsync(
            """
            insert into public.user_identity (id, user_id, provider, subject, email_at_provider)
            values (@id, @user_id, @provider, @subject, @email)
            """,
            [
                ("id", Guid.CreateVersion7()),
                ("user_id", userId),
                ("provider", provider),
                ("subject", subject),
                ("email", (object?)email ?? DBNull.Value),
            ],
            Token);

    /// <summary>
    /// One membership row. 0044's foreign key needs a real user row, and the seeded user is the one the
    /// caller names — this is the fixture shape the key makes mandatory.
    /// </summary>
    private Task<int> SeedMembershipAsync(Guid orgId, Guid userId, string status)
    {
        var id = Guid.CreateVersion7();

        return Fixture.SeedAsync(
            """
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values (@id, @org_id, @user_id, @email, @status)
            """,
            [
                ("id", id),
                ("org_id", orgId),
                ("user_id", userId),
                ("email", $"m-{id:N}@example.test"),
                ("status", status),
            ],
            Token);
    }

    /// <summary>
    /// One live session for <paramref name="userId"/>, returned as the candidate hashes a link call presents. The
    /// caller holds the cookie, never the user id, so every link is made through a session.
    /// </summary>
    private async Task<byte[][]> SignedInAsync(Guid userId, string label)
    {
        var cookieHmac = ExpectedHmac(label);

        await SeedSessionAsync(userId, cookieHmac);

        return [cookieHmac];
    }

    /// <summary>
    /// <c>evidgate_resolve.login_link_identity(...)</c> as the app role, no tenant setting. The function takes the
    /// candidate cookie hashes, never a user id: it resolves the live session and its account from them. There is
    /// no profile-name argument: the function has none, so the signature this call builds is the whole contract.
    /// </summary>
    private async Task<(string Outcome, Guid? MovedFrom)> LinkAsync(
        byte[][] cookieHmacs,
        string provider,
        string subject,
        string email)
    {
        await using var command = AppDataSource.CreateCommand(
            "select outcome, moved_from_user_id from evidgate_resolve.login_link_identity("
            + "@cookie_hmacs, @now, @provider, @subject, @email, @identity_id)");
        command.Parameters.AddWithValue("cookie_hmacs", cookieHmacs);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("provider", provider);
        command.Parameters.AddWithValue("subject", subject);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("identity_id", Guid.CreateVersion7());

        await using var reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).ShouldBeTrue();

        var outcome = reader.GetString(0);
        var movedFrom = await reader.IsDBNullAsync(1, Token) ? (Guid?)null : reader.GetGuid(1);

        return (outcome, movedFrom);
    }

    /// <summary>The identity row's <c>user_id</c> and <c>email_at_provider</c>, read as the owner.</summary>
    private Task<object?[]> IdentityRowAsync(string provider, string subject) =>
        OwnerRowAsync(
            "select user_id, email_at_provider from public.user_identity where provider = @provider and subject = @subject",
            ("provider", provider),
            ("subject", subject));

    /// <summary>Opens a superuser connection switched to <paramref name="role"/> (the harness has no password for it).</summary>
    private async Task<NpgsqlConnection> OpenAsRoleAsync(string role)
    {
        var database = new NpgsqlConnectionStringBuilder(Fixture.AppConnectionString).Database!;
        var connection = await Fixture.OpenSuperuserAsync(database, Token);

        await using var switchRole = connection.CreateCommand();
        switchRole.CommandText = $"set role {role}";
        await switchRole.ExecuteNonQueryAsync(Token);

        return connection;
    }
}
