using Npgsql;
using Shouldly;

namespace Identity.IntegrationTests.Login.Persistence;

/// <summary>
/// The arrangement of the link resolver cases: rows seeded through the owner seam, the credential hashes a call
/// presents, the call itself and the role connection used for a privilege probe.
/// </summary>
public sealed partial class LinkResolverTests
{
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
    /// <c>evidgate_resolve.login_link_identity_audited(...)</c> as the app role, no tenant setting: the one link
    /// entry point the role holds. The function takes the candidate cookie hashes, never a user id, and appends
    /// the rows the link owes in its own transaction.
    /// </summary>
    private async Task<(string Outcome, Guid? MovedFrom)> LinkAsync(
        byte[][] cookieHmacs,
        string provider,
        string subject,
        string email)
    {
        await using var command = AppDataSource.CreateCommand(
            "select outcome, moved_from_user_id from evidgate_resolve.login_link_identity_audited("
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
