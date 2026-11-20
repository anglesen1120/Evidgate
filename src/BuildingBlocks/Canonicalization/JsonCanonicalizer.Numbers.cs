using System.Globalization;

namespace Evidgate.BuildingBlocks.Canonicalization;

public static partial class JsonCanonicalizer
{
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
}
