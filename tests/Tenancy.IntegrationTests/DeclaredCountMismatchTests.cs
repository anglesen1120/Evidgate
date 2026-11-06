using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The declared-vs-observed check: which codes a report's declared counters and its mapped cases produce, how they are
/// ordered, and the effective codes a stored row means when the discrepancy is derived from its outcome rather than
/// stored. The mapper still owns <c>count_mismatch</c>/<c>unmapped_case</c>/<c>duplicate_case</c>; the check adds
/// <c>status_mismatch</c> and the canonical order.
/// </summary>
public sealed class DeclaredCountMismatchTests
{
    [Fact]
    public void DeclaredCountMismatch_DeclaredZeroObservedZero_NoDiscrepancy()
    {
        const string Report = "<testsuite name=\"pytest\" tests=\"0\" />";

        var result = PytestJUnitMapperReplay.Map(Report);

        result.Cases.ShouldBeEmpty();
        result.Declared.ShouldBe(new JUnitDeclaredCounters(Tests: 0, Failures: null, Errors: null, Skipped: null));
        Codes(result).ShouldBeEmpty();
    }

    [Fact]
    public void DeclaredCountMismatch_DeclaredZeroObservedThree()
    {
        var report = Suite(
            "tests=\"0\"",
            Case("one"),
            Case("two"),
            Case("three"));

        var result = PytestJUnitMapperReplay.Map(report);

        result.Cases.Count.ShouldBe(3);
        Codes(result).ShouldBe([JUnitDiscrepancies.CountMismatch]);
    }

    [Fact]
    public void DeclaredCountMismatch_DeclaredGreaterThanObserved()
    {
        var report = Suite("tests=\"5\"", Case("one"), Case("two"), Case("three"));

        var result = PytestJUnitMapperReplay.Map(report);

        result.Cases.Count.ShouldBe(3);
        Codes(result).ShouldBe([JUnitDiscrepancies.CountMismatch]);
    }

    [Fact]
    public void DeclaredCountMismatch_DeclaredLessThanObserved()
    {
        var report = Suite("tests=\"2\"", Case("one"), Case("two"), Case("three"));

        var result = PytestJUnitMapperReplay.Map(report);

        result.Cases.Count.ShouldBe(3);
        Codes(result).ShouldBe([JUnitDiscrepancies.CountMismatch]);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("99999999999")]
    public void DeclaredCountMismatch_AbsurdOrNegativeDeclared_IsAbsentNotMismatch(string claimed)
    {
        var result = PytestJUnitMapperReplay.Map(Suite($"tests=\"{claimed}\"", Case("one")));

        result.Declared.Tests.ShouldBeNull();
        Codes(result).ShouldBeEmpty();

        var status = PytestJUnitMapperReplay.Map(
            Suite(
                "tests=\"1\" failures=\"-1\" errors=\"99999999999999999999\"",
                "<testcase classname=\"M\" name=\"fails\"><failure message=\"x\">y</failure></testcase>"));

        status.Declared.Failures.ShouldBeNull();
        status.Declared.Errors.ShouldBeNull();
        Codes(status).ShouldBeEmpty();
    }

    [Fact]
    public void DeclaredCountMismatch_UnmappedCase()
    {
        const string Report =
            """
            <testsuite name="pytest" tests="2" failures="0">
              <testcase classname="M" name="planned"><failure message="boom">x</failure></testcase>
              <testcase classname="M" name="unplanned" />
            </testsuite>
            """;

        var result = PytestJUnitMapperReplay.Map(
            Report,
            (classname, name) => name == "planned" ? classname + "::" + name : null);

        // The unmapped testcase is not an observed case, and the declared failure count is never compared while the
        // observed cases miss part of the document.
        result.Cases.ShouldHaveSingleItem().Normalized.ShouldBe(PytestStatus.Failed);
        Codes(result).ShouldBe([JUnitDiscrepancies.UnmappedCase]);
    }

    [Fact]
    public void DeclaredCountMismatch_AllCasesUnmapped()
    {
        const string Report =
            """
            <testsuite name="pytest" tests="3" failures="1" errors="1" skipped="1">
              <testcase classname="M" name="one" />
              <testcase classname="M" name="two"><failure message="x">y</failure></testcase>
              <testcase classname="M" name="three"><error message="x">y</error></testcase>
            </testsuite>
            """;

        var result = PytestJUnitMapperReplay.Map(Report, (_, _) => null);

        result.Cases.ShouldBeEmpty();
        Codes(result).ShouldBe(
        [
            JUnitDiscrepancies.UnmappedCase,
            JUnitDiscrepancies.UnmappedCase,
            JUnitDiscrepancies.UnmappedCase,
        ]);
    }

    [Theory]
    [InlineData("failures")]
    [InlineData("errors")]
    [InlineData("skipped")]
    public void DeclaredCountMismatch_StatusMismatch_PerStatus(string counter)
    {
        var result = PytestJUnitMapperReplay.Map(
            Suite($"tests=\"1\" {counter}=\"1\"", Case("passes")));

        Codes(result).ShouldBe([JUnitDiscrepancies.StatusMismatch]);
    }

    [Fact]
    public void DeclaredCountMismatch_StatusMismatch_TwoStatusesDiffer_TwoEntries()
    {
        var result = PytestJUnitMapperReplay.Map(
            Suite("tests=\"1\" failures=\"1\" errors=\"1\" skipped=\"0\"", Case("passes")));

        Codes(result).ShouldBe(
        [
            JUnitDiscrepancies.StatusMismatch,
            JUnitDiscrepancies.StatusMismatch,
        ]);
    }

    [Fact]
    public void DeclaredCountMismatch_StatusMatchesAreSilent()
    {
        const string Report =
            """
            <testsuite name="pytest" tests="3" failures="1" errors="1" skipped="1">
              <testcase classname="M" name="f"><failure message="x">y</failure></testcase>
              <testcase classname="M" name="e"><error message="x">y</error></testcase>
              <testcase classname="M" name="s"><skipped message="later" /></testcase>
            </testsuite>
            """;

        var result = PytestJUnitMapperReplay.Map(Report);

        result.Cases.Count.ShouldBe(3);
        Codes(result).ShouldBeEmpty();
    }

    [Fact]
    public void DeclaredCountMismatch_SeveralCodes_StableOrder()
    {
        const string Report =
            """
            <testsuite name="pytest" tests="5">
              <testcase classname="M" name="ghost" />
              <testcase classname="M" name="repeated" />
              <testcase classname="M" name="repeated" />
              <testcase classname="M" name="other" />
            </testsuite>
            """;

        var result = PytestJUnitMapperReplay.Map(
            Report,
            (classname, name) => name == "ghost" ? null : classname + "::" + name);

        // The mapper reports in document order; the check writes the canonical order.
        result.Discrepancies.ShouldBe(
        [
            JUnitDiscrepancies.UnmappedCase,
            JUnitDiscrepancies.DuplicateCase,
            JUnitDiscrepancies.CountMismatch,
        ]);
        Codes(result).ShouldBe(
        [
            JUnitDiscrepancies.CountMismatch,
            JUnitDiscrepancies.UnmappedCase,
            JUnitDiscrepancies.DuplicateCase,
        ]);
    }

    [Fact]
    public void DeclaredCountMismatch_Compare_ScrambledMapperCodesAreCanonical()
    {
        var codes = JUnitDeclaredObservedCheck.Compare(
            new JUnitDeclaredCounters(Tests: 4, Failures: 1, Errors: null, Skipped: null),
            new ObservedCounters(Tests: 4, Failures: 0, Errors: 0, Skipped: 0),
            [
                "future_b",
                JUnitDiscrepancies.DuplicateCase,
                "future_a",
                JUnitDiscrepancies.CountMismatch,
                JUnitDiscrepancies.UnmappedCase,
                "future_b",
            ]);

        codes.ShouldBe(
        [
            JUnitDiscrepancies.CountMismatch,
            JUnitDiscrepancies.UnmappedCase,
            JUnitDiscrepancies.DuplicateCase,
            "future_b",
            "future_b",
            "future_a",
        ]);
    }

    [Fact]
    public void DeclaredCountMismatch_SameBytesSameCodes()
    {
        const string Report =
            """
            <testsuite name="pytest" tests="4" failures="0" errors="1">
              <testcase classname="M" name="one" />
              <testcase classname="M" name="one" />
              <testcase classname="M" name="two"><error message="x">y</error></testcase>
              <testcase classname="M" name="three"><skipped message="later" /></testcase>
            </testsuite>
            """;

        var first = PytestJUnitMapperReplay.Map(Report);
        var second = PytestJUnitMapperReplay.Map(Report);

        second.Declared.ShouldBe(first.Declared);
        second.Discrepancies.ShouldBe(first.Discrepancies);
        Codes(second).ShouldBe(Codes(first));
    }

    [Fact]
    public void DeclaredCountMismatch_EffectiveCodes_MalformedOutcomeIsMalformed()
    {
        JUnitDeclaredObservedCheck
            .EffectiveCodes(RawReportOutcomes.Malformed, "dtd_prohibited", [])
            .ShouldBe([JUnitDiscrepancies.Malformed]);
    }

    [Fact]
    public void DeclaredCountMismatch_EffectiveCodes_MalformedIgnoresStoredCodes()
    {
        JUnitDeclaredObservedCheck
            .EffectiveCodes(
                RawReportOutcomes.Malformed,
                "size_exceeded",
                [JUnitDiscrepancies.CountMismatch, JUnitDiscrepancies.StatusMismatch])
            .ShouldBe([JUnitDiscrepancies.Malformed]);
    }

    [Fact]
    public void DeclaredCountMismatch_EffectiveCodes_MappedWithNoCodesIsEmpty()
    {
        JUnitDeclaredObservedCheck
            .EffectiveCodes(RawReportOutcomes.Mapped, null, [])
            .ShouldBeEmpty();
    }

    [Fact]
    public void DeclaredCountMismatch_EffectiveCodes_PartialKeepsCanonicalOrder()
    {
        JUnitDeclaredObservedCheck
            .EffectiveCodes(
                RawReportOutcomes.Partial,
                null,
                [
                    JUnitDiscrepancies.StatusMismatch,
                    JUnitDiscrepancies.DuplicateCase,
                    JUnitDiscrepancies.UnmappedCase,
                    JUnitDiscrepancies.CountMismatch,
                ])
            .ShouldBe(
            [
                JUnitDiscrepancies.CountMismatch,
                JUnitDiscrepancies.UnmappedCase,
                JUnitDiscrepancies.DuplicateCase,
                JUnitDiscrepancies.StatusMismatch,
            ]);
    }

    [Fact]
    public void DeclaredCountMismatch_EffectiveCodes_UnsupportedIsEmpty()
    {
        JUnitDeclaredObservedCheck
            .EffectiveCodes(RawReportOutcomes.Unsupported, "unknown_producer_kind", [])
            .ShouldBeEmpty();
    }

    /// <summary>The codes a mapped report produces after the check, counted from its own cases.</summary>
    private static IReadOnlyList<string> Codes(JUnitMapResult result) =>
        JUnitDeclaredObservedCheck.Compare(
            result.Declared,
            new ObservedCounters(
                result.Cases.Count,
                result.Cases.Count(mapped => mapped.Normalized == PytestStatus.Failed),
                result.Cases.Count(mapped => mapped.Normalized == PytestStatus.Error),
                result.Cases.Count(mapped => mapped.Normalized == PytestStatus.Skipped)),
            result.Discrepancies);

    private static string Suite(string attributes, params string[] cases) =>
        $"<testsuite name=\"pytest\" {attributes}>{string.Concat(cases)}</testsuite>";

    private static string Case(string name) =>
        $"<testcase classname=\"M\" name=\"{name}\" />";
}
