using Evidgate.Modules.Policy.Application.Evaluation;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvaluationTaskTests
{
    /// <summary>
    /// A fork run's evaluation goes through the real plan and input readers: complete, clean evidence is stored as
    /// INCONCLUSIVE with the advisory reason, while the same evidence of a non-fork run is PASS.
    /// </summary>
    [Fact]
    public async Task Evaluate_ForkRunWithCleanEvidence_StoresInconclusiveAdvisoryOnly()
    {
        // Each run gets its own world: the seed writes a fixed policy revision number per project.
        await using var forkWorld = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var fork = await SeedAsync(forkWorld, fork: true);
        var forked = await EvaluateAsync(forkWorld, fork, EvaluationMode.Live, TestContext.Current.CancellationToken);

        await using var plainWorld = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var plain = await SeedAsync(plainWorld);
        var control = await EvaluateAsync(plainWorld, plain, EvaluationMode.Live, TestContext.Current.CancellationToken);

        forked.Decision.ShouldBe("INCONCLUSIVE");
        forked.Completeness.ShouldBe("complete");
        forked.Reasons.ShouldBe(["advisory_only_run"]);
        control.Decision.ShouldBe("PASS");
    }
}
