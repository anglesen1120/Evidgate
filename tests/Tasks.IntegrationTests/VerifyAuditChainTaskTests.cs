using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// 02.7.1-T3.2 (AC6): the scheduled chain verification walks every org's chain inside that org's
/// own tenant scope and turns a fault into a dead letter operators can see.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class VerifyAuditChainTaskTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly PostgresTasksFixture _fixture;

    public VerifyAuditChainTaskTests(PostgresTasksFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task VerifyAuditChain_IntactChains_Completes()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await SeedChainAsync(owner, "alpha", 3);
        await SeedChainAsync(owner, "bravo", 2);
        await EnqueueAsync(owner, orgBatch: 1);

        await using var host = TasksTestHost.Start(_fixture, database);
        (await host.Consumer.PollOnceAsync(AuditSweeps.Queue, Token)).ShouldBe(1);

        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where task_type = 'audit.verify_chain'")).ShouldBe("done");
    }

    [Fact]
    public async Task VerifyAuditChain_CorruptedRow_ReportsMismatch_AndDeadLetters()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        var good = await SeedChainAsync(owner, "alpha", 3);
        var bad = await SeedChainAsync(owner, "bravo", 3);
        await using (var superuser = await _fixture.OpenSuperuserAsync(database))
        {
            await TaskSql.ExecuteAsync(
                superuser, "update public.audit_log set action = 'tampered.x' where org_id = @org and seq = 2", ("org", bad));
        }

        await EnqueueAsync(owner, orgBatch: 1);

        await using var host = TasksTestHost.Start(_fixture, database);
        (await host.Consumer.PollOnceAsync(AuditSweeps.Queue, Token)).ShouldBe(1);

        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where task_type = 'audit.verify_chain'")).ShouldBe("dead_letter");
        var error = await TaskSql.ScalarAsync<string>(owner, "select last_error from public.task where task_type = 'audit.verify_chain'");
        error.ShouldNotBeNull().ShouldContain(bad.ToString());
        error.ShouldContain("HashMismatch@2");
        error.ShouldNotContain(good.ToString());
    }

    [Fact]
    public async Task VerifyAuditChain_DeletedRow_ReportsGap()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        var org = await SeedChainAsync(owner, "alpha", 4);
        await using (var superuser = await _fixture.OpenSuperuserAsync(database))
        {
            await TaskSql.ExecuteAsync(superuser, "delete from public.audit_log where org_id = @org and seq = 3", ("org", org));
        }

        await EnqueueAsync(owner, orgBatch: 10);

        await using var host = TasksTestHost.Start(_fixture, database);
        await host.Consumer.PollOnceAsync(AuditSweeps.Queue, Token);

        (await TaskSql.ScalarAsync<string>(owner, "select last_error from public.task where task_type = 'audit.verify_chain'"))!
            .ShouldContain("Gap@3");
    }

    [Fact]
    public async Task VerifyAuditChain_SameFireAt_NoDuplicateRun()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await using var host = TasksTestHost.Start(_fixture, database);
        var sixOClockSeven = new DateTimeOffset(2026, 10, 3, 6, 7, 30, TimeSpan.Zero);

        var fired = await Task.WhenAll(
            host.Runner.RunOnceAsync(sixOClockSeven, Token),
            host.Runner.RunOnceAsync(sixOClockSeven, Token));

        fired.Sum().ShouldBe(1);
        (await host.Runner.RunOnceAsync(sixOClockSeven, Token)).ShouldBe(0);
        (await TaskSql.ScalarAsync<long>(
            owner, "select count(*) from public.task where schedule_name = @name", ("name", AuditSweeps.VerifyChainTaskType)))
            .ShouldBe(1);
    }

    private static async Task EnqueueAsync(string owner, int orgBatch) =>
        await TaskSql.SeedTaskAsync(
            owner,
            AuditSweeps.Queue,
            AuditSweeps.VerifyChainTaskType,
            payload: $$"""{"org_batch":{{orgBatch}}}""",
            orgId: null,
            maxAttempts: 1,
            idempotencyKey: "verify-" + Guid.NewGuid().ToString("N"));

    /// <summary>An org with an intact chain of <paramref name="rows"/> entries, written the way the writer does.</summary>
    private static async Task<Guid> SeedChainAsync(string owner, string slug, int rows)
    {
        var org = Guid.CreateVersion7();
        await TaskSql.ExecuteAsync(
            owner,
            "insert into public.organization (id, name, slug) values (@id, @slug, @slug)",
            ("id", org),
            ("slug", slug));

        var prev = AuditHashing.Genesis;
        for (var seq = 1; seq <= rows; seq++)
        {
            var row = new AuditRow(
                Guid.CreateVersion7(), org, seq, "system", "system", "probe.seed", null, null, null, null, null, null, null,
                prev, [], AuditHashing.Truncate(DateTimeOffset.UtcNow));
            var hash = AuditHashing.EntryHash(prev, row);

            await TaskSql.ExecuteAsync(
                owner,
                """
                insert into public.audit_log (id, org_id, seq, actor_type, actor_id, action, prev_hash, entry_hash, created_at)
                values (@id, @org, @seq, 'system', 'system', 'probe.seed', @prev, @hash, @at)
                """,
                ("id", row.Id),
                ("org", org),
                ("seq", (long)seq),
                ("prev", prev),
                ("hash", hash),
                ("at", row.CreatedAt));
            prev = hash;
        }

        return org;
    }
}
