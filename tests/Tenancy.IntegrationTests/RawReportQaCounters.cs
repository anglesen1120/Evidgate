using System.Text;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRawReportParseTests
{
    [Theory]
    [InlineData("", 0, 0, 0)]
    [InlineData("<failure/>", 1, 0, 0)]
    [InlineData("<error/>", 0, 1, 0)]
    [InlineData("<skipped/>", 0, 0, 1)]
    public async Task RawReport_Acceptance_StatusCounters_StoreEachNormalizedOutcome(
        string element,
        int failures,
        int errors,
        int skipped)
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var xml = $"<testsuite tests=\"1\" failures=\"{failures}\" errors=\"{errors}\" skipped=\"{skipped}\">"
            + $"<testcase name=\"probe\">{element}</testcase></testsuite>";
        var artifact = await SeedArtifactAsync(world, Encoding.UTF8.GetBytes(xml));

        var outcome = await ParseAsync(world, artifact, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        var row = await RawReportAsync(world, artifact);
        Text(row, "outcome").ShouldBe("mapped");
        NullableNumber(row, "declared_tests").ShouldBe(1);
        NullableNumber(row, "declared_failures").ShouldBe(failures);
        NullableNumber(row, "declared_errors").ShouldBe(errors);
        NullableNumber(row, "declared_skipped").ShouldBe(skipped);
        Number(row, "observed_tests").ShouldBe(1);
        Number(row, "observed_failures").ShouldBe(failures);
        Number(row, "observed_errors").ShouldBe(errors);
        Number(row, "observed_skipped").ShouldBe(skipped);
        Discrepancies(row).ShouldBeEmpty();
        (await CountAsync(world, "event", artifact)).ShouldBe(1);
    }

    [Theory]
    [InlineData("this is not XML")]
    [InlineData("\u0000\u0001\u0002")]
    public async Task RawReport_Acceptance_NonXmlBody_StoresMalformedWithNoEvents(string content)
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var body = Encoding.UTF8.GetBytes(content);
        var artifact = await SeedArtifactAsync(world, body);

        var outcome = await ParseAsync(world, artifact, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        var row = await RawReportAsync(world, artifact);
        Text(row, "outcome").ShouldBe("malformed");
        Text(row, "reason").ShouldNotBeNullOrWhiteSpace();
        Number(row, "observed_tests").ShouldBe(0);
        Discrepancies(row).ShouldBeEmpty();
        await AssertNothingWrittenAsync(world, artifact, 1);
        (await _fixture.ScalarAsync<long>(world.Database,
            $"select bytes from public.artifact where id = '{artifact:D}'")).ShouldBe(body.LongLength);
    }

    [Fact]
    public async Task RawReport_Acceptance_EmptySuite_StoresExplicitZeroCountersWithoutEvents()
    {
        var world = await StartAsync();
        await using var _ = world;
        var artifact = await SeedArtifactAsync(world,
            "<testsuite tests=\"0\" failures=\"0\" errors=\"0\" skipped=\"0\"/>"u8.ToArray());

        var outcome = await ParseAsync(world, artifact, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(0);
        var row = await RawReportAsync(world, artifact);
        foreach (var counter in new[] { "tests", "failures", "errors", "skipped" })
        {
            NullableNumber(row, "declared_" + counter).ShouldBe(0);
            Number(row, "observed_" + counter).ShouldBe(0);
        }

        Discrepancies(row).ShouldBeEmpty();
        // Nothing but the report's row and the run's own queued -> running edge: no event, attempt, slot or receipt.
        (await CountAsync(world, "event", artifact)).ShouldBe(0);
        (await CountAsync(world, "case_attempt")).ShouldBe(0);
        (await CountAsync(world, "event_batch")).ShouldBe(0);
        (await CountAsync(world, "producer_slot")).ShouldBe(0);
        (await OutboxRowsAsync(world, RunStateTransitionContract.OutboxTaskType)).ShouldBe(1);
    }
}
