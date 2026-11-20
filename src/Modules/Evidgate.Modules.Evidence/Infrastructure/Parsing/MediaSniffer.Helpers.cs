namespace Evidgate.Modules.Evidence.Infrastructure.Parsing;

internal static partial class MediaSniffer
{
    /// <summary>Drops a leading UTF-8 BOM and the ASCII whitespace before the first significant byte.</summary>
    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> prefix)
    {
        var start = 0;
        if (prefix.Length >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
        {
            start = 3;
        }

        while (start < prefix.Length && IsAsciiWhitespace(prefix[start]))
        {
            start++;
        }

        return prefix[start..];
    }

    /// <summary>True when the next code units spell <paramref name="word"/> (ASCII, case-sensitive).</summary>
    private static bool MatchesWord(ReadOnlySpan<byte> body, int width, bool littleEndian, string word)
    {
        var rest = body;
        foreach (var expected in word)
        {
            if (ReadUnit(ref rest, width, littleEndian) != expected)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when the next two code units are <c>--</c> (an XML comment start).</summary>
    private static bool IsEncodedCommentStart(ReadOnlySpan<byte> body, int width, bool littleEndian)
    {
        var rest = body;
        return ReadUnit(ref rest, width, littleEndian) == '-'
            && ReadUnit(ref rest, width, littleEndian) == '-';
    }

    /// <summary>
    /// True when the next code units spell <c>DOCTYPE</c> case-insensitively. A prefix that ends inside the
    /// keyword is accepted, exactly like the UTF-8 path: the parser profile, not the sniff, refuses the DTD.
    /// </summary>
    private static bool IsEncodedDoctypeStart(ReadOnlySpan<byte> body, int width, bool littleEndian)
    {
        ReadOnlySpan<byte> keyword = "DOCTYPE"u8;
        var rest = body;
        var matched = 0;

        while (matched < keyword.Length)
        {
            var unit = ReadUnit(ref rest, width, littleEndian);
            if (unit < 0)
            {
                break;
            }

            if (ToAsciiUpperCodeUnit(unit) != ToAsciiUpperCodeUnit(keyword[matched]))
            {
                return false;
            }

            matched++;
        }

        return matched > 0;
    }

    /// <summary>
    /// Reads the next code unit at the head of <paramref name="body"/> in the width/endianness the BOM named and
    /// advances it, or <c>-1</c> when fewer than one whole code unit remains (a prefix cut mid-unit, fail closed).
    /// </summary>
    private static int ReadUnit(ref ReadOnlySpan<byte> body, int width, bool littleEndian)
    {
        if (body.Length < width)
        {
            return -1;
        }

        var value = width == 2
            ? littleEndian ? body[0] | (body[1] << 8) : (body[0] << 8) | body[1]
            : littleEndian
                ? body[0] | (body[1] << 8) | (body[2] << 16) | (body[3] << 24)
                : (body[0] << 24) | (body[1] << 16) | (body[2] << 8) | body[3];

        body = body[width..];
        return value;
    }

    /// <summary>
    /// The BOM table (06.3.1-T2.2): the byte order and code-unit width the leading bytes name, or UTF-8 with no
    /// BOM when none is present. The UTF-32 LE BOM is tested before the UTF-16 LE BOM it starts with.
    /// </summary>
    /// <param name="Offset">How many leading bytes the BOM occupies.</param>
    /// <param name="Width">The code-unit width in bytes (1 for UTF-8).</param>
    /// <param name="LittleEndian">The byte order of a multi-byte encoding.</param>
    private readonly record struct Bom(int Offset, int Width, bool LittleEndian);

    private static Bom DetectBom(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length >= 4 && prefix[0] == 0xFF && prefix[1] == 0xFE && prefix[2] == 0x00 && prefix[3] == 0x00)
        {
            return new Bom(4, 4, LittleEndian: true);
        }

        if (prefix.Length >= 4 && prefix[0] == 0x00 && prefix[1] == 0x00 && prefix[2] == 0xFE && prefix[3] == 0xFF)
        {
            return new Bom(4, 4, LittleEndian: false);
        }

        if (prefix.Length >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
        {
            return new Bom(3, 1, LittleEndian: false);
        }

        if (prefix.Length >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE)
        {
            return new Bom(2, 2, LittleEndian: true);
        }

        if (prefix.Length >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF)
        {
            return new Bom(2, 2, LittleEndian: false);
        }

        return new Bom(0, 1, LittleEndian: false);
    }

    private static bool IsAsciiWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static bool IsWhitespace(int codeUnit) =>
        codeUnit is ' ' or '\t' or '\r' or '\n';

    private static bool IsAsciiLetter(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';

    private static bool IsAsciiDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';

    /// <summary>An XML name start: an ASCII letter, <c>_</c>, <c>:</c> or any non-ASCII byte (a UTF-8 lead byte).</summary>
    private static bool IsNameStart(byte value) =>
        IsAsciiLetter(value) || value is (byte)'_' or (byte)':' || value >= 0x80;

    /// <summary>An XML name start as a decoded code unit: an ASCII letter, <c>_</c>, <c>:</c> or any non-ASCII unit.</summary>
    private static bool IsNameStartUnit(int codeUnit) =>
        (codeUnit >= 'A' && codeUnit <= 'Z')
        || (codeUnit >= 'a' && codeUnit <= 'z')
        || codeUnit is '_' or ':'
        || codeUnit >= 0x80;

    private static bool AsciiStartsWith(ReadOnlySpan<byte> value, string expected)
    {
        if (value.Length < expected.Length)
        {
            return false;
        }

        for (var index = 0; index < expected.Length; index++)
        {
            if (value[index] != (byte)expected[index])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Case-insensitive ASCII prefix comparison, for the one XML keyword that is not lowercase.</summary>
    private static bool AsciiStartsWithIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> expected)
    {
        for (var index = 0; index < expected.Length; index++)
        {
            if (ToAsciiUpper(value[index]) != ToAsciiUpper(expected[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static byte ToAsciiUpper(byte value) =>
        value is >= (byte)'a' and <= (byte)'z' ? (byte)(value - 32) : value;

    private static int ToAsciiUpperCodeUnit(int value) =>
        value is >= 'a' and <= 'z' ? value - 32 : value;
}
