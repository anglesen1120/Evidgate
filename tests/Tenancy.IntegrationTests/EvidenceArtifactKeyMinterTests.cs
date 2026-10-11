using System.Globalization;
using Evidgate.Modules.Evidence.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.1.1-T2.1 / FR-06-1: the object key is minted only by the server and matches the 0028
/// <c>artifact_object_key_shape_check</c> exactly, and the minter refuses any execution key or attempt number that
/// could contribute a client-controlled path segment. Pure unit tests — no container and no database.
/// </summary>
public sealed class EvidenceArtifactKeyMinterTests
{
    /// <summary>A well-formed execution key: sha256 hex (00 §7).</summary>
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void KeyMinter_MintsContractShape()
    {
        var org = Guid.Parse("0192f3a4-0000-7000-8000-000000000001");
        var project = Guid.Parse("0192f3a4-0000-7000-8000-000000000002");
        var run = Guid.Parse("0192f3a4-0000-7000-8000-000000000003");
        var artifact = Guid.Parse("0192f3a4-0000-7000-8000-000000000004");

        var key = ArtifactKeyMinter.Mint(org, project, run, ExecutionKey, 1, artifact);

        // The exact final form the 0028 CHECK accepts: every id lowercase `D`, the attempt number invariant.
        key.ShouldBe(
            "org/0192f3a4-0000-7000-8000-000000000001/project/0192f3a4-0000-7000-8000-000000000002"
            + "/run/0192f3a4-0000-7000-8000-000000000003/attempt/" + ExecutionKey
            + "/1/0192f3a4-0000-7000-8000-000000000004");

        // The CHECK is a concatenation of `uuid::text` (lowercase `D`) and `attempt_no::text`, so the minted key
        // must equal that concatenation for the same values.
        key.ShouldBe(
            string.Create(
                CultureInfo.InvariantCulture,
                $"org/{org:D}/project/{project:D}/run/{run:D}/attempt/{ExecutionKey}/1/{artifact:D}"));
    }

    [Fact]
    public void KeyMinter_MintsHigherAttemptNumber()
    {
        var key = ArtifactKeyMinter.Mint(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), ExecutionKey, 12, Guid.CreateVersion7());

        key.ShouldContain("/attempt/" + ExecutionKey + "/12/");
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("..")]
    [InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef/")]
    public void KeyMinter_RefusesAnExecutionKeyThatIsNotSha256Hex(string executionKey) =>
        Should.Throw<ArgumentException>(() =>
            ArtifactKeyMinter.Mint(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), executionKey, 1, Guid.CreateVersion7()));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void KeyMinter_RefusesAnAttemptNumberBelowOne(int attemptNo) =>
        Should.Throw<ArgumentException>(() =>
            ArtifactKeyMinter.Mint(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), ExecutionKey, attemptNo, Guid.CreateVersion7()));
}
