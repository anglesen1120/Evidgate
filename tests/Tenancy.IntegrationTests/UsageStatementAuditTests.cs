using System.Net;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// What one issue attempt leaves in the audit chain: exactly one row for every refusal and for a replay, the org as a
/// refusal's object because no statement exists to name there, a payload that carries no amount, worksheet or
/// submitted value, and no row at all for an org the caller cannot see.
/// </summary>
/// <remarks>
/// The world runs on one fixed instant (2026-11-06), so <see cref="Period"/> has ended and <see cref="CurrentPeriod"/>
/// has not. Every audit row is read as the owner: the assertions are about what the chain holds, not about what a read
/// route reports. The late-ledger case holds a real open transaction against the period's advisory lock, which is the
/// only way to place a row between the worksheet read and the freeze.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class UsageStatementAuditTests(PostgresTenancyFixture fixture)
{
    private const string Period = "2026-10";
    private const string CurrentPeriod = "2026-11";
    private const string IssuedAction = "usage.statement_issued";
    private const string RefusedAction = "usage.statement_refused";
    private const string ReplayedAction = "usage.statement_replayed";
    private const string RefusedOutcome = "refused";
    private const string ReplayedOutcome = "replayed";

    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Statement_OpenPeriod_RefusesAndAuditsThePeriodOnce()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "open:1", 5m));

        var answer = await world.PostStatementAsync(CurrentPeriod, 5m, "USD");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        Reason(answer).ShouldBe("period_open");
        (await world.StatementCountAsync(CurrentPeriod)).ShouldBe(0L);
        (await world.AuditCountAsync(IssuedAction)).ShouldBe(0L);

        var rows = await world.AuditRowsAsync(RefusedAction);
        rows.Count.ShouldBe(1, "one attempt leaves one row");
        RefusalShouldBe(rows[0], world.Org, "period_open", CurrentPeriod);
    }

    [Fact]
    public async Task Statement_WorksheetRule_RefusesAndAuditsTheRuleCode()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "mismatch:1", 10.50m));

        var answer = await world.PostStatementAsync(Period, 10.51m, "USD");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        Reason(answer).ShouldBe("total_mismatch");
        (await world.StatementCountAsync(Period)).ShouldBe(0L);
        (await world.AuditCountAsync(IssuedAction)).ShouldBe(0L);

        var rows = await world.AuditRowsAsync(RefusedAction);
        rows.Count.ShouldBe(1, "one attempt leaves one row");
        RefusalShouldBe(rows[0], world.Org, "total_mismatch", Period);

        // The submitted total is request text: the chain must not be able to answer what was asked for.
        rows[0].ShouldNotContain("10.51", Case.Sensitive);
    }

    [Fact]
    public async Task Statement_UnparsablePeriod_ExistingOrg_AuditsTheShapeWithoutTheSubmittedText()
    {
        await using var world = await StartAsync();

        var answer = await world.PostStatementAsync("2026-13", 1m, "USD");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        Reason(answer).ShouldBe("format");
        (await world.AuditCountAsync(IssuedAction)).ShouldBe(0L);

        var rows = await world.AuditRowsAsync(RefusedAction);
        rows.Count.ShouldBe(1, "the org exists, so its refusal is recorded");
        RefusalShouldBe(rows[0], world.Org, "format", null);
        rows[0].ShouldNotContain("2026-13", Case.Sensitive, "a period that failed the shape rule is request text");
    }

    [Fact]
    public async Task Statement_UnknownOrg_AnswersWithoutAuditingAnything()
    {
        await using var world = await StartAsync();
        var unknown = Guid.CreateVersion7();

        // The period is wrong and the org does not exist: the answer stays the period's own refusal, so a bad shape
        // is not a way to probe which orgs exist.
        var invalid = await world.PostStatementAsync("2026-13", 1m, "USD", unknown);
        invalid.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, invalid.ErrorMessage);
        Reason(invalid).ShouldBe("format");

        var absent = await world.PostStatementAsync(Period, 1m, "USD", unknown);
        absent.Status.ShouldBe(HttpStatusCode.NotFound, absent.ErrorMessage);

        (await world.AuditCountForAsync(unknown, RefusedAction)).ShouldBe(
            0L,
            "nothing happened in an org this request cannot see");
        (await world.AuditCountAsync(RefusedAction)).ShouldBe(0L);
        (await world.AuditCountAsync(IssuedAction)).ShouldBe(0L);
    }

    [Fact]
    public async Task Statement_StoredStatementOfAnotherContent_RefusesAndAuditsTheConflict()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "conflict:1", 5m));
        var stored = new string('a', 64);
        await world.SeedStatementAsync(Period, 5m, stored);

        var answer = await world.PostStatementAsync(Period, 5m, "USD");

        answer.Status.ShouldBe(HttpStatusCode.Conflict, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("statement_exists");
        (await world.StatementCountAsync(Period)).ShouldBe(1L, "the stored document is left as issued");
        (await world.StatementRowAsync(Period)).ShouldNotBeNull().Split('|', 8)[5].ShouldBe(stored);
        (await world.AuditCountAsync(IssuedAction)).ShouldBe(0L);

        var rows = await world.AuditRowsAsync(RefusedAction);
        rows.Count.ShouldBe(1, "a conflict is still one attempt");
        RefusalShouldBe(rows[0], world.Org, "statement_exists", Period);
    }

    /// <summary>
    /// The refused statement must not survive its own refusal: the row is written, the ledger moves underneath it, and
    /// the answer is 409 while the chain keeps the refusal that replaced the document.
    /// </summary>
    [Fact]
    public async Task Statement_LedgerMovesDuringTheFreeze_RollsTheStatementBackAndKeepsTheRefusal()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "moved:1", 5m));
        var cancellationToken = TestContext.Current.CancellationToken;

        // The uncommitted row holds the period's shared advisory lock, so the statement's own insert waits for it:
        // the row lands between the worksheet read and the freeze by construction, not by timing.
        await using var late = await world.BeginLateLedgerRowAsync(Period, 5m);
        var issue = world.PostStatementAsync(Period, 5m, "USD");
        await late.WaitForStatementAsync(cancellationToken);
        await late.CommitAsync(cancellationToken);

        var answer = await issue;
        answer.Status.ShouldBe(HttpStatusCode.Conflict, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("ledger_changed");
        (await world.StatementCountAsync(Period)).ShouldBe(0L, "the statement's scope was discarded");
        (await world.LedgerRowCountAsync(Period)).ShouldBe(2L, "the late row committed with its own transaction");
        (await world.AuditCountAsync(IssuedAction)).ShouldBe(0L);

        var rows = await world.AuditRowsAsync(RefusedAction);
        rows.Count.ShouldBe(1, "the refusal outlives the rollback that removed its statement");
        RefusalShouldBe(rows[0], world.Org, "ledger_changed", Period);
    }

    [Fact]
    public async Task Statement_Reissue_AuditsTheReplayOnceAndIssuesTheStatementOnce()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "replay:1", 5m));

        var issued = await world.PostStatementAsync(Period, 5m, "USD");
        var replayed = await world.PostStatementAsync(Period, 5m, "USD");

        issued.Status.ShouldBe(HttpStatusCode.OK, issued.ErrorMessage);
        replayed.Status.ShouldBe(HttpStatusCode.OK, replayed.ErrorMessage);
        replayed.Body.GetProperty("replayed").GetBoolean().ShouldBeTrue();
        (await world.StatementCountAsync(Period)).ShouldBe(1L);

        (await world.AuditCountAsync(IssuedAction)).ShouldBe(1L, "the issue that wrote the document");
        (await world.AuditRowsAsync(IssuedAction))[0].Split('|', 5)[3]
            .ShouldBe(issued.Body.GetProperty("id").GetGuid().ToString("D"));

        (await world.AuditCountAsync(RefusedAction)).ShouldBe(0L);
        var rows = await world.AuditRowsAsync(ReplayedAction);
        rows.Count.ShouldBe(1, "a replay changed nothing, but the request is still one audited attempt");

        var parts = rows[0].Split('|', 5);
        parts[0].ShouldBe("platform_operator");
        parts[1].ShouldBe(UsageReadsWorld.OperatorId.ToString("D"));
        parts[2].ShouldBe("billing_statement");
        parts[3].ShouldBe(issued.Body.GetProperty("id").GetGuid().ToString("D"),
            "a replay names the stored statement it answered with");

        using var metadata = JsonDocument.Parse(parts[4]);
        var values = Values(metadata.RootElement);
        values["period"].ShouldBe(Period);
        values["outcome"].ShouldBe(ReplayedOutcome);
        values.Count.ShouldBe(2, "a replay records the period and the outcome, and no amount");
    }

    [Fact]
    public async Task Statement_RefusalWhoseAuditCannotBeWritten_FailsTheRequestAndCommitsNothing()
    {
        await using var world = await StartAsync();

        await using var failing = world.Host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITenantDb>();
            services.AddSingleton<TenantDb>();
            services.AddSingleton<ITenantDb>(provider =>
                new ThrowingTenantDb(provider.GetRequiredService<TenantDb>(), "insert into public.audit_log"));
        }));
        using var client = failing.CreateClient();

        var answer = await PostStatementAsync(client, world.StatementUrl(), CurrentPeriod, 5m, "USD");

        answer.Status.ShouldBe(HttpStatusCode.InternalServerError, answer.ErrorMessage);
        (await world.AuditCountAsync(RefusedAction)).ShouldBe(0L, "a refusal that could not be recorded must not answer");
        (await world.AuditCountAsync(IssuedAction)).ShouldBe(0L);
        (await world.StatementCountAsync(CurrentPeriod)).ShouldBe(0L);
    }

    private Task<UsageReadsWorld> StartAsync() =>
        UsageReadsWorld.StartAsync(fixture, OperatorGateMode.Permitted, new FakeTimeProvider(Now));

    private static LedgerSeed Ledger(UsageReadsWorld world, string key, decimal amount) =>
        new(
            Guid.CreateVersion7(),
            world.Project,
            "results",
            1m,
            Period,
            key,
            new DateTimeOffset(2026, 10, 20, 8, 20, 0, TimeSpan.Zero),
            Amount: amount,
            Currency: "USD");

    /// <summary>One request of a host this test composed itself, whose body is the shape the route expects.</summary>
    private static async Task<ApiAnswer> PostStatementAsync(
        HttpClient client,
        string url,
        string? period,
        decimal? totalAmount,
        string? currency)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var json = JsonSerializer.Serialize(new { period, total_amount = totalAmount, currency });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(url, content, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    /// <summary>
    /// A refusal row: the operator as its actor, the org as its object, and a payload of the reason code, the outcome
    /// and the period only when the period passed the shape rule.
    /// </summary>
    private static void RefusalShouldBe(string row, Guid org, string reason, string? period)
    {
        var parts = row.Split('|', 5);
        parts[0].ShouldBe("platform_operator");
        parts[1].ShouldBe(UsageReadsWorld.OperatorId.ToString("D"));
        parts[2].ShouldBe("billing_statement");
        parts[3].ShouldBe(org.ToString("D"), "a refusal names the org: the period has no statement to name");

        using var metadata = JsonDocument.Parse(parts[4]);
        var values = Values(metadata.RootElement);
        values["reason"].ShouldBe(reason);
        values["outcome"].ShouldBe(RefusedOutcome);

        if (period is null)
        {
            values.ShouldNotContainKey("period", "a period that failed the shape rule must not be echoed");
        }
        else
        {
            values["period"].ShouldBe(period);
        }

        values.Count.ShouldBe(period is null ? 2 : 3, "only the reason, the outcome and the period may be recorded");
    }

    private static Dictionary<string, string> Values(JsonElement metadata) =>
        metadata.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.GetString() ?? string.Empty,
            StringComparer.Ordinal);

    private static string? Reason(ApiAnswer answer) =>
        answer.Body.GetProperty("error").GetProperty("details")[0].GetProperty("reason").GetString();
}
