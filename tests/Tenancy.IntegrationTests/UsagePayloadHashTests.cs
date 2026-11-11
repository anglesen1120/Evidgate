// The canonical payload hash a billing key claims, without a database: a fixed digest vector pins the canonical JSON,
// the decimal and culture cases pin the normalisation, and the field-by-field cases pin what is billable identity and
// what is not (occurred_at, the key itself). The batch rules half proves the in-request repeat classification.
using System.Globalization;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <c>UsagePayloadHash</c>: the digest of what a ledger row bills. It decides whether a reused billing key is a
/// verified replay or a conflict, so the same payload must always hash alike (any culture, any decimal scale) and a
/// different billable field must always hash differently.
/// </summary>
public sealed class UsagePayloadHashTests
{
    private const string Period = "2026-10";

    private static readonly Guid Project = Guid.Parse("0199c8f0-1111-7000-8000-000000000001");

    /// <summary>
    /// The canonical JSON of <c>results</c>, no project, quantity 1.5, unit <c>batch</c>, period 2026-10 is
    /// <c>{"metric":"results","period":"2026-10","project_id":null,"quantity":"1.5","unit":"batch"}</c> (RFC 8785 sorts
    /// the keys); this digest pins that byte sequence and <see cref="CanonicalHash.Prefix"/>.
    /// </summary>
    [Fact]
    public void Compute_MatchesTheFixedVector()
    {
        UsagePayloadHash.Compute(UsageLedgerMetricNames.Results, null, 1.5m, "batch", Period)
            .ShouldBe("sha256:e9b14db58364fb693b62b1bac8c0aaa38006ca363b805095b12539ac31674e1a");
    }

    [Fact]
    public void Compute_WithAProject_WritesTheLowerCaseDProjectId()
    {
        UsagePayloadHash.Compute(UsageLedgerMetricNames.Results, Project, 2m, "batch", Period)
            .ShouldBe("sha256:188e70f262e4c0c3f224f59c06a21313a75826b4fbca994fb2bdf6736883b035");
    }

    /// <summary>
    /// The hash is the durable identity of a row, so it cannot depend on the environment that computed it: a Turkish
    /// or Vietnamese current culture changes how .NET formats a decimal unless the formatter says otherwise.
    /// </summary>
    /// <param name="culture">The current culture the hash is computed under.</param>
    [Theory]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    [InlineData("vi-VN")]
    [InlineData("en-US")]
    public void Compute_IsIndependentOfTheCurrentCulture(string culture)
    {
        var expected = UsagePayloadHash.Compute(UsageLedgerMetricNames.Results, Project, 1.5m, "batch", Period);
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);

            UsagePayloadHash.Compute(UsageLedgerMetricNames.Results, Project, 1.5m, "batch", Period)
                .ShouldBe(expected);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    /// <summary>A stored row keeps its scale, so 1.50 and 1.5 are the same bill and must hash alike.</summary>
    /// <param name="quantity">The quantity at one scale.</param>
    [Theory]
    [InlineData("1.5")]
    [InlineData("1.50")]
    [InlineData("1.500")]
    public void Compute_NormalisesTheDecimalScale(string quantity)
    {
        var expected = UsagePayloadHash.Compute(
            UsageLedgerMetricNames.Results, Project, 1.5m, "batch", Period);

        UsagePayloadHash.Compute(
                UsageLedgerMetricNames.Results,
                Project,
                decimal.Parse(quantity, CultureInfo.InvariantCulture),
                "batch",
                Period)
            .ShouldBe(expected);
    }

    [Fact]
    public void Compute_DiffersForEveryHashedFieldInTurn()
    {
        // Canonical null in JSON: a hash is compared by its text, so it travels as the payload's embedded value.
        var baseline = UsagePayloadHash.Compute(
            UsageLedgerMetricNames.Results, Project, 1.5m, "batch", Period);

        var variants = new[]
        {
            UsagePayloadHash.Compute(UsageLedgerMetricNames.ArtifactGbMonth, Project, 1.5m, "batch", Period),
            UsagePayloadHash.Compute(UsageLedgerMetricNames.Results, null, 1.5m, "batch", Period),
            UsagePayloadHash.Compute(
                UsageLedgerMetricNames.Results, Guid.NewGuid(), 1.5m, "batch", Period),
            UsagePayloadHash.Compute(UsageLedgerMetricNames.Results, Project, 1.6m, "batch", Period),
            UsagePayloadHash.Compute(UsageLedgerMetricNames.Results, Project, 1.5m, "event", Period),
            UsagePayloadHash.Compute(UsageLedgerMetricNames.Results, Project, 1.5m, "batch", "2026-11"),
        };

        foreach (var variant in variants)
        {
            variant.ShouldNotBe(baseline);
        }

        variants.Distinct(StringComparer.Ordinal).Count().ShouldBe(variants.Length);
    }

    /// <summary>
    /// <c>occurred_at</c> is not billable identity and the billing key is the identity itself: two entries differing only
    /// in those two never differ in hash.
    /// </summary>
    [Fact]
    public void Prepare_HashesOnlyBillableFields_NotTheKeyOrTheInstant()
    {
        var first = Entry("batch:1:results", occurredAt: DateTimeOffset.UnixEpoch);
        var second = Entry("batch:2:results", occurredAt: new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero));

        var batch = UsageBatchRules.Prepare([first, second]);

        batch.Rows.Count.ShouldBe(2);
        batch.Rows[0].PayloadHash.ShouldBe(batch.Rows[1].PayloadHash);
        batch.Rows[0].PayloadHash.ShouldStartWith("sha256:");
        batch.Rows[0].PayloadHash.Length.ShouldBe(71);
        batch.ConflictingBillingKeys.ShouldBeEmpty();
    }

    [Fact]
    public void Prepare_RepeatedKeyWithAnotherPayload_IsAConflict()
    {
        var batch = UsageBatchRules.Prepare(
        [
            Entry("batch:1:results", quantity: 1m),
            Entry("batch:1:results", quantity: 2m),
        ]);

        batch.Rows.Count.ShouldBe(1);
        batch.RepeatedBillingKeys.ShouldBe(["batch:1:results"]);
        batch.ConflictingBillingKeys.ShouldBe(["batch:1:results"]);
        batch.ConflictingPayloadHashes["batch:1:results"].ShouldNotBe(batch.Rows[0].PayloadHash);
    }

    [Fact]
    public void Prepare_RepeatedKeyWithTheSamePayload_IsOnlyARepeat()
    {
        var batch = UsageBatchRules.Prepare(
        [
            Entry("batch:1:results", quantity: 1.50m),
            Entry("batch:1:results", quantity: 1.5m),
        ]);

        batch.RepeatedBillingKeys.ShouldBe(["batch:1:results"]);
        batch.ConflictingBillingKeys.ShouldBeEmpty();
        batch.ConflictingPayloadHashes.ShouldBeEmpty();
    }

    private static UsageEntryInput Entry(
        string billingKey,
        decimal quantity = 1.5m,
        DateTimeOffset? occurredAt = null) =>
        new(
            billingKey,
            UsageMetric.Results,
            Project,
            quantity,
            "batch",
            Period,
            occurredAt ?? DateTimeOffset.UnixEpoch);
}
