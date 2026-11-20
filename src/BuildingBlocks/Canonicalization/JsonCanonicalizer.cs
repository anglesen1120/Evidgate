using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Evidgate.BuildingBlocks.Canonicalization;

/// <summary>
/// RFC 8785 (JSON Canonicalization Scheme) serializer over
/// <see cref="System.Text.Json"/>, the exact byte sequence the
/// <c>sha256:&lt;hex&gt;</c> digests of 00 §7 are taken over (manifest, policy,
/// candidate, evidence). Property names are sorted by UTF-16 code unit,
/// strings are escaped per §3.2.2.2, numbers use the ECMAScript shortest
/// round-trip form of §3.2.2.3, and duplicate names, lone surrogates and
/// numbers outside the IEEE 754 double range are rejected — never silently
/// rewritten to <c>null</c> or U+FFFD.
/// </summary>
/// <remarks>
/// The writer is manual rather than <see cref="Utf8JsonWriter"/> because both
/// the escaping and the number rules differ from STJ's defaults, and the value
/// is streamed straight out of a <see cref="Utf8JsonReader"/>: no
/// <see cref="JsonDocument"/> is materialized, so only the object names that
/// must be sorted are allocated (00 §5.7 P-rules). The reader's
/// <see cref="MaxDepth"/> cap keeps untrusted input off the stack (00 §5.7 S5).
/// </remarks>
public static partial class JsonCanonicalizer
{
    /// <summary>Nesting depth cap applied while reading untrusted JSON (00 §5.7 S5).</summary>
    public const int MaxDepth = 64;

    /// <summary>Longest RFC 8785 number: <c>-1.7976931348623157e+308</c> and friends fit in 32 bytes.</summary>
    private const int MaxNumberLength = 32;

    /// <summary>Escaped UTF-8 representation of the character that opens and closes a JSON string.</summary>
    private static ReadOnlySpan<byte> Quote => "\""u8;

    /// <summary>
    /// Encoder for the text entry points: it emits no BOM and throws instead of
    /// substituting U+FFFD, because a .NET string can hold an unpaired surrogate and
    /// RFC 8785 §3.2.2.2 forbids one — replacing it would hash a different document.
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// UTF-8 encoding of JSON text for the three string entry points
    /// (<see cref="Write(string, IBufferWriter{byte})"/>, <see cref="CanonicalizeUtf8(string)"/>
    /// and <see cref="CanonicalHash.Compute(string)"/>). An unpaired surrogate is reported
    /// as <see cref="JsonCanonicalizationException"/> rather than being repaired.
    /// </summary>
    internal static byte[] EncodeStrict(string json)
    {
        try
        {
            return StrictUtf8.GetBytes(json);
        }
        catch (EncoderFallbackException error)
        {
            throw new JsonCanonicalizationException(
                "the JSON text contains an unpaired surrogate (RFC 8785 §3.2.2.2)", error);
        }
    }

    /// <summary>Writes the RFC 8785 canonical form of a UTF-8 JSON document into <paramref name="writer"/>.</summary>
    /// <exception cref="JsonCanonicalizationException">The document cannot be canonicalized.</exception>
    /// <exception cref="JsonException">
    /// The input is not a single well-formed JSON value. The reader runs with
    /// <c>isFinalBlock</c>, so an empty document and data after the root value are its
    /// errors — the canonicalizer never has to guess where a document ends.
    /// </exception>
    public static void Write(ReadOnlySpan<byte> utf8Json, IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            MaxDepth = MaxDepth,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        });

        // The reader validates lazily: the first read positions it on the root value (and
        // rejects an empty document), the read after the value is what makes trailing data
        // its error instead of silently hashing only the root value.
        _ = reader.Read();
        WriteValue(ref reader, utf8Json, writer);
        _ = reader.Read();
    }

    /// <summary>Writes the RFC 8785 canonical form of a JSON text document into <paramref name="writer"/>.</summary>
    /// <exception cref="JsonCanonicalizationException">The document cannot be canonicalized.</exception>
    public static void Write(string json, IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(json);
        Write(EncodeStrict(json), writer);
    }

    /// <summary>Canonical UTF-8 bytes of a JSON document.</summary>
    public static byte[] CanonicalizeUtf8(ReadOnlySpan<byte> utf8Json)
    {
        var writer = new ArrayBufferWriter<byte>(utf8Json.Length + 16);
        Write(utf8Json, writer);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>Canonical UTF-8 bytes of a JSON text document.</summary>
    /// <exception cref="JsonCanonicalizationException">The document cannot be canonicalized.</exception>
    public static byte[] CanonicalizeUtf8(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return CanonicalizeUtf8(EncodeStrict(json));
    }

    /// <summary>Canonical JSON text of a JSON text document.</summary>
    public static string Canonicalize(string json) => Encoding.UTF8.GetString(CanonicalizeUtf8(json));

    /// <summary>Canonical UTF-8 bytes of a parsed <see cref="JsonElement"/>.</summary>
    public static byte[] CanonicalizeUtf8(JsonElement element) => CanonicalizeUtf8(element.GetRawText());

    /// <summary>Canonical UTF-8 bytes of a parsed <see cref="JsonNode"/>.</summary>
    public static byte[] CanonicalizeUtf8(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return CanonicalizeUtf8(node.ToJsonString());
    }

    private static void WriteValue(ref Utf8JsonReader reader, ReadOnlySpan<byte> input, IBufferWriter<byte> writer)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                WriteObject(ref reader, input, writer);
                break;
            case JsonTokenType.StartArray:
                WriteArray(ref reader, input, writer);
                break;
            case JsonTokenType.String:
                WriteString(ref reader, writer);
                break;
            case JsonTokenType.Number:
                WriteNumber(ref reader, writer);
                break;
            case JsonTokenType.True:
                writer.Write("true"u8);
                break;
            case JsonTokenType.False:
                writer.Write("false"u8);
                break;
            case JsonTokenType.Null:
                writer.Write("null"u8);
                break;
            default:
                throw new JsonCanonicalizationException($"unexpected JSON token {reader.TokenType}");
        }
    }

    /// <summary>
    /// RFC 8785 §3.2.3: members are re-emitted in UTF-16 code unit order. The
    /// reader is only moved over each member value, so the object's contents are
    /// read twice but never copied.
    /// </summary>
    private static void WriteObject(ref Utf8JsonReader reader, ReadOnlySpan<byte> input, IBufferWriter<byte> writer)
    {
        var members = new List<Member>();

        while (true)
        {
            if (!reader.Read())
            {
                throw new JsonCanonicalizationException("the JSON object is not terminated");
            }

            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            var name = ReadPropertyName(ref reader);
            if (!reader.Read())
            {
                throw new JsonCanonicalizationException($"the property '{name}' has no value");
            }

            // Documents larger than 2 GiB are outside the ingest size contract (00 §5.7 P8),
            // and the reader's offsets are int-based anyway; `checked` turns the narrowing
            // into a fault rather than a wrapped, silently wrong member range.
            var start = checked((int)reader.TokenStartIndex);
            reader.Skip();
            members.Add(new Member(name, start, checked((int)reader.BytesConsumed)));
        }

        members.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));

        // RFC 8785 §3.2.3 defines no order for two members with the same name, so a
        // duplicate is rejected. The sort puts them next to each other, so one ordinal
        // comparison per member finds them — no per-object HashSet (00 §5.7 P-rules).
        for (var i = 1; i < members.Count; i++)
        {
            if (string.Equals(members[i - 1].Name, members[i].Name, StringComparison.Ordinal))
            {
                throw new JsonCanonicalizationException(
                    $"the JSON object has the duplicate property name '{members[i].Name}'");
            }
        }

        writer.Write("{"u8);
        for (var i = 0; i < members.Count; i++)
        {
            if (i > 0)
            {
                writer.Write(","u8);
            }

            WriteJsonString(members[i].Name, writer);
            writer.Write(":"u8);

            var value = input[members[i].Start..members[i].End];
            var valueReader = new Utf8JsonReader(value);
            if (!valueReader.Read())
            {
                throw new JsonCanonicalizationException($"the property '{members[i].Name}' has no value");
            }

            WriteValue(ref valueReader, value, writer);
        }

        writer.Write("}"u8);
    }

    /// <summary>RFC 8785 §3.2.3: array order is preserved; objects inside are still canonicalized.</summary>
    private static void WriteArray(ref Utf8JsonReader reader, ReadOnlySpan<byte> input, IBufferWriter<byte> writer)
    {
        writer.Write("["u8);

        var first = true;
        while (true)
        {
            if (!reader.Read())
            {
                throw new JsonCanonicalizationException("the JSON array is not terminated");
            }

            if (reader.TokenType == JsonTokenType.EndArray)
            {
                break;
            }

            if (!first)
            {
                writer.Write(","u8);
            }

            first = false;
            WriteValue(ref reader, input, writer);
        }

        writer.Write("]"u8);
    }

    private static void WriteString(ref Utf8JsonReader reader, IBufferWriter<byte> writer)
    {
        writer.Write(Quote);
        if (reader.ValueIsEscaped)
        {
            WriteEscapedUtf8Run(reader.ValueSpan, writer);
        }
        else
        {
            WriteRawUtf8Run(reader.ValueSpan, writer);
        }

        writer.Write(Quote);
    }

    /// <summary>RFC 8785 §3.2.2.3: ECMAScript <c>Number::toString</c> over an IEEE 754 double.</summary>
    private static void WriteNumber(ref Utf8JsonReader reader, IBufferWriter<byte> writer)
    {
        if (!reader.TryGetDouble(out var value) || !double.IsFinite(value))
        {
            throw new JsonCanonicalizationException(
                $"the JSON number '{Encoding.UTF8.GetString(reader.ValueSpan)}' is not a finite IEEE 754 double, so RFC 8785 §3.2.2.3 cannot serialize it");
        }

        Span<byte> buffer = stackalloc byte[MaxNumberLength];
        writer.Write(buffer[..FormatNumber(value, buffer)]);
    }

    /// <summary>A member of a JSON object: its name and the range of its value in the original input.</summary>
    private readonly struct Member(string name, int start, int end)
    {
        public string Name { get; } = name;

        public int Start { get; } = start;

        public int End { get; } = end;
    }
}
