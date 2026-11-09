// The byte cap the parse read obeys is its run's project's, never the project column on the artifact row: nothing ties
// those two columns together, so a policy of another project must not speak for a run it does not belong to.
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceParseTaskTests
{
    /// <summary>A cap far above the seeded report, so a policy this large never refuses the read.</summary>
    private const long PermissiveCap = 1024 * 1024;

    /// <summary>
    /// The run's project's cap binds an artifact whose own project column names another project of the same org: the
    /// object is over the run project's 8 bytes while the artifact's own project admits a megabyte, and the read is still
    /// refused <c>too_large</c> before any child starts.
    /// </summary>
    [Fact]
    public async Task Parse_Artifact_Of_Another_Project_Is_Rejected_By_The_Runs_Projects_Cap()
    {
        await using var world = await StartAsync();
        var otherProject = await FixtureSeed.ProjectAsync(_fixture, world.Database, world.Org);
        await SeedKindPolicyAsync(world, maxBytes: 8);
        await SeedKindPolicyAsync(world, PermissiveCap, projectId: otherProject);
        var seeded = await SeedAsync(world, ArtifactState.Ready, failureCode: null, projectId: otherProject);
        var runner = new StubParseRunner();

        await InvokeAsync(world, seeded.ArtifactId, runner);

        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Rejected);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBe(ArtifactFailureCode.TooLarge);
        runner.Calls.ShouldBe(0, "the run's project's cap refuses the object before a child is started");
    }

    /// <summary>
    /// The control of the case above: the same mismatched artifact, with a permissive cap on both projects, is read and
    /// handed to the child, so the mismatch alone never refuses the read.
    /// </summary>
    [Fact]
    public async Task Parse_Mismatched_Artifact_Under_A_Permissive_Run_Project_Is_Parsed()
    {
        await using var world = await StartAsync();
        var otherProject = await FixtureSeed.ProjectAsync(_fixture, world.Database, world.Org);
        await SeedKindPolicyAsync(world, PermissiveCap);
        await SeedKindPolicyAsync(world, PermissiveCap, projectId: otherProject);
        var seeded = await SeedAsync(world, ArtifactState.Ready, failureCode: null, projectId: otherProject);
        var runner = new StubParseRunner
        {
            Result = new ParseRunSucceeded(
                [Record("""{"t":"xml","e":"end"}""")],
                ChildExitCode: 0,
                OutputBytes: 32,
                OutputRecords: 1),
        };

        await InvokeAsync(world, seeded.ArtifactId, runner);

        runner.Inputs.ShouldHaveSingleItem().ShouldBe(ReportBody);
        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Ready);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBeNull();
    }

    /// <summary>
    /// The other direction of the same rule: the run's project admits a megabyte while the artifact's own project
    /// declares 8 bytes, and the report between them is read and handed to the child — the row's project cap never
    /// bounds a run it does not belong to.
    /// </summary>
    [Fact]
    public async Task Parse_Mismatched_Artifact_Under_A_Restrictive_Artifact_Cap_Is_Parsed()
    {
        await using var world = await StartAsync();
        var otherProject = await FixtureSeed.ProjectAsync(_fixture, world.Database, world.Org);
        await SeedKindPolicyAsync(world, PermissiveCap);
        await SeedKindPolicyAsync(world, maxBytes: 8, projectId: otherProject);
        var seeded = await SeedAsync(world, ArtifactState.Ready, failureCode: null, projectId: otherProject);
        var runner = new StubParseRunner
        {
            Result = new ParseRunSucceeded(
                [Record("""{"t":"xml","e":"end"}""")],
                ChildExitCode: 0,
                OutputBytes: 32,
                OutputRecords: 1),
        };

        await InvokeAsync(world, seeded.ArtifactId, runner);

        runner.Calls.ShouldBe(1, "the run project's permissive cap admits the report, so a child starts");
        runner.Inputs.ShouldHaveSingleItem().ShouldBe(ReportBody);
        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Ready);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBeNull();
    }

    /// <summary>
    /// An artifact of the run's own project keeps the behaviour it had: its project's cap is the run's, so the same
    /// 8-byte row with a permissive policy on a second project beside it is still refused <c>too_large</c>.
    /// </summary>
    [Fact]
    public async Task Parse_Artifact_Of_The_Runs_Own_Project_Is_Rejected_By_That_Projects_Cap()
    {
        await using var world = await StartAsync();
        var otherProject = await FixtureSeed.ProjectAsync(_fixture, world.Database, world.Org);
        await SeedKindPolicyAsync(world, maxBytes: 8);
        await SeedKindPolicyAsync(world, PermissiveCap, projectId: otherProject);
        var seeded = await SeedReadyAsync(world);
        var runner = new StubParseRunner();

        await InvokeAsync(world, seeded.ArtifactId, runner);

        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Rejected);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBe(ArtifactFailureCode.TooLarge);
        runner.Calls.ShouldBe(0);
    }
}
