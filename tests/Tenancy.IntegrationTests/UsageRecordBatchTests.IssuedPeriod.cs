// The issued-period guard of the ledger write path against a real PostgreSQL: a period an issued statement froze
// refuses the whole batch before anything is written, leaves the caller's transaction usable, and answers the same
// typed refusal when a concurrent issue wins the race inside the trigger.
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
/// An issued billing statement freezes its month: the ledger takes no further row for it, so a correction belongs to a
/// later open period. Both write shapes prove it — the operator command answers a conflict, the in-process ledger throws
/// the module's typed refusal — and both leave the batch's open periods alone, because the refusal is decided before
/// the insert runs.
/// </summary>
/// <remarks>
/// The issued period is seeded as the statement an issue writes (one row per period, the table's own key), so the
/// guard is exercised on the path production takes without the issuer's closed-period and pricing rules in the way.
/// The concurrent case holds that row uncommitted: the ledger's insert then passes its read, waits on the trigger's
/// advisory lock, and is refused once the statement commits.
/// </remarks>
public sealed partial class UsageRecordBatchTests
{
    /// <summary>The month every refusal case bills: a closed period with a statement, not the request's usual period.</summary>
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

    // ------------------------------------------------------------------ the operator command's conflict

    [Fact]
    public async Task RecordUsageBatch_IssuedPeriod_409PeriodIssuedAndNothingWritten()
    {
        await using var world = await StartAsync();
        await IssueStatementAsync(world, world.Org, IssuedPeriod);

        var refused = await SendAsync(
            world,
            Batch("usage-issued", [Entry("batch:issued:results", world.Project, period: IssuedPeriod)]));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Status.ShouldBe(409);
        refused.Error.Code.ShouldBe(RecordUsageBatchHandler.PeriodIssuedCode);
        refused.Error.Message.ShouldBe(
            $"the billing period {IssuedPeriod} is already issued",
            "the refusal names the period and nothing else of the batch");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(0, "the refusal is decided before the insert runs");
        LedgerStatements(world).ShouldBeEmpty("no insert statement reached the ledger");
        (await DuplicateAttemptCountAsync(world, world.Org)).ShouldBe(0, "a refused batch leaves no duplicate tally");
        (await AuditCountAsync(world, "usage.record_batch")).ShouldBe(0, "a failed command writes no audit row");
    }

    [Fact]
    public async Task RecordUsageBatch_OneIssuedPeriodOfTwo_RefusesTheWholeBatch()
    {
        await using var world = await StartAsync();
        await IssueStatementAsync(world, world.Org, IssuedPeriod);

        var refused = await SendAsync(
            world,
            Batch(
                "usage-mixed",
                [
                    Entry("batch:open:results", world.Project),
                    Entry("batch:issued:results", world.Project, period: IssuedPeriod),
                ]));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Status.ShouldBe(409);
        refused.Error.Code.ShouldBe(RecordUsageBatchHandler.PeriodIssuedCode);
        refused.Error.Message.ShouldBe(
            $"the billing period {IssuedPeriod} is already issued",
            "the issued period is named, not the open entry that comes first");
        (await LedgerCountAsync(world, world.Org))
            .ShouldBe(0, "the batch is refused whole, so its open-period entry is not written either");
    }

    [Fact]
    public async Task RecordUsageBatch_OpenPeriod_StillInserts()
    {
        await using var world = await StartAsync();
        await IssueStatementAsync(world, world.Org, IssuedPeriod);

        var recorded = await SendAsync(world, Batch("usage-open", [Entry("batch:open:results", world.Project)]));

        recorded.IsSuccess.ShouldBeTrue(recorded.Error?.Message);
        recorded.Value!.Inserted.ShouldBe(["batch:open:results"]);
        (await LedgerCountAsync(world, world.Org)).ShouldBe(1);
        (await AuditCountAsync(world, "usage.record_batch")).ShouldBe(1);
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
    }

    // ------------------------------------------------------------------ the in-process path's typed refusal

    [Fact]
    public async Task RecordAsync_IssuedPeriod_ThrowsTheTypedRefusalAndLeavesTheTransactionUsable()
    {
        await using var world = await StartAsync();
        await IssueStatementAsync(world, world.Org, IssuedPeriod);

        await using var services = world.Commands.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var scopes = provider.GetRequiredService<ITenantScopeFactory>();
        var token = TestContext.Current.CancellationToken;
        await using var scope = await scopes.BeginAsync(world.Org, null, false, token);
        var ledger = provider.GetRequiredService<IUsageLedger>();

        var refused = await Should.ThrowAsync<LedgerPeriodIssuedException>(
            async () => await ledger.RecordAsync(
                new RecordUsageBatchRequest([Entry("batch:issued:results", world.Project, period: IssuedPeriod)]),
                token));

        refused.Period.ShouldBe(IssuedPeriod);
        refused.Message.ShouldBe($"the billing period {IssuedPeriod} is already issued");

        // The refusal came from the read before the insert, so the caller is mid-work and can still write.
        var accepted = await ledger.RecordAsync(
            new RecordUsageBatchRequest([Entry("batch:open:results", world.Project)]),
            token);
        accepted.Inserted.ShouldBe(["batch:open:results"], "a refused batch leaves the caller's transaction usable");
        await scope.CommitAsync(token);

        (await LedgerCountAsync(world, world.Org)).ShouldBe(1, "only the open period's row landed");
    }

    // ------------------------------------------------------------------ the race the trigger settles

    [Fact]
    public async Task RecordAsync_StatementCommittedWhileTheInsertWaits_ThrowsTheSameRefusal()
    {
        await using var world = await StartAsync();
        var token = TestContext.Current.CancellationToken;

        // An uncommitted statement row holds the period's exclusive advisory lock, so the ledger's insert passes the
        // read that would have refused it and then queues on the trigger's shared lock: the race the trigger exists for.
        await using var connection = await _fixture.OpenSuperuserAsync(world.Database);
        await using var statement = await connection.BeginTransactionAsync(token);
        await InsertStatementAsync(connection, statement, world.Org, IssuedPeriod);

        var pending = Task.Run(
            async () =>
            {
                try
                {
                    await RecordAsync(
                        world,
                        [Entry("batch:race:results", world.Project, period: IssuedPeriod)],
                        cancellationToken: token);
                    return null;
                }
                catch (LedgerPeriodIssuedException refusal)
                {
                    return refusal;
                }
            },
            token);

        await WaitForBlockedLedgerInsertAsync(world.Database);
        await statement.CommitAsync(token);

        var refused = await pending;
        refused.ShouldNotBeNull("the batch that queued behind the statement is refused once it commits");
        refused.Period.ShouldBe(IssuedPeriod, "the trigger's message still names the period it refused");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(0, "the batch that lost the race wrote nothing");
    }

    // ------------------------------------------------------------------ seeds

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

    /// <summary>How many duplicate billing-key attempts the org has tallied, across every batch it ever sent.</summary>
    private Task<long> DuplicateAttemptCountAsync(World world, Guid org) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.usage_duplicate_attempt where org_id = '{org:D}'");

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
