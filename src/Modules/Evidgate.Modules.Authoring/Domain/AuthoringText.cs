using System.Globalization;
using System.Text;

namespace Evidgate.Modules.Authoring.Domain;

/// <summary>
/// Text hygiene for every authoring column of 13.1.1: the composed Unicode form (NFC) that the
/// write path applies before a value is counted, hashed or stored, and the length unit the
/// validators count.
/// </summary>
/// <remarks>
/// <para>
/// NFC is applied to the <em>input</em>, never by a JSON Schema and never by the database: a
/// decomposed Vietnamese input and its composed form must be one row, one <c>content_hash</c> and
/// one authoring version (13.1.1 AC14; 13.2.1 §4 states the same rule for the settings document).
/// Counting follows the same rule as PostgreSQL: <c>char_length</c> counts code points, so
/// <see cref="CodePointLength"/> counts code points too — <c>"😀"</c> is 1 and a flag emoji built
/// from two code points is 2, the same number in the schema, the CHECK and the UI counter
/// (13.2.1 §4 "char_length vs the UI counter").
/// </para>
/// <para>
/// <b>Normalisation needs ICU.</b> Globalization-invariant mode (00 §5.7 P7) makes
/// <see cref="string.Normalize(NormalizationForm)"/> a no-op: it returns the input unchanged instead of
/// composing it. <c>Evidgate.Worker</c> runs that mode (it hosts no authoring write path); <c>Evidgate.Api</c>
/// must keep ICU — <c>Directory.Build.props</c> leaves <c>InvariantGlobalization</c> off for it and its image
/// runs the <c>-extra</c> chiseled base that ships ICU (13.1.1-T2, D-S55-Q1). A silent no-op would store
/// decomposed text and break AC14 while every test passes, so <see cref="Normalize"/> and
/// <see cref="EnsureNfcSupported"/> refuse instead of pretending. <see cref="IsNfcSupported"/> is the runtime
/// probe; <see cref="NfcUnavailableMessage"/> names the conflict so a host that cannot normalize fails
/// at composition instead of at the first write.
/// </para>
/// </remarks>
internal static class AuthoringText
{
    /// <summary>A decomposed <c>e</c> + U+0301, the shortest input that NFC must compose.</summary>
    private const string DecomposedProbe = "e\u0301";

    /// <summary>The composed result NFC must produce for <see cref="DecomposedProbe"/>.</summary>
    private const string ComposedProbe = "\u00e9";

    /// <summary>
    /// The failure message of <see cref="Normalize"/> and <see cref="EnsureNfcSupported"/>: why the
    /// text cannot be composed and how a host resolves it. Never a user-facing message.
    /// </summary>
    public const string NfcUnavailableMessage =
        "Unicode NFC normalisation is unavailable in this process: it runs with globalization-invariant mode " +
        "(`InvariantGlobalization`, 00 §5.7 P7), where string.Normalize(FormC) returns the input unchanged. " +
        "Authorship text must be stored composed (13.1.1 AC14), so a host that composes the authoring module " +
        "must keep ICU: `InvariantGlobalization` is off for Evidgate.Api in Directory.Build.props and its image " +
        "runs the `-extra` chiseled base that ships ICU (13.1.1-T2, D-S55-Q1). " +
        "Either run the authoring write path on such a host or do not enable it on this one.";

    /// <summary>
    /// True when this process can compose text, i.e. when <see cref="Normalize"/> actually
    /// normalises. Probed once at type initialisation by composing a decomposed <c>é</c>: no
    /// internal runtime API is used, so the answer is the behaviour itself.
    /// </summary>
    public static bool IsNfcSupported { get; } = Probe();

    /// <summary>
    /// The NFC form of <paramref name="value"/> — the form every authoring text column stores.
    /// Idempotent (an NFC value is returned unchanged), and null-safe: <c>null</c> stays
    /// <c>null</c> and empty text returns empty without touching the normaliser.
    /// </summary>
    /// <param name="value">The text to compose.</param>
    /// <returns>The same text in NFC, or <c>null</c> when <paramref name="value"/> is null.</returns>
    /// <exception cref="InvalidOperationException">
    /// The process cannot normalise (<see cref="IsNfcSupported"/> is false); see
    /// <see cref="NfcUnavailableMessage"/>.
    /// </exception>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (!IsNfcSupported)
        {
            throw new InvalidOperationException(NfcUnavailableMessage);
        }

        return value.Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Refuses to continue when this process cannot compose text, for a composition root that
    /// wants the failure at startup rather than at the first write (13.1.1 AC14).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The process cannot normalise; the message is <see cref="NfcUnavailableMessage"/>.
    /// </exception>
    public static void EnsureNfcSupported()
    {
        if (!IsNfcSupported)
        {
            throw new InvalidOperationException(NfcUnavailableMessage);
        }
    }

    /// <summary>
    /// The length of <paramref name="value"/> in code points — the unit PostgreSQL
    /// <c>char_length</c> and JSON Schema <c>maxLength</c> count, so a validator and a CHECK agree
    /// on every value. Never UTF-16 units: a surrogate pair is one code point.
    /// </summary>
    /// <param name="value">The text; <c>null</c> has length 0.</param>
    /// <returns>The number of code points.</returns>
    public static int CodePointLength(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var length = 0;
        for (var index = 0; index < value.Length;)
        {
            if (!Rune.TryGetRuneAt(value, index, out var rune))
            {
                // A lone surrogate is not a code point; it counts as the unit it occupies so an
                // ill-formed value is never reported as shorter than the bytes it stores.
                length++;
                index++;
                continue;
            }

            length++;
            index += rune.Utf16SequenceLength;
        }

        return length;
    }

    /// <summary>
    /// The longest caller-supplied name echoed in an error (the catalog's <c>TextRules.Brief</c> bound): an unknown
    /// member name or a path segment is clipped to this many characters plus an ellipsis, so a caller cannot inflate a
    /// <c>details[].path</c> with a 4 096-character name (security S-8). One bound for the edge
    /// (<c>AuthoringEndpointSupport</c>) and the content parser, so both report the same name the same way.
    /// </summary>
    public const int MaximumEchoedNameLength = 64;

    /// <summary>
    /// A short, single-line rendering of a caller-supplied name for an error echo (the catalog's <c>TextRules.Brief</c>
    /// rule, mirrored locally): at most <see cref="MaximumEchoedNameLength"/> UTF-16 units on a rune boundary, control
    /// and format runes shown as <c>?</c>, and an ellipsis when anything was dropped. Clipping at a raw index could cut a
    /// surrogate pair in half and put an unpaired surrogate into <c>details[].path</c> — the one thing this surface's
    /// placeholder exists to keep out of an answer — while a raw newline or a U+202E would reorder the rendered line.
    /// </summary>
    /// <param name="text">The name to shorten.</param>
    /// <param name="limit">The longest accepted answer, in UTF-16 units.</param>
    /// <returns>The name unchanged when it fits, the clipped form otherwise.</returns>
    public static string Brief(string text, int limit = MaximumEchoedNameLength)
    {
        var builder = new StringBuilder(Math.Min(text.Length, limit));
        foreach (var rune in (text.Length <= limit ? text : Truncate(text, limit)).EnumerateRunes())
        {
            var echoable = Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format
                ? "?"
                : rune.ToString();
            builder.Append(echoable);
        }

        return builder.Length < text.Length ? builder + "…" : builder.ToString();
    }

    /// <summary>The longest prefix of <paramref name="text"/> with at most <paramref name="maxChars"/> UTF-16 units that ends on a rune boundary.</summary>
    /// <param name="text">The text.</param>
    /// <param name="maxChars">The limit.</param>
    /// <returns>The prefix, never splitting a surrogate pair.</returns>
    private static string Truncate(string text, int maxChars)
    {
        var end = maxChars;
        if (end > 0 && end < text.Length && char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        return text[..end];
    }

    /// <summary>
    /// True when <paramref name="value"/> carries U+0000 — the one character PostgreSQL refuses in both a <c>text</c>
    /// column (<c>22021 invalid byte sequence for encoding "UTF8": 0x00</c>) and a <c>jsonb</c> document
    /// (<c>22P05</c>), so text holding it is not storable by this module at all. Every authoring write and read
    /// parameter passes a guard that calls this before it reaches a statement, so a caller's own byte is a 422 rather
    /// than a 500 (the rule <c>Ingestion</c>'s <c>UnstorableText</c> states for its own surface).
    /// </summary>
    /// <param name="value">The text; <c>null</c> is storable.</param>
    /// <returns><c>true</c> when the value contains a NUL.</returns>
    public static bool ContainsNul(string? value) => value is not null && value.Contains('\0');

    /// <summary>True when <paramref name="value"/> is at most <paramref name="maxCodePoints"/> code points long.</summary>
    /// <param name="value">The text.</param>
    /// <param name="maxCodePoints">The inclusive limit.</param>
    public static bool Fits(string? value, int maxCodePoints) =>
        CodePointLength(value) <= maxCodePoints;

    private static bool Probe()
    {
        try
        {
            return string.Equals(DecomposedProbe.Normalize(NormalizationForm.FormC), ComposedProbe, StringComparison.Ordinal);
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }
}
