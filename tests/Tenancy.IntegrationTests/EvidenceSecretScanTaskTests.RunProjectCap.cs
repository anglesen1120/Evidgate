// The scan's byte cap and its rejection switch are its run's project's, never the project column on the artifact row:
// nothing ties those two columns together, so a policy of another project must not speak for a run it does not belong to.
using Evidgate.Modules.Evidence.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceSecretScanTaskTests
{
    /// <summary>A cap far above the seeded body, so a policy this large never refuses the read.</summary>
    private const long PermissiveCap = 1024 * 1024;

    /// <summary>
    /// The run's project's switch decides an artifact whose own project column names another project of the same org: the
    /// run's project rejects on a secret while the artifact's own project keeps it, and the row still leaves evidence —
    /// so the project that admits the run is the one asked, not the row's column.
    /// </summary>
    [Fact]
    public async Task Scan_Artifact_Of_Another_Project_Is_Rejected_By_The_Runs_Projects_Switch()
    {
        await using var world = await StartAsync();
        var otherProject = await FixtureSeed.ProjectAsync(_fixture, world.Database, world.Org);
        await SeedKindPolicyAsync(world, rejectOnSecret: true, maxBytes: PermissiveCap);
        await SeedKindPolicyAsync(world, rejectOnSecret: false, projectId: otherProject, maxBytes: PermissiveCap);
        var seeded = await SeedAsync(
            world, ArtifactState.Ready, BodyWith(AwsPrefix + AwsBody), storeBody: true, projectId: otherProject);

        await InvokeAsync(world, seeded.ArtifactId);

        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.PendingDelete);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBe(ArtifactFailureCode.SecretRejected);
        (await ColumnAsync(world, seeded.ArtifactId, "scan_state")).ShouldBe(ArtifactScanState.Flagged);
        (await ColumnAsync(world, seeded.ArtifactId, "sensitivity")).ShouldBe(ArtifactSensitivity.Sensitive);
        (await EventsAsync(world, seeded.ArtifactId)).ShouldBe(
            [(SecretPatternClass.AwsAccessKey, 1, MaskingEventAction.Rejected)]);
    }

    /// <summary>
    /// The control and the other direction of the same rule: the run's project keeps secrets while the artifact's own
    /// project rejects them, and the artifact stays evidence <c>flagged</c> — the row's column's switch is never the one
    /// that decides.
    /// </summary>
    [Fact]
    public async Task Scan_Mismatched_Artifact_Under_A_Keeping_Run_Project_Is_Flagged()
    {
        await using var world = await StartAsync();
        var otherProject = await FixtureSeed.ProjectAsync(_fixture, world.Database, world.Org);
        await SeedKindPolicyAsync(world, rejectOnSecret: false, maxBytes: PermissiveCap);
        await SeedKindPolicyAsync(world, rejectOnSecret: true, projectId: otherProject, maxBytes: PermissiveCap);
        var seeded = await SeedAsync(
            world, ArtifactState.Ready, BodyWith(AwsPrefix + AwsBody), storeBody: true, projectId: otherProject);

        await InvokeAsync(world, seeded.ArtifactId);

        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(
            ArtifactState.Ready, "a keeping run project leaves a flagged artifact in evidence");
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBeNull();
        (await ColumnAsync(world, seeded.ArtifactId, "scan_state")).ShouldBe(ArtifactScanState.Flagged);
        (await EventsAsync(world, seeded.ArtifactId)).ShouldBe(
            [(SecretPatternClass.AwsAccessKey, 1, MaskingEventAction.Flagged)]);
    }

    /// <summary>
    /// The read is bounded by the run's project's cap too: an object over the run project's 8 bytes while the artifact's
    /// own project admits a megabyte throws instead of being scanned truncated, so the row keeps its pending scan and no
    /// verdict is invented.
    /// </summary>
    [Fact]
    public async Task Scan_Artifact_Of_Another_Project_Is_Read_Under_The_Runs_Projects_Cap()
    {
        await using var world = await StartAsync();
        var otherProject = await FixtureSeed.ProjectAsync(_fixture, world.Database, world.Org);
        await SeedKindPolicyAsync(world, rejectOnSecret: false, maxBytes: 8);
        await SeedKindPolicyAsync(world, rejectOnSecret: false, projectId: otherProject, maxBytes: PermissiveCap);
        var seeded = await SeedAsync(
            world, ArtifactState.Ready, BodyWith(AwsPrefix + AwsBody), storeBody: true, projectId: otherProject);

        await Should.ThrowAsync<InvalidOperationException>(() => InvokeAsync(world, seeded.ArtifactId));

        (await ColumnAsync(world, seeded.ArtifactId, "scan_state")).ShouldBe(ArtifactScanState.Pending);
        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Ready);
        (await EventCountAsync(world, seeded.ArtifactId)).ShouldBe(0);
    }

    /// <summary>
    /// An artifact of the run's own project keeps the behaviour it had: the same seeded row with its own project's
    /// rejecting switch leaves evidence, whether or not a second project carries another policy.
    /// </summary>
    [Fact]
    public async Task Scan_Artifact_Of_The_Runs_Own_Project_Rejects_On_That_Projects_Switch()
    {
        await using var world = await StartAsync();
        var otherProject = await FixtureSeed.ProjectAsync(_fixture, world.Database, world.Org);
        await SeedKindPolicyAsync(world, rejectOnSecret: true, maxBytes: PermissiveCap);
        await SeedKindPolicyAsync(world, rejectOnSecret: false, projectId: otherProject, maxBytes: PermissiveCap);
        var seeded = await SeedReadyAsync(world, BodyWith(AwsPrefix + AwsBody));

        await InvokeAsync(world, seeded.ArtifactId);

        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.PendingDelete);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBe(ArtifactFailureCode.SecretRejected);
    }
}
