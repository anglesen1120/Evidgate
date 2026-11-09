// Late metering against a real PostgreSQL: a row metered for a month an issued statement already froze is re-dated
// into the next open month instead of refused, keeps the month it was metered for in original_period, and is audited in
// the caller's own transaction. A month with no open month after it is the one remaining refusal.
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Application.Commands;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// An issued statement freezes its month: the ledger takes no further row for it, and a row metered after the fact is
/// billed in the first month that is still open, with the month it was metered for kept as provenance. Both write shapes
/// prove it — the operator command and the in-process ledger — and both audit the move in the caller's transaction.
/// </summary>
/// <remarks>
/// The issued period is seeded as the statement an issue writes (one row per period, the table's own key), so the guard
/// is exercised on the path production takes without the issuer's closed-period and pricing rules in the way. The
/// concurrent case holds that row uncommitted: the ledger's insert then queues on the trigger's advisory lock, and once
/// the statement commits the same trigger re-dates the row rather than refusing it.
/// </remarks>
public sealed partial class UsageRecordBatchTests
{
    /// <summary>The month a late entry bills: a closed period with a statement, behind the request's usual period.</summary>
    private const string IssuedPeriod = "2026-09";

    /// <summary>How long the concurrent case waits for the ledger's insert to queue on the trigger's lock.</summary>
    private const int BlockedInsertAttempts = 600;

    private static readonly TimeSpan BlockedInsertPoll = TimeSpan.FromMilliseconds(25);

    /// <summary>The statement row an issue writes; every non-default column, so the guard reads a real row.</summary>
    private const string StatementSql = """
        insert into public.billing_statement
            (id, org_id, period, kind, total_amount, currency, line_count, content_hash, worksheet_json,
             issued_by, issued_at)
        values (@id, @org_id, @period, 'invoice', 1, 'USD', 1, @content_hash, '{}'::jsonb, @issued_by, now())
        """;

    // ------------------------------------------------------------------ the re-date

    [Fact]
    public async Task RecordUsageBatch_IssuedPeriod_RedatesIntoTheNextOpenMonthAndAudits()
    {
        await using var world = await StartAsync();
        await IssueStatementAsync(world, world.Org, IssuedPeriod);

        var recorded = await SendAsync(
            world,
            Batch("usage-late", [Entry("batch:late:results", world.Project, period: IssuedPeriod)]));

        recorded.IsSuccess.ShouldBeTrue(recorded.Error?.Message);
        recorded.Value!.Inserted.ShouldBe(["batch:late:results"], "a late row is billed, never dropped");
        recorded.Value.Duplicates.ShouldBeEmpty();

        (await ColumnAsync(world, world.Org, "batch:late:results", "period"))
            .ShouldBe(Period, "the row lands in the first month that is still open");
        (await ColumnAsync(world, world.Org, "batch:late:results", "original_period"))
            .ShouldBe(IssuedPeriod, "the month it was metered for is kept as provenance");
        (await AuditCountAsync(world, "usage.record_batch")).ShouldBe(1);
        (await RedateRowsAsync(world)).ShouldBe(
            [$"{IssuedPeriod}|{Period}|1"],
            "the move is audited with both months and the number of rows it moved");
    }

    [Fact]
    public async Task RecordUsageBatch_ConsecutiveIssuedMonths_SkipsToTheFirstOpenMonth()
    {
        await using var world = await StartAsync();
        await IssueStatementAsync(world, world.Org, IssuedPeriod);
        await IssueStatementAsync(world, world.Org, Period);

        var recorded = await SendAsync(
            world,
            Batch("usage-late", [Entry("batch:late:results", world.Project, period: IssuedPeriod)]));

        recorded.IsSuccess.ShouldBeTrue(recorded.Error?.Message);
        (await ColumnAsync(world, world.Org, "batch:late:results", "period"))
            .ShouldBe("2026-11", "every issued month is skipped, not just the requested one");
        (await ColumnAsync(world, world.Org, "batch:late:results", "original_period")).ShouldBe(IssuedPeriod);
        (await RedateRowsAsync(world)).ShouldBe([$"{IssuedPeriod}|2026-11|1"]);
    }

    [Fact]
    public async Task RecordUsageBatch_OneLateEntryAmongOpenOnes_RedatesOnlyThatEntry()
    {
        await using var world = await StartAsync();
        await IssueStatementAsync(world, world.Org, IssuedPeriod);

        var recorded = await SendAsync(
            world,
            Batch(
                "usage-mixed",
                [
                    Entry("batch:open:results", world.Project),
                    Entry("batch:late:results", world.Project, period: IssuedPeriod),
                ]));

        recorded.IsSuccess.ShouldBeTrue(recorded.Error?.Message);
        recorded.Value!.Inserted.ShouldBe(["batch:open:results", "batch:late:results"]);
        (await ColumnAsync(world, world.Org, "batch:open:results", "original_period"))
            .ShouldBeNull("an entry for an open month is written exactly as it was metered");
        (await ColumnAsync(world, world.Org, "batch:late:results", "original_period")).ShouldBe(IssuedPeriod);
        (await RedateRowsAsync(world)).ShouldBe(
            [$"{IssuedPeriod}|{Period}|1"],
            "the audit names the one entry that moved, not the batch");
    }

    [Fact]
    public async Task RecordUsageBatch_AllKeysOnFile_AnswersDuplicatesAndRedatesNothing()
    {
        await using var world = await StartAsync();
        await IssueStatementAsync(world, world.Org, IssuedPeriod);
        var late = Batch("usage-late-1", [Entry("batch:late:results", world.Project, period: IssuedPeriod)]);

        (await SendAsync(world, late)).IsSuccess.ShouldBeTrue();
        var replay = await SendAsync(
            world,
            Batch("usage-late-2", [Entry("batch:late:results", world.Project, period: IssuedPeriod)]));

        replay.IsSuccess.ShouldBeTrue(replay.Error?.Message);
        replay.Value!.Inserted.ShouldBeEmpty();
        replay.Value.Duplicates.ShouldBe(["batch:late:results"], "a batch whose keys are all on file is a replay");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(1, "a replay writes no second row");
        (await ColumnAsync(world, world.Org, "batch:late:results", "period"))
            .ShouldBe(Period, "a replay leaves the row's own month alone");
        (await RedateRowsAsync(world)).ShouldBe(
            [$"{IssuedPeriod}|{Period}|1"],
            "a replay that moved nothing audits no move, and a re-dated key replays as its own duplicate");
    }

    [Fact]
    public async Task RecordUsageBatch_NoOpenMonthAfterTheLastPeriod_409NoOpenPeriodAndNothingWritten()
    {
        await using var world = await StartAsync();
        await IssueStatementAsync(world, world.Org, "9999-12");

        var refused = await SendAsync(
            world,
            Batch("usage-last", [Entry("batch:last:results", world.Project, period: "9999-12")]));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Status.ShouldBe(409);
        refused.Error.Code.ShouldBe(RecordUsageBatchHandler.NoOpenPeriodCode);
        refused.Error.Message.ShouldBe(
            "the billing period 9999-12 and every period after it are already issued",
            "the refusal names the period and the reason, and nothing else of the batch");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(0, "there was nowhere to bill, so nothing was written");
        (await AuditCountAsync(world, "usage.record_batch")).ShouldBe(0, "a failed command writes no audit row");
        (await RedateRowsAsync(world)).ShouldBeEmpty("the refused insert re-dated nothing");
    }

    [Fact]
    public async Task RecordUsageBatch_OpenPeriod_StillInsertsWithNoProvenance()
    {
        await using var world = await StartAsync();
        await IssueStatementAsync(world, world.Org, IssuedPeriod);

        var recorded = await SendAsync(world, Batch("usage-open", [Entry("batch:open:results", world.Project)]));

        recorded.IsSuccess.ShouldBeTrue(recorded.Error?.Message);
        recorded.Value!.Inserted.ShouldBe(["batch:open:results"]);
        (await LedgerCountAsync(world, world.Org)).ShouldBe(1);
        (await ColumnAsync(world, world.Org, "batch:open:results", "original_period"))
            .ShouldBeNull("a row that did not move records no provenance");
        (await AuditCountAsync(world, "usage.record_batch")).ShouldBe(1);
        (await RedateRowsAsync(world)).ShouldBeEmpty("nothing moved, so no move is audited");
    }

    [Fact]
    public async Task RecordUsageBatch_PeriodIssuedInAnotherOrg_IsAccepted()
    {
        await using var world = await StartAsync();
        var otherOrg = await CreateOrgAsync(world.Client, Guid.CreateVersion7(), Unique.Slug("globex"));
        await IssueStatementAsync(world, otherOrg, Period);

        var recorded = await SendAsync(world, Batch("usage-other-org", [Entry("batch:1:results", world.Project)]));

        recorded.IsSuccess.ShouldBeTrue(recorded.Error?.Message);
        recorded.Value!.Inserted.ShouldBe(["batch:1:results"], "another tenant's statement freezes nothing of this org");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(1);
        (await ColumnAsync(world, world.Org, "batch:1:results", "original_period")).ShouldBeNull();
    }

    // ------------------------------------------------------------------ the in-process path

    [Fact]
    public async Task RecordAsync_IssuedPeriod_RedatesAndLeavesTheTransactionUsable()
    {
        await using var world = await StartAsync();
        await IssueStatementAsync(world, world.Org, IssuedPeriod);

        await using var services = world.Commands.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var scopes = provider.GetRequiredService<ITenantScopeFactory>();
        var token = TestContext.Current.CancellationToken;
        await using var scope = await scopes.BeginAsync(world.Org, null, false, token);
        var ledger = provider.GetRequiredService<IUsageLedger>();

        var late = await ledger.RecordAsync(
            new RecordUsageBatchRequest([Entry("batch:late:results", world.Project, period: IssuedPeriod)]),
            token);
        late.Inserted.ShouldBe(["batch:late:results"]);

        var later = await ledger.RecordAsync(
            new RecordUsageBatchRequest([Entry("batch:open:results", world.Project, period: "2026-11")]),
            token);
        later.Inserted.ShouldBe(["batch:open:results"], "the caller is mid-work and its transaction is still usable");

        await scope.CommitAsync(token);

        (await ColumnAsync(world, world.Org, "batch:late:results", "period")).ShouldBe(Period);
        (await ColumnAsync(world, world.Org, "batch:late:results", "original_period")).ShouldBe(IssuedPeriod);
        (await RedateRowsAsync(world)).ShouldBe(
            [$"{IssuedPeriod}|{Period}|1"],
            "the in-process path audits the move in the caller's own transaction");
    }

    [Fact]
    public async Task RecordAsync_NoOpenMonthAfterTheLastPeriod_ThrowsTheTypedRefusal()
    {
        await using var world = await StartAsync();
        await IssueStatementAsync(world, world.Org, "9999-12");

        await using var services = world.Commands.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var scopes = provider.GetRequiredService<ITenantScopeFactory>();
        var token = TestContext.Current.CancellationToken;

        // The refusal aborts the transaction (it comes out of the insert statement), so the scope is disposed without a
        // commit and everything the attempted write touched rolls back with it.
        await using (var scope = await scopes.BeginAsync(world.Org, null, false, token))
        {
            var ledger = provider.GetRequiredService<IUsageLedger>();

            var refused = await Should.ThrowAsync<LedgerNoOpenPeriodException>(
                async () => await ledger.RecordAsync(
                    new RecordUsageBatchRequest([Entry("batch:last:results", world.Project, period: "9999-12")]),
                    token));

            refused.Period.ShouldBe("9999-12");
            refused.Message.ShouldBe("the billing period 9999-12 and every period after it are already issued");
        }

        (await LedgerCountAsync(world, world.Org)).ShouldBe(0, "the refused insert wrote nothing");
        (await RedateRowsAsync(world)).ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ the race the trigger settles

    [Fact]
    public async Task RecordAsync_StatementCommittedWhileTheInsertWaits_RedatesIntoTheOpenMonth()
    {
        await using var world = await StartAsync();
        var token = TestContext.Current.CancellationToken;

        // An uncommitted statement row holds the period's exclusive advisory lock, so the ledger's insert queues on the
        // trigger's shared lock: the race the trigger exists for.
        await using var connection = await _fixture.OpenSuperuserAsync(world.Database);
        await using var statement = await connection.BeginTransactionAsync(token);
        await InsertStatementAsync(connection, statement, world.Org, IssuedPeriod);

        var pending = Task.Run(
            async () => await RecordAsync(
                world,
                [Entry("batch:race:results", world.Project, period: IssuedPeriod)],
                cancellationToken: token),
            token);

        await WaitForBlockedLedgerInsertAsync(world.Database);
        await statement.CommitAsync(token);

        var recorded = await pending;
        recorded.Inserted.ShouldBe(["batch:race:results"], "the batch that queued behind the statement is still billed");
        (await ColumnAsync(world, world.Org, "batch:race:results", "period"))
            .ShouldBe(Period, "the month the statement froze is not the month the row landed in");
        (await ColumnAsync(world, world.Org, "batch:race:results", "original_period")).ShouldBe(IssuedPeriod);
    }

    [Fact]
    public async Task RecordAsync_IssueOfTheAssignedMonthRacingTheWalk_LeavesNoRowInAnIssuedMonth()
    {
        await using var world = await StartAsync();
        await IssueStatementAsync(world, world.Org, IssuedPeriod);

        // The walk's own month is held uncommitted while the late row moves through it, so the insert cannot see it as
        // issued. Because the walk takes that month's shared lock and the issue needs the exclusive one, the issue waits
        // for this transaction either way: the row cannot land in the month that is committing.
        var token = TestContext.Current.CancellationToken;
        await using var connection = await _fixture.OpenSuperuserAsync(world.Database);
        await using var statement = await connection.BeginTransactionAsync(token);
        await InsertStatementAsync(connection, statement, world.Org, Period);

        var pending = Task.Run(
            async () => await RecordAsync(
                world,
                [Entry("batch:walk:results", world.Project, period: IssuedPeriod)],
                cancellationToken: token),
            token);

        await WaitForBlockedLedgerInsertAsync(world.Database);
        await statement.CommitAsync(token);

        var recorded = await pending;
        recorded.Inserted.ShouldBe(["batch:walk:results"]);
        (await ColumnAsync(world, world.Org, "batch:walk:results", "original_period")).ShouldBe(IssuedPeriod);

        var assigned = await ColumnAsync(world, world.Org, "batch:walk:results", "period");
        assigned.ShouldNotBeOneOf(IssuedPeriod, Period);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.billing_statement where org_id = '{world.Org:D}' and period = '{assigned}'"))
            .ShouldBe(0, "a row never lands in a month that holds a statement");
    }

    // ------------------------------------------------------------------ seeds and reads

    /// <summary>
    /// Seeds the one statement row an issue writes for <paramref name="period"/>, committed. The write runs as the
    /// superuser because an issued period is normally reached through the issuer's rules (priced rows, a closed month),
    /// which this suite does not need: the guard reacts to the row, not to how it came to exist.
    /// </summary>
    private async Task IssueStatementAsync(World world, Guid org, string period)
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = await _fixture.OpenSuperuserAsync(world.Database);
        await using var statement = await connection.BeginTransactionAsync(token);
        await InsertStatementAsync(connection, statement, org, period);
        await statement.CommitAsync(token);
    }

    /// <summary>Writes one statement row inside <paramref name="transaction"/>, so a caller can hold it uncommitted.</summary>
    private static async Task InsertStatementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid org,
        string period)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = StatementSql;
        command.Parameters.Add(new NpgsqlParameter("id", Guid.CreateVersion7()));
        command.Parameters.Add(new NpgsqlParameter("org_id", org));
        command.Parameters.Add(new NpgsqlParameter("period", period));
        command.Parameters.Add(new NpgsqlParameter("content_hash", new string('a', 64)));
        command.Parameters.Add(new NpgsqlParameter("issued_by", Guid.CreateVersion7()));

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The org's re-dated entries as <c>original_period|period|count</c> in chain order: the three facts the audit row
    /// exists to carry, and nothing else of the row or the batch.
    /// </summary>
    private Task<IReadOnlyList<string>> RedateRowsAsync(World world) =>
        _fixture.ColumnAsync<string>(
            world.Database,
            $"""
            select coalesce(metadata ->> 'original_period', '-') || '|' || coalesce(metadata ->> 'period', '-')
                   || '|' || coalesce(metadata ->> 'count', '-')
              from public.audit_log
             where org_id = '{world.Org:D}' and action = 'usage.entry_redated'
             order by seq
            """);

    /// <summary>
    /// Waits until a session of <paramref name="database"/> running the ledger's insert is queued on an advisory lock:
    /// that insert has passed its read and is inside the trigger that settles the race. The condition is observed, never
    /// timed, so a slow host only makes the wait longer; the statement text keeps another suite's blocked backend out.
    /// </summary>
    private async Task WaitForBlockedLedgerInsertAsync(string database)
    {
        var sql = $"""
            select count(*)
              from pg_locks l
              join pg_stat_activity a on a.pid = l.pid
             where l.locktype = 'advisory'
               and not l.granted
               and a.datname = '{database}'
               and a.query like '%public.usage_ledger_entry%'
            """;

        var token = TestContext.Current.CancellationToken;
        await using var connection = await _fixture.OpenSuperuserAsync(database);

        for (var attempt = 0; attempt < BlockedInsertAttempts; attempt++)
        {
            if (await PostgresTenancyFixture.ScalarAsync<long>(connection, sql) > 0)
            {
                return;
            }

            await Task.Delay(BlockedInsertPoll, token);
        }

        throw new InvalidOperationException(
            $"no session of {database} was waiting on an advisory lock after {BlockedInsertAttempts} polls");
    }
}
