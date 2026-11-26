using System.Text;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.2.2-T1.4/AC9 at the command: the request identity the <c>Idempotency-Key</c> is hashed against is the RFC 8785
/// digest of the body alone, so a retry replays whichever credential of the project sends it.
/// </summary>
public sealed class CreateRunCommandHashTests
{
    private static CreateRunCommand Command(string body) =>
        new(Guid.CreateVersion7(), Encoding.UTF8.GetBytes(body), "key");

    [Fact]
    public void BodyHash_Body_IsThePlainCanonicalDigest()
    {
        const string body = """{"manifest_id":"x","components":{"a":"1"}}""";

        Command(body).BodyHash.ShouldBe(CanonicalHash.Compute(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void BodyHash_ReorderedMembersAndWhitespace_SameDigest_DifferentBody_Differs()
    {
        // Positive control for the replay rule beside the conflict rule: only the content of the body matters.
        Command("""{"manifest_id":"x","ref":{"kind":"branch","name":"main"}}""").BodyHash
            .ShouldBe(Command("""{ "ref": {"name":"main","kind":"branch"}, "manifest_id": "x" }""").BodyHash);
        Command("""{"manifest_id":"x","ref":{"kind":"branch","name":"main"}}""").BodyHash
            .ShouldNotBe(Command("""{"manifest_id":"x","ref":{"kind":"branch","name":"release"}}""").BodyHash);
        Command("""{"manifest_id":"x"}""").BodyHash.ShouldStartWith("sha256:");
    }

    [Fact]
    public void BodyHash_UncanonicalizableBody_FallsBackToRawSha256OfItsOwnBytes()
    {
        // A body that cannot be canonicalized still reaches the handler, which answers its normal 400.
        Command("{ not json").BodyHash.ShouldStartWith("raw-sha256:");
        Command("{ not json").BodyHash.ShouldNotBe(Command("{ not json again").BodyHash);
    }
}
