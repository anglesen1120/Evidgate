using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Evidgate.Api.OperatorIngress;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Issuing the invoice statement of one closed period over the real host and a real PostgreSQL: the row and the
/// audit entry one issue writes, the total the server recomputes instead of trusting, every refusal in its order,
/// the idempotent replay of an unchanged period, the conflict a late correction causes, and the tenant scope the
/// statement can never leave.
/// </summary>
/// <remarks>
/// The world runs on one fixed instant (2026-11-06), so <see cref="Period"/> is closed and
/// <see cref="CurrentPeriod"/> is not. The statement is a document: every assertion about a stored row reads the
/// table as the owner, because the document must outlive whatever the routes answer afterwards.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class UsageStatementIssueTests
{
    private const string Period = "2026-10";
    private const string CurrentPeriod = "2026-11";
    private const string Earlier = "2026-09";
    private const string Action = "usage.statement_issued";
    private const string RefusedAction = "usage.statement_refused";

    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresTenancyFixture _fixture;

    public UsageStatementIssueTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Statement_PricedPeriod_StoresTheDocumentAndItsAuditRow()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(
            Ledger(world, "issue:1", 1m, 2m, 20),
            Ledger(world, "issue:2", 2m, 3m, 21));
        var worksheet = await world.GetAsync(world.WorksheetUrl($"period={Period}"));
        var worksheetHash = worksheet.Body.GetProperty("content_hash").GetString();

        var answer = await world.PostStatementAsync(Period, 5m, "USD");

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("period").GetString().ShouldBe(Period);
        answer.Body.GetProperty("total_amount").GetDecimal().ShouldBe(5m);
        answer.Body.GetProperty("currency").GetString().ShouldBe("USD");
        answer.Body.GetProperty("line_count").GetInt32().ShouldBe(2);
        answer.Body.GetProperty("content_hash").GetString().ShouldBe(worksheetHash);
        answer.Body.GetProperty("replayed").GetBoolean().ShouldBeFalse();

        var statementId = answer.Body.GetProperty("id").GetGuid();
        (await world.StatementCountAsync(Period)).ShouldBe(1L);

        var parts = (await world.StatementRowAsync(Period)).ShouldNotBeNull().Split('|', 8);
        parts[0].ShouldBe(statementId.ToString("D"));
        parts[1].ShouldBe("invoice");
        parts[2].ShouldBe("5");
        parts[3].ShouldBe("USD");
        parts[4].ShouldBe("2");
        parts[5].ShouldBe(worksheetHash, "the stored hash is the worksheet's, not a number beside it");
        parts[6].ShouldBe(UsageReadsWorld.OperatorId.ToString("D"), "the operator who issued it");

        // The document column is jsonb, so the database normalises its text: the hash above is its address,
        // and the columns beside it are what a reader compares the parsed document against.
        using var document = JsonDocument.Parse(parts[7]);
        document.RootElement.GetProperty("period").GetString().ShouldBe(Period);
        document.RootElement.GetProperty("total").GetString().ShouldBe("5");
        document.RootElement.GetProperty("currency").GetString().ShouldBe("USD");
        document.RootElement.GetProperty("lines").GetArrayLength().ShouldBe(2);

        var rows = await world.AuditRowsAsync(Action);
        rows.Count.ShouldBe(1);

        var audit = rows[0].Split('|', 5);
        audit[0].ShouldBe("platform_operator");
        audit[1].ShouldBe(UsageReadsWorld.OperatorId.ToString("D"));
        audit[2].ShouldBe("billing_statement");
        audit[3].ShouldBe(statementId.ToString("D"), "the audit row names the statement it froze");

        using var metadata = JsonDocument.Parse(audit[4]);
        metadata.RootElement.GetProperty("period").GetString().ShouldBe(Period);
        metadata.RootElement.GetProperty("count").GetString().ShouldBe("2");
    }

    [Fact]
    public async Task Statement_TotalOffByOneMinorUnit_422AndNoRow()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "offset:1", 1m, 10.50m, 20));

        var answer = await world.PostStatementAsync(Period, 10.51m, "USD");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe(ApiErrorCode.RequestInvalid);
        Detail(answer).GetProperty("path").GetString().ShouldBe("total_amount");
        Detail(answer).GetProperty("reason").GetString().ShouldBe("total_mismatch");
        (await world.StatementCountAsync(Period)).ShouldBe(0L);
        (await world.AuditCountAsync(Action)).ShouldBe(0L);
    }

    [Fact]
    public async Task Statement_TotalAtAnotherScale_IsTheSameTotal()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "scale:1", 1m, 10.50m, 20));

        var answer = await world.PostStatementAsync(Period, 10.5000m, "USD");

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("total_amount").GetDecimal().ShouldBe(10.5m);
    }

    [Fact]
    public async Task Statement_WrongCurrency_422()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "currency:1", 1m, 5m, 20));

        var answer = await world.PostStatementAsync(Period, 5m, "EUR");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        Detail(answer).GetProperty("path").GetString().ShouldBe("currency");
        Detail(answer).GetProperty("reason").GetString().ShouldBe("total_mismatch");
        (await world.StatementCountAsync(Period)).ShouldBe(0L);
    }

    [Fact]
    public async Task Statement_MissingTotalOrCurrency_422()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "missing:1", 1m, 5m, 20));

        var noTotal = await world.PostStatementAsync(Period, null, "USD");
        noTotal.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, "an absent total cannot equal the recompute");
        Detail(noTotal).GetProperty("path").GetString().ShouldBe("total_amount");

        var noCurrency = await world.PostStatementAsync(Period, 5m, null);
        noCurrency.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Detail(noCurrency).GetProperty("path").GetString().ShouldBe("currency");
        (await world.StatementCountAsync(Period)).ShouldBe(0L);
    }

    [Fact]
    public async Task Statement_OpenPeriod_422BeforeAnythingElseIsJudged()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "open:1", 1m, 5m, 20, CurrentPeriod));

        // The submission is otherwise perfect: the period is not over, which is the first thing wrong with it.
        var answer = await world.PostStatementAsync(CurrentPeriod, 5m, "USD");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        Detail(answer).GetProperty("path").GetString().ShouldBe("period");
        Detail(answer).GetProperty("reason").GetString().ShouldBe("period_open");
        (await world.StatementCountAsync(CurrentPeriod)).ShouldBe(0L);
        (await world.AuditCountAsync(Action)).ShouldBe(0L);
    }

    [Fact]
    public async Task Statement_EmptyPeriod_422NoRows()
    {
        await using var world = await StartAsync();

        var answer = await world.PostStatementAsync(Earlier, 0m, "USD");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        Detail(answer).GetProperty("reason").GetString().ShouldBe("no_rows");
        (await world.StatementCountAsync(Earlier)).ShouldBe(0L);
    }

    [Fact]
    public async Task Statement_UnpricedRow_422UnpricedRows()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(
            Ledger(world, "unpriced:1", 1m, 2m, 20),
            Ledger(world, "unpriced:2", 2m, null, 21));

        var answer = await world.PostStatementAsync(Period, 2m, "USD");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        Detail(answer).GetProperty("reason").GetString().ShouldBe("unpriced_rows");
        (await world.StatementCountAsync(Period)).ShouldBe(0L);
    }

    [Fact]
    public async Task Statement_AdjustmentsOnlyPeriod_IssuesWithItsNegativeTotal()
    {
        await using var world = await StartAsync();
        var original = Ledger(world, "net:original", 1m, 2m, 20, Earlier);
        await world.SeedLedgerAsync(
            original,
            Ledger(world, "net:adjustment", -2m, -2m, 21) with
            {
                AdjustmentOf = original.Id,
                Reason = "the earlier row was billed twice",
            });

        var answer = await world.PostStatementAsync(Period, -2m, "USD");

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("total_amount").GetDecimal().ShouldBe(-2m, "a correction-only period bills a credit");
        answer.Body.GetProperty("line_count").GetInt32().ShouldBe(0);

        var worksheet = await world.GetAsync(world.WorksheetUrl($"period={Period}"));
        worksheet.Body.GetProperty("total").GetDecimal().ShouldBe(-2m);
        worksheet.Body.GetProperty("adjustment_total").GetDecimal().ShouldBe(-2m);
        (await world.StatementRowAsync(Period)).ShouldNotBeNull().Split('|', 8)[2].ShouldBe("-2");
    }

    [Fact]
    public async Task Statement_Reissue_ReplaysTheStoredStatementAndWritesNothingMore()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "replay:1", 1m, 2m, 20));

        var first = await world.PostStatementAsync(Period, 2m, "USD");
        var second = await world.PostStatementAsync(Period, 2m, "USD");

        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
        first.Body.GetProperty("replayed").GetBoolean().ShouldBeFalse();
        second.Body.GetProperty("replayed").GetBoolean().ShouldBeTrue();
        second.Body.GetProperty("id").GetGuid().ShouldBe(first.Body.GetProperty("id").GetGuid());
        second.Body.GetProperty("content_hash").GetString()
            .ShouldBe(first.Body.GetProperty("content_hash").GetString());

        (await world.StatementCountAsync(Period)).ShouldBe(1L);
        (await world.AuditCountAsync(Action)).ShouldBe(1L, "only the first issue wrote a document");
    }

    [Fact]
    public async Task Statement_PeriodAlreadyIssuedWithAnotherDocument_409AndTheStoredDocumentIsUntouched()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "late:1", 1m, 5m, 20));
        var foreign = new string('a', 64);
        await world.SeedStatementAsync(Period, 2m, foreign);
        var issued = await world.StatementRowAsync(Period);

        // The recompute passes every rule and then meets a document of the period it does not describe: another issue
        // wrote the period first, and the org is billed on what that document froze.
        var answer = await world.PostStatementAsync(Period, 5m, "USD");

        answer.Status.ShouldBe(HttpStatusCode.Conflict, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("statement_exists");
        (await world.StatementRowAsync(Period)).ShouldBe(issued, "the issued document is never rewritten");
        (await world.StatementCountAsync(Period)).ShouldBe(1L);
        (await world.AuditCountAsync(Action)).ShouldBe(0L, "nothing was issued");
        (await world.AuditCountAsync(RefusedAction)).ShouldBe(1L);
    }

    [Fact]
    public async Task Statement_ConcurrentIssues_WriteOneRowAndReplayTheOther()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(
            Ledger(world, "race:1", 1m, 2m, 20),
            Ledger(world, "race:2", 2m, 3m, 21));

        var answers = await Task.WhenAll(
            world.PostStatementAsync(Period, 5m, "USD"),
            world.PostStatementAsync(Period, 5m, "USD"));

        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.OK);
        answers.Count(answer => !answer.Body.GetProperty("replayed").GetBoolean()).ShouldBe(1,
            "exactly one request writes the statement, the other reads it back");
        answers.Select(answer => answer.Body.GetProperty("id").GetGuid()).Distinct().Count().ShouldBe(1);
        (await world.StatementCountAsync(Period)).ShouldBe(1L);
    }

    [Fact]
    public async Task Statement_PeriodOnlyAnotherOrgHolds_422NoRows()
    {
        await using var world = await StartAsync();
        var (otherOrg, otherProject) = await world.AddOtherOrgAsync();
        await world.SeedLedgerAsync(
            Ledger(world, "foreign:1", 1m, 5m, 20) with
            {
                Org = otherOrg,
                ProjectId = otherProject,
            });

        var answer = await world.PostStatementAsync(Period, 5m, "USD");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        Detail(answer).GetProperty("reason").GetString().ShouldBe("no_rows");
        (await world.StatementCountAsync(Period)).ShouldBe(0L);

        // The org that holds the rows can issue the same period, so the refusal was the scope and not the data.
        var owned = await world.PostStatementAsync(Period, 5m, "USD", otherOrg);
        owned.Status.ShouldBe(HttpStatusCode.OK, owned.ErrorMessage);
    }

    [Fact]
    public async Task Statement_LaterPriceAndLedgerChanges_DoNotAlterTheIssuedDocument()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "frozen:1", 1m, 5m, 20));
        var issued = await world.PostStatementAsync(Period, 5m, "USD");
        issued.Status.ShouldBe(HttpStatusCode.OK, issued.ErrorMessage);
        var stored = await world.StatementRowAsync(Period);

        // A rate-card change and a differently priced row of a later period are exactly what must not reach back.
        await world.SeedGlobalPriceAsync("results");
        await world.SeedLedgerAsync(Ledger(world, "frozen:later", 1m, 99m, 20, "2026-12"));

        var worksheet = await world.GetAsync(world.WorksheetUrl($"period={Period}"));
        worksheet.Body.GetProperty("content_hash").GetString()
            .ShouldBe(issued.Body.GetProperty("content_hash").GetString());

        var reissue = await world.PostStatementAsync(Period, 5m, "USD");
        reissue.Status.ShouldBe(HttpStatusCode.OK, reissue.ErrorMessage);
        reissue.Body.GetProperty("replayed").GetBoolean().ShouldBeTrue();
        (await world.StatementRowAsync(Period)).ShouldBe(stored);
    }

    [Fact]
    public async Task Statement_PastTheRowBound_422NotReconciled()
    {
        await using var world = await StartAsync();
        await world.SeedManyPricedLedgerRowsAsync(
            count: 10_001, period: Period, metric: "results", quantityEach: 1m, amountEach: 1m, currency: "USD");

        var answer = await world.PostStatementAsync(Period, 10_001m, "USD");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        Detail(answer).GetProperty("reason").GetString().ShouldBe("not_reconciled");
        (await world.StatementCountAsync(Period)).ShouldBe(0L);
    }

    [Fact]
    public async Task Statement_NonOperator_ForbiddenBeforeTheBodyIsRead()
    {
        await using var world = await StartAsync(OperatorGateMode.Refused);
        await world.SeedLedgerAsync(Ledger(world, "refused:1", 1m, 5m, 20));

        // The gate answers before the body is parsed, so a caller it refuses learns nothing about its body.
        var malformed = await world.PostStatementRawAsync("{ \"period\": ");
        malformed.Status.ShouldBe(HttpStatusCode.Forbidden);
        malformed.ErrorCode.ShouldBe("forbidden");

        var answer = await world.PostStatementAsync(Period, 5m, "USD");
        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        (await world.StatementCountAsync(Period)).ShouldBe(0L);
        (await world.AuditCountAsync(Action)).ShouldBe(0L);
    }

    [Fact]
    public async Task Statement_AdmittedWithoutSubject_403()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "subjectless:1", 1m, 5m, 20));

        // A gate that admits a principal with no id: the statement would have no actor to be attributed to.
        await using var api = TenancyApiFactory.RealResolver(
            _fixture.AppConnectionString(world.Database),
            services: services => services.AddSingleton<IOperatorGate>(new SubjectlessGate()));
        using var client = api.CreateClient();

        var answer = await client.SendAsync(
            HttpMethod.Post,
            world.StatementUrl(),
            _ => { },
            new { period = Period, total_amount = 5m, currency = "USD" });

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe("forbidden");
        (await world.StatementCountAsync(Period)).ShouldBe(0L);
        (await world.AuditCountAsync(Action)).ShouldBe(0L);
    }

    [Fact]
    public async Task Statement_AnonymousCaller_Unauthenticated()
    {
        await using var world = await StartAsync(OperatorGateMode.Denied);

        var answer = await world.PostStatementAsync(Period, 5m, "USD");

        answer.Status.ShouldBe(HttpStatusCode.Unauthorized);
        (await world.StatementCountAsync(Period)).ShouldBe(0L);
    }

    [Fact]
    public async Task Statement_AdmittedButMalformedBody_400AndNoRow()
    {
        await using var world = await StartAsync();

        var answer = await world.PostStatementRawAsync("{ \"period\": ");

        answer.Status.ShouldBe(HttpStatusCode.BadRequest, "an admitted operator gets the body's own refusal");
        answer.ErrorCode.ShouldBe(ApiErrorCode.RequestInvalid);
        Detail(answer).GetProperty("path").GetString().ShouldBe("body");
        Detail(answer).GetProperty("reason").GetString().ShouldBe("invalid_json");
        (await world.StatementCountAsync(Period)).ShouldBe(0L);
    }

    [Fact]
    public async Task Statement_OverCapBody_413BeforeTheParse()
    {
        await using var world = await StartAsync();
        var oversized = "{\"period\":\"" + new string('a', 5_000) + "\"}";

        var declared = await world.PostStatementRawAsync(oversized);
        declared.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        declared.ErrorCode.ShouldBe("payload_too_large");

        // No header declares this one: the cap counts the body while it is read.
        var chunked = await world.PostStatementRawAsync(oversized, chunked: true);
        chunked.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        chunked.ErrorCode.ShouldBe("payload_too_large");
        (await world.StatementCountAsync(Period)).ShouldBe(0L);
    }

    [Fact]
    public async Task Statement_UnknownOrg_NotFound()
    {
        await using var world = await StartAsync();
        var unknown = Guid.CreateVersion7();

        var answer = await world.PostStatementAsync(Period, 5m, "USD", unknown);

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe(ApiErrorCode.NotFound);
        (await world.AuditCountForAsync(unknown, Action)).ShouldBe(0L);
    }

    private Task<UsageReadsWorld> StartAsync(OperatorGateMode gate = OperatorGateMode.Permitted) =>
        UsageReadsWorld.StartAsync(_fixture, gate, new FakeTimeProvider(Now));

    /// <summary>One priced usage row of <paramref name="period"/>, at the minute <paramref name="minute"/> of the 20th.</summary>
    private static LedgerSeed Ledger(
        UsageReadsWorld world,
        string key,
        decimal quantity,
        decimal? amount,
        int minute,
        string period = Period) =>
        new(
            Guid.CreateVersion7(),
            world.Project,
            "results",
            quantity,
            period,
            key,
            new DateTimeOffset(2026, 10, 20, 8, minute, 0, TimeSpan.Zero),
            Amount: amount,
            Currency: amount is null ? null : "USD");

    private static JsonElement Detail(ApiAnswer answer) =>
        answer.Body.GetProperty("error").GetProperty("details")[0];

    /// <summary>A gate that admits a principal carrying no user id: an action nobody can be held to.</summary>
    private sealed class SubjectlessGate : IOperatorGate
    {
        /// <inheritdoc />
        public ValueTask<bool> IsOperatorAsync(HttpContext httpContext)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "operator-test"));

            return ValueTask.FromResult(true);
        }
    }
}
