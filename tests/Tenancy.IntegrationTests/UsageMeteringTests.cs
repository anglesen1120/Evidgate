using Evidgate.Modules.Usage.Application;
using Evidgate.Modules.Usage.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The usage-units measurement of a committed ledger batch: it sums the inserted rows per metric in the counter's own
/// representation (double), so a batch the ledger accepted can never be thrown away by a decimal sum that overflows.
/// The suite's exclusive collection keeps a sibling ledger batch from reaching this counter inside the assertion.
/// </summary>
[Collection(ExclusiveTenancySuite.Name)]
public sealed class UsageMeteringTests
{
    private const string Metric = "results";

    /// <summary>
    /// Two ledger rows at <see cref="decimal.MaxValue"/> are a legal batch — the column admits each one — so
    /// scheduling their measurement must not throw: the sum happens in double, where the total is representable.
    /// </summary>
    [Fact]
    public async Task Schedule_TwoMaxDecimalQuantities_Is_Counted_Without_Overflow()
    {
        var scope = new TestTenantScope();
        var rows = new[] { Entry("key-1"), Entry("key-2") };
        var insertedKeys = new[] { "key-1", "key-2" };

        using var listener = IngestTelemetryCapture.UsageUnits(out var units);

        Should.NotThrow(() => UsageMetering.Schedule(scope, rows, insertedKeys));

        units.Count.ShouldBe(0, "the measurement waits for the commit the caller owns");
        await scope.CommitAsync(TestContext.Current.CancellationToken);

        var recorded = units.ShouldHaveSingleItem();
        recorded.Metric.ShouldBe(Metric);
        recorded.Quantity.ShouldBe((double)decimal.MaxValue + (double)decimal.MaxValue);
    }

    private static PendingUsageEntry Entry(string billingKey)
    {
        var projectId = Guid.CreateVersion7();

        return new PendingUsageEntry(
            Guid.CreateVersion7(),
            ProjectId: projectId,
            Metric,
            Quantity: decimal.MaxValue,
            Unit: "count",
            Period: "2026-11",
            BillingKey: billingKey,
            OccurredAt: DateTimeOffset.UnixEpoch,
            PayloadHash: UsagePayloadHash.Compute(Metric, projectId, decimal.MaxValue, "count", "2026-11"));
    }
}
