using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The billing operator's ledger page over the real host and a real PostgreSQL: the keyset that keeps a
/// page boundary correct, the filters, the refusals, and the audit row every successful read appends.
/// </summary>
/// <remarks>
/// The route is driven over HTTP only — the wire names, the status codes and the envelope are the
/// contract and nothing here reaches a module type. Rows are seeded as the owner with pinned
/// instants, because the two properties this suite exists for (a keyset over equal instants, and a
/// period boundary at midnight UTC) cannot be arranged through the write path.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class UsageLedgerReadTests
{
    private const string Period = "2026-10";

    private static readonly DateTimeOffset SameInstant =
        new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresTenancyFixture _fixture;

    public UsageLedgerReadTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task LedgerPage_EqualCreatedAt_KeysetPagesEveryRowExactlyOnce()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);

        // Five rows share one instant — a batch's rows do — and two are later; a page boundary inside the tied
        // instant is the only case a comparison on created_at alone would lose a row on.
        var tied = Enumerable.Range(0, 5)
            .Select(index => new LedgerSeed(
                Guid.CreateVersion7(), world.Project, "results", 1m, Period, $"tied:{index}", SameInstant))
            .ToArray();
        var late = new[] { Later(world, "late:1", 1), Later(world, "late:2", 2) };
        await world.SeedLedgerAsync([.. tied, .. late]);

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var answer = await world.GetAsync(world.LedgerUrl(
                $"period={Period}&limit=2{(cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor))}"));

            answer.Status.ShouldBe(HttpStatusCode.OK);
            seen.AddRange(UsageReadsWorld.ItemIds(answer));
            cursor = answer.Body.GetProperty("next_cursor") is { ValueKind: JsonValueKind.String } next
                ? next.GetString()
                : null;
        }
        while (cursor is not null);

        seen.Count.ShouldBe(7);
        seen.Distinct().Count().ShouldBe(7, "a keyset page never repeats a row");
        seen.ShouldBe([.. tied.Select(row => row.Id), .. late.Select(row => row.Id)], ignoreOrder: true);
    }

    [Fact]
    public async Task LedgerPage_TamperedCursor_BadRequest()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        await world.SeedLedgerAsync(Later(world, "a:1", 1), Later(world, "a:2", 2), Later(world, "a:3", 3));

        var tampered = await world.GetAsync(world.LedgerUrl("limit=1&cursor=not-a-cursor"));
        tampered.Status.ShouldBe(HttpStatusCode.BadRequest);
        tampered.ErrorCode.ShouldBe(ApiErrorCode.RequestInvalid);
        tampered.Body.GetProperty("error").GetProperty("details")[0].GetProperty("path").GetString()
            .ShouldBe("cursor");

        // The positive control: the cursor this endpoint issued is accepted and continues where page one ended.
        var first = await world.GetAsync(world.LedgerUrl("limit=1"));
        first.Status.ShouldBe(HttpStatusCode.OK);
        var firstIds = UsageReadsWorld.ItemIds(first);
        firstIds.Count.ShouldBe(1);

        var continuation = first.Body.GetProperty("next_cursor").GetString();
        continuation.ShouldNotBeNull();

        var second = await world.GetAsync(world.LedgerUrl(
            $"limit=1&cursor={Uri.EscapeDataString(continuation!)}"));
        second.Status.ShouldBe(HttpStatusCode.OK);
        UsageReadsWorld.ItemIds(second).ShouldNotContain(firstIds[0]);
    }

    [Fact]
    public async Task LedgerPage_UnknownOrg_NotFoundAndNoAuditRow()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);

        var unknown = Guid.CreateVersion7();
        var answer = await world.GetAsync($"/internal/operator/orgs/{unknown:D}/usage-ledger?period={Period}");

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe(ApiErrorCode.NotFound);

        (await world.AuditCountForAsync(unknown, UsageLedgerReadAction())).ShouldBe(0);
    }

    [Fact]
    public async Task LedgerPage_OtherOrgsRows_AreInvisible()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        var (otherOrg, otherProject) = await world.AddOtherOrgAsync();

        var mine = new[] { Later(world, "mine:1", 1), Later(world, "mine:2", 2) };
        await world.SeedLedgerAsync(mine);
        await world.SeedLedgerAsync(
            new LedgerSeed(Guid.CreateVersion7(), otherProject, "results", 9m, Period, "theirs:1",
                SameInstant, Org: otherOrg),
            new LedgerSeed(Guid.CreateVersion7(), null, "results", 9m, Period, "theirs:2",
                SameInstant, Org: otherOrg));

        var answer = await world.GetAsync(world.LedgerUrl($"period={Period}"));
        answer.Status.ShouldBe(HttpStatusCode.OK);

        UsageReadsWorld.ItemIds(answer).ShouldBe([.. mine.Select(row => row.Id)], ignoreOrder: true);
    }

    [Fact]
    public async Task LedgerPage_DeniedGate_Unauthenticated()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Denied);
        await world.SeedLedgerAsync(Later(world, "denied:1", 1));

        var answer = await world.GetAsync(world.LedgerUrl());

        answer.Status.ShouldBe(HttpStatusCode.Unauthorized);
        answer.ErrorCode.ShouldBe(ApiErrorCode.Unauthenticated);
        (await world.AuditCountAsync(UsageLedgerReadAction())).ShouldBe(0, "a denied read writes no audit row");
    }

    [Fact]
    public async Task LedgerPage_AuthenticatedButNotOperator_Forbidden()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Refused);
        await world.SeedLedgerAsync(Later(world, "refused:1", 1));

        var answer = await world.GetAsync(world.LedgerUrl());

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe(ApiErrorCode.Forbidden);
        (await world.AuditCountAsync(UsageLedgerReadAction())).ShouldBe(0, "a refused read writes no audit row");
    }

    [Fact]
    public async Task LedgerPage_EachSuccessfulRead_WritesOneAuditRow()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        await world.SeedLedgerAsync(Later(world, "audited:1", 1), Later(world, "audited:2", 2));

        var first = await world.GetAsync(world.LedgerUrl($"period={Period}&metric=results&limit=1"));
        first.Status.ShouldBe(HttpStatusCode.OK);

        var continuation = first.Body.GetProperty("next_cursor").GetString();
        continuation.ShouldNotBeNull();
        var second = await world.GetAsync(world.LedgerUrl(
            $"period={Period}&metric=results&limit=1&cursor={Uri.EscapeDataString(continuation!)}"));
        second.Status.ShouldBe(HttpStatusCode.OK);

        var rows = await world.AuditRowsAsync(UsageLedgerReadAction());
        rows.Count.ShouldBe(2);

        var recorded = ParseAudit(rows[0]);
        recorded.ActorType.ShouldBe("platform_operator");
        recorded.ActorId.ShouldBe(UsageReadsWorld.OperatorId.ToString("D"));
        recorded.ObjectType.ShouldBe("usage_ledger");
        recorded.ObjectId.ShouldBe(world.Org.ToString("D"));
        recorded.Metadata["period"].ShouldBe(Period);
        recorded.Metadata["metric"].ShouldBe("results");
        recorded.Metadata["limit"].ShouldBe("1");
        recorded.Metadata["count"].ShouldBe("1");
        recorded.Metadata.ShouldNotContainKey("has_cursor", "the first page names no continuation");

        var continued = ParseAudit(rows[1]);
        continued.Metadata["has_cursor"].ShouldBe("true", "the audit row names that a continuation was used");
        continued.Metadata.ShouldNotContainKey("cursor", "the continuation itself is never recorded");

        // A refused request is not a read: nothing is appended for it.
        var refused = await world.GetAsync(world.LedgerUrl("period=2026-13"));
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await world.AuditRowsAsync(UsageLedgerReadAction())).Count.ShouldBe(2);
    }

    [Fact]
    public async Task LedgerPage_PeriodFilter_FollowsTheRowsOwnMonthNotItsInstant()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);

        // The instant straddles the month boundary; the period names the month each row belongs to, and the
        // filter must follow the row rather than re-derive a month from occurred_at in another zone.
        var january = new LedgerSeed(
            Guid.CreateVersion7(), world.Project, "results", 1m, "2026-01", "boundary:jan",
            new DateTimeOffset(2026, 2, 3, 0, 0, 1, TimeSpan.Zero),
            OccurredAt: new DateTimeOffset(2026, 1, 31, 23, 59, 59, TimeSpan.Zero));
        var february = new LedgerSeed(
            Guid.CreateVersion7(), world.Project, "results", 1m, "2026-02", "boundary:feb",
            new DateTimeOffset(2026, 2, 3, 0, 0, 2, TimeSpan.Zero),
            OccurredAt: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        await world.SeedLedgerAsync(january, february);

        var januaryPage = await world.GetAsync(world.LedgerUrl("period=2026-01"));
        januaryPage.Status.ShouldBe(HttpStatusCode.OK);
        UsageReadsWorld.ItemIds(januaryPage).ShouldBe([january.Id]);

        var februaryPage = await world.GetAsync(world.LedgerUrl("period=2026-02"));
        februaryPage.Status.ShouldBe(HttpStatusCode.OK);
        UsageReadsWorld.ItemIds(februaryPage).ShouldBe([february.Id]);
    }

    [Fact]
    public async Task LedgerPage_MetricFilter_ReturnsOnlyThatMetric()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        var results = Later(world, "metric:results", 1);
        var support = Later(world, "metric:support", 2) with { Metric = "support_hours", ProjectId = null };
        await world.SeedLedgerAsync(results, support, Later(world, "period:other", 3) with { Period = "2026-09" });

        var byMetric = await world.GetAsync(world.LedgerUrl("metric=support_hours"));
        byMetric.Status.ShouldBe(HttpStatusCode.OK);
        UsageReadsWorld.ItemIds(byMetric).ShouldBe([support.Id]);

        var byPeriod = await world.GetAsync(world.LedgerUrl($"period={Period}"));
        UsageReadsWorld.ItemIds(byPeriod).ShouldBe([results.Id, support.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task LedgerPage_InvalidPeriodOrMetric_Unprocessable()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);

        var period = await world.GetAsync(world.LedgerUrl("period=2026-10-01"));
        period.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        period.ErrorCode.ShouldBe(ApiErrorCode.RequestInvalid);
        period.Body.GetProperty("error").GetProperty("details")[0].GetProperty("path").GetString()
            .ShouldBe("period");

        var metric = await world.GetAsync(world.LedgerUrl("metric=storage"));
        metric.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        metric.Body.GetProperty("error").GetProperty("details")[0].GetProperty("path").GetString()
            .ShouldBe("metric");
    }

    [Fact]
    public async Task LedgerPage_Limit_ClampedToAtLeastOneAndRefusedWhenNotAnInteger()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        await world.SeedLedgerAsync(Later(world, "limit:1", 1), Later(world, "limit:2", 2));

        var zero = await world.GetAsync(world.LedgerUrl("limit=0"));
        zero.Status.ShouldBe(HttpStatusCode.OK);
        UsageReadsWorld.ItemIds(zero).Count.ShouldBe(1, "a page is never empty because a caller asked for none");

        var text = await world.GetAsync(world.LedgerUrl("limit=abc"));
        text.Status.ShouldBe(HttpStatusCode.BadRequest);
        text.Body.GetProperty("error").GetProperty("details")[0].GetProperty("path").GetString()
            .ShouldBe("limit");
    }

    [Fact]
    public async Task LedgerPage_ReadRow_CarriesEveryStoredBillingFact()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        // The ledger's price_ref is a real foreign key: the row must name a price its org may see.
        var priceRef = await world.SeedGlobalPriceAsync("artifact_gb_month");
        var original = Later(world, "facts:original", 1) with
        {
            Quantity = 4.5m,
            Unit = "gb_month",
            Metric = "artifact_gb_month",
            PriceRef = priceRef,
            Amount = 12.5m,
            Currency = "USD",
        };
        var adjustment = Later(world, "facts:adjustment", 2) with
        {
            Quantity = -1.5m,
            Metric = "artifact_gb_month",
            AdjustmentOf = original.Id,
            Reason = "overcounted upload",
        };
        await world.SeedLedgerAsync(original, adjustment);

        var answer = await world.GetAsync(world.LedgerUrl($"period={Period}&metric=artifact_gb_month"));
        answer.Status.ShouldBe(HttpStatusCode.OK);

        var items = answer.Body.GetProperty("items").EnumerateArray().ToArray();
        items.Length.ShouldBe(2);

        var correction = items.Single(item => item.GetProperty("id").GetGuid() == adjustment.Id);
        correction.GetProperty("adjustment_of").GetGuid().ShouldBe(original.Id);
        correction.GetProperty("reason").GetString().ShouldBe("overcounted upload");
        correction.GetProperty("quantity").GetDecimal().ShouldBe(-1.5m);
        correction.GetProperty("currency").ValueKind.ShouldBe(JsonValueKind.Null, "an unpriced row has no amount");

        var first = items.Single(item => item.GetProperty("id").GetGuid() == original.Id);
        first.GetProperty("billing_key").GetString().ShouldBe("facts:original");
        first.GetProperty("unit").GetString().ShouldBe("gb_month");
        first.GetProperty("project_id").GetGuid().ShouldBe(world.Project);
        first.GetProperty("period").GetString().ShouldBe(Period);
        first.GetProperty("price_ref").GetGuid().ShouldBe(priceRef, "the card the row was priced with is recorded");
        first.GetProperty("amount").GetDecimal().ShouldBe(12.5m);
        first.GetProperty("currency").GetString().ShouldBe("USD");
    }

    [Fact]
    public async Task LedgerPage_RedatedRow_CarriesTheMonthItWasMeteredFor()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        // Seeded as the owner: a month that was never issued is written as it comes, and the re-dating the write
        // path would have done is recorded here as the two periods it leaves behind.
        var redated = Later(world, "redated:1", 1) with { OriginalPeriod = "2026-09" };
        var ordinary = Later(world, "redated:2", 2);
        await world.SeedLedgerAsync(redated, ordinary);

        var answer = await world.GetAsync(world.LedgerUrl($"period={Period}"));
        answer.Status.ShouldBe(HttpStatusCode.OK);

        var items = answer.Body.GetProperty("items").EnumerateArray().ToArray();
        items.Single(item => item.GetProperty("id").GetGuid() == redated.Id)
            .GetProperty("original_period").GetString().ShouldBe("2026-09");
        items.Single(item => item.GetProperty("id").GetGuid() == ordinary.Id)
            .GetProperty("original_period").ValueKind.ShouldBe(
                JsonValueKind.Null,
                "a row that landed in its own month names no other month");
    }

    /// <summary>A ledger row of this world's project at <paramref name="offset"/> days after the shared instant.</summary>
    /// <param name="world">The world whose project the row belongs to.</param>
    /// <param name="key">The billing key.</param>
    /// <param name="offset">Days after <see cref="SameInstant"/>, so a page order is predictable.</param>
    private static LedgerSeed Later(UsageReadsWorld world, string key, int offset) => new(
        Guid.CreateVersion7(),
        world.Project,
        "results",
        1m,
        Period,
        key,
        SameInstant.AddDays(offset));

    /// <summary>The audited action of a ledger read.</summary>
    private static string UsageLedgerReadAction() => "usage.ledger_read";

    /// <summary>Splits one audit row's <c>actor_type|actor_id|object_type|object_id|metadata</c> form.</summary>
    private static RecordedAudit ParseAudit(string row)
    {
        var parts = row.Split('|', 5);
        parts.Length.ShouldBe(5);

        var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(parts[4])
            ?? throw new InvalidOperationException("the audit metadata of a billing read is never null");

        return new RecordedAudit(parts[0], parts[1], parts[2], parts[3], metadata);
    }

    /// <summary>One audit row, in the form the assertions read it.</summary>
    private sealed record RecordedAudit(
        string ActorType,
        string ActorId,
        string ObjectType,
        string ObjectId,
        IReadOnlyDictionary<string, string> Metadata);
}
