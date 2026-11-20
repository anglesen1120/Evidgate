using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evidgate.Testing;
using Identity.IntegrationTests.Login.Persistence;
using Npgsql;
using NpgsqlTypes;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// Measures what <c>evidgate_resolve.rate_limit_acquire</c> does for an acquire refused by a live window. The
/// function is plpgsql, so <c>EXPLAIN</c> shows one Function Scan and no <c>ModifyTable</c> node; the statements inside
/// are visible only through transaction-level witnesses: no transaction id assigned, no tuple counted by
/// <c>pg_stat_xact_user_tables</c>. The WAL counters of <c>EXPLAIN (ANALYZE, BUFFERS, WAL)</c> are printed for every
/// run but asserted only on the granted control, because a read can write WAL on its own (hint bits, pruning).
/// The granted acquire is the positive control on all witnesses, and a mutant that rewrites the refused row proves
/// the witnesses can see a write.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RateLimitRefusedPathPlanTests(DatabaseFixture fixture)
    : SessionTestBase(fixture, DatabaseIsolation.Dedicated)
{
    private const int Limit = 2;
    private const int Repeats = 3;

    // The statement the application runs (ExchangeRateLimiter.AcquireAsync), with the same argument order.
    private const string Call =
        "evidgate_resolve.rate_limit_acquire('login', @key_hash, 2, 3600, 1000, 1)";

    [Fact]
    public async Task RefusedAcquire_AssignsNoTransactionAndWritesNoTuple_WhileTheGrantedAcquireDoes()
    {
        var refusedKey = Hash("plan-refused");
        await SeedRefusedWindowAsync(refusedKey);

        await using var connection = await AppDataSource.OpenConnectionAsync(Token);
        Print(await SettingsAsync(connection));

        for (var run = 0; run < Repeats; run++)
        {
            var refused = await MeasureRolledBackAsync(connection, refusedKey);
            Print($"refused acquire, run {run}: xid={refused.Xid ?? "none"} ins/upd/del={refused.Inserted}/{refused.Updated}/{refused.Deleted} wal={refused.WalRecords} records, {refused.WalBytes} bytes\n{refused.Json}");

            refused.Xid.ShouldBeNull("a refused acquire never takes a transaction id");
            refused.Inserted.ShouldBe(0);
            refused.Updated.ShouldBe(0, "a refused acquire updates no row version");
            refused.Deleted.ShouldBe(0);
        }

        var granted = await MeasureRolledBackAsync(connection, Hash("plan-granted"));
        Print($"granted acquire (control): xid={granted.Xid ?? "none"} ins/upd/del={granted.Inserted}/{granted.Updated}/{granted.Deleted} wal={granted.WalRecords} records, {granted.WalBytes} bytes\n{granted.Json}");

        granted.Xid.ShouldNotBeNull("the control: the granted insert takes a transaction id");
        granted.Inserted.ShouldBe(1, "the control: the granted path inserts the row");
        granted.WalRecords.ShouldBeGreaterThan(0, "the control: the granted insert reaches the WAL counter");
        (await HitCountAsync(refusedKey)).ShouldBe(Limit, "the measured refusals charged nothing");
    }

    [Fact]
    public async Task AMutantThatRewritesTheRefusedRow_IsSeenByTheWitnesses()
    {
        var key = Hash("plan-mutant");
        await SeedRefusedWindowAsync(key);

        // Owner connection, one transaction that is rolled back: the replaced function never outlives the test.
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await using (var replace = new NpgsqlCommand(
            """
            create or replace function evidgate_resolve.rate_limit_acquire(
              p_surface text, p_key_hash bytea, p_limit integer, p_window_seconds integer, p_max_keys integer, p_cost integer)
            returns table (allowed boolean, retry_after_seconds integer)
            language plpgsql volatile set search_path = ''
            as $$
            begin
              update public.rate_limit_window w set hit_count = w.hit_count
               where w.surface = p_surface and w.key_hash = p_key_hash;
              return query select false, 1;
            end
            $$
            """,
            connection,
            transaction))
        {
            await replace.ExecuteNonQueryAsync(Token);
        }

        var mutant = await MeasureAsync(connection, transaction, key);
        Print($"mutant refused acquire: xid={mutant.Xid ?? "none"} upd={mutant.Updated} wal={mutant.WalRecords}\n{mutant.Json}");
        await transaction.RollbackAsync(Token);

        mutant.Updated.ShouldBeGreaterThan(0, "the witnesses see a refused path that writes the row again");
    }

    /// <summary>Admits <see cref="Limit"/> acquires and commits, then reads once so the row is warm and refused.</summary>
    private async Task SeedRefusedWindowAsync(byte[] keyHash)
    {
        for (var index = 0; index < Limit; index++)
        {
            (await DecideAsync(keyHash)).ShouldBeTrue("seeding admits up to the limit");
        }

        (await DecideAsync(keyHash)).ShouldBeFalse("the committed row rests at its limit, so the next call is a refusal");
        (await HitCountAsync(keyHash)).ShouldBe(Limit, "the seed is committed and visible to an independent connection");
    }

    private async Task<bool> DecideAsync(byte[] keyHash)
    {
        await using var command = AppDataSource.CreateCommand("select allowed from " + Call);
        command.Parameters.Add(new NpgsqlParameter("key_hash", NpgsqlDbType.Bytea) { Value = keyHash });

        return (bool)(await command.ExecuteScalarAsync(Token))!;
    }

    private Task<int> HitCountAsync(byte[] keyHash) =>
        OwnerScalarAsync<int>("select hit_count from public.rate_limit_window where key_hash = @hash", ("hash", keyHash));

    private static async Task<Witness> MeasureRolledBackAsync(NpgsqlConnection connection, byte[] keyHash)
    {
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var witness = await MeasureAsync(connection, transaction, keyHash);
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);

        return witness;
    }

    private static async Task<Witness> MeasureAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, byte[] keyHash)
    {
        var token = TestContext.Current.CancellationToken;
        await using var explain = new NpgsqlCommand(
            "explain (analyze, buffers, wal, format json) select * from " + Call, connection, transaction);
        explain.Parameters.Add(new NpgsqlParameter("key_hash", NpgsqlDbType.Bytea) { Value = keyHash });
        var json = (string)(await explain.ExecuteScalarAsync(token))!;

        using var document = JsonDocument.Parse(json);
        var plan = document.RootElement[0].GetProperty("Plan");

        await using var stats = new NpgsqlCommand(
            "select pg_current_xact_id_if_assigned()::text, coalesce(n_tup_ins, 0), coalesce(n_tup_upd, 0), coalesce(n_tup_del, 0) " +
            "from (select 1) one left join pg_stat_xact_user_tables t on t.relname = 'rate_limit_window'",
            connection,
            transaction);
        await using var reader = await stats.ExecuteReaderAsync(token);
        (await reader.ReadAsync(token)).ShouldBeTrue();

        var xid = await reader.IsDBNullAsync(0, token) ? null : reader.GetString(0);

        return new Witness(
            json,
            xid,
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            plan.GetProperty("WAL Records").GetInt64(),
            plan.GetProperty("WAL Bytes").GetInt64());
    }

    private static async Task<string> SettingsAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "select 'data_checksums=' || current_setting('data_checksums') || ' wal_log_hints=' || current_setting('wal_log_hints')",
            connection);

        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static void Print(string text) => TestContext.Current.TestOutputHelper?.WriteLine(text);

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    private sealed record Witness(
        string Json, string? Xid, long Inserted, long Updated, long Deleted, long WalRecords, long WalBytes);
}
