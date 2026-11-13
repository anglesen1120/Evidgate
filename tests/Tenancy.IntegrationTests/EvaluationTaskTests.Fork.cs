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
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var fork = await SeedAsync(world, fork: true);
        var plain = await SeedAsync(world);

        var forked = await EvaluateAsync(world, fork, EvaluationMode.Live, TestContext.Current.CancellationToken);
        var control = await EvaluateAsync(world, plain, EvaluationMode.Live, TestContext.Current.CancellationToken);

        forked.Decision.ShouldBe("INCONCLUSIVE");
        forked.Completeness.ShouldBe("complete");
        forked.Reasons.ShouldBe(["advisory_only_run"]);
        control.Decision.ShouldBe("PASS");
    }
}
