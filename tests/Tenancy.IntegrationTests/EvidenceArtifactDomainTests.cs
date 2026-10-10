using Evidgate.Modules.Evidence.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.1.1-T1.2 / 00 §5.5 C6: the Evidence domain types carry the same closed lists as the migration's CHECK
/// constraints, so a value the database would refuse is refused before any statement is built. Pure unit tests —
/// no container and no database.
/// </summary>
public sealed class EvidenceArtifactDomainTests
{
    /// <summary>One artifact with every closed-list column overridable.</summary>
    private static Artifact ArtifactWith(
        string kind = ArtifactKind.Trace,
        string sensitivity = ArtifactSensitivity.Normal,
        string? state = null,
        string? scanState = null) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "execution-key",
            1,
            kind,
            "org/o/project/p/run/r/attempt/e/1/a",
            "application/zip",
            "sha256:abc",
            sensitivity,
            state: state,
            scanState: scanState);

    [Fact]
    public void ArtifactKind_All_IsTheEightValueContractList() =>
        ArtifactKind.All.ShouldBe(
            ["trace", "video", "screenshot", "raw_report", "dom_snapshot", "a11y_snapshot", "patch", "ui_hierarchy"]);

    [Theory]
    [InlineData("bogus")]
    [InlineData("")]
    [InlineData("TRACE")]
    public void Artifact_WithKindOutsideTheList_Throws(string kind) =>
        Should.Throw<ArgumentOutOfRangeException>(() => ArtifactWith(kind: kind));

    [Fact]
    public void Artifact_WithSensitivityOutsideTheList_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => ArtifactWith(sensitivity: "bogus"));

    [Theory]
    [InlineData("bogus", null)]
    [InlineData(null, "bogus")]
    public void Artifact_WithStateOrScanStateOutsideTheList_Throws(string? state, string? scanState) =>
        Should.Throw<ArgumentOutOfRangeException>(() => ArtifactWith(state: state, scanState: scanState));

    [Fact]
    public void Artifact_WithoutStateOrScanState_LeavesThemNullForTheDatabaseDefault()
    {
        var artifact = ArtifactWith();

        artifact.State.ShouldBeNull();
        artifact.ScanState.ShouldBeNull();
        artifact.AttemptId.ShouldBeNull();
    }

    [Fact]
    public void ArtifactKindPolicy_WithKindOrSensitivityOutsideTheList_Throws()
    {
        static ArtifactKindPolicy Policy(string kind, string sensitivity) =>
            new(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                kind,
                1024,
                ["application/zip"],
                sensitivity);

        Should.Throw<ArgumentOutOfRangeException>(() => Policy("bogus", ArtifactSensitivity.Normal));
        Should.Throw<ArgumentOutOfRangeException>(() => Policy(ArtifactKind.Trace, "bogus"));
    }
}
