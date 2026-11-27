using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The completion rule and the aggregate's owner carry, without a database.</summary>
public sealed class EvidenceArtifactOwnershipTests
{
    [Theory]
    [InlineData("token-a", "token-a", true)]
    [InlineData("token-a", "token-b", false)]
    [InlineData("token-a", "TOKEN-A", false)]
    [InlineData("token-a", null, false)]
    [InlineData(null, "token-b", true)]
    [InlineData(null, null, true)]
    public void Permits_OwnerAndCaller_AppliesTheOwnerRuleAndTheLegacyNullRule(string? owner, string? caller, bool expected) =>
        ArtifactOwnership.Permits(owner, caller).ShouldBe(expected);

    [Fact]
    public void Transitions_KeepTheOwnerOfTheRow()
    {
        var artifact = new Artifact(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            1,
            ArtifactKind.Trace,
            "projects/key",
            "application/zip",
            "sha256:" + new string('a', 64),
            ArtifactSensitivity.Normal,
            state: ArtifactState.PendingUpload,
            ownerCredentialId: "token-a");

        artifact.MarkReady(1024).OwnerCredentialId.ShouldBe("token-a");
        artifact.MarkRejected(ArtifactFailureCode.ObjectMissing).OwnerCredentialId.ShouldBe("token-a");
    }
}
