using System.Data;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class UsageInvoiceGuardsTests(PostgresTenancyFixture fixture)
{
    private const string Period = "2026-10";
    private const string Earlier = "2026-09";
    private const string Current = "2026-11";
    private const string RefusedAction = "usage.statement_refused";
    private const string IssuedAction = "usage.statement_issued";
    private const string ReplayedAction = "usage.statement_replayed";
    private const string Guard = "usage_ledger_entry_period_issued";
    // 23514: the issued-period guard; 25000: the guard refusing a snapshot it cannot trust; 40001: serialization.
    private static readonly string[] RefusalStates = ["23514", "25000", "40001"];
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    private const string RawLedgerSql = """
        insert into public.usage_ledger_entry
            (id, org_id, project_id, metric, quantity, unit, period, billing_key, occurred_at,
             adjustment_of, reason, amount, currency)
        values (@id, @org_id, @project_id, 'results', @quantity, 'count', @period, @billing_key,
                @occurred_at, @adjustment_of, @reason, @amount, 'USD')
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ledger_RawInsertIntoIssuedPeriod_RefusesPlainAndAdjustmentRowsButAcceptsLaterPeriod(bool adjustment)
    {
        await using var world = await StartAsync();
        var original = Ledger(world, "original");
        await world.SeedLedgerAsync(original);
        var issued = await world.PostStatementAsync(Period, 5m, "USD");
        issued.Status.ShouldBe(HttpStatusCode.OK, issued.ErrorMessage);
        var frozen = await world.StatementRowAsync(Period);
        Guid? adjustmentOf = adjustment ? original.Id : null;

        await using (var refused = await TenantScope.BeginAsync(fixture.AppConnectionString(world.Database), world.Org))
        {
            var error = await Should.ThrowAsync<PostgresException>(async () =>
                await RawInsertAsync(refused, world, Period, adjustmentOf));
            error.SqlState.ShouldBe("23514");
            error.ConstraintName.ShouldBe(Guard);
        }

        await using (var accepted = await TenantScope.BeginAsync(fixture.AppConnectionString(world.Database), world.Org))
        {
            (await RawInsertAsync(accepted, world, Current, adjustmentOf)).ShouldBe(1);
            await accepted.CommitAsync();
        }

        (await world.LedgerRowCountAsync(Period)).ShouldBe(1L);
        (await world.LedgerRowCountAsync(Current)).ShouldBe(1L);
        (await world.StatementRowAsync(Period)).ShouldBe(frozen);
        var replayed = await world.PostStatementAsync(Period, 5m, "USD");
        replayed.Status.ShouldBe(HttpStatusCode.OK, replayed.ErrorMessage);
        replayed.Body.GetProperty("content_hash").GetString()
            .ShouldBe(issued.Body.GetProperty("content_hash").GetString());
    }

    [Theory]
    [InlineData(null, "required")]
    [InlineData("", "format")]
    [InlineData("2026-00", "format")]
    [InlineData("2026-13", "format")]
    [InlineData("2026-1", "format")]
    [InlineData("2026-10\n", "format")]
    [InlineData("2026-10secret", "format")]
    public async Task Statement_MalformedPeriod_AuditsEachAttemptWithoutRequestTextAndAcceptsValidShape(
        string? period, string reason)
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "valid-period"));

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var refused = await world.PostStatementAsync(period, 5m, "USD");
            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.ErrorMessage);
            Reason(refused).ShouldBe(reason);
            await RefusalsShouldBeAsync(world, attempt, reason, null);
        }

        (await world.StatementCountAsync(Period)).ShouldBe(0L);
        (await world.PostStatementAsync(Period, 5m, "USD")).Status.ShouldBe(HttpStatusCode.OK);
        (await world.AuditCountAsync(RefusedAction)).ShouldBe(2L);
    }

    [Theory]
    [InlineData("period_open", "period_open")]
    [InlineData("no_rows", "no_rows")]
    [InlineData("unpriced_rows", "unpriced_rows")]
    [InlineData("mixed_currency", "mixed_currency")]
    [InlineData("not_reconciled", "not_reconciled")]
    [InlineData("total_mismatch", "total_mismatch")]
    [InlineData("currency_mismatch", "total_mismatch")]
    [InlineData("missing_total", "total_mismatch")]
    [InlineData("missing_currency", "total_mismatch")]
    [InlineData("statement_exists", "statement_exists")]
    public async Task Statement_RefusalKind_WritesExactlyOneMinimalAuditRowPerAttempt(string scenario, string reason)
    {
        await using var world = await StartAsync();
        await SeedRefusalAsync(world, scenario);
        var period = scenario == "period_open" ? Current : Period;
        decimal? total = scenario == "missing_total" ? null : scenario == "total_mismatch" ? 5.01m : 5m;
        var currency = scenario == "missing_currency" ? null : scenario == "currency_mismatch" ? "EUR" : "USD";
        var conflict = scenario == "statement_exists";
        var stored = await world.StatementRowAsync(period);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var refused = await world.PostStatementAsync(period, total, currency);
            refused.Status.ShouldBe(conflict ? HttpStatusCode.Conflict : HttpStatusCode.UnprocessableEntity,
                refused.ErrorMessage);
            (conflict ? refused.ErrorCode : Reason(refused)).ShouldBe(reason);
            await RefusalsShouldBeAsync(world, attempt, reason, period);
        }

        (await world.StatementRowAsync(period)).ShouldBe(stored);
        await world.SeedLedgerAsync(Ledger(world, "control") with { Period = Earlier });
        var accepted = await world.PostStatementAsync(Earlier, 5m, "USD");
        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        (await world.AuditCountAsync(RefusedAction)).ShouldBe(2L);
    }

    [Fact]
    public async Task Statement_OversizedCorrectionWorksheet_AuditsTheRefusalWithoutCorrectionText()
    {
        await using var world = await StartAsync();
        var original = Ledger(world, "large-original");
        await world.SeedLedgerAsync(original);
        var reason = "sensitive-correction-text:" + new string('x', 1_900);
        await world.SeedManyCorrectionsAsync(3_000, Period, "results", original.Id, reason, "USD");

        var refused = await world.PostStatementAsync(Period, -2_995m, "USD");
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.ErrorMessage);
        Reason(refused).ShouldBe("worksheet_too_large");
        await RefusalsShouldBeAsync(world, 1, "worksheet_too_large", Period);
        (await world.AuditRowsAsync(RefusedAction))[0].ShouldNotContain("sensitive-correction-text");
        (await world.StatementCountAsync(Period)).ShouldBe(0L);

        await world.SeedLedgerAsync(Ledger(world, "small-original") with { Period = Earlier });
        await world.SeedManyCorrectionsAsync(3_000, Earlier, "results", original.Id, "corrected", "USD");
        var accepted = await world.PostStatementAsync(Earlier, -2_995m, "USD");
        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        (await world.AuditCountAsync(RefusedAction)).ShouldBe(1L);
    }

    [Fact]
    public async Task Statement_ConcurrentLedgerCommit_AuditsThe409AndAcceptsTheRecomputedTotal()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "race-original"));
        await using var late = await world.BeginLateLedgerRowAsync(Period, 5m);
        var issue = world.PostStatementAsync(Period, 5m, "USD");
        await late.WaitForStatementAsync(TestContext.Current.CancellationToken);
        await late.CommitAsync(TestContext.Current.CancellationToken);

        var refused = await issue;
        refused.Status.ShouldBe(HttpStatusCode.Conflict, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("ledger_changed");
        await RefusalsShouldBeAsync(world, 1, "ledger_changed", Period);
        (await world.StatementCountAsync(Period)).ShouldBe(0L);
        (await world.LedgerRowCountAsync(Period)).ShouldBe(2L);

        var accepted = await world.PostStatementAsync(Period, 10m, "USD");
        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        (await world.AuditCountAsync(IssuedAction)).ShouldBe(1L);
        (await world.AuditCountAsync(RefusedAction)).ShouldBe(1L);
    }

    [Fact]
    public async Task Statement_ConcurrentIdenticalIssues_AuditsOneIssueAndOneReplay()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "concurrent-issue"));
        var answers = await Task.WhenAll(
            world.PostStatementAsync(Period, 5m, "USD"),
            world.PostStatementAsync(Period, 5m, "USD"));

        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.OK);
        answers.Count(answer => answer.Body.GetProperty("replayed").GetBoolean()).ShouldBe(1);
        answers.Select(answer => answer.Body.GetProperty("id").GetGuid()).Distinct().Count().ShouldBe(1);
        (await world.StatementCountAsync(Period)).ShouldBe(1L);
        (await world.AuditCountAsync(IssuedAction)).ShouldBe(1L);
        (await world.AuditCountAsync(ReplayedAction)).ShouldBe(1L);
        (await world.AuditCountAsync(RefusedAction)).ShouldBe(0L);
    }

    [Theory]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    public async Task Ledger_StatementCommitsAfterWriterSnapshot_StillRefusesAnIssuedPeriod(IsolationLevel isolation)
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "snapshot-original"));
        var token = TestContext.Current.CancellationToken;
        await using var writer = new NpgsqlConnection(fixture.AppConnectionString(world.Database));
        await writer.OpenAsync(token);
        await using var transaction = await writer.BeginTransactionAsync(isolation, token);
        await using var command = writer.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select set_config('evidgate.org_id', @org, true)";
        command.Parameters.AddWithValue("org", world.Org.ToString("D"));
        await command.ExecuteScalarAsync(token);
        command.Parameters.Clear();
        command.CommandText = "select count(*) from public.billing_statement";
        ((long)(await command.ExecuteScalarAsync(token))!).ShouldBe(0L);

        var issued = await world.PostStatementAsync(Period, 5m, "USD");
        issued.Status.ShouldBe(HttpStatusCode.OK, issued.ErrorMessage);
        var frozen = await world.StatementRowAsync(Period);

        command.CommandText = RawLedgerSql;
        foreach (var (name, value) in RawParameters(world, Period, null))
        {
            command.Parameters.AddWithValue(name, value);
        }

        var error = await Should.ThrowAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync(token));
        RefusalStates.ShouldContain(error.SqlState);
        if (error.SqlState == "23514")
        {
            error.ConstraintName.ShouldBe(Guard);
        }

        await transaction.RollbackAsync(token);
        (await world.LedgerRowCountAsync(Period)).ShouldBe(1L);
        (await world.StatementRowAsync(Period)).ShouldBe(frozen);
    }

    private Task<UsageReadsWorld> StartAsync() =>
        UsageReadsWorld.StartAsync(fixture, OperatorGateMode.Permitted, new FakeTimeProvider(Now));

    private static LedgerSeed Ledger(UsageReadsWorld world, string key) =>
        new(Guid.CreateVersion7(), world.Project, "results", 1m, Period, key, Now, Amount: 5m, Currency: "USD");

    private static async Task SeedRefusalAsync(UsageReadsWorld world, string scenario)
    {
        var row = Ledger(world, "refusal-row");
        switch (scenario)
        {
            case "no_rows":
                return;
            case "unpriced_rows":
                await world.SeedLedgerAsync(row with { Amount = null, Currency = null });
                return;
            case "mixed_currency":
                await world.SeedLedgerAsync(row, Ledger(world, "eur-row") with { Currency = "EUR" });
                return;
            case "period_open":
                await world.SeedLedgerAsync(row with { Period = Current });
                return;
            default:
                await world.SeedLedgerAsync(row);
                break;
        }

        if (scenario == "not_reconciled")
        {
            await world.SeedRollupAsync(new RollupSeed(Guid.CreateVersion7(), world.Project, Period,
                "results", 1m, 1, Now, GapFlag: true, TotalAmount: 5m, Currency: "USD"));
        }
        else if (scenario == "statement_exists")
        {
            await world.SeedStatementAsync(Period, 5m, new string('a', 64));
        }
    }

    private static async Task RefusalsShouldBeAsync(UsageReadsWorld world, int count, string reason, string? period)
    {
        var rows = await world.AuditRowsAsync(RefusedAction);
        rows.Count.ShouldBe(count);
        (await world.AuditCountAsync(IssuedAction)).ShouldBe(0L);
        (await world.AuditCountAsync(ReplayedAction)).ShouldBe(0L);
        foreach (var row in rows)
        {
            var parts = row.Split('|', 5);
            parts[0].ShouldBe("platform_operator");
            parts[1].ShouldBe(UsageReadsWorld.OperatorId.ToString("D"));
            parts[2].ShouldBe("billing_statement");
            parts[3].ShouldBe(world.Org.ToString("D"));
            using var document = JsonDocument.Parse(parts[4]);
            var metadata = document.RootElement;
            metadata.GetProperty("reason").GetString().ShouldBe(reason);
            metadata.GetProperty("outcome").GetString().ShouldBe("refused");
            metadata.EnumerateObject().Count().ShouldBe(period is null ? 2 : 3);
            if (period is null)
            {
                metadata.TryGetProperty("period", out _).ShouldBeFalse();
            }
            else
            {
                metadata.GetProperty("period").GetString().ShouldBe(period);
            }
        }
    }

    private static Task<int> RawInsertAsync(TenantScope scope, UsageReadsWorld world, string period, Guid? adjustmentOf) =>
        scope.ExecuteAsync(RawLedgerSql, RawParameters(world, period, adjustmentOf));

    private static (string Name, object Value)[] RawParameters(UsageReadsWorld world, string period, Guid? adjustmentOf) =>
    [
        ("id", Guid.CreateVersion7()),
        ("org_id", world.Org),
        ("project_id", world.Project),
        ("quantity", adjustmentOf is null ? 1m : -1m),
        ("period", period),
        ("billing_key", Unique.Slug("raw-insert")),
        ("occurred_at", Now),
        ("adjustment_of", (object?)adjustmentOf ?? DBNull.Value),
        ("reason", adjustmentOf is null ? DBNull.Value : "correction"),
        ("amount", adjustmentOf is null ? 5m : -1m),
    ];

    private static string? Reason(ApiAnswer answer) =>
        answer.Body.GetProperty("error").GetProperty("details")[0].GetProperty("reason").GetString();
}
