using Evidgate.Modules.Evidence.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed class ArtifactScanTransitionTests
{
    [Theory]
    [InlineData(ArtifactSensitivity.Normal)]
    [InlineData(ArtifactSensitivity.Sensitive)]
    public void MarkScanned_Clean_PreservesSensitivityAndCoordinates(string sensitivity)
    {
        var before = Create(sensitivity: sensitivity);
        var after = before.MarkScanned(ArtifactScanState.Clean);
        after.Sensitivity.ShouldBe(sensitivity);
        after.ScanState.ShouldBe(ArtifactScanState.Clean);
        after.State.ShouldBe(ArtifactState.Ready);
        after.Id.ShouldBe(before.Id);
        after.OrgId.ShouldBe(before.OrgId);
        after.ProjectId.ShouldBe(before.ProjectId);
        after.RunId.ShouldBe(before.RunId);
        after.AttemptId.ShouldBe(before.AttemptId);
        after.ObjectKey.ShouldBe(before.ObjectKey);
        after.Checksum.ShouldBe(before.Checksum);
        after.Bytes.ShouldBe(before.Bytes);
        after.Version.ShouldBe(before.Version);
        before.ScanState.ShouldBe(ArtifactScanState.Pending);
    }

    [Fact]
    public void MarkScanned_Flagged_RaisesSensitivityAndRejectsSecondScan()
    {
        var after = Create().MarkScanned(ArtifactScanState.Flagged);
        after.Sensitivity.ShouldBe(ArtifactSensitivity.Sensitive);
        Should.Throw<InvalidOperationException>(() => after.MarkScanned(ArtifactScanState.Clean));
    }

    [Fact]
    public void MarkScanned_PendingOrUnknownTarget_Refuses()
    {
        var artifact = Create();
        Should.Throw<InvalidOperationException>(() => artifact.MarkScanned(ArtifactScanState.Pending));
        Should.Throw<ArgumentOutOfRangeException>(() => artifact.MarkScanned("unknown"));
        artifact.MarkScanned(ArtifactScanState.Clean).ScanState.ShouldBe(ArtifactScanState.Clean);
    }

    [Theory]
    [InlineData(ArtifactState.PendingUpload)]
    [InlineData(ArtifactState.Uploaded)]
    [InlineData(ArtifactState.Rejected)]
    [InlineData(ArtifactState.PendingDelete)]
    [InlineData(ArtifactState.Deleted)]
    public void MarkPendingDelete_NotReady_RefusesIllegalTransition(string state)
    {
        Should.Throw<InvalidOperationException>(() => Create(state: state).MarkPendingDelete(ArtifactFailureCode.SecretRejected));
        Create().MarkPendingDelete(ArtifactFailureCode.SecretRejected).State.ShouldBe(ArtifactState.PendingDelete);
    }

    [Fact]
    public void MarkPendingDelete_FailureCodeLengthBoundary_ValidatesWithoutLosingScan()
    {
        var flagged = Create().MarkScanned(ArtifactScanState.Flagged);
        flagged.MarkPendingDelete(new string('x', 128)).FailureCode.ShouldBe(new string('x', 128));
        Should.Throw<ArgumentOutOfRangeException>(() => flagged.MarkPendingDelete(new string('x', 129)));
        Should.Throw<ArgumentException>(() => flagged.MarkPendingDelete(" "));
        var rejected = flagged.MarkPendingDelete(ArtifactFailureCode.SecretRejected);
        rejected.ScanState.ShouldBe(ArtifactScanState.Flagged);
        rejected.Sensitivity.ShouldBe(ArtifactSensitivity.Sensitive);
        rejected.State.ShouldBe(ArtifactState.PendingDelete);
        rejected.FailureCode.ShouldBe(ArtifactFailureCode.SecretRejected);
    }

    private static Artifact Create(string state = ArtifactState.Ready, string sensitivity = ArtifactSensitivity.Normal) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            new string('a', 64), 1, ArtifactKind.RawReport, "fixture/object", "application/xml",
            "sha256:" + new string('b', 64), sensitivity, attemptId: Guid.CreateVersion7(),
            bytes: 128, scanState: ArtifactScanState.Pending, state: state, version: 4);
}
