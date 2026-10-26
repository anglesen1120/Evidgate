using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Testing;
using Npgsql;
using NpgsqlTypes;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Store;

/// <summary>
/// 02.3.2-T2.1's sweep (<c>evidgate.session_cleanup</c>, migration 0048) end to end through
/// <see cref="SessionRoutesStore.CleanupAsync"/>: it stamps a dead row once, deletes only rows whose LAST
/// deadline passed more than 30 days ago, unwinds a rotation chain from its tip, never touches a live row,
/// is idempotent, and stops at the batch limit.
/// </summary>
/// <remarks>
/// Rows are arranged with the owner connection and read back the same way (the app role holds no privilege on
/// <c>public.session</c>, 0041). The batch itself runs through the store over the sweep connection — the same
/// call the <c>session-cleanup</c> task makes — so the SQL of this task's half is exercised, not a copy of it.
/// </remarks>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class SessionCleanupSqlTests : SessionHardeningTestBase
{
    public SessionCleanupSqlTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// A row past idle and a row past its absolute deadline are stamped once; a live row and a revoked row are
    /// not stamped (revocation is the state a revoked row reached) and a revocation inside the window keeps
    /// its row.
    /// </summary>
    [Fact]
    public async Task Cleanup_Stamps_Dead_Rows_Once_And_Never_A_Live_One()
    {
        var clock = ClockAt();
        var now = clock.UtcNow;
        var user = await SeedUserAsync("cleanup-stamp@example.com");

        var live = await InsertSessionAsync(user, now.AddHours(8), now.AddDays(7));
        var pastIdle = await InsertSessionAsync(user, now.AddMinutes(-1), now.AddDays(7));
        var pastAbsolute = await InsertSessionAsync(user, now.AddHours(-9), now.AddMinutes(-2));
        var revoked = await InsertSessionAsync(
            user, now.AddHours(-6), now.AddHours(-5), revokedAt: now.AddDays(-5));

        var store = CreateSweep();

        var first = await store.CleanupAsync(now, 100, Token);
        first.Stamped.ShouldBe(2, "the two unrevoked rows past a deadline are stamped");
        first.Deleted.ShouldBe(0, "nothing is past the 30-day window yet");

        (await IsStampedAsync(pastIdle, now)).ShouldBeTrue();
        (await IsStampedAsync(pastAbsolute, now)).ShouldBeTrue();
        (await IsStampedAsync(live, now)).ShouldBeFalse("a live session is never stamped");
        (await IsStampedAsync(revoked, now)).ShouldBeFalse("revocation is the state a revoked row reached");
        (await ExistsAsync(revoked)).ShouldBeTrue("a revocation inside the window keeps its row");

        var second = await store.CleanupAsync(now, 100, Token);
        second.Stamped.ShouldBe(0, "a stamped row is not stamped again");
        second.Deleted.ShouldBe(0);
    }

    /// <summary>
    /// A rotation chain whose rows all died more than 30 days ago unwinds from its tip: <c>session.rotated_from</c>
    /// is a self-FK with no <c>on delete</c>, so a row a remaining row still references cannot be removed, and
    /// each call deletes the rows whose successors are already gone.
    /// </summary>
    [Fact]
    public async Task Cleanup_Deletes_Old_Rows_Leaf_First_And_Is_Idempotent()
    {
        var clock = ClockAt();
        var now = clock.UtcNow;
        var user = await SeedUserAsync("cleanup-chain@example.com");
        var store = CreateSweep();

        var live = await InsertSessionAsync(user, now.AddHours(8), now.AddDays(7));
        var root = await InsertSessionAsync(
            user, now.AddDays(-62), now.AddDays(-61), revokedAt: now.AddDays(-61));
        var middle = await InsertSessionAsync(
            user, now.AddDays(-62), now.AddDays(-61), revokedAt: now.AddDays(-61), rotatedFrom: root);
        var tip = await InsertSessionAsync(
            user, now.AddDays(-62), now.AddDays(-61), revokedAt: now.AddDays(-61), rotatedFrom: middle);
        var recent = await InsertSessionAsync(
            user, now.AddDays(-6), now.AddDays(-5), revokedAt: now.AddDays(-5));

        var first = await store.CleanupAsync(now, 100, Token);
        first.Deleted.ShouldBe(1, "only the tip has no successor to satisfy the self-FK for");
        (await ExistsAsync(tip)).ShouldBeFalse();
        (await ExistsAsync(middle)).ShouldBeTrue("its successor is only gone after this call");

        var second = await store.CleanupAsync(now, 100, Token);
        second.Deleted.ShouldBe(1);
        (await ExistsAsync(middle)).ShouldBeFalse();

        var third = await store.CleanupAsync(now, 100, Token);
        third.Deleted.ShouldBe(1);
        (await ExistsAsync(root)).ShouldBeFalse();

        var fourth = await store.CleanupAsync(now, 100, Token);
        fourth.Deleted.ShouldBe(0, "the chain is gone; a rerun changes nothing");
        fourth.Stamped.ShouldBe(0);

        (await ExistsAsync(recent)).ShouldBeTrue("a revocation inside the window keeps its row");
        (await ExistsAsync(live)).ShouldBeTrue("a live session is never deleted");
    }

    /// <summary>One call removes at most the batch limit, so a long backlog is finished over several fires.</summary>
    [Fact]
    public async Task Cleanup_Is_Bounded_By_The_Limit()
    {
        var clock = ClockAt();
        var now = clock.UtcNow;
        var user = await SeedUserAsync("cleanup-bound@example.com");
        var store = CreateSweep();

        var dead = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            dead.Add(await InsertSessionAsync(
                user,
                now.AddDays(-41 - index),
                now.AddDays(-40 - index),
                revokedAt: now.AddDays(-40 - index)));
        }

        (await store.CleanupAsync(now, 2, Token)).Deleted.ShouldBe(2);
        (await store.CleanupAsync(now, 2, Token)).Deleted.ShouldBe(1);
        (await store.CleanupAsync(now, 2, Token)).Deleted.ShouldBe(0);

        foreach (var id in dead)
        {
            (await ExistsAsync(id)).ShouldBeFalse();
        }
    }

    /// <summary>
    /// The function's own guard, called directly: a zero, a negative or an absent limit sweeps nothing, and an
    /// absent clock sweeps nothing even with a real limit — a caller that does not know "now" must not delete.
    /// </summary>
    [Fact]
    public async Task The_Sweep_Refuses_An_Absent_Limit_And_An_Absent_Clock()
    {
        var clock = ClockAt();
        var now = clock.UtcNow;
        var user = await SeedUserAsync("cleanup-absent@example.com");

        var dead = await InsertSessionAsync(user, now.AddMinutes(-1), now.AddDays(7));
        var old = await InsertSessionAsync(
            user, now.AddDays(-41), now.AddDays(-40), revokedAt: now.AddDays(-40));

        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);

        (await SweepAsync(connection, now, limit: 0)).ShouldBe((0, 0));
        (await SweepAsync(connection, now, limit: -5)).ShouldBe((0, 0));
        (await SweepAsync(connection, now, limit: null)).ShouldBe((0, 0));
        (await SweepAsync(connection, pNow: null, limit: 10_000)).ShouldBe((0, 0), "an absent clock sweeps nothing");

        (await IsStampedAsync(dead, now)).ShouldBeFalse();
        (await ExistsAsync(old)).ShouldBeTrue("a bounded call no-ops instead of sweeping anyway");

        (await SweepAsync(connection, now, limit: 10_000)).ShouldBe((1, 1), "a real call then does the work");
    }

    /// <summary>The store over the sweep seam, with the owner connection standing in for the sweep role.</summary>
    private SessionRoutesStore CreateSweep() =>
        new(AppDataSource, new OwnerSweep(Fixture.OwnerConnectionString), TimeProvider.System);

    /// <summary>One session row written directly, so each deadline is exact.</summary>
    /// <param name="userId">The account the session belongs to.</param>
    /// <param name="expiresAt">The idle deadline.</param>
    /// <param name="absoluteExpiresAt">The absolute deadline.</param>
    /// <param name="revokedAt">When the row was revoked, or null for a live row.</param>
    /// <param name="rotatedFrom">The row this session rotated out of, or null.</param>
    private async Task<Guid> InsertSessionAsync(
        Guid userId,
        DateTimeOffset expiresAt,
        DateTimeOffset absoluteExpiresAt,
        DateTimeOffset? revokedAt = null,
        Guid? rotatedFrom = null)
    {
        var id = Guid.CreateVersion7();
        var createdAt = expiresAt < absoluteExpiresAt ? expiresAt.AddDays(-1) : absoluteExpiresAt.AddDays(-1);

        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into public.session
                (id, user_id, cookie_hmac, pepper_version, created_at, last_seen_at, expires_at,
                 absolute_expires_at, revoked_at, rotated_from)
            values (@id, @user_id, @hmac, @version, @created, @created, @expires,
                    @absolute, @revoked_at, @rotated_from)
            """;
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id });
        command.Parameters.Add(new NpgsqlParameter("user_id", NpgsqlDbType.Uuid) { Value = userId });
        command.Parameters.Add(new NpgsqlParameter("hmac", NpgsqlDbType.Bytea)
        {
            Value = HmacOf(Pepper, UnknownCookieValue()),
        });
        command.Parameters.Add(new NpgsqlParameter("version", NpgsqlDbType.Integer) { Value = CurrentVersion });
        command.Parameters.Add(new NpgsqlParameter("created", NpgsqlDbType.TimestampTz) { Value = createdAt });
        command.Parameters.Add(new NpgsqlParameter("expires", NpgsqlDbType.TimestampTz) { Value = expiresAt });
        command.Parameters.Add(new NpgsqlParameter("absolute", NpgsqlDbType.TimestampTz) { Value = absoluteExpiresAt });
        command.Parameters.Add(new NpgsqlParameter("revoked_at", NpgsqlDbType.TimestampTz)
        {
            Value = (object?)revokedAt ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("rotated_from", NpgsqlDbType.Uuid)
        {
            Value = (object?)rotatedFrom ?? DBNull.Value,
        });

        await command.ExecuteNonQueryAsync(Token);
        return id;
    }

    /// <summary>One direct call of the sweep function, with an optionally absent clock or limit.</summary>
    /// <param name="connection">The owner connection.</param>
    /// <param name="pNow">The instant, or null to exercise the refusal.</param>
    /// <param name="limit">The batch size, or null to exercise the refusal.</param>
    private static async Task<(int Stamped, int Deleted)> SweepAsync(
        NpgsqlConnection connection,
        DateTimeOffset? pNow,
        int? limit)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select stamped, deleted from evidgate.session_cleanup(@now, @limit)";
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz)
        {
            Value = pNow is { } instant ? instant : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer)
        {
            Value = (object?)limit ?? DBNull.Value,
        });

        await using var reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).ShouldBeTrue("the sweep always answers one row");

        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    /// <summary>Whether the row still exists.</summary>
    /// <param name="id">The session id.</param>
    private Task<bool> ExistsAsync(Guid id) =>
        OwnerScalarAsync<bool>("select exists (select 1 from public.session where id = @id)", ("id", id));

    /// <summary>Whether the row carries the stamp, at exactly the instant the sweep was given.</summary>
    /// <param name="id">The session id.</param>
    /// <param name="now">The instant the sweep judged with.</param>
    private Task<bool> IsStampedAsync(Guid id, DateTimeOffset now) =>
        OwnerScalarAsync<bool>(
            "select coalesce(expired_at = @now, false) from public.session where id = @id",
            ("id", id),
            ("now", now));

    /// <summary>The sweep seam over the owner connection the suite already owns.</summary>
    /// <param name="connectionString">The owner connection string.</param>
    private sealed class OwnerSweep(string connectionString) : IWorkerSchedDataSource
    {
        public async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
        {
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
    }
}
