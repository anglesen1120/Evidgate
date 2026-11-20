using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Evidgate.BuildingBlocks.Canonicalization;

public static partial class JsonCanonicalizer
{
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
}
