using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The pytest dialect's mapping: one case per resolvable testcase, the producer's status preserved verbatim
/// beside its normalised value, document-order sequences, and a report that never silently invents a case.
/// </summary>
public sealed class PytestJUnitMapperTests
{
    private const string WellFormedReport =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites>
          <testsuite name="pytest" errors="0" failures="1" skipped="1" tests="3" time="0.081">
            <testcase classname="tests.test_payments" name="test_capture_succeeds" time="0.010" />
            <testcase classname="tests.test_payments" name="test_capture_declined" time="0.021">
              <failure message="AssertionError: expected DECLINED, got CAPTURED">assert status == "DECLINED"</failure>
            </testcase>
            <testcase classname="tests.test_payments" name="test_refund_after_capture" time="0.000">
              <skipped message="requires the sandbox gateway" type="pytest.skip" />
            </testcase>
          </testsuite>
        </testsuites>
        """;

    [Fact]
    public void WellFormed_MapsAll()
    {
        var result = PytestJUnitMapperReplay.Map(WellFormedReport);

        result.Cases.Count.ShouldBe(3);
        result.Discrepancies.ShouldBeEmpty();

        result.Cases.Select(mapped => mapped.Sequence).ShouldBe([1, 2, 3]);
        result.Cases.Select(mapped => mapped.ExecutionKey).ShouldBe(
        [
            "tests.test_payments::test_capture_succeeds",
            "tests.test_payments::test_capture_declined",
            "tests.test_payments::test_refund_after_capture",
        ]);
        result.Cases.Select(mapped => mapped.RawStatus).ShouldBe(["passed", "failure", "skipped"]);
        result.Cases.Select(mapped => mapped.Normalized).ShouldBe(["passed", "failed", "skipped"]);
        result.Cases.Select(mapped => mapped.DurationMs).ShouldBe([10L, 21L, 0L]);
        result.Cases.ShouldAllBe(mapped => mapped.Classname == "tests.test_payments");

        result.Declared.ShouldBe(new JUnitDeclaredCounters(Tests: 3, Failures: 1, Errors: 0, Skipped: 1));
    }

    [Fact]
    public void ParserIdentity_IsThePytestDialectAndVersion()
    {
        var mapper = new PytestJUnitMapper(new ParsingService(PytestJUnitMapperReplay.MaxCases));

        mapper.ParserId.ShouldBe("junit-pytest");
        mapper.Version.ShouldBe("1.0.0");
    }

    [Fact]
    public void UnmappedCase_NoEvent_Discrepancy()
    {
        const string Report =
            """
            <testsuite name="pytest" tests="2">
              <testcase classname="tests.test_a" name="test_planned" time="0.100" />
              <testcase classname="tests.test_a" name="test_unplanned" time="0.200" />
            </testsuite>
            """;

        var result = PytestJUnitMapperReplay.Map(
            Report,
            (classname, name) => name == "test_planned" ? classname + "::" + name : null);

        var mapped = result.Cases.ShouldHaveSingleItem();
        mapped.ExecutionKey.ShouldBe("tests.test_a::test_planned");
        mapped.Sequence.ShouldBe(1);
        result.Discrepancies.ShouldBe([JUnitDiscrepancies.UnmappedCase]);
    }

    [Fact]
    public void UnmappedCase_EmptyResolvedKey_IsRefusedRatherThanStored()
    {
        const string Report =
            """
            <testsuite name="pytest" tests="1">
              <testcase classname="tests.test_a" name="test_blank" time="0.100" />
            </testsuite>
            """;

        var result = PytestJUnitMapperReplay.Map(Report, (_, _) => "   ");

        result.Cases.ShouldBeEmpty();
        result.Discrepancies.ShouldBe([JUnitDiscrepancies.UnmappedCase]);
    }

    [Fact]
    public void StatusMatrix_PreservesRawWordAndNormalisesEachOutcome()
    {
        const string Report =
            """
            <testsuite name="pytest" tests="4">
              <testcase classname="M" name="passes" time="0.001" />
              <testcase classname="M" name="fails" time="0.002"><failure message="boom">x</failure></testcase>
              <testcase classname="M" name="errors" time="0.003"><error message="kaboom">y</error></testcase>
              <testcase classname="M" name="skips" time="0.004"><skipped message="not today" type="pytest.skip" /></testcase>
            </testsuite>
            """;

        var result = PytestJUnitMapperReplay.Map(Report);

        result.Cases.Select(mapped => (mapped.RawStatus, mapped.Normalized)).ShouldBe(
        [
            ("passed", "passed"),
            ("failure", "failed"),
            ("error", "error"),
            ("skipped", "skipped"),
        ]);
    }

    [Fact]
    public void StatusMatrix_MoreSevereOutcomeWinsDeterministically()
    {
        const string Report =
            """
            <testsuite name="pytest" tests="2">
              <testcase classname="M" name="both_failure_and_error">
                <failure message="assertion">x</failure>
                <error message="teardown">y</error>
              </testcase>
              <testcase classname="M" name="skip_and_failure">
                <skipped message="not today" />
                <failure message="assertion">x</failure>
              </testcase>
            </testsuite>
            """;

        var result = PytestJUnitMapperReplay.Map(Report);

        result.Cases.Select(mapped => (mapped.RawStatus, mapped.Normalized)).ShouldBe(
        [
            ("error", "error"),
            ("failure", "failed"),
        ]);
    }

    [Fact]
    public void StatusMatrix_OutcomeTextNeverCountsAsASiblingStatus()
    {
        const string Report =
            """
            <testsuite name="pytest" tests="1">
              <testcase classname="M" name="wraps" time="0.010">
                <failure message="wrapped"><error>this is failure text, not a status</error></failure>
              </testcase>
            </testsuite>
            """;

        var result = PytestJUnitMapperReplay.Map(Report);

        result.Cases.ShouldHaveSingleItem().RawStatus.ShouldBe("failure");
    }

    [Fact]
    public void DuplicateCase_LaterTestcaseSkippedWithDiscrepancyAndGap()
    {
        const string Report =
            """
            <testsuite name="pytest" tests="3">
              <testcase classname="M" name="test_retried" time="0.100" />
              <testcase classname="M" name="test_retried" time="0.200"><failure message="flaked">x</failure></testcase>
              <testcase classname="M" name="test_other" time="0.300" />
            </testsuite>
            """;

        var result = PytestJUnitMapperReplay.Map(Report);

        result.Cases.Select(mapped => (mapped.ExecutionKey, mapped.RawStatus, mapped.Sequence)).ShouldBe(
        [
            ("M::test_retried", "passed", 1),
            ("M::test_other", "passed", 3),
        ]);
        result.Discrepancies.ShouldBe([JUnitDiscrepancies.DuplicateCase]);
    }

    [Fact]
    public void MissingIdentityAttributes_ArePassedRawAsEmptyStrings()
    {
        const string Report =
            """
            <testsuite name="pytest" tests="1">
              <testcase time="0.010" />
            </testsuite>
            """;

        string? seenClassname = null;
        string? seenName = null;

        var result = PytestJUnitMapperReplay.Map(
            Report,
            (classname, name) =>
            {
                seenClassname = classname;
                seenName = name;
                return "M::test_anonymous";
            });

        seenClassname.ShouldBe(string.Empty);
        seenName.ShouldBe(string.Empty);
        result.Cases.ShouldHaveSingleItem().DurationMs.ShouldBe(10L);
    }

    [Fact]
    public void SingleTestsuiteRoot_ReadsItsOwnCounters()
    {
        const string Report =
            """
            <testsuite name="pytest" tests="2" failures="0" errors="0" skipped="1">
              <testcase classname="M" name="one" />
              <testcase classname="M" name="two"><skipped /></testcase>
            </testsuite>
            """;

        var result = PytestJUnitMapperReplay.Map(Report);

        result.Declared.ShouldBe(new JUnitDeclaredCounters(Tests: 2, Failures: 0, Errors: 0, Skipped: 1));
        result.Discrepancies.ShouldBeEmpty();
    }

    [Fact]
    public void TestsSuitesRoot_WithoutCounters_SumsItsDirectSuites()
    {
        const string Report =
            """
            <testsuites>
              <testsuite name="shard-a" tests="2" failures="1" errors="0" skipped="0">
                <testcase classname="A" name="one" />
                <testcase classname="A" name="two"><failure message="x">y</failure></testcase>
              </testsuite>
              <testsuite name="shard-b" tests="1" failures="0" errors="1" skipped="0">
                <testcase classname="B" name="three"><error message="x">y</error></testcase>
              </testsuite>
            </testsuites>
            """;

        var result = PytestJUnitMapperReplay.Map(Report);

        result.Declared.ShouldBe(new JUnitDeclaredCounters(Tests: 3, Failures: 1, Errors: 1, Skipped: 0));
        result.Discrepancies.ShouldBeEmpty();
        result.Cases.Select(mapped => mapped.Sequence).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void TestsSuitesRoot_OwnCountersWinOverItsSuites()
    {
        const string Report =
            """
            <testsuites tests="9" failures="2" errors="3" skipped="4">
              <testsuite name="shard" tests="1" failures="0" errors="0" skipped="0">
                <testcase classname="A" name="one" />
              </testsuite>
            </testsuites>
            """;

        var result = PytestJUnitMapperReplay.Map(Report);

        result.Declared.ShouldBe(new JUnitDeclaredCounters(Tests: 9, Failures: 2, Errors: 3, Skipped: 4));
        result.Discrepancies.ShouldBe([JUnitDiscrepancies.CountMismatch]);
    }

    [Fact]
    public void DeclaredTestsDiffersFromObserved_RecordsCountMismatch()
    {
        const string Report =
            """
            <testsuite name="pytest" tests="4">
              <testcase classname="M" name="one" />
              <testcase classname="M" name="two" />
              <testcase classname="M" name="three" />
            </testsuite>
            """;

        var result = PytestJUnitMapperReplay.Map(Report);

        result.Cases.Count.ShouldBe(3);
        result.Discrepancies.ShouldBe([JUnitDiscrepancies.CountMismatch]);
    }

    [Fact]
    public void DeclaredCounters_UnusableValuesAreAbsentNotZero()
    {
        const string Report =
            """
            <testsuite name="pytest" tests="abc" failures="-1" errors="99999999999999999999">
              <testcase classname="M" name="one" />
            </testsuite>
            """;

        var result = PytestJUnitMapperReplay.Map(Report);

        result.Declared.ShouldBe(new JUnitDeclaredCounters(Tests: null, Failures: null, Errors: null, Skipped: null));
        result.Discrepancies.ShouldBeEmpty();
    }

    [Fact]
    public void DocumentWithoutCounters_ReportsNoDeclaredNumbers()
    {
        const string Report =
            """
            <testsuites>
              <testsuite name="shard">
                <testcase classname="M" name="one" />
              </testsuite>
            </testsuites>
            """;

        var result = PytestJUnitMapperReplay.Map(Report);

        result.Declared.ShouldBe(new JUnitDeclaredCounters(Tests: null, Failures: null, Errors: null, Skipped: null));
    }

    [Fact]
    public void CasesAreInDocumentOrder_NotResolverOrder()
    {
        const string Report =
            """
            <testsuite name="pytest" tests="3">
              <testcase classname="M" name="zulu" />
              <testcase classname="M" name="alpha" />
              <testcase classname="M" name="mike" />
            </testsuite>
            """;

        var result = PytestJUnitMapperReplay.Map(Report);

        result.Cases.Select(mapped => mapped.Name).ShouldBe(["zulu", "alpha", "mike"]);
    }

    [Fact]
    public void TwoMappedTestcasesSharingAKey_SecondIsNeverStored()
    {
        const string Report =
            """
            <testsuite name="pytest" tests="2">
              <testcase classname="M" name="one" />
              <testcase classname="M" name="two" />
            </testsuite>
            """;

        var result = PytestJUnitMapperReplay.Map(Report, (_, _) => "shared-key");

        result.Cases.ShouldHaveSingleItem().Sequence.ShouldBe(1);
        result.Discrepancies.ShouldBe([JUnitDiscrepancies.DuplicateCase]);
    }
}
