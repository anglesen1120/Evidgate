using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Options;
using Evidgate.Testing;
using Identity.IntegrationTests.Login.Persistence;
using Identity.IntegrationTests.Sessions.Http;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// What the weighted attempt owes a caller that holds the row: a live window already at its limit must refuse without
/// waiting for a lock, the guarded upsert must admit exactly one of two weighted racers, and the cost bound must hold
/// at both ends. Every wait observes <c>pg_stat_activity</c> or a SQLSTATE within a deadline; nothing sleeps and hopes.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RateLimitStoreCostRaceTests(DatabaseFixture fixture)
    : SessionTestBase(fixture, DatabaseIsolation.Dedicated)
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task Acquire_LiveRowsHeldByAnotherTransaction_RefusesAtTheLimitAndWaitsBelowIt()
    {
        var exhausted = Hash("locked-exhausted");
        var available = Hash("locked-available");
        await SeedAsync(exhausted, hitCount: 3);
        await SeedAsync(available, hitCount: 2);
        await using var pool = AppPool();
        await using var app = await pool.OpenConnectionAsync(Token);
        await ConfigureAsync(app, lockTimeout: "1s", statementTimeout: "5s", cancellationToken: Token);
        await using var owner = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await owner.OpenAsync(Token);
        await using var transaction = await owner.BeginTransactionAsync(Token);
        await LockRowAsync(owner, transaction, exhausted);
        await LockRowAsync(owner, transaction, available);

        var refused = await AcquireAsync(app, exhausted, limit: 3, cost: 2, cancellationToken: Token);
        refused.Allowed.ShouldBeFalse();
        refused.RetryAfterSeconds.ShouldBeGreaterThanOrEqualTo(1);

        // The requirement is "while the owner transaction stays open": an assigned xid proves the locking
        // transaction is still live here, and the statement itself would fail inside a rolled-back transaction.
        await using var stillOpen = new NpgsqlCommand(
            "select pg_current_xact_id_if_assigned() is not null", owner, transaction);
        ((bool)(await stillOpen.ExecuteScalarAsync(Token))!)
            .ShouldBeTrue("the owner transaction is still open when the refusal returns");

        // The 55P03 below is this case's positive control: it can only fire while the owner still holds the row
        // lock, so the refusal above returned without taking or waiting for that lock.
        var blocked = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await AcquireAsync(app, available, limit: 3, cost: 2, cancellationToken: Token);
        });
        blocked.SqlState.ShouldBe(
            PostgresErrorCodes.LockNotAvailable,
            "a below-limit row must reach the guarded upsert and wait for the same owner's row lock");

        await transaction.RollbackAsync(Token);
        (await AcquireAsync(app, available, limit: 3, cost: 2, cancellationToken: Token)).ShouldBe(
            new RateDecision(true, 0), "with the lock released the same call is admitted");
    }

    [Fact]
    public async Task Acquire_TwoCostTwoCallsBlockedOnOneRow_AdmitExactlyOneAndStoreFour()
    {
        var hash = Hash("weighted-race");
        await SeedAsync(hash, hitCount: 2);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        await using var pool = AppPool();
        await using var owner = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await owner.OpenAsync(deadline.Token);
        await using var transaction = await owner.BeginTransactionAsync(deadline.Token);
        await LockRowAsync(owner, transaction, hash);
        await using var first = await pool.OpenConnectionAsync(deadline.Token);
        await using var second = await pool.OpenConnectionAsync(deadline.Token);
        await ConfigureAsync(first, lockTimeout: "12s", statementTimeout: "15s", cancellationToken: deadline.Token);
        await ConfigureAsync(second, lockTimeout: "12s", statementTimeout: "15s", cancellationToken: deadline.Token);

        var pending = Task.WhenAll(
            AcquireAsync(first, hash, limit: 3, cost: 2, cancellationToken: deadline.Token),
            AcquireAsync(second, hash, limit: 3, cost: 2, cancellationToken: deadline.Token));
        bool bothParked;
        try
        {
            bothParked = await WaitForBothRowWaitersAsync(
                owner.ProcessID, [first.ProcessID, second.ProcessID], deadline.Token);
        }
        finally
        {
            // Release the holder even when observation fails, so both calls end on their own bounds.
            await transaction.RollbackAsync(CancellationToken.None);
        }

        var results = await pending;
        bothParked.ShouldBeTrue("both weighted calls must wait on the locked row before the holder releases it");
        results.Count(result => result.Allowed).ShouldBe(1);
        results.Count(result => !result.Allowed).ShouldBe(1);
        results.Single(result => result.Allowed).RetryAfterSeconds.ShouldBe(0);
        results.Single(result => !result.Allowed).RetryAfterSeconds.ShouldBeGreaterThanOrEqualTo(1);
        (await RowAsync(hash)).HitCount.ShouldBe(4, "the admitted cost-2 call is clamped at limit plus one");
    }

    [Fact]
    public async Task Acquire_CostSixteenOfTwentyInAnEmptyWindow_StoresSixteen()
    {
        var limiter = Limiter(AppDataSource, limit: 20);

        (await limiter.AcquireAsync(RateSurface.Login, "sixteen-empty", 16, Token))
            .ShouldBe(new RateDecision(true, 0));
        (await RowAsync(Hash("sixteen-empty"))).HitCount.ShouldBe(16);
    }

    [Fact]
    public async Task Acquire_CostSixteenOverTwoOfThree_StoresFourThenRefusesWithoutRewritingTheRow()
    {
        var hash = Hash("sixteen-live");
        await SeedAsync(hash, hitCount: 2);
        var limiter = Limiter(AppDataSource, limit: 3);

        (await limiter.AcquireAsync(RateSurface.Login, "sixteen-live", 16, Token))
            .ShouldBe(new RateDecision(true, 0));
        var admitted = await RowAsync(hash);
        admitted.HitCount.ShouldBe(4, "the whole cost is spent, clamped at limit plus one");

        var refused = await limiter.AcquireAsync(RateSurface.Login, "sixteen-live", 16, Token);
        refused.Allowed.ShouldBeFalse();
        refused.RetryAfterSeconds.ShouldBeGreaterThanOrEqualTo(1);
        (await RowAsync(hash)).ShouldBe(admitted, "a refusal on a live window changes neither count nor xmin/ctid");
    }

    [Fact]
    public async Task TheSixArgumentFunction_ANullIntegerCost_RefusesWithInvalidParameterValue()
    {
        var hash = Hash("null-cost");
        await using var app = await AppDataSource.OpenConnectionAsync(Token);
        (await AcquireAsync(app, hash, limit: 3, cost: 1, cancellationToken: Token))
            .ShouldBe(new RateDecision(true, 0));
        var before = await RowAsync(hash);

        var error = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await AcquireAsync(app, hash, limit: 3, cost: null, cancellationToken: Token);
        });

        error.SqlState.ShouldBe(PostgresErrorCodes.InvalidParameterValue);
        (await RowAsync(hash)).ShouldBe(before, "the invalid cost is refused before the window is written");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public async Task Acquire_ACostOutsideOneToSixteen_ThrowsBeforeSendingAnyStatement(int cost)
    {
        using var log = new StatementLog();
        var builder = new NpgsqlDataSourceBuilder(Fixture.AppConnectionString);
        builder.UseLoggerFactory(log);
        await using var source = builder.Build();
        var limiter = Limiter(source, limit: 3);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
        {
            await limiter.AcquireAsync(RateSurface.Login, "invalid-bound", cost, Token);
        });

        log.Snapshot().ShouldBeEmpty("an out-of-range cost must be refused before a statement reaches the store");
        (await limiter.AcquireAsync(RateSurface.Login, "invalid-bound", 1, Token)).Allowed.ShouldBeTrue();
        log.Containing("rate_limit_acquire").Count.ShouldBe(1, "the same limiter still serves a legal cost");
    }

    /// <summary>Only the login budget matters here, so the snapshot sets just that surface.</summary>
    private static ExchangeRateLimiter Limiter(NpgsqlDataSource source, int limit) =>
        new(source, Options.Create(new RateLimitOptions
        {
            LoginLimit = limit,
            LoginWindowSeconds = 3600,
            LoginMaxKeys = 50000,
        }));

    /// <summary>
    /// A pool private to one case: a session-level bound set on a connection would outlive a shared pool and change
    /// a sibling's lock behaviour, so the pool (not just the connection) is thrown away with the case.
    /// </summary>
    private NpgsqlDataSource AppPool() =>
        new NpgsqlDataSourceBuilder(Fixture.AppConnectionString).Build();

    /// <summary>Bounds how long the app role waits for any lock and how long one statement may run.</summary>
    private static async Task ConfigureAsync(
        NpgsqlConnection connection, string lockTimeout, string statementTimeout, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select set_config('lock_timeout', @lock_timeout, false), "
            + "set_config('statement_timeout', @statement_timeout, false)",
            connection);
        command.Parameters.AddWithValue("lock_timeout", lockTimeout);
        command.Parameters.AddWithValue("statement_timeout", statementTimeout);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    private Task SeedAsync(byte[] hash, int hitCount) =>
        OwnerExecuteAsync(
            "insert into public.rate_limit_window (surface, key_hash, window_start, hit_count) "
            + "values ('login', @hash, clock_timestamp(), @hit_count)",
            ("hash", hash),
            ("hit_count", hitCount));

    /// <summary>Takes the row lock the guarded upsert needs, so a later writer must wait for it.</summary>
    private static async Task LockRowAsync(NpgsqlConnection owner, NpgsqlTransaction transaction, byte[] hash)
    {
        await using var command = new NpgsqlCommand(
            "select hit_count from public.rate_limit_window where surface = 'login' and key_hash = @hash for update",
            owner,
            transaction);
        command.Parameters.AddWithValue("hash", hash);
        (await command.ExecuteScalarAsync(Token)).ShouldNotBeNull("the case must lock the row it seeded");
    }

    private async Task<StoredWindow> RowAsync(byte[] hash)
    {
        var row = await OwnerRowAsync(
            "select hit_count, xmin::text, ctid::text from public.rate_limit_window "
            + "where surface = 'login' and key_hash = @hash",
            ("hash", hash));

        return new StoredWindow((int)row[0]!, (string)row[1]!, (string)row[2]!);
    }

    /// <summary>
    /// Polls both backends until each is parked in a lock wait whose blocking chain reaches <paramref name="holderPid"/>.
    /// A waiter is not always blocked by the holder directly: tuple-lock arbitration parks the second racer behind the
    /// first, which in turn waits on the holder's transaction, so the chain is walked instead of assumed one hop deep.
    /// </summary>
    private async Task<bool> WaitForBothRowWaitersAsync(int holderPid, int[] pids, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < WaitLimit)
        {
            var parked = await OwnerScalarAsync<int>(
                "with recursive reach(origin, pid, depth) as ("
                + "select a.pid, b.blocker, 1 "
                + "from pg_stat_activity a "
                + "cross join lateral unnest(pg_blocking_pids(a.pid)) as b(blocker) "
                + "where a.pid = any(@pids) "
                + "union "
                + "select r.origin, b.blocker, r.depth + 1 "
                + "from reach r "
                + "cross join lateral unnest(pg_blocking_pids(r.pid)) as b(blocker) "
                + "where r.depth < 8) "
                + "select count(distinct a.pid)::integer from pg_stat_activity a "
                + "where a.pid = any(@pids) "
                + "and a.wait_event_type = 'Lock' "
                + "and exists (select 1 from reach r where r.origin = a.pid and r.pid = @holder)",
                ("pids", pids),
                ("holder", holderPid));

            if (parked == 2)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        return false;
    }

    /// <summary>Calls the store as <c>evidgate_app</c>; a null <paramref name="cost"/> passes <c>NULL::integer</c>.</summary>
    private static async Task<RateDecision> AcquireAsync(
        NpgsqlConnection app, byte[] hash, int limit, int? cost, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select allowed, retry_after_seconds from evidgate_resolve.rate_limit_acquire("
            + "'login', @hash, @limit, 3600, 50000, " + (cost is null ? "NULL::integer)" : "@cost)"),
            app);
        command.Parameters.AddWithValue("hash", hash);
        command.Parameters.AddWithValue("limit", limit);
        if (cost is { } value)
        {
            command.Parameters.AddWithValue("cost", value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        (await reader.ReadAsync(cancellationToken)).ShouldBeTrue();

        return new RateDecision(reader.GetBoolean(0), reader.GetInt32(1));
    }

    private sealed record StoredWindow(int HitCount, string Xmin, string Ctid);
}
