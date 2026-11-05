using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed class PytestJUnitMappingExplorationTests
{
    private static readonly string FirstKey = new('a', 64);
    private static readonly string SecondKey = new('b', 64);

    [Fact]
    public void ExplicitForeignClassname_DoesNotBorrowTheUniquePlannedName()
    {
        var plan = PlannedExecutionIndex.Build([
            new PlannedExecutionHint(FirstKey, "test_capture", "tests/test_payments.py"),
        ]);
        const string Matching = "<testsuite><testcase classname=\"tests.test_payments\" name=\"test_capture\" /></testsuite>";
        const string Foreign = "<testsuite><testcase classname=\"unplanned.foreign_module\" name=\"test_capture\" /></testsuite>";

        PytestJUnitMapperReplay.Map(Matching, plan.Resolve).Cases.ShouldHaveSingleItem().ExecutionKey.ShouldBe(FirstKey);

        var result = PytestJUnitMapperReplay.Map(Foreign, plan.Resolve);

        result.Cases.ShouldBeEmpty();
        result.Discrepancies.ShouldContain(JUnitDiscrepancies.UnmappedCase);
    }

    [Fact]
    public void ExplicitModule_PrefersTheFullFilePathOverItsParentPrefix()
    {
        var plan = PlannedExecutionIndex.Build([
            new PlannedExecutionHint(FirstKey, "test_capture", "tests.py"),
            new PlannedExecutionHint(SecondKey, "test_capture", "tests/test_payments.py"),
        ]);
        const string Report = "<testsuite><testcase classname=\"tests.test_payments\" name=\"test_capture\" /></testsuite>";

        var result = PytestJUnitMapperReplay.Map(Report, plan.Resolve);

        result.Cases.ShouldHaveSingleItem().ExecutionKey.ShouldBe(SecondKey);
    }

    [Fact]
    public void MissingClassname_StillResolvesTheUniquePlannedName()
    {
        var plan = PlannedExecutionIndex.Build([
            new PlannedExecutionHint(FirstKey, "test_capture", "tests/test_payments.py"),
        ]);

        var result = PytestJUnitMapperReplay.Map("<testsuite><testcase name=\"test_capture\" /></testsuite>", plan.Resolve);

        result.Cases.ShouldHaveSingleItem().ExecutionKey.ShouldBe(FirstKey);
        result.Discrepancies.ShouldBeEmpty();
    }

    [Fact]
    public void AmbiguousBareName_EmitsNoEvent()
    {
        var plan = PlannedExecutionIndex.Build([
            new PlannedExecutionHint(FirstKey, "test_capture", "tests/one.py"),
            new PlannedExecutionHint(SecondKey, "test_capture", "tests/two.py"),
        ]);
        PytestJUnitMapperReplay.Map("<testsuite><testcase classname=\"tests.one\" name=\"test_capture\" /></testsuite>", plan.Resolve)
            .Cases.ShouldHaveSingleItem().ExecutionKey.ShouldBe(FirstKey);

        var result = PytestJUnitMapperReplay.Map("<testsuite><testcase name=\"test_capture\" /></testsuite>", plan.Resolve);

        result.Cases.ShouldBeEmpty();
        result.Discrepancies.ShouldContain(JUnitDiscrepancies.UnmappedCase);
    }

    [Theory]
    [InlineData("<failure />", "failure", "failed")]
    [InlineData("<error />", "error", "error")]
    [InlineData("<skipped />", "skipped", "skipped")]
    public void DuplicateIdentity_WithLaterNonPass_PromotesTheWorseStatus(string later, string raw, string normalized)
    {
        var single = PytestJUnitMapperReplay.Map($"<testsuite><testcase classname=\"M\" name=\"t\">{later}</testcase></testsuite>");
        single.Cases.ShouldHaveSingleItem().RawStatus.ShouldBe(raw);
        single.Cases.ShouldHaveSingleItem().Normalized.ShouldBe(normalized);
        var report = $"<testsuite tests=\"2\"><testcase classname=\"M\" name=\"t\" /><testcase classname=\"M\" name=\"t\">{later}</testcase></testsuite>";

        var result = PytestJUnitMapperReplay.Map(report);

        result.Cases.ShouldHaveSingleItem().Normalized.ShouldBe(normalized);
        result.Cases.ShouldHaveSingleItem().Sequence.ShouldBe(1);
        result.Discrepancies.ShouldContain(JUnitDiscrepancies.DuplicateCase);
        result.Declared.Tests.ShouldBe(2);
    }

    [Fact]
    public void ReferencedUnicodeAndControlNames_RoundTripThroughTheRealPlan()
    {
        const string Name = "test_đúng_😀\t\n\r";
        var plan = PlannedExecutionIndex.Build([
            new PlannedExecutionHint(FirstKey, Name, "tests/emoji.py"),
        ]);
        const string Report = "<testsuite><testcase classname=\"tests.emoji\" name=\"test_đúng_😀&#9;&#10;&#13;\" /></testsuite>";

        var result = PytestJUnitMapperReplay.Map(Report, plan.Resolve);

        result.Cases.ShouldHaveSingleItem().Name.ShouldBe(Name);
        result.Cases.ShouldHaveSingleItem().ExecutionKey.ShouldBe(FirstKey);
    }

    [Theory]
    [InlineData("1e309")]
    [InlineData("9223372036854776")]
    [InlineData("9999999999999999999999999999999999999999999999999999999999")]
    public void HugeTime_IsAbsentWithoutChangingTheOutcome(string time)
    {
        var result = PytestJUnitMapperReplay.Map($"<testsuite><testcase classname=\"M\" name=\"t\" time=\"{time}\" /></testsuite>");

        result.Cases.ShouldHaveSingleItem().DurationMs.ShouldBeNull();
        result.Cases.ShouldHaveSingleItem().Normalized.ShouldBe("passed");
    }

    [Fact]
    public void SameReportTwice_ProducesIdenticalMappingIncludingDocumentOrderGaps()
    {
        const string Report = "<testsuite tests=\"3\"><testcase classname=\"M\" name=\"one\" /><testcase classname=\"M\" name=\"ghost\" /><testcase classname=\"M\" name=\"two\"><failure /></testcase></testsuite>";
        static string? Resolve(string classname, string name) => name == "ghost" ? null : classname + "::" + name;

        var first = PytestJUnitMapperReplay.Map(Report, Resolve);
        var second = PytestJUnitMapperReplay.Map(Report, Resolve);

        first.Cases.Select(item => item.Sequence).ShouldBe([1, 3]);
        second.Cases.ShouldBe(first.Cases);
        second.Discrepancies.ShouldBe(first.Discrepancies);
        second.Declared.ShouldBe(first.Declared);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unknown-dialect")]
    [InlineData("pytest-junitxml\n")]
    public async Task UnknownProducerKind_ReturnsAnUnsupportedResolution(string producerKind)
    {
        var resolver = new ReportParserResolver(Substitute.For<ITenantDb>());

        var result = await resolver.ResolveAsync(producerKind, TestContext.Current.CancellationToken);

        result.IsResolved.ShouldBeFalse();
        result.UnsupportedReason.ShouldBe(ReportParserResolver.UnknownProducerKind);
        result.ParserId.ShouldBeNull();
        result.Version.ShouldBeNull();
    }
}
