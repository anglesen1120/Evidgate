// The scan handler over a requirement document: a DOCX keeps its text in a deflated word/document.xml, so the verdict
// only matches the text when the handler reads the package as an archive. Same world, store and policy rows as the cases
// beside it; only the kind and the declared media type differ.
using Evidgate.Modules.Evidence.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceSecretScanTaskTests
{
    private const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scan_DocxWithSecretInDocumentPart_IsFlaggedWhetherOrNotTheProjectRejects(bool rejectOnSecret)
    {
        await using var world = await StartAsync();
        await SeedKindPolicyAsync(
            world, rejectOnSecret, kind: ArtifactKind.RequirementDocument, mediaType: DocxType);
        var seeded = await SeedAsync(
            world,
            ArtifactState.Ready,
            AuthoringExtractionFixtures.Docx("Sign in with " + AwsPrefix + AwsBody),
            storeBody: true,
            kind: ArtifactKind.RequirementDocument,
            mediaType: DocxType);

        await InvokeAsync(world, seeded.ArtifactId);

        (await ColumnAsync(world, seeded.ArtifactId, "scan_state")).ShouldBe(ArtifactScanState.Flagged);
        (await ColumnAsync(world, seeded.ArtifactId, "sensitivity")).ShouldBe(ArtifactSensitivity.Sensitive);
        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(
            rejectOnSecret ? ArtifactState.PendingDelete : ArtifactState.Ready);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBe(
            rejectOnSecret ? ArtifactFailureCode.SecretRejected : null);
        (await EventsAsync(world, seeded.ArtifactId)).ShouldBe(
            [(SecretPatternClass.AwsAccessKey, 1, rejectOnSecret ? MaskingEventAction.Rejected : MaskingEventAction.Flagged)]);
    }

    [Fact]
    public async Task Scan_CleanDocx_UnderARejectingPolicy_StaysReadyAndClean()
    {
        await using var world = await StartAsync();
        await SeedKindPolicyAsync(
            world, rejectOnSecret: true, kind: ArtifactKind.RequirementDocument, mediaType: DocxType);
        var seeded = await SeedAsync(
            world,
            ArtifactState.Ready,
            AuthoringExtractionFixtures.Docx("Check the sign-in screen"),
            storeBody: true,
            kind: ArtifactKind.RequirementDocument,
            mediaType: DocxType);

        await InvokeAsync(world, seeded.ArtifactId);

        (await ColumnAsync(world, seeded.ArtifactId, "scan_state")).ShouldBe(ArtifactScanState.Clean);
        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Ready);
        (await EventCountAsync(world, seeded.ArtifactId)).ShouldBe(0);
    }
}
