using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// What issuing a statement refuses when the document or the period itself does not hold still: a worksheet past
/// the size a statement row stores, a worksheet near that size that the column does store whole, and a period whose
/// ledger gains a row between the worksheet read and the freeze.
/// </summary>
/// <remarks>
/// The world runs on one fixed instant (2026-11-06), so <see cref="Period"/> has ended. The late row is written by a
/// trigger on the statement insert, which is the only way to place a ledger row inside the window deterministically:
/// the row is real, it is written in the statement's own transaction, and it rolls back with the refused issue.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class UsageStatementFreezeTests(PostgresTenancyFixture fixture)
{
    private const string Period = "2026-10";
    private const string Action = "usage.statement_issued";
    private const int LongestReason = 2_000;

    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Statement_CorrectionsPastTheDocumentCap_422WorksheetTooLarge()
    {
        await using var world = await StartAsync();
        var original = Ledger(world, "cap:original", 1m, 1m, 20);
        await world.SeedLedgerAsync(original);

        // Three thousand corrections of the longest reason the ledger accepts: past what the app freezes and,
        // without that rule, past the column's own bound, which the caller would meet as a server error.
        await world.SeedManyCorrectionsAsync(
            count: 3_000, period: Period, metric: "results", adjustmentOf: original.Id,
            reason: new string('x', LongestReason), currency: "USD");

        var answer = await world.PostStatementAsync(Period, -2_999m, "USD");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        Detail(answer).GetProperty("path").GetString().ShouldBe("period");
        Detail(answer).GetProperty("reason").GetString().ShouldBe("worksheet_too_large");
        (await world.StatementCountAsync(Period)).ShouldBe(0L);
        (await world.AuditCountAsync(Action)).ShouldBe(0L);
    }

    [Fact]
    public async Task Statement_LargeWorksheetWithinTheDocumentCap_IsStored()
    {
        await using var world = await StartAsync();
        var original = Ledger(world, "large:original", 1m, 1m, 20);
        await world.SeedLedgerAsync(original);
        await world.SeedManyCorrectionsAsync(
            count: 2_000, period: Period, metric: "results", adjustmentOf: original.Id,
            reason: new string('x', LongestReason), currency: "USD");

        var answer = await world.PostStatementAsync(Period, -1_999m, "USD");

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("line_count").GetInt32().ShouldBe(1);

        var stored = (await world.StatementRowAsync(Period)).ShouldNotBeNull().Split('|', 8)[7];
        stored.Length.ShouldBeGreaterThan(3_000_000,
            "a multi-megabyte document is what the cap bounds, and the column stores it whole");
    }

    [Fact]
    public async Task Statement_LedgerRowArrivingBeforeTheFreeze_409AndNothingIsWritten()
    {
        await using var world = await StartAsync();
        await world.SeedLedgerAsync(Ledger(world, "moved:1", 1m, 5m, 20));
        await world.InstallLateLedgerRowTriggerAsync();

        var answer = await world.PostStatementAsync(Period, 5m, "USD");

        answer.Status.ShouldBe(HttpStatusCode.Conflict, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("ledger_changed");
        (await world.StatementCountAsync(Period)).ShouldBe(0L, "the scope is never committed");
        (await world.AuditCountAsync(Action)).ShouldBe(0L);
        (await world.LedgerRowCountAsync(Period)).ShouldBe(1L,
            "the late row was written in the statement's own transaction, so it rolls back with it");

        // The period itself is sound: with nothing else writing to it, the same issue is accepted.
        await world.RemoveLateLedgerRowTriggerAsync();
        var issued = await world.PostStatementAsync(Period, 5m, "USD");
        issued.Status.ShouldBe(HttpStatusCode.OK, issued.ErrorMessage);
        (await world.StatementCountAsync(Period)).ShouldBe(1L);
    }

    private Task<UsageReadsWorld> StartAsync() =>
        UsageReadsWorld.StartAsync(fixture, OperatorGateMode.Permitted, new FakeTimeProvider(Now));

    /// <summary>One priced usage row of <see cref="Period"/>, at the minute <paramref name="minute"/> of the 20th.</summary>
    private static LedgerSeed Ledger(UsageReadsWorld world, string key, decimal quantity, decimal amount, int minute) =>
        new(
            Guid.CreateVersion7(),
            world.Project,
            "results",
            quantity,
            Period,
            key,
            new DateTimeOffset(2026, 10, 20, 8, minute, 0, TimeSpan.Zero),
            Amount: amount,
            Currency: "USD");

    private static JsonElement Detail(ApiAnswer answer) =>
        answer.Body.GetProperty("error").GetProperty("details")[0];
}
