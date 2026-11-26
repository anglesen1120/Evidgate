namespace Evidgate.Modules.Authoring.Application.Intake;

/// <summary>
/// Finds where a raw PDF stream body ends, for the lexical probe. The declared <c>/Length</c> decides, whether it is
/// a direct integer or a reference to an integer object; only when that length does not land on <c>endstream</c> is
/// the first <c>endstream</c> that starts a line and precedes <c>endobj</c> used. A word inside the body never ends it.
/// </summary>
internal sealed class PdfStreamBoundary
{
    private const int MaxIndexedObjects = 100_000;
    private const int MaxDigits = 10;

    private static readonly byte[] EndStream = "endstream"u8.ToArray();
    private static readonly byte[] EndObject = "endobj"u8.ToArray();
    private static readonly byte[] ObjectKeyword = "obj"u8.ToArray();

    private long direct = -1;
    private int reference = -1;
    private Dictionary<int, long>? integerObjects;

    /// <summary>True for the PDF white-space bytes.</summary>
    /// <param name="value">The byte to test.</param>
    /// <returns>Whether the byte is white space.</returns>
    public static bool IsWhitespace(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;

    /// <summary>Records the <c>/Length</c> entry that follows the name, for the next stream body.</summary>
    /// <param name="bytes">The PDF bytes.</param>
    /// <param name="index">The position just after the <c>/Length</c> name.</param>
    public void NoteLength(ReadOnlySpan<byte> bytes, int index)
    {
        direct = -1;
        reference = -1;

        if (!TryReadInteger(bytes, ref index, out var first))
        {
            return;
        }

        if (!TryReadInteger(bytes, ref index, out _))
        {
            direct = first;

            return;
        }

        SkipWhitespace(bytes, ref index);

        if (index < bytes.Length && bytes[index] == (byte)'R' && first <= int.MaxValue)
        {
            reference = (int)first;
        }
    }

    /// <summary>Finds the position just after <c>endstream</c> for the body that starts at <paramref name="bodyStart"/>.</summary>
    /// <param name="bytes">The PDF bytes.</param>
    /// <param name="bodyStart">The first byte of the stream body.</param>
    /// <returns>The position after <c>endstream</c>, or -1 when no trustworthy end is found.</returns>
    public int FindEnd(ReadOnlySpan<byte> bytes, int bodyStart)
    {
        var length = direct >= 0 ? direct : reference >= 0 ? LookupLength(bytes, reference) : -1;
        direct = -1;
        reference = -1;

        if (length >= 0 && length <= bytes.Length - bodyStart)
        {
            var marker = SkipLineBreak(bytes, bodyStart + (int)length);

            if (bytes[marker..].StartsWith(EndStream))
            {
                return marker + EndStream.Length;
            }
        }

        return FindMarkedEnd(bytes, bodyStart);
    }

    private static int FindMarkedEnd(ReadOnlySpan<byte> bytes, int bodyStart)
    {
        var from = bodyStart;

        while (bytes[from..].IndexOf(EndStream) is var relative and >= 0)
        {
            var at = from + relative;
            var end = at + EndStream.Length;

            if ((at == bodyStart || bytes[at - 1] is 10 or 13) && StartsWithEndObject(bytes, end))
            {
                return end;
            }

            from = at + 1;
        }

        return -1;
    }

    private long LookupLength(ReadOnlySpan<byte> bytes, int objectNumber)
    {
        integerObjects ??= IndexIntegerObjects(bytes);

        return integerObjects.TryGetValue(objectNumber, out var value) ? value : -1;
    }

    /// <summary>Maps object number to value for every <c>N G obj &lt;integer&gt; endobj</c>; the last definition wins.</summary>
    private static Dictionary<int, long> IndexIntegerObjects(ReadOnlySpan<byte> bytes)
    {
        var map = new Dictionary<int, long>();
        var from = 0;

        while (map.Count < MaxIndexedObjects && bytes[from..].IndexOf(ObjectKeyword) is var relative and >= 0)
        {
            var at = from + relative;
            from = at + ObjectKeyword.Length;
            var after = at + ObjectKeyword.Length;

            if (after < bytes.Length && IsWhitespace(bytes[after])
                && TryReadObjectNumberBefore(bytes, at, out var number)
                && TryReadIntegerObject(bytes, after, out var value))
            {
                map[number] = value;
            }
        }

        return map;
    }

    private static bool TryReadObjectNumberBefore(ReadOnlySpan<byte> bytes, int keywordAt, out int number)
    {
        number = 0;
        var position = keywordAt - 1;

        if (position < 0 || !IsWhitespace(bytes[position]))
        {
            return false;
        }

        SkipWhitespaceBackwards(bytes, ref position);

        if (!SkipDigitsBackwards(bytes, ref position, out _) || position < 0 || !IsWhitespace(bytes[position]))
        {
            return false;
        }

        SkipWhitespaceBackwards(bytes, ref position);

        return SkipDigitsBackwards(bytes, ref position, out number) && (position < 0 || IsWhitespace(bytes[position]));
    }

    private static bool TryReadIntegerObject(ReadOnlySpan<byte> bytes, int index, out long value)
    {
        if (!TryReadInteger(bytes, ref index, out value))
        {
            return false;
        }

        return StartsWithEndObject(bytes, index);
    }

    private static bool StartsWithEndObject(ReadOnlySpan<byte> bytes, int index)
    {
        SkipWhitespace(bytes, ref index);

        return bytes[index..].StartsWith(EndObject);
    }

    private static int SkipLineBreak(ReadOnlySpan<byte> bytes, int index)
    {
        if (index < bytes.Length && bytes[index] == 13)
        {
            index++;
        }

        return index < bytes.Length && bytes[index] == 10 ? index + 1 : index;
    }

    /// <summary>Reads an unsigned integer of at most ten digits after optional white space.</summary>
    private static bool TryReadInteger(ReadOnlySpan<byte> bytes, ref int index, out long value)
    {
        SkipWhitespace(bytes, ref index);
        value = 0;
        var digits = 0;

        while (index < bytes.Length && bytes[index] is >= (byte)'0' and <= (byte)'9')
        {
            if (++digits > MaxDigits)
            {
                return false;
            }

            value = (value * 10) + (bytes[index] - '0');
            index++;
        }

        return digits > 0;
    }

    private static void SkipWhitespace(ReadOnlySpan<byte> bytes, ref int index)
    {
        while (index < bytes.Length && IsWhitespace(bytes[index]))
        {
            index++;
        }
    }

    private static void SkipWhitespaceBackwards(ReadOnlySpan<byte> bytes, ref int position)
    {
        while (position >= 0 && IsWhitespace(bytes[position]))
        {
            position--;
        }
    }

    private static bool SkipDigitsBackwards(ReadOnlySpan<byte> bytes, ref int position, out int value)
    {
        value = 0;
        var scale = 1;
        var digits = 0;

        while (position >= 0 && bytes[position] is >= (byte)'0' and <= (byte)'9')
        {
            if (++digits > 9)
            {
                return false;
            }

            value += (bytes[position] - '0') * scale;
            scale *= 10;
            position--;
        }

        return digits > 0;
    }
}
