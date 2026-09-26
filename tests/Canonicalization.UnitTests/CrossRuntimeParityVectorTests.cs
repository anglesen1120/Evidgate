using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;
using Shouldly;
using Xunit;

namespace Canonicalization.UnitTests;

/// <summary>
/// AC5 and 01.3.2-T4.2: the adversarial half of the cross-runtime contract. The same
/// table is asserted by <c>packages/cli/src/canonical.test.ts</c> through
/// <c>canonicalize</c> 5.1.0, so a divergence in either runtime fails that runtime's
/// suite. The expected canonical text follows RFC 8785 §3.2.2/§3.2.3 (shortest
/// round-trip numbers, UTF-16 code unit member order, only U+0000–U+001F escaped);
/// every digest was re-derived from the expected bytes with an independent SHA-256
/// implementation (<c>python3 -c "hashlib.sha256(...)"</c>) rather than from this
/// canonicalizer.
/// </summary>
public sealed class CrossRuntimeParityVectorTests
{
    /// <summary>Input JSON text, expected canonical text, expected <c>sha256:&lt;hex&gt;</c> digest.</summary>
    public static TheoryData<string, string, string> ParityVectors =>
        new()
        {
            // Numbers: -0, the fixed/exponential switchovers, subnormals, overflow to
            // zero, integers wider than a double, and literal forms that normalize away.
            { """[-0,0,-0.0,0.0,1.0,1E2,1e+21]""", "[0,0,0,0,1,100,1e+21]", "sha256:1bea963f1afa945ca4f8335ec394c79f38127ed7dc4083e80e3cca5e9c7bd801" },
            { """[0.30000000000000004,0.1,0.2]""", "[0.30000000000000004,0.1,0.2]", "sha256:7716063af38cf70cb04545912c972f2c1ae47dcc11110a3db80744387f2734ca" },
            { """[9007199254740993,1000000000000000128]""", "[9007199254740992,1000000000000000100]", "sha256:394cbca831c10541ba39a7d997927f68632546465cf602ed21d8ec862cb43391" },
            { """[1e-400,-1e-400,5e-324]""", "[0,0,5e-324]", "sha256:346d05c70998e776771fbb847896d9d452b3eb68d42f21cf6dde2ed928297af7" },
            { """[12345678901234567890123456789012345678901234567890]""", "[1.2345678901234567e+49]", "sha256:d9a7c7290b6bcfc4cd372956b8967d73a6263c962d473f6c7e873aa32f245ad9" },
            { """[2.2250738585072011e-308,2.2250738585072014e-308]""", "[2.225073858507201e-308,2.2250738585072014e-308]", "sha256:a0c21e1959079b96ee74847b069bdf895d5153d6752dde4c87802756e50ee3f2" },
            { """[1.7976931348623157e308,1.7976931348623158e308]""", "[1.7976931348623157e+308,1.7976931348623157e+308]", "sha256:5f3591f12a80bc6f4b84bc0accbc6f5e037bd9fb1b2ea2f76328b3d9321545b1" },
            { """[1e21,1e20,1e22,1e-6,1e-7,0.000001,0.0000001]""", "[1e+21,100000000000000000000,1e+22,0.000001,1e-7,0.000001,1e-7]", "sha256:f69dd5f809bc6e321a24d1528ceb5a5967ec1b0e64e2d81ae9345023b05946ce" },

            // Strings: U+2028/U+2029, U+007F and U+009F stay raw; the predefined set and
            // the remaining C0 controls are escaped; \/ and \u0041 normalize away.
            { """["\u2028\u2029\u007f\u009f\ud83d\ude00"]""", "[\"\u2028\u2029\u007f\u009f\ud83d\ude00\"]", "sha256:975ce8644d87705a349d5198324b7cb30fce66baf2d983226af3a3b020defa63" },
            { """["\u0041\/"]""", "[\"A/\"]", "sha256:1d39b7979d7f552f16057c3ac947e0a80a7fc19cd75e905635f03cdb2e19edb0" },
            { """["\b\t\n\f\r\"\\"]""", "[\"\\b\\t\\n\\f\\r\\\"\\\\\"]", "sha256:d4277361ed3d96172703b0b72063e0e59ffb5f63a59c56d42731721618421dd9" },
            { """["\u0000\u000b\u001f"]""", "[\"\\u0000\\u000b\\u001f\"]", "sha256:3f8af4e1c1c61e3cd7e32826517e5b188d32b05035e8f2c85f314957de695732" },

            // Empty containers, and member order: UTF-16 code units, so an astral pair
            // (0xD83D…) sorts before U+FB33 but after "1", and "" sorts first.
            { """{}""", "{}", "sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a" },
            { """[]""", "[]", "sha256:4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945" },
            { """{"":""}""", "{\"\":\"\"}", "sha256:86cfeee6df382a203f626305afbca44d228cac67acd151ccf2db29530c548fea" },
            { """{"a":{}}""", "{\"a\":{}}", "sha256:5d8171b9fc362e385a79ccd9d7992c96bcb4afca51d6278068bd1df49863b3a7" },
            { """{"\ufb33":1,"\ud83d\ude00":2,"\r":3,"1":4,"":5}""", "{\"\":5,\"\\r\":3,\"1\":4,\"\ud83d\ude00\":2,\"\ufb33\":1}", "sha256:718ebb063d8f2770d90a279a7ee1703f1765a3489824e1f76f8ba91841ef7695" },
            { """{"b":[2,{"d":true,"10":null,"1":[]}],"a":1}""", "{\"a\":1,\"b\":[2,{\"1\":[],\"10\":null,\"d\":true}]}", "sha256:555fd3aa5a43ca692a82bacdb049d11c81947130609e419a457ae37f5820911c" },
            { """{"\ue000":1,"\ud800\udc00":2}""", "{\"\ud800\udc00\":2,\"\ue000\":1}", "sha256:9d4cdc71dda603c42f9b21d88d0c2ffc31a76cd1bd461d7359406cf169845f1e" },
        };

    [Theory]
    [MemberData(nameof(ParityVectors))]
    public void Canonical_Form_And_Digest_Match_The_Shared_Vector(string input, string canonical, string digest)
    {
        JsonCanonicalizer.Canonicalize(input).ShouldBe(canonical);
        CanonicalHash.Compute(input).ShouldBe(digest);

        // The canonical bytes are the digest pre-image, and the canonical text is a fixed point.
        CanonicalHash.FromCanonical(Encoding.UTF8.GetBytes(canonical)).ShouldBe(digest);
        CanonicalHash.Compute(canonical).ShouldBe(digest);
        JsonCanonicalizer.CanonicalizeUtf8(Encoding.UTF8.GetBytes(canonical)).ShouldBe(Encoding.UTF8.GetBytes(canonical));
    }

    /// <summary>
    /// The ingest depth cap is exact: 64 nested containers canonicalize, 65 are rejected
    /// before the document is bound (00 §5.7 S5, <c>JsonCanonicalizer.MaxDepth</c>). The
    /// CLI applies the same rule (<c>canonicalize</c> itself has no cap; the wrapper
    /// enforces it), and <c>packages/cli/src/canonical.test.ts</c> asserts the identical
    /// boundary — accept 64, reject 65 — so the two runtimes agree on the verdict.
    /// </summary>
    [Fact]
    public void Depth_Cap_Is_Exact()
    {
        var atCap = new string('[', JsonCanonicalizer.MaxDepth) + new string(']', JsonCanonicalizer.MaxDepth);
        var overCap = "[" + atCap + "]";

        JsonCanonicalizer.Canonicalize(atCap).ShouldBe(atCap);
        CanonicalHash.Compute(atCap).ShouldBe("sha256:b3ff3b51ce17ef2a2a68203329ff0e73738c07df874044149c283b28841a0035");

        var objectsAtCap = string.Concat(Enumerable.Repeat("""{"a":""", JsonCanonicalizer.MaxDepth)) + "1" + new string('}', JsonCanonicalizer.MaxDepth);
        JsonCanonicalizer.Canonicalize(objectsAtCap).ShouldBe(objectsAtCap);

        var overCapError = Should.Throw<JsonException>(() => JsonCanonicalizer.Canonicalize(overCap));
        overCapError.Message.ShouldContain($"maximum configured depth of {JsonCanonicalizer.MaxDepth}");

        // One member deeper, not a wrapped copy: `{` + <64 nested objects> + `}` is not JSON
        // at all and would pass for the wrong reason.
        var objectsOverCap = string.Concat(Enumerable.Repeat("""{"a":""", JsonCanonicalizer.MaxDepth + 1)) + "1" + new string('}', JsonCanonicalizer.MaxDepth + 1);
        var objectsOverCapError = Should.Throw<JsonException>(() => JsonCanonicalizer.Canonicalize(objectsOverCap));
        objectsOverCapError.Message.ShouldContain($"maximum configured depth of {JsonCanonicalizer.MaxDepth}");
    }

    /// <summary>
    /// A UTF-8 byte order mark is not part of the JSON grammar, so a BOM-prefixed manifest
    /// is rejected instead of hashed. The CLI's <c>JSON.parse</c> throws <c>SyntaxError</c>
    /// for the same input, so both sides agree on the verdict (00 §7 hashes a document,
    /// never a byte stream).
    /// </summary>
    [Theory]
    [InlineData("\uFEFF{}")]
    [InlineData("\uFEFF{\"a\":1}")]
    public void Rejects_A_Byte_Order_Mark_Prefix(string json)
    {
        Should.Throw<JsonException>(() => JsonCanonicalizer.Canonicalize(json));
    }
}
