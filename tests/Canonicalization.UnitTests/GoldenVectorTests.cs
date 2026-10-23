using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;
using Shouldly;
using Xunit;

namespace Canonicalization.UnitTests;

/// <summary>
/// AC5 and 01.3.2-T4.2: <c>fixtures/canonical/*.json</c> is the cross-runtime contract
/// with the CLI (<c>packages/cli/src/canonical.test.ts</c>) — one document, one canonical
/// byte sequence, one <c>sha256:&lt;hex&gt;</c> digest in both runtimes (00 §7).
/// </summary>
public sealed class GoldenVectorTests
{
    [Fact]
    public void Golden_Vectors_Canonicalize_And_Hash_Identically()
    {
        var files = Directory
            .GetFiles(RepoPaths.Fixtures("canonical"), "*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        files.Select(Path.GetFileNameWithoutExtension)
            .ShouldBe(["candidate", "evidence", "manifest", "policy"]);

        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file));
            var vector = document.RootElement;
            var input = vector.GetProperty("input");
            var canonical = vector.GetProperty("canonical").GetString()!;
            var digest = vector.GetProperty("sha256").GetString()!;

            JsonCanonicalizer.Canonicalize(input.GetRawText()).ShouldBe(canonical, Path.GetFileName(file));
            CanonicalHash.Compute(input).ShouldBe(digest, Path.GetFileName(file));
            CanonicalHash.FromCanonical(Encoding.UTF8.GetBytes(canonical)).ShouldBe(digest);

            // Byte-exact and idempotent: the canonical form is a fixed point.
            var bytes = Encoding.UTF8.GetBytes(canonical);
            JsonCanonicalizer.CanonicalizeUtf8(bytes).ShouldBe(bytes);
        }
    }

    [Fact]
    public void Digest_Is_A_Prefixed_Lowercase_Sha256_Of_The_Canonical_Bytes()
    {
        // Independent oracle: printf '{"a":1}' | sha256sum
        CanonicalHash.Compute("""{"a":1}""")
            .ShouldBe("sha256:015abd7f5cc57a2dd94b7590f04ad8084273905ee33ec5cebeae62276a97f862");
        CanonicalHash.Prefix.ShouldBe("sha256:");

        CanonicalHash.Compute("""{"a":1}""").Length.ShouldBe("sha256:".Length + 64);
    }

    [Fact]
    public void Digest_Ignores_Property_Order_And_Formatting()
    {
        var compact = CanonicalHash.Compute("""{"b":[2],"a":1}""");
        var formatted = CanonicalHash.Compute("""
            {
              "a": 1,
              "b": [2]
            }
            """);

        compact.ShouldBe(formatted);
    }

    [Fact]
    public void Digest_Of_A_JsonElement_Matches_The_Text_Form()
    {
        using var document = JsonDocument.Parse("""{"b":2,"a":1}""");

        CanonicalHash.Compute(document.RootElement).ShouldBe(CanonicalHash.Compute("""{"b":2,"a":1}"""));
    }
}
