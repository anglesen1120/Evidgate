using System.Diagnostics;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Persistence;

/// <summary>
/// The audit half of the link resolver: the audited definer reads the acting user BEFORE the inner writer, so a
/// credential revoked while the link waits cannot leave a committed link whose rows name nobody. A linked outcome
/// with no pre-read actor aborts the call; that guard needs a session to become live between the two statements,
/// which no arrangement can drive, so the ordering assertion in the migration suite covers it instead.
/// </summary>
public sealed partial class LinkResolverTests
{
    /// <summary>How long a concurrency case waits for an observable lock state or a finished call.</summary>
    private static readonly TimeSpan LockWaitLimit = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A link whose credential is revoked after the definer read the actor still writes the actor's
    /// <c>auth.link</c> row: the row is the one the lock-free pre-read saw, not the state after the link.
    /// </summary>
    [Fact]
    public async Task A_Link_That_Wins_After_Its_Session_Is_Revoked_Names_The_Pre_Read_Actor()
    {
        var userId = await SeedUserAsync("revoke-race@example.com");
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await SeedMembershipAsync(org, userId, "active");
        var cookie = ExpectedHmac("revoke-race");
        await SeedSessionAsync(userId, cookie);
        var subject = $"revoke-race-{Guid.NewGuid():N}";

        // The account row is what the inner writer locks: holding it parks the call after its actor read and
        // before the identity write.
        await using var holder = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await holder.OpenAsync(Token);
        await using var held = await holder.BeginTransactionAsync(Token);
        await ExecuteAsync(
            holder,
            held,
            "select 1 from public.\"user\" where id = @id for no key update",
            ("id", userId));

        await using var linker = await AppDataSource.OpenConnectionAsync(Token);

        var pending = Task.Run(
            () => LinkOnAsync(linker, [cookie], subject, "revoke-race@example.test"),
            Token);

        await WaitForParkedAsync(linker.ProcessID);
        pending.IsCompleted.ShouldBeFalse("the link parks on the account row");

        // Committed while the link waits: only the actor read before it can attribute the rows now.
        await OwnerExecuteAsync(
            "update public.session set revoked_at = clock_timestamp() where cookie_hmac = @hmac",
            ("hmac", cookie));

        await held.CommitAsync(Token);

        var (outcome, _) = await pending.WaitAsync(LockWaitLimit, Token);

        outcome.ShouldBe("linked");
        (await IdentityRowAsync(Provider, subject))[0].ShouldBe(userId);
        (await OwnerScalarAsync<long>(
            "select count(*) from public.audit_log "
            + "where org_id = @org and action = 'auth.link' and actor_id = @actor",
            ("org", org),
            ("actor", userId.ToString()))).ShouldBe(
            1L, "the row names the actor the credential resolved before the link");
    }

    /// <summary>Whether the app role holds EXECUTE on one function of the resolver schema.</summary>
    private Task<bool> HasExecuteAsync(string role, string function) =>
        OwnerScalarAsync<bool>(
            "select pg_catalog.has_function_privilege(@role::name, @function::text, 'execute')",
            ("role", role),
            ("function", function));

    /// <summary>One audited link on a caller-owned app connection, so the case can observe its backend.</summary>
    private static async Task<(string Outcome, Guid? MovedFrom)> LinkOnAsync(
        NpgsqlConnection connection,
        byte[][] cookieHmacs,
        string subject,
        string email)
    {
        await using var command = new NpgsqlCommand(
            "select outcome, moved_from_user_id from evidgate_resolve.login_link_identity_audited("
            + "@cookie_hmacs, @now, @provider, @subject, @email, @identity_id)",
            connection);
        command.Parameters.AddWithValue("cookie_hmacs", cookieHmacs);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("provider", Provider);
        command.Parameters.AddWithValue("subject", subject);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("identity_id", Guid.CreateVersion7());

        await using var reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).ShouldBeTrue();

        var outcome = reader.GetString(0);
        var movedFrom = await reader.IsDBNullAsync(1, Token) ? (Guid?)null : reader.GetGuid(1);

        return (outcome, movedFrom);
    }

    /// <summary>Polls <c>pg_locks</c> until <paramref name="pid"/> is queued on any lock it cannot have yet.</summary>
    private async Task WaitForParkedAsync(int pid)
    {
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < LockWaitLimit)
        {
            var waiting = await OwnerScalarAsync<long>(
                "select count(*) from pg_locks where pid = @pid and not granted",
                ("pid", pid));

            if (waiting >= 1)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), Token);
        }

        throw new TimeoutException($"the backend {pid} never queued on the account row lock");
    }

    /// <summary>Runs one statement on a caller-owned connection.</summary>
    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        params (string Name, object Value)[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Token);
    }
}
