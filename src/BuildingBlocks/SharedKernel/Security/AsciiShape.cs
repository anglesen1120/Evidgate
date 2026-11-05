using System.Buffers;

namespace Evidgate.BuildingBlocks.SharedKernel.Security;

/// <summary>
/// Whole-string validation of the "first character, then a run of allowed characters, optionally a closing
/// character" shapes used for identifiers on request paths. One forward pass over at most
/// <c>maxLength</c> characters and no wall-clock setting, so a slow host can never turn a refusal into an exception.
/// </summary>
public static class AsciiShape
{
    /// <summary><c>[a-z0-9]</c>.</summary>
    public static readonly SearchValues<char> LowerAlnum = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789");

    /// <summary><c>[a-z0-9-]</c>.</summary>
    public static readonly SearchValues<char> LowerAlnumDash = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-");

    /// <summary><c>[0-9a-f]</c>; with <c>IsMatch(v, min, max, LowerHex, LowerHex)</c> it is a lowercase hex run.</summary>
    public static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    /// <summary><c>[A-Za-z0-9_.-]</c>.</summary>
    public static readonly SearchValues<char> AlnumDotDash = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_.-");

    /// <summary><c>[A-Z]</c>.</summary>
    public static readonly SearchValues<char> UpperAlpha = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZ");

    /// <summary>
    /// <c>[A-Za-z0-9._~%+:*?/-]</c>: a declared header, cookie, form-field or query-parameter name, or a <c>*</c>/<c>?</c>
    /// glob over it. The two glob characters are the only metacharacters, and every other character is literal.
    /// </summary>
    public static readonly SearchValues<char> NameGlob =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._~%+:*?/-");

    /// <summary><c>[A-Z0-9]</c>.</summary>
    public static readonly SearchValues<char> UpperAlnum = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");

    /// <summary>
    /// True when <paramref name="value"/> has <paramref name="minLength"/>..<paramref name="maxLength"/> characters,
    /// starts with a <paramref name="first"/> character, every later character is in <paramref name="rest"/>, and,
    /// when <paramref name="last"/> is given, the value has at least two characters and the final one is in <paramref name="last"/>. Anchored at both ends:
    /// a trailing newline is refused (the <c>\z</c> semantics, not <c>$</c>).
    /// </summary>
    public static bool IsMatch(
        ReadOnlySpan<char> value,
        int minLength,
        int maxLength,
        SearchValues<char> first,
        SearchValues<char> rest,
        SearchValues<char>? last = null)
    {
        if (value.Length < minLength || value.Length > maxLength || value.Length == 0 || !first.Contains(value[0]))
        {
            return false;
        }

        if (last is not null && (value.Length < 2 || !last.Contains(value[^1])))
        {
            return false;
        }

        var body = last is null ? value[1..] : value[1..^1];
        return body.IndexOfAnyExcept(rest) < 0;
    }
}
