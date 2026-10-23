using System.Buffers;
using System.Globalization;
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
public static class JsonCanonicalizer
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

    /// <summary>
    /// Writes the shortest round-trip decimal representation of <paramref name="value"/>
    /// using the ECMAScript rules of RFC 8785 §3.2.2.3 ("Note 2" included) and
    /// returns its length. .NET's <c>"R"</c> format supplies the same shortest
    /// digit sequence the spec's algorithm requires; only the fixed/exponential
    /// layout rules are re-implemented here.
    /// </summary>
    private static int FormatNumber(double value, Span<byte> destination)
    {
        if (value == 0)
        {
            // RFC 8785 §3.2.2.3: both 0 and -0 serialize as 0.
            destination[0] = (byte)'0';
            return 1;
        }

        var negative = value < 0;
        var magnitude = negative ? -value : value;

        Span<char> text = stackalloc char[MaxNumberLength];
        if (!magnitude.TryFormat(text, out var written, "R", CultureInfo.InvariantCulture))
        {
            throw new JsonCanonicalizationException("the JSON number could not be serialized");
        }

        Span<byte> digits = stackalloc byte[MaxNumberLength];
        var count = SplitDigits(text[..written], digits, out var exponent);

        var length = 0;
        if (negative)
        {
            destination[length++] = (byte)'-';
        }

        if (count <= exponent && exponent <= 21)
        {
            digits[..count].CopyTo(destination[length..]);
            length += count;
            for (var i = count; i < exponent; i++)
            {
                destination[length++] = (byte)'0';
            }
        }
        else if (exponent is > 0 and <= 21)
        {
            digits[..exponent].CopyTo(destination[length..]);
            length += exponent;
            destination[length++] = (byte)'.';
            digits[exponent..count].CopyTo(destination[length..]);
            length += count - exponent;
        }
        else if (exponent is > -6 and <= 0)
        {
            destination[length++] = (byte)'0';
            destination[length++] = (byte)'.';
            for (var i = 0; i < -exponent; i++)
            {
                destination[length++] = (byte)'0';
            }

            digits[..count].CopyTo(destination[length..]);
            length += count;
        }
        else
        {
            destination[length++] = digits[0];
            if (count > 1)
            {
                destination[length++] = (byte)'.';
                digits[1..count].CopyTo(destination[length..]);
                length += count - 1;
            }

            destination[length++] = (byte)'e';
            var power = exponent - 1;
            if (power < 0)
            {
                destination[length++] = (byte)'-';
                power = -power;
            }
            else
            {
                destination[length++] = (byte)'+';
            }

            Span<byte> exponentDigits = stackalloc byte[4];
            var exponentLength = 0;
            do
            {
                exponentDigits[exponentLength++] = (byte)('0' + (power % 10));
                power /= 10;
            }
            while (power > 0);

            for (var i = exponentLength - 1; i >= 0; i--)
            {
                destination[length++] = exponentDigits[i];
            }
        }

        return length;
    }

    /// <summary>
    /// Turns the shortest round-trip decimal text of a positive double into its
    /// significant ASCII digits (no leading or trailing zero) plus the decimal exponent
    /// <c>n</c> of RFC 8785 §3.2.2.3, where the value is <c>0.&lt;digits&gt; × 10^n</c>.
    /// </summary>
    private static int SplitDigits(ReadOnlySpan<char> text, Span<byte> digits, out int exponent)
    {
        var exponentIndex = text.IndexOfAny('E', 'e');
        var mantissa = exponentIndex < 0 ? text : text[..exponentIndex];
        exponent = exponentIndex < 0
            ? 0
            : int.Parse(text[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        var pointIndex = mantissa.IndexOf('.');
        if (pointIndex >= 0)
        {
            exponent -= mantissa.Length - pointIndex - 1;
        }

        var count = 0;
        foreach (var character in mantissa)
        {
            if (character != '.')
            {
                digits[count++] = (byte)character;
            }
        }

        var first = 0;
        while (first < count - 1 && digits[first] == (byte)'0')
        {
            first++;
        }

        var last = count;
        while (last - 1 > first && digits[last - 1] == (byte)'0')
        {
            last--;
            exponent++;
        }

        count = last - first;
        if (first > 0)
        {
            digits.Slice(first, count).CopyTo(digits);
        }

        exponent += count;
        return count;
    }

    /// <summary>Reads a JSON string token (property name) into an unescaped .NET string.</summary>
    private static string ReadPropertyName(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.PropertyName)
        {
            throw new JsonCanonicalizationException($"expected a property name but found {reader.TokenType}");
        }

        var raw = reader.ValueSpan;
        var characters = new char[raw.Length];
        var written = reader.ValueIsEscaped
            ? UnescapeToChars(raw, characters)
            : DecodeUtf8ToChars(raw, characters);

        return new string(characters, 0, written);
    }

    private static void WriteJsonString(string value, IBufferWriter<byte> writer)
    {
        writer.Write(Quote);
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                throw new JsonCanonicalizationException("the JSON string contains a lone surrogate (RFC 8785 §3.2.2.2)");
            }

            WriteRune(rune, writer);
            remaining = remaining[consumed..];
        }

        writer.Write(Quote);
    }

    /// <summary>Writes the unescaped UTF-8 content of a JSON string token, escaping it per §3.2.2.2.</summary>
    private static void WriteRawUtf8Run(ReadOnlySpan<byte> raw, IBufferWriter<byte> writer)
    {
        while (!raw.IsEmpty)
        {
            var status = Rune.DecodeFromUtf8(raw, out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                throw new JsonCanonicalizationException("the JSON string is not valid UTF-8");
            }

            WriteRune(rune, writer);
            raw = raw[consumed..];
        }
    }

    /// <summary>
    /// Writes the content of an escaped JSON string token. Unescaping is done here
    /// rather than by <see cref="Utf8JsonReader.GetString"/> so that a lone
    /// surrogate escape is rejected instead of turning into U+FFFD.
    /// </summary>
    private static void WriteEscapedUtf8Run(ReadOnlySpan<byte> raw, IBufferWriter<byte> writer)
    {
        var index = 0;
        while (index < raw.Length)
        {
            if (raw[index] != (byte)'\\')
            {
                var start = index;
                while (index < raw.Length && raw[index] != (byte)'\\')
                {
                    index++;
                }

                WriteRawUtf8Run(raw[start..index], writer);
                continue;
            }

            index++;
            if (index >= raw.Length)
            {
                throw new JsonCanonicalizationException("the JSON string ends in an incomplete escape");
            }

            var escape = raw[index++];
            switch (escape)
            {
                case (byte)'"':
                    WriteRune(new Rune('"'), writer);
                    break;
                case (byte)'\\':
                    WriteRune(new Rune('\\'), writer);
                    break;
                case (byte)'/':
                    WriteRune(new Rune('/'), writer);
                    break;
                case (byte)'b':
                    WriteRune(new Rune('\b'), writer);
                    break;
                case (byte)'f':
                    WriteRune(new Rune('\f'), writer);
                    break;
                case (byte)'n':
                    WriteRune(new Rune('\n'), writer);
                    break;
                case (byte)'r':
                    WriteRune(new Rune('\r'), writer);
                    break;
                case (byte)'t':
                    WriteRune(new Rune('\t'), writer);
                    break;
                case (byte)'u':
                    WriteRune(ReadUnicodeEscape(raw, ref index), writer);
                    break;
                default:
                    throw new JsonCanonicalizationException($"the JSON string has the invalid escape '\\{(char)escape}'");
            }
        }
    }

    /// <summary>
    /// Reads a <c>\uXXXX</c> escape (already past the <c>u</c>), combining a
    /// surrogate pair and rejecting a lone surrogate (RFC 8785 §3.2.2.2).
    /// </summary>
    private static Rune ReadUnicodeEscape(ReadOnlySpan<byte> raw, ref int index)
    {
        var high = (char)ReadHex4(raw, ref index);
        if (char.IsHighSurrogate(high))
        {
            if (index + 1 >= raw.Length || raw[index] != (byte)'\\' || raw[index + 1] != (byte)'u')
            {
                throw new JsonCanonicalizationException("the JSON string contains a lone surrogate (RFC 8785 §3.2.2.2)");
            }

            index += 2;
            var low = (char)ReadHex4(raw, ref index);
            if (!char.IsLowSurrogate(low))
            {
                throw new JsonCanonicalizationException("the JSON string contains a lone surrogate (RFC 8785 §3.2.2.2)");
            }

            return new Rune(high, low);
        }

        if (char.IsLowSurrogate(high))
        {
            throw new JsonCanonicalizationException("the JSON string contains a lone surrogate (RFC 8785 §3.2.2.2)");
        }

        return new Rune(high);
    }

    private static int ReadHex4(ReadOnlySpan<byte> raw, ref int index)
    {
        if (index + 4 > raw.Length)
        {
            throw new JsonCanonicalizationException("the JSON string ends in an incomplete \\u escape");
        }

        var value = 0;
        for (var i = 0; i < 4; i++)
        {
            var digit = HexValue(raw[index + i]);
            if (digit < 0)
            {
                throw new JsonCanonicalizationException("the JSON string has an invalid \\u escape");
            }

            value = (value << 4) | digit;
        }

        index += 4;
        return value;
    }

    private static int HexValue(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - '0',
        >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
        _ => -1,
    };

    /// <summary>RFC 8785 §3.2.2.2: escape only what the scheme requires, in lowercase for <c>\uXXXX</c>.</summary>
    private static void WriteRune(Rune rune, IBufferWriter<byte> writer)
    {
        switch (rune.Value)
        {
            case '"':
                writer.Write("\\\""u8);
                break;
            case '\\':
                writer.Write("\\\\"u8);
                break;
            case '\b':
                writer.Write("\\b"u8);
                break;
            case '\t':
                writer.Write("\\t"u8);
                break;
            case '\n':
                writer.Write("\\n"u8);
                break;
            case '\f':
                writer.Write("\\f"u8);
                break;
            case '\r':
                writer.Write("\\r"u8);
                break;
            default:
                if (rune.Value < 0x20)
                {
                    Span<byte> escape = stackalloc byte[6];
                    escape[0] = (byte)'\\';
                    escape[1] = (byte)'u';
                    escape[2] = HexDigit((rune.Value >> 12) & 0xF);
                    escape[3] = HexDigit((rune.Value >> 8) & 0xF);
                    escape[4] = HexDigit((rune.Value >> 4) & 0xF);
                    escape[5] = HexDigit(rune.Value & 0xF);
                    writer.Write(escape);
                }
                else
                {
                    Span<byte> utf8 = stackalloc byte[4];
                    writer.Write(utf8[..rune.EncodeToUtf8(utf8)]);
                }

                break;
        }
    }

    private static byte HexDigit(int value) => (byte)"0123456789abcdef"[value];

    /// <summary>Unescapes a JSON string token (escaped form) into UTF-16 code units.</summary>
    private static int UnescapeToChars(ReadOnlySpan<byte> raw, Span<char> destination)
    {
        var written = 0;
        var index = 0;
        while (index < raw.Length)
        {
            if (raw[index] != (byte)'\\')
            {
                var start = index;
                while (index < raw.Length && raw[index] != (byte)'\\')
                {
                    index++;
                }

                written += DecodeUtf8ToChars(raw[start..index], destination[written..]);
                continue;
            }

            index++;
            if (index >= raw.Length)
            {
                throw new JsonCanonicalizationException("the JSON string ends in an incomplete escape");
            }

            var escape = raw[index++];
            if (escape == (byte)'u')
            {
                var rune = ReadUnicodeEscape(raw, ref index);
                written += rune.EncodeToUtf16(destination[written..]);
                continue;
            }

            destination[written++] = escape switch
            {
                (byte)'"' => '"',
                (byte)'\\' => '\\',
                (byte)'/' => '/',
                (byte)'b' => '\b',
                (byte)'f' => '\f',
                (byte)'n' => '\n',
                (byte)'r' => '\r',
                (byte)'t' => '\t',
                _ => throw new JsonCanonicalizationException($"the JSON string has the invalid escape '\\{(char)escape}'"),
            };
        }

        return written;
    }

    /// <summary>Decodes valid UTF-8 into UTF-16 code units, rejecting malformed input.</summary>
    private static int DecodeUtf8ToChars(ReadOnlySpan<byte> raw, Span<char> destination)
    {
        var written = 0;
        while (!raw.IsEmpty)
        {
            var status = Rune.DecodeFromUtf8(raw, out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                throw new JsonCanonicalizationException("the JSON string is not valid UTF-8");
            }

            written += rune.EncodeToUtf16(destination[written..]);
            raw = raw[consumed..];
        }

        return written;
    }

    /// <summary>A member of a JSON object: its name and the range of its value in the original input.</summary>
    private readonly struct Member(string name, int start, int end)
    {
        public string Name { get; } = name;

        public int Start { get; } = start;

        public int End { get; } = end;
    }
}
