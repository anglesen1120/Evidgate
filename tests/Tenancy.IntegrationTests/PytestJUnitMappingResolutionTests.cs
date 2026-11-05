using System.Diagnostics;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The identity hint is hostile input and the plan is the only evidence a result may be attributed by. These tests
/// pin what the resolution guarantees: bounded work whatever the attribute holds, the full module before a prefix,
/// no pick between two equally specific matches, the bare-name rule, and the bounded identity echo.
/// </summary>
public sealed class PytestJUnitMappingResolutionTests
{
    private static readonly string FirstKey = new('a', 64);
    private static readonly string SecondKey = new('b', 64);

    [Fact]
    public void FiftyThousandSegments_IsUnresolvedWithoutExpandingThem()
    {
        var plan = PlannedExecutionIndex.Build([new PlannedExecutionHint(FirstKey, "a", "a.py")]);
        var classname = string.Join('.', Enumerable.Repeat("a", 50_000));

        var stopwatch = Stopwatch.StartNew();
        var result = PytestJUnitMapperReplay.Map(Report(classname, "a"), plan.Resolve);
        stopwatch.Stop();

        result.Cases.ShouldBeEmpty();
        result.Discrepancies.ShouldContain(JUnitDiscrepancies.UnmappedCase);

        // The bound is checked before a single per-level string exists, so the work stays linear in the report.
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));

        // Only the depth changed: the same plan resolves the same name under a short classname.
        PytestJUnitMapperReplay.Map(Report("a", "a"), plan.Resolve)
            .Cases.ShouldHaveSingleItem().ExecutionKey.ShouldBe(FirstKey);
    }

    [Fact]
    public void SegmentBound_IsInclusiveAndOneSegmentMoreIsUnresolved()
    {
        var segments = Enumerable.Repeat("a", PlannedExecutionIndex.MaxClassnameSegments).ToArray();
        var plan = PlannedExecutionIndex.Build([
            new PlannedExecutionHint(FirstKey, "t", string.Join('/', segments) + ".py"),
        ]);
        var atBound = string.Join('.', segments);

        PytestJUnitMapperReplay.Map(Report(atBound, "t"), plan.Resolve)
            .Cases.ShouldHaveSingleItem().ExecutionKey.ShouldBe(FirstKey);

        var overBound = PytestJUnitMapperReplay.Map(Report(atBound + ".a", "t"), plan.Resolve);

        overBound.Cases.ShouldBeEmpty();
        overBound.Discrepancies.ShouldContain(JUnitDiscrepancies.UnmappedCase);
    }

    [Fact]
    public void NameBound_IsInclusiveAndOneCharacterMoreIsUnresolved()
    {
        var atBound = new string('n', PlannedExecutionIndex.MaxNameLength);
        var plan = PlannedExecutionIndex.Build([new PlannedExecutionHint(FirstKey, atBound, "tests/long.py")]);

        PytestJUnitMapperReplay.Map(Report("tests.long", atBound), plan.Resolve)
            .Cases.ShouldHaveSingleItem().ExecutionKey.ShouldBe(FirstKey);

        var overBound = PytestJUnitMapperReplay.Map(Report("tests.long", atBound + "n"), plan.Resolve);

        overBound.Cases.ShouldBeEmpty();
        overBound.Discrepancies.ShouldContain(JUnitDiscrepancies.UnmappedCase);
    }

    [Fact]
    public void TwoExecutionsAtTheSameSpecificity_AreUnresolvedRatherThanChosen()
    {
        // A plan that lists one file and case twice cannot say which execution the report means.
        var plan = PlannedExecutionIndex.Build([
            new PlannedExecutionHint(FirstKey, "test_capture", "tests/test_payments.py"),
            new PlannedExecutionHint(SecondKey, "test_capture", "tests/test_payments.py"),
        ]);

        var result = PytestJUnitMapperReplay.Map(Report("tests.test_payments", "test_capture"), plan.Resolve);

        result.Cases.ShouldBeEmpty();
        result.Discrepancies.ShouldContain(JUnitDiscrepancies.UnmappedCase);

        // With only one of the two rows the same hint resolves, so the ambiguity is what refuses it.
        var single = PlannedExecutionIndex.Build([
            new PlannedExecutionHint(FirstKey, "test_capture", "tests/test_payments.py"),
        ]);

        PytestJUnitMapperReplay.Map(Report("tests.test_payments", "test_capture"), single.Resolve)
            .Cases.ShouldHaveSingleItem().ExecutionKey.ShouldBe(FirstKey);
    }

    [Fact]
    public void NodeIdShapeAndFilePathShape_DisagreeingAtTheSameLevel_AreUnresolved()
    {
        var plan = PlannedExecutionIndex.Build([
            new PlannedExecutionHint(FirstKey, "tests.test_payments::test_capture", "elsewhere/other.py"),
            new PlannedExecutionHint(SecondKey, "test_capture", "tests/test_payments.py"),
        ]);

        var result = PytestJUnitMapperReplay.Map(Report("tests.test_payments", "test_capture"), plan.Resolve);

        result.Cases.ShouldBeEmpty();
        result.Discrepancies.ShouldContain(JUnitDiscrepancies.UnmappedCase);

        // Each shape resolves on its own; only their disagreement at one level refuses the hint.
        PlannedExecutionIndex.Build([
            new PlannedExecutionHint(FirstKey, "tests.test_payments::test_capture", "elsewhere/other.py"),
        ]).Resolve("tests.test_payments", "test_capture").ShouldBe(FirstKey);
        PlannedExecutionIndex.Build([
            new PlannedExecutionHint(SecondKey, "test_capture", "tests/test_payments.py"),
        ]).Resolve("tests.test_payments", "test_capture").ShouldBe(SecondKey);
    }

    [Fact]
    public void ClassSuffixedModule_ResolvesThroughTheShorterModulePrefix()
    {
        var plan = PlannedExecutionIndex.Build([
            new PlannedExecutionHint(FirstKey, "test_capture", "tests/test_payments.py"),
        ]);

        var result = PytestJUnitMapperReplay.Map(Report("tests.test_payments.TestPayments", "test_capture"), plan.Resolve);

        result.Cases.ShouldHaveSingleItem().ExecutionKey.ShouldBe(FirstKey);
        result.Discrepancies.ShouldBeEmpty();
    }

    [Fact]
    public void QualifiedClassPathInThePlan_ResolvesBesideTheShorterModulePrefix()
    {
        var plan = PlannedExecutionIndex.Build([
            new PlannedExecutionHint(FirstKey, "TestPayments::test_capture", "nowhere/plan.py"),
        ]);

        var result = PytestJUnitMapperReplay.Map(Report("tests.test_payments.TestPayments", "test_capture"), plan.Resolve);

        result.Cases.ShouldHaveSingleItem().ExecutionKey.ShouldBe(FirstKey);
    }

    [Fact]
    public void VerbatimFilePathClassname_BeatsTheFileItsDottedSplitSpells()
    {
        // The report writes the path where pytest writes a module path. Its extension is not a directory segment, so
        // the planned file it names verbatim wins over the deeper file the split would otherwise reach first.
        var plan = PlannedExecutionIndex.Build([
            new PlannedExecutionHint(FirstKey, "t", "tests/test_payments.py"),
            new PlannedExecutionHint(SecondKey, "t", "tests/test_payments/py.py"),
        ]);

        PytestJUnitMapperReplay.Map(Report("tests/test_payments.py", "t"), plan.Resolve)
            .Cases.ShouldHaveSingleItem().ExecutionKey.ShouldBe(FirstKey);

        // The same plan and the module spelling of the same file still agree, so only the split of a path-shaped
        // classname was at fault.
        plan.Resolve("tests.test_payments", "t").ShouldBe(FirstKey);
    }

    [Fact]
    public void VerbatimFilePathNamingTwoExecutions_IsUnresolved()
    {
        var plan = PlannedExecutionIndex.Build([
            new PlannedExecutionHint(FirstKey, "t", "tests/test_payments.py"),
            new PlannedExecutionHint(SecondKey, "t", "tests/test_payments.py"),
        ]);

        plan.Resolve("tests/test_payments.py", "t").ShouldBeNull();
    }

    [Fact]
    public void BareName_ResolvesWithoutAClassnameAndItsAmbiguityIsUnresolved()
    {
        var unique = PlannedExecutionIndex.Build([new PlannedExecutionHint(FirstKey, "test_capture", "tests/one.py")]);

        PytestJUnitMapperReplay.Map(Report(string.Empty, "test_capture"), unique.Resolve)
            .Cases.ShouldHaveSingleItem().ExecutionKey.ShouldBe(FirstKey);

        var ambiguous = PlannedExecutionIndex.Build([
            new PlannedExecutionHint(FirstKey, "test_capture", "tests/one.py"),
            new PlannedExecutionHint(SecondKey, "test_capture", "tests/two.py"),
        ]);

        var result = PytestJUnitMapperReplay.Map(Report(string.Empty, "test_capture"), ambiguous.Resolve);

        result.Cases.ShouldBeEmpty();
        result.Discrepancies.ShouldContain(JUnitDiscrepancies.UnmappedCase);
    }

    [Fact]
    public void ExplicitForeignClassname_IsUnresolvedEvenWhenTheNameIsUniqueInThePlan()
    {
        var plan = PlannedExecutionIndex.Build([new PlannedExecutionHint(FirstKey, "test_capture", "tests/one.py")]);

        plan.Resolve("unplanned.foreign_module", "test_capture").ShouldBeNull();
        plan.Resolve("tests.one", "test_capture").ShouldBe(FirstKey);
    }

    [Fact]
    public void LongName_ResolvesRawAndEchoesBounded()
    {
        var name = new string('n', 400);
        var plan = PlannedExecutionIndex.Build([new PlannedExecutionHint(FirstKey, name, "tests/long.py")]);

        var mapped = PytestJUnitMapperReplay.Map(Report("tests.long", name), plan.Resolve).Cases.ShouldHaveSingleItem();

        mapped.ExecutionKey.ShouldBe(FirstKey);
        mapped.Normalized.ShouldBe(PytestStatus.Passed);
        mapped.Classname.ShouldBe("tests.long");
        mapped.Name.Length.ShouldBe(PytestJUnitMapper.MaxEchoedIdentityLength);
        mapped.Name.ShouldStartWith(
            new string('n', PytestJUnitMapper.MaxEchoedIdentityLength - PytestJUnitMapper.TruncationMarker.Length));
        mapped.Name.ShouldEndWith(PytestJUnitMapper.TruncationMarker);
    }

    [Fact]
    public void LongClassname_ResolvesRawAndEchoesBounded()
    {
        var segment = new string('m', 300);
        var plan = PlannedExecutionIndex.Build([
            new PlannedExecutionHint(FirstKey, "t", "tests/" + segment + ".py"),
        ]);

        var mapped = PytestJUnitMapperReplay.Map(Report("tests." + segment, "t"), plan.Resolve)
            .Cases.ShouldHaveSingleItem();

        mapped.ExecutionKey.ShouldBe(FirstKey);
        mapped.Name.ShouldBe("t");
        mapped.Classname.Length.ShouldBe(PytestJUnitMapper.MaxEchoedIdentityLength);
        mapped.Classname.ShouldEndWith(PytestJUnitMapper.TruncationMarker);
    }

    [Fact]
    public void EchoNeverSplitsASurrogatePair()
    {
        var cut = PytestJUnitMapper.MaxEchoedIdentityLength - PytestJUnitMapper.TruncationMarker.Length;
        var value = new string('a', cut - 1) + "\U0001F600" + new string('b', 11);

        var echoed = PytestJUnitMapper.BoundedEcho(value);

        // A value at the cap is echoed verbatim: only an over-cap value gains the marker.
        PytestJUnitMapper.BoundedEcho(new string('a', PytestJUnitMapper.MaxEchoedIdentityLength))
            .ShouldBe(new string('a', PytestJUnitMapper.MaxEchoedIdentityLength));

        echoed.ShouldEndWith(PytestJUnitMapper.TruncationMarker);
        echoed.Length.ShouldBe(PytestJUnitMapper.MaxEchoedIdentityLength - 1);

        for (var index = 0; index < echoed.Length; index++)
        {
            if (!char.IsHighSurrogate(echoed[index]))
            {
                char.IsLowSurrogate(echoed[index]).ShouldBeFalse();
                continue;
            }

            char.IsLowSurrogate(echoed[index + 1]).ShouldBeTrue();
            index++;
        }
    }

    [Theory]
    [InlineData("<failure />", "<skipped />", "failed")]
    [InlineData("<skipped />", "<failure />", "failed")]
    [InlineData("<error />", "<failure />", "error")]
    [InlineData("<failure />", "<error />", "error")]
    [InlineData("", "<skipped />", "skipped")]
    [InlineData("<skipped />", "", "skipped")]
    public void RepeatedExecution_TakesTheWorstStatusInEitherDocumentOrder(
        string first,
        string second,
        string worst)
    {
        var report =
            $"<testsuite tests=\"2\"><testcase classname=\"M\" name=\"t\">{first}</testcase>"
            + $"<testcase classname=\"M\" name=\"t\">{second}</testcase></testsuite>";

        var result = PytestJUnitMapperReplay.Map(report);

        var mapped = result.Cases.ShouldHaveSingleItem();
        mapped.Normalized.ShouldBe(worst);
        mapped.Sequence.ShouldBe(1);
        result.Discrepancies.ShouldBe([JUnitDiscrepancies.DuplicateCase]);
    }

    [Fact]
    public void RepeatedExecution_RecordsOneDiscrepancyPerRepeat()
    {
        const string Report =
            """
            <testsuite name="pytest" tests="4">
              <testcase classname="M" name="t" />
              <testcase classname="M" name="t"><skipped /></testcase>
              <testcase classname="M" name="t"><failure /></testcase>
              <testcase classname="M" name="other" />
            </testsuite>
            """;

        var result = PytestJUnitMapperReplay.Map(Report);

        result.Cases.Select(mapped => mapped.ExecutionKey).ShouldBe(["M::t", "M::other"]);
        result.Cases[0].Normalized.ShouldBe(PytestStatus.Failed);
        result.Discrepancies.ShouldBe([JUnitDiscrepancies.DuplicateCase, JUnitDiscrepancies.DuplicateCase]);
    }

    [Fact]
    public void PassingRepeat_DoesNotOverwriteTheKeptStatus()
    {
        const string Report =
            """
            <testsuite name="pytest" tests="2">
              <testcase classname="M" name="t"><error /></testcase>
              <testcase classname="M" name="t" />
            </testsuite>
            """;

        var mapped = PytestJUnitMapperReplay.Map(Report).Cases.ShouldHaveSingleItem();

        mapped.RawStatus.ShouldBe(PytestStatus.Error);
        mapped.Normalized.ShouldBe(PytestStatus.Error);
    }

    private static string Report(string classname, string name) =>
        $"<testsuite tests=\"1\"><testcase classname=\"{classname}\" name=\"{name}\" /></testsuite>";
}
