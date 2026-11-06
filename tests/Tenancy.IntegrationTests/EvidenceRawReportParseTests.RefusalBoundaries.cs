using System.Text;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRawReportParseTests
{
    [Theory]
    [InlineData("<results><testcase name=\"probe\" /></results>")]
    [InlineData("\uFEFF<results><testcase name=\"probe\" /></results>")]
    [InlineData("<j:results xmlns:j=\"urn:junit\"><j:testcase name=\"probe\" /></j:results>")]
    [InlineData("<results><testsuites><testsuite><testcase name=\"probe\" /></testsuite></testsuites></results>")]
    [InlineData("<")]
    [InlineData("x")]
    [InlineData("<testsuite><testcase name=\"probe\"/><testcase name=\"next\"><failure>truncated")]
    [InlineData("<!DOCTYPE testsuite [<!ENTITY x SYSTEM \"file:///unreadable-qa-sentinel\">]><testsuite><testcase name=\"probe\">&x;</testcase></testsuite>")]
    public async Task Refused_Boundary_Document_Persists_Zero_Events_And_One_Trigger(string xml)
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var body = Encoding.UTF8.GetBytes(xml);
        var artifactId = await SeedArtifactAsync(world, body);
        var storage = new CountingStorage(
            world.Host.Services.GetRequiredService<IObjectStorage>(),
            ObjectKeyFor(world, artifactId));
        var runner = new EmulatedChildRunner(world.Host.Services.GetRequiredService<IParsing>());

        var outcome = await ExecuteAsync(world, storage, runner, artifactId);

        await AssertRefusedRowAsync(
            world,
            artifactId,
            outcome,
            xml.StartsWith("<!DOCTYPE", StringComparison.Ordinal)
                ? ParsingFailureReason.DtdProhibited
                : ParsingFailureReason.Malformed);
        await AssertOneRefusalTriggerAsync(world, artifactId);
        storage.ObjectReads.ShouldBe(1);
        storage.OtherCalls.ShouldBe(0);
        var retained = await world.Host.Services.GetRequiredService<IObjectStorage>()
            .GetAsync(ObjectKeyFor(world, artifactId), body.LongLength, Token);
        retained.ShouldBe(body);
    }

    [Theory]
    [InlineData("<testsuite><testcase name=\"probe\" /></testsuite>")]
    [InlineData("\uFEFF<testsuite><testcase name=\"probe\" /></testsuite>")]
    [InlineData("<j:testsuite xmlns:j=\"urn:junit\"><j:testcase name=\"probe\" /></j:testsuite>")]
    public async Task Suite_Root_Control_Persists_The_Planned_Case(string xml)
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, Encoding.UTF8.GetBytes(xml));

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(1);
        Text(await RawReportAsync(world, artifactId), "outcome").ShouldBe("mapped");
        (await CountAsync(world, "event", artifactId)).ShouldBe(1);
        (await CountAsync(world, "case_attempt")).ShouldBe(1);
    }

    [Fact]
    public async Task Foreign_Root_Replay_Preserves_One_Refusal_And_One_Trigger()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(
            world,
            "<results><testcase name=\"probe\" /></results>"u8.ToArray());
        var storage = new CountingStorage(
            world.Host.Services.GetRequiredService<IObjectStorage>(),
            ObjectKeyFor(world, artifactId));
        var runner = new EmulatedChildRunner(world.Host.Services.GetRequiredService<IParsing>());

        var first = await ExecuteAsync(world, storage, runner, artifactId);
        var replay = await ExecuteAsync(world, storage, runner, artifactId);

        first.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        replay.ShouldBe(first);
        await AssertRefusedRowAsync(world, artifactId, replay, ParsingFailureReason.Malformed);
        await AssertOneRefusalTriggerAsync(world, artifactId);
        storage.ObjectReads.ShouldBe(2, "each attempt reads once, never again to write its refusal");
        storage.OtherCalls.ShouldBe(0);
    }
}
