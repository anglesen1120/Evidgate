using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.Observability;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// A fork run is labelled advisory-only in the run read model, and run creation is counted with the fork label. The
/// bind refusals are pinned beside the release tests (<c>ExplicitBind_ForkRun_422ForkRunNotBindable_NoRows</c>,
/// <c>BindAtRunCreate_ForkIdentity_422NoRun</c>), the evaluator's own rule in <c>AdvisoryOnlyRunTests</c>.
/// </summary>
[Trait("Category", "Docker")]
[Collection(ProcessGlobalRunMetricsSuite.Name)]
public sealed class ForkRunAdvisoryTests : IClassFixture<TenancyHostPool>
{
    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;

    public ForkRunAdvisoryTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) =>
        (_fixture, _hosts) = (fixture, hosts);

    private static string Body(Guid manifestId, string refJson) =>
        $$"""{"manifest_id":"{{manifestId}}","ref":{{refJson}}}""";

    private static async Task<ApiAnswer> GetRunAsync(RunCreationTests.World world, Guid runId) =>
        await world.Client.SendAsync(HttpMethod.Get, $"/v1/runs/{runId:D}", world.Admin);

    [Fact]
    public async Task GetRun_ForkRun_IsLabelledAdvisoryOnly()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        var created = await RunCreationTests.CreateRunAsync(
            world,
            Body(world.ManifestId, """{"kind":"pull_request","name":"7/merge","pr_number":7,"is_fork":true}"""));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        var answer = await GetRunAsync(world, created.Id("run_id"));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("advisory_only").GetBoolean().ShouldBeTrue();
        answer.Body.GetProperty("ref").GetProperty("is_fork").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task GetRun_NonForkAndIdentitylessRuns_AreNotAdvisoryOnly()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        var branch = await RunCreationTests.CreateRunAsync(
            world,
            Body(world.ManifestId, """{"kind":"branch","name":"main","is_fork":false}"""));
        var bare = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        branch.Status.ShouldBe(HttpStatusCode.Created, branch.ErrorMessage);
        bare.Status.ShouldBe(HttpStatusCode.Created, bare.ErrorMessage);

        (await GetRunAsync(world, branch.Id("run_id"))).Body.GetProperty("advisory_only").GetBoolean().ShouldBeFalse();
        (await GetRunAsync(world, bare.Id("run_id"))).Body.GetProperty("advisory_only").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task CreateRun_CountsEachCommittedRunOnceWithItsForkAndUnverifiedLabels()
    {
        var counted = new ConcurrentQueue<(string? Trigger, bool IsFork, bool Unverified, long Value)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, publisher) =>
            {
                if (instrument.Name == EvidgateTelemetry.RunsCreatedMetricName)
                {
                    publisher.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? trigger = null;
            var isFork = false;
            var unverified = false;
            foreach (var tag in tags)
            {
                switch (tag.Key)
                {
                    case "trigger_event":
                        trigger = tag.Value as string;
                        break;
                    case "is_fork":
                        isFork = tag.Value is true;
                        break;
                    case "unverified_ref":
                        unverified = tag.Value is true;
                        break;
                }
            }

            counted.Enqueue((trigger, isFork, unverified, value));
        });
        listener.Start();

        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var forkBody = Body(world.ManifestId, """{"kind":"pull_request","name":"9/merge","pr_number":9,"trigger_event":"pull_request","is_fork":true}""");
        var forkKey = NewKey();

        var fork = await RunCreationTests.CreateRunAsync(world, forkBody, idempotencyKey: forkKey);
        var plain = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        var replay = await RunCreationTests.CreateRunAsync(world, forkBody, idempotencyKey: forkKey);
        var malformed = await RunCreationTests.CreateRunAsync(world, "{\"manifest_id\":");
        var unknownManifest = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(Guid.NewGuid()));

        fork.Status.ShouldBe(HttpStatusCode.Created, fork.ErrorMessage);
        plain.Status.ShouldBe(HttpStatusCode.Created, plain.ErrorMessage);
        replay.Status.ShouldBe(HttpStatusCode.Created, replay.ErrorMessage);
        replay.Id("run_id").ShouldBe(fork.Id("run_id"));
        ((int)malformed.Status).ShouldBeInRange(400, 499);
        ((int)unknownManifest.Status).ShouldBeInRange(400, 499);

        // Exactly the two committed runs: the replay, the malformed body and the unknown manifest add nothing.
        counted.OrderBy(entry => entry.Trigger).ShouldBe(
        [
            ("none", false, false, 1L),
            ("pull_request", true, true, 1L),
        ]);
    }
}

/// <summary>
/// The run-created counter lives on a process-global meter, so a listener hears every host in the process. Tests
/// that assert exact counts join this collection, which xunit runs with nothing else in parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessGlobalRunMetricsSuite
{
    public const string Name = "process-global-run-metrics";
}
