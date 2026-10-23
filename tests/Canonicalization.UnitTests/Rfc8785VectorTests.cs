using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Canonicalization;
using Shouldly;
using Xunit;

namespace Canonicalization.UnitTests;

/// <summary>
/// AC5 and 01.3.2-T4.1: <see cref="JsonCanonicalizer"/> against the RFC 8785 reference
/// vectors — the input/output pairs published with cyberphone/json-canonicalization
/// (Apache-2.0; see <c>fixtures/rfc8785/README.md</c>) plus the number, escaping and
/// rejection rules of RFC 8785 §3.2.2/§3.2.3 and Appendix B. The pairs live in
/// <c>fixtures/rfc8785/</c> because <c>packages/cli/src/canonical.test.ts</c> asserts the
/// same six documents, so a regression on either side fails that side's suite.
/// </summary>
public sealed class Rfc8785VectorTests
{
    public static TheoryData<string> ReferenceVectors =>
        new() { "arrays", "french", "structures", "unicode", "values", "weird" };

    /// <summary>Every reference pair must canonicalize to the exact expected bytes, which are themselves canonical.</summary>
    [Theory]
    [MemberData(nameof(ReferenceVectors))]
    public void Reference_Vector_Matches_Byte_For_Byte(string name)
    {
        var input = File.ReadAllBytes(RepoPaths.Fixtures($"rfc8785/input/{name}.json"));
        var expected = File.ReadAllBytes(RepoPaths.Fixtures($"rfc8785/output/{name}.json"));

        JsonCanonicalizer.CanonicalizeUtf8(input).ShouldBe(expected);
        JsonCanonicalizer.CanonicalizeUtf8(expected).ShouldBe(expected);
    }

    /// <summary>
    /// RFC 8785 Appendix B, "ECMAScript-Compatible JSON Number Serialization Samples":
    /// IEEE 754 bits in, JSON representation out. The JSON text is the shortest
    /// round-trip form, so the reader hands the canonicalizer the exact double.
    /// </summary>
    [Theory]
    [InlineData("0000000000000000", "0")] // Zero
    [InlineData("8000000000000000", "0")] // Minus zero
    [InlineData("0000000000000001", "5e-324")] // Min pos number
    [InlineData("8000000000000001", "-5e-324")] // Min neg number
    [InlineData("7fefffffffffffff", "1.7976931348623157e+308")] // Max pos number
    [InlineData("ffefffffffffffff", "-1.7976931348623157e+308")] // Max neg number
    [InlineData("4340000000000000", "9007199254740992")] // Max pos int
    [InlineData("c340000000000000", "-9007199254740992")] // Max neg int
    [InlineData("4430000000000000", "295147905179352830000")] // ~2**68
    [InlineData("44b52d02c7e14af5", "9.999999999999997e+22")]
    [InlineData("44b52d02c7e14af6", "1e+23")]
    [InlineData("44b52d02c7e14af7", "1.0000000000000001e+23")]
    [InlineData("444b1ae4d6e2ef4e", "999999999999999700000")]
    [InlineData("444b1ae4d6e2ef4f", "999999999999999900000")]
    [InlineData("444b1ae4d6e2ef50", "1e+21")]
    [InlineData("3eb0c6f7a0b5ed8c", "9.999999999999997e-7")]
    [InlineData("3eb0c6f7a0b5ed8d", "0.000001")]
    [InlineData("41b3de4355555553", "333333333.3333332")]
    [InlineData("41b3de4355555554", "333333333.33333325")]
    [InlineData("41b3de4355555555", "333333333.3333333")]
    [InlineData("41b3de4355555556", "333333333.3333334")]
    [InlineData("41b3de4355555557", "333333333.33333343")]
    [InlineData("becbf647612f3696", "-0.0000033333333333333333")]
    [InlineData("43143ff3c1cb0959", "1424953923781206.2")] // Round to even
    [InlineData("42f2f2a36ecd5556", "333333333333333.4")]
    [InlineData("3fd5555555555555", "0.3333333333333333")]
    [InlineData("0010000000000000", "2.2250738585072014e-308")] // Min normal
    public void Appendix_B_Number_Sample_Serializes_As_Json_Representation(string ieee754Bits, string expected)
    {
        var value = BitConverter.UInt64BitsToDouble(
            ulong.Parse(ieee754Bits, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        var json = "[" + value.ToString("R", CultureInfo.InvariantCulture) + "]";

        JsonCanonicalizer.Canonicalize(json).ShouldBe("[" + expected + "]");
    }

    /// <summary>RFC 8785 §3.2.2.2: escape only U+0000–U+001F (lowercase <c>\u</c>), plus the predefined set.</summary>
    [Fact]
    public void Escapes_Only_What_Rfc_8785_Requires()
    {
        // U+000B and U+001F are outside the predefined \b \t \n \f \r set; U+007F (DEL)
        // and U+0080 (C1) are above the escaped range and must stay raw. The input also
        // uses \/ , which must come out as a plain /.
        JsonCanonicalizer.Canonicalize("{\"a\":\"\\u000b\\u001f\\u007f\\u0080\\\"\\\\\\/\"}").ShouldBe(
            "{\"a\":\"\\u000b\\u001f\u007f\u0080\\\"\\\\/\"}");
    }

    /// <summary>RFC 8785 §3.2.3: property names sort by UTF-16 code unit, never by locale.</summary>
    [Fact]
    public void Sorts_By_Utf16_Code_Unit_Not_By_Locale()
    {
        var input = """
            {"\u20ac":"Euro Sign","\r":"Carriage Return","\ufb33":"Hebrew Letter Dalet With Dagesh",
             "1":"One","\ud83d\ude00":"Emoji: Grinning Face","\u0080":"Control",
             "\u00f6":"Latin Small Letter O With Diaeresis"}
            """;

        // \r (U+000D) < "1" < U+0080 < ö < € < the U+1F600 pair (0xD83D) < U+FB33: the
        // last two are the opposite of code-point order, and the accented names in
        // french.json prove the ordering ignores the locale.
        JsonCanonicalizer.Canonicalize(input).ShouldBe(
            "{\"\\r\":\"Carriage Return\",\"1\":\"One\",\"\u0080\":\"Control\","
            + "\"\u00f6\":\"Latin Small Letter O With Diaeresis\",\"\u20ac\":\"Euro Sign\","
            + "\"\ud83d\ude00\":\"Emoji: Grinning Face\",\"\ufb33\":\"Hebrew Letter Dalet With Dagesh\"}");
    }

    /// <summary>Every entry point must produce the same bytes, and hashing must not need a JsonDocument.</summary>
    [Fact]
    public void Every_Entry_Point_Produces_The_Same_Bytes()
    {
        const string json = """{"b":2,"a":[1,{"c":true}]}""";
        var expected = """{"a":[1,{"c":true}],"b":2}"""u8.ToArray();

        JsonCanonicalizer.CanonicalizeUtf8(json).ShouldBe(expected);
        JsonCanonicalizer.Canonicalize(json).ShouldBe(Encoding.UTF8.GetString(expected));
        JsonCanonicalizer.CanonicalizeUtf8(Encoding.UTF8.GetBytes(json)).ShouldBe(expected);

        var buffer = new ArrayBufferWriter<byte>();
        JsonCanonicalizer.Write(Encoding.UTF8.GetBytes(json), buffer);
        buffer.WrittenSpan.ToArray().ShouldBe(expected);

        using var document = JsonDocument.Parse(json);
        JsonCanonicalizer.CanonicalizeUtf8(document.RootElement).ShouldBe(expected);
        JsonCanonicalizer.CanonicalizeUtf8(JsonNode.Parse(json)!).ShouldBe(expected);
    }

    /// <summary>RFC 8785 §3.2.2.2/§3.2.2.3: the document is rejected, never silently repaired.</summary>
    [Theory]
    [InlineData("""{"a":1,"a":2}""", "duplicate property name")]
    [InlineData("""{"a":1,"\u0061":2}""", "duplicate property name")] // same name once unescaped
    [InlineData("""{"a":"\ud83d"}""", "lone surrogate")] // high surrogate alone
    [InlineData("""{"a":"\udead"}""", "lone surrogate")] // low surrogate alone
    [InlineData("""{"a":"\ud83dx"}""", "lone surrogate")] // high surrogate + literal
    [InlineData("""{"\ud83d":1}""", "lone surrogate")] // in a property name
    public void Rejects_Documents_That_Cannot_Be_Canonicalized(string json, string message)
    {
        var error = Should.Throw<JsonCanonicalizationException>(() => JsonCanonicalizer.Canonicalize(json));

        error.Message.ShouldContain(message);
    }

    /// <summary>
    /// RFC 8785 §3.2.2.3: NaN and Infinity are not JSON and numbers outside the IEEE 754
    /// double range have no representation, so they are rejected instead of being emitted
    /// as <c>null</c> or <c>Infinity</c>.
    /// </summary>
    [Theory]
    [InlineData("[1e400]")]
    [InlineData("[-1e400]")]
    [InlineData("[1e999]")]
    public void Rejects_Numbers_Outside_The_Double_Range(string json)
    {
        var error = Should.Throw<JsonCanonicalizationException>(() => JsonCanonicalizer.Canonicalize(json));

        error.Message.ShouldContain("IEEE 754");
    }

    /// <summary>
    /// The <see cref="JsonNode"/> entry point never writes a non-finite number as
    /// <c>null</c> or <c>Infinity</c>: System.Text.Json refuses to serialize one, so the
    /// failure happens before the canonicalizer is reached.
    /// </summary>
    [Fact]
    public void Rejects_Non_Finite_Numbers_Through_The_JsonNode_Overload()
    {
        var node = JsonNode.Parse("""{"a":1}""")!.AsObject();
        node["a"] = JsonValue.Create(double.NaN); // accepted here, rejected on serialization

        Should.Throw<ArgumentException>(() => JsonCanonicalizer.CanonicalizeUtf8(node));
    }

    /// <summary>
    /// Malformed or ambiguous input surfaces as the reader's exception: it runs with
    /// <c>isFinalBlock</c>, so an empty document and a second root value are its errors,
    /// not the canonicalizer's (00 §5.7 S5's depth cap is enforced the same way).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("[1,]")]
    [InlineData("nope")]
    [InlineData("""{"a":1} 2""")] // a second root value
    [InlineData("""{"a":1}x""")] // garbage after the root value
    [InlineData("""{"a":1} []""")]
    public void Rejects_Malformed_Input(string json)
    {
        Should.Throw<JsonException>(() => JsonCanonicalizer.Canonicalize(json));
    }

    /// <summary>Invalid UTF-8 in a string token is rejected by the canonicalizer, not replaced with U+FFFD.</summary>
    [Fact]
    public void Rejects_Invalid_Utf8_Bytes()
    {
        var utf8 = new byte[] { (byte)'"', 0xC3, 0x28, (byte)'"' };

        Should.Throw<JsonCanonicalizationException>(() => JsonCanonicalizer.CanonicalizeUtf8(utf8));
    }

    /// <summary>
    /// A .NET string can hold an unpaired surrogate, and the default UTF-8 encoder would
    /// replace it with U+FFFD — a different document with a different digest. Every text
    /// entry point rejects it instead (RFC 8785 §3.2.2.2), while the UTF-8 overloads are
    /// unaffected: no byte sequence encodes a lone surrogate.
    /// </summary>
    [Fact]
    public void Rejects_Unpaired_Surrogates_In_Text_Entry_Points()
    {
        const string json = "{\"a\":\"\uD800\"}";

        Should.Throw<JsonCanonicalizationException>(() => JsonCanonicalizer.Canonicalize(json))
            .Message.ShouldContain("unpaired surrogate");
        Should.Throw<JsonCanonicalizationException>(() => JsonCanonicalizer.CanonicalizeUtf8(json))
            .Message.ShouldContain("unpaired surrogate");
        Should.Throw<JsonCanonicalizationException>(
                () => JsonCanonicalizer.Write(json, new ArrayBufferWriter<byte>()))
            .Message.ShouldContain("unpaired surrogate");
        Should.Throw<JsonCanonicalizationException>(() => CanonicalHash.Compute(json))
            .Message.ShouldContain("unpaired surrogate");

        // A UTF-8 byte sequence cannot hold a lone surrogate: the JSON escape is ASCII, and
        // it is the canonicalizer — not the encoder — that rejects it.
        var escape = Encoding.UTF8.GetBytes("""{"a":"\ud800"}""");
        Should.Throw<JsonCanonicalizationException>(() => CanonicalHash.Compute(escape))
            .Message.ShouldContain("lone surrogate");

        // A well-formed pair is accepted, and both overloads still agree on it.
        CanonicalHash.Compute(Encoding.UTF8.GetBytes("""{"a":"\ud83d\ude00"}"""))
            .ShouldBe(CanonicalHash.Compute("""{"a":"\ud83d\ude00"}"""));
    }

    [Fact]
    public void Rejects_Input_Nested_Deeper_Than_The_Cap()
    {
        var depth = JsonCanonicalizer.MaxDepth + 1;
        var json = new string('[', depth) + new string(']', depth);

        Should.Throw<JsonException>(() => JsonCanonicalizer.Canonicalize(json));
    }
}
