// The declared-vs-observed check as a committed row: what the parse stores when the document's own counters disagree
// with its cases, and when an empty suite carries nothing to compare. The row is read back through the owner
// connection, the way a reviewer sees it, so the assertion covers the stored array and the counters beside it.
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRawReportParseTests
{
    /// <summary>An empty suite that declares nothing: a mapped row with zero events and no discrepancy.</summary>
    private static readonly byte[] EmptySuiteBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites>
          <testsuite name="pytest" tests="0" failures="0" errors="0" skipped="0"></testsuite>
        </testsuites>
        """u8.ToArray();

    /// <summary>The planned case fails while the suite declares no failure, so only the status of the cases disagrees.</summary>
    private static readonly byte[] StatusOnlyBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites>
          <testsuite name="pytest" tests="1" failures="0">
            <testcase name="probe" time="0.012"><failure message="boom"/></testcase>
          </testsuite>
        </testsuites>
        """u8.ToArray();

    /// <summary>Three codes at once: a declared total the document misses, two unmapped cases and one repeat.</summary>
    private static readonly byte[] SeveralCodesBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites>
          <testsuite name="pytest" tests="5">
            <testcase name="ghost" time="0.001"/>
            <testcase name="probe" time="0.012"/>
            <testcase name="probe" time="0.013"/>
            <testcase classname="tests.test_payments" name="other" time="0.002"/>
          </testsuite>
        </testsuites>
        """u8.ToArray();

    [Fact]
    public async Task DeclaredCountMismatch_EmptySuite_StoresMappedRowWithNoDiscrepancy()
    {
        var world = await StartAsync();
        await using var _ = world;
        var artifactId = await SeedArtifactAsync(world, EmptySuiteBody);

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(0);
        (await RawReportCountAsync(world, artifactId)).ShouldBe(1);

        var row = await RawReportAsync(world, artifactId);
        Text(row, "outcome").ShouldBe("mapped");
        NullableNumber(row, "declared_tests").ShouldBe(0);
        Number(row, "observed_tests").ShouldBe(0);
        Discrepancies(row).ShouldBeEmpty();
    }

    [Fact]
    public async Task DeclaredCountMismatch_StatusMismatch_IsStoredOnce()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, StatusOnlyBody);

        await ParseAsync(world, artifactId, ProducerKind);

        var row = await RawReportAsync(world, artifactId);
        Text(row, "outcome").ShouldBe("partial");
        NullableNumber(row, "declared_tests").ShouldBe(1);
        NullableNumber(row, "declared_failures").ShouldBe(0);
        Number(row, "observed_tests").ShouldBe(1);
        Number(row, "observed_failures").ShouldBe(1);
        Discrepancies(row).ShouldBe([(JUnitDiscrepancies.StatusMismatch, 1)]);
    }

    [Fact]
    public async Task DeclaredCountMismatch_SeveralCodes_AreStoredInCanonicalOrder()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, SeveralCodesBody);

        await ParseAsync(world, artifactId, ProducerKind);

        var row = await RawReportAsync(world, artifactId);
        Text(row, "outcome").ShouldBe("partial");
        Number(row, "observed_tests").ShouldBe(1);
        Discrepancies(row).ShouldBe(
        [
            (JUnitDiscrepancies.CountMismatch, 1),
            (JUnitDiscrepancies.UnmappedCase, 2),
            (JUnitDiscrepancies.DuplicateCase, 1),
        ]);
    }
}
