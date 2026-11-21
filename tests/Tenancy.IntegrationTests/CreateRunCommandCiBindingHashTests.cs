using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.2.2-T1.4/AC9 at the command: the request identity the <c>Idempotency-Key</c> is hashed against now also covers the
/// caller's verified CI run reference, and a request without one hashes exactly as it did before this story (so no
/// stored record mass-conflicts on deploy).
/// </summary>
public sealed class CreateRunCommandCiBindingHashTests
{
    private static CreateRunCommand Command(string body, string? binding = null) =>
        new(Guid.CreateVersion7(), Encoding.UTF8.GetBytes(body), "key", binding);

    /// <summary>
    /// The plan §9 AC9 evidence <c>CreateRun_Replay_NoCiRunNoRef_HashUnchanged</c>: a request with neither a CI run
    /// reference nor a <c>ref</c> member hashes exactly as it did before 05.2.2, so no stored idempotency record
    /// mass-conflicts on deploy.
    /// </summary>
    [Fact]
    public void CreateRun_Replay_NoCiRunNoRef_HashUnchanged()
    {
        const string body = """{"manifest_id":"x","components":{"a":"1"}}""";
        var bytes = Encoding.UTF8.GetBytes(body);

        // The literal pre-change algorithm: the RFC 8785 digest of the body, nothing else.
        Command(body).BodyHash.ShouldBe(CanonicalHash.Compute(bytes));

        // Null and empty are both "no reference", so a member or ingest-token caller is unaffected.
        Command(body, string.Empty).BodyHash.ShouldBe(CanonicalHash.Compute(bytes));
        Command(body, null).BodyHash.ShouldBe(CanonicalHash.Compute(bytes));
    }

    [Fact]
    public void BodyHash_WithCiRunReference_FoldsBothHalvesIntoOneCanonicalDigest()
    {
        const string body = """{"manifest_id":"x"}""";
        var bodyHash = CanonicalHash.Compute(Encoding.UTF8.GetBytes(body));

        // Expected value built here by an independent path (one canonical hash of a two-member document), so the test
        // pins the layout rather than restating the implementation.
        var expected = CanonicalHash.Compute(
            $$$"""{"body_hash":{{{JsonSerializer.Serialize(bodyHash)}}},"ci_run_ref":{{{JsonSerializer.Serialize("987654321/42424242/3")}}}}""");

        Command(body, "987654321/42424242/3").BodyHash.ShouldBe(expected);
    }

    [Fact]
    public void BodyHash_DifferentCiRunReferenceOrBody_Differs()
    {
        const string body = """{"manifest_id":"x"}""";

        // A different run attempt of the repository is a different request identity.
        Command(body, "1/7/2").BodyHash.ShouldNotBe(Command(body, "1/7/3").BodyHash);
        Command(body, "1/7/2").BodyHash.ShouldNotBe(Command("""{"manifest_id":"y"}""", "1/7/2").BodyHash);
        Command(body, "1/7/2").BodyHash.ShouldStartWith("sha256:");
    }

    [Fact]
    public void BodyHash_UncanonicalizableBody_StillFallsBackToRawSha256OfItsOwnBytes()
    {
        // The reference is folded onto whatever the body hashes to, so the fallback path keeps its prefix (and a body
        // that cannot be canonicalized still reaches the handler, which answers its normal 400).
        Command("{ not json").BodyHash.ShouldStartWith("raw-sha256:");
        Command("{ not json", "1/7/2").BodyHash.ShouldStartWith("sha256:");
        Command("{ not json", "1/7/2").BodyHash.ShouldNotBe(Command("{ not json", "1/7/3").BodyHash);
    }
}
