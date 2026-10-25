using Evidgate.Modules.Authoring.Domain;

namespace Evidgate.Modules.Authoring.Application.Import.Formats;

/// <summary>
/// The text rules every import/export format of 13.1.1-T5 shares (AC14/AC16): the composed Unicode form both directions
/// use, the two characters that make text unstorable, the spreadsheet formula neutralisation and the UTF-8 preamble the
/// exporters write and the readers tolerate.
/// </summary>
/// <remarks>
/// <para>
/// <b>NFC on both sides.</b> AC14 requires import to normalise and export to write NFC, so a decomposed Vietnamese title
/// and its composed spelling are one row, one <c>content_hash</c> and one authoring version — and two exports of an
/// unchanged tree are byte-identical. The composition itself is <see cref="AuthoringText.Normalize"/>, never a second
/// implementation: it refuses when the process cannot compose (00 §5.7 P7, D-S55-Q1) instead of silently storing
/// decomposed text.
/// </para>
/// <para>
/// <b>NUL and lone surrogates are refused, never repaired (AC13 <c>invalid_text</c>).</b> PostgreSQL refuses U+0000 in
/// both <c>text</c> (22021) and <c>jsonb</c> (22P05), and a lone surrogate is not a code point at all, so text carrying
/// either is not storable by this module and a caller's byte must be a 422 rather than a 500 from the statement — the
/// same rule <c>AuthoringContent.Compose</c> applies to a content member.
/// </para>
/// <para>
/// <b>Formula neutralisation (AC16, 00 §5.7 S5).</b> A spreadsheet evaluates a cell whose text starts with <c>=</c>,
/// <c>+</c>, <c>-</c>, <c>@</c>, a tab, a carriage return or a line feed, so an authoring title like
/// <c>=HYPERLINK(…)</c> must never reach a spreadsheet as a formula. Export writes such a cell with one leading
/// apostrophe (the spreadsheet convention for literal text) and import strips exactly one apostrophe back — but only
/// where a formula could start, so a genuine value such as <c>'hello</c> keeps its apostrophe. A value that itself
/// begins with an apostrophe followed by a trigger character is written with two (<c>'=SUM(1)</c> →
/// <c>''=SUM(1)</c>) and read back as one, so that case round-trips too; the remaining ambiguity — a genuine
/// <c>''=…</c> reads back as <c>'=…</c> — is stated in the plan's export note because a one-character prefix cannot
/// express it. Neutralised cells are stored as literal text and are never evaluated: JSON export is not neutralised at
/// all, because a JSON consumer does not evaluate cell text.
/// </para>
/// </remarks>
internal static class ImportText
{
    /// <summary>
    /// The longest single cell a reader accepts, in bytes: a 5 MiB body may not spend its whole budget on one cell, so a
    /// cell over this cap is refused as <c>value_invalid</c> before it is decoded or allocated
    /// (<see cref="MaxCellChars"/> is the same cap after decoding, for the XML formats).
    /// </summary>
    internal const int MaxCellBytes = 64 * 1024;

    /// <summary>The same 64 KiB cap counted in characters, for a format that decodes before it can measure bytes.</summary>
    internal const int MaxCellChars = 64 * 1024;

    /// <summary>The UTF-8 byte-order mark every CSV/XLSX export starts with and every reader tolerates (AC14).</summary>
    private static ReadOnlySpan<byte> Preamble => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// True when a cell starting with <paramref name="value"/> would be evaluated as a formula by a spreadsheet
    /// (<c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab, carriage return, line feed — AC16).
    /// </summary>
    /// <param name="value">The first character of a cell.</param>
    /// <returns>True for a formula trigger.</returns>
    /// <remarks>
    /// Line feed is in the set because a cell a consumer trims of its line breaks can start with a trigger once the
    /// break is gone, and because the escaping and un-escaping rules must name one set: a value starting with a line
    /// break is prefixed on export and stripped on import exactly like <c>=x</c> is.
    /// </remarks>
    internal static bool IsSpreadsheetTrigger(char value) =>
        value is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n';

    /// <summary>The body without its UTF-8 preamble, or unchanged when it carries none.</summary>
    /// <param name="body">The raw body.</param>
    /// <returns>The body from its first content byte.</returns>
    internal static ReadOnlySpan<byte> WithoutPreamble(ReadOnlySpan<byte> body) =>
        body.StartsWith(Preamble) ? body[Preamble.Length..] : body;

    /// <summary>
    /// The NFC form of one cell value, refusing (never repairing) text this module cannot store.
    /// </summary>
    /// <param name="value">The decoded cell text; <c>null</c> and empty text pass through.</param>
    /// <param name="invalid">
    /// Set when the value carries U+0000 or a lone surrogate, i.e. when the reader must answer
    /// <c>invalid_text</c> for the cell.
    /// </param>
    /// <returns>The composed value, or <c>null</c> when <paramref name="invalid"/> is set.</returns>
    internal static string? Normalise(string? value, out bool invalid)
    {
        invalid = false;
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (!IsStorable(value))
        {
            invalid = true;
            return null;
        }

        try
        {
            return AuthoringText.Normalize(value);
        }
        catch (ArgumentException)
        {
            // The process's normaliser refuses text this module's own storable check let through — a code point the
            // normaliser does not accept. A caller's byte must then be a 422 (invalid_text), never the 500 an escaping
            // ArgumentException would be, and never text stored in a form the module cannot compose (AC13/AC14).
            invalid = true;
            return null;
        }
    }

    /// <summary>
    /// The NFC form of a value an exporter writes: the composed text, or the value unchanged when this process cannot
    /// compose it (a code point the normaliser refuses).
    /// </summary>
    /// <remarks>
    /// The writers read rows the module stored, so the ordinary answer is NFC and this type never repairs an import.
    /// The unchanged answer exists so an export can never throw on the way out: a value the reader would refuse
    /// (<see cref="Normalise"/> answers <c>invalid_text</c>) is written as it is, escaped where the carrier needs it,
    /// and the file round-trips to the same refusal rather than to a 500 on the export.
    /// </remarks>
    /// <param name="value">The stored text.</param>
    /// <returns>The composed text, or the value unchanged.</returns>
    internal static string Compose(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        try
        {
            return AuthoringText.Normalize(value) ?? value;
        }
        catch (ArgumentException)
        {
            return value;
        }
    }

    /// <summary>
    /// True when <paramref name="value"/> can be stored at all: no U+0000, no lone surrogate and no noncharacter
    /// (<see cref="AuthoringText.ContainsNul"/> for the character, the code-point walk for the rest).
    /// </summary>
    /// <remarks>
    /// A noncharacter — any code point whose last UTF-16 unit is U+FFFE or U+FFFF, in every plane — is storable
    /// column text in PostgreSQL but <em>not</em> composable: the process's normaliser refuses it
    /// (<c>ArgumentException</c>), and AC14 stores composed text only. It is therefore refused by the same rule as
    /// U+0000 and a lone surrogate, before any reader reaches <c>AuthoringText.Normalize</c> and long before a
    /// statement could fail, so the answer is one <c>invalid_text</c> issue rather than a 500.
    /// </remarks>
    /// <param name="value">The text; <c>null</c> is storable.</param>
    /// <returns>True when every UTF-16 unit of the text belongs to a storable code point.</returns>
    internal static bool IsStorable(string? value)
    {
        if (value is null)
        {
            return true;
        }

        if (AuthoringText.ContainsNul(value))
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    // A well-formed pair is one code point; the noncharacters of planes 1..16 end in U+FFFE/U+FFFF,
                    // which no surrogate carries, so a pair is storable.
                    index++;
                    continue;
                }

                return false;
            }

            if (char.IsLowSurrogate(value[index]))
            {
                return false;
            }

            if (value[index] is '\uFFFE' or '\uFFFF')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The cell text an exporter writes for <paramref name="value"/>: the value with one leading apostrophe when a
    /// spreadsheet would otherwise evaluate it (AC16).
    /// </summary>
    /// <remarks>
    /// The rule is a <em>run</em> of leading apostrophes, not one character, because the reader strips exactly one
    /// apostrophe per read: the writer grows the run by one whenever the first character that is not an apostrophe is a
    /// formula trigger, and the reader shrinks it by one under the same test. So <c>=x</c> is written <c>'=x</c>,
    /// stored <c>'=x</c> is written <c>''=x</c> and stored <c>''=x</c> is written <c>'''=x</c> — every stored value
    /// round-trips — while <c>'hello</c> and <c>''hello</c> are written and read back unchanged.
    /// </remarks>
    /// <param name="value">The stored text.</param>
    /// <returns>The cell text to write.</returns>
    internal static string NeutraliseForSpreadsheet(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0)
        {
            return value;
        }

        var index = 0;
        while (index < value.Length && value[index] == '\'')
        {
            index++;
        }

        // A trigger in the first position, or a trigger just after the leading apostrophe run: both need one more
        // apostrophe, so the reader's single strip returns the stored text.
        var needsPrefix = index == 0
            ? IsSpreadsheetTrigger(value[0])
            : index < value.Length && IsSpreadsheetTrigger(value[index]);

        return needsPrefix ? "'" + value : value;
    }

    /// <summary>
    /// The stored text of an imported cell: exactly one leading apostrophe is removed when the first character that is
    /// not an apostrophe is a formula trigger, so a neutralised cell is stored as literal text and a genuine
    /// <c>'hello</c> keeps its apostrophe (AC16).
    /// </summary>
    /// <param name="value">The cell text as the file carries it.</param>
    /// <returns>The text to store.</returns>
    internal static string UnneutraliseFromSpreadsheet(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length < 2 || value[0] != '\'')
        {
            return value;
        }

        var index = 0;
        while (index < value.Length && value[index] == '\'')
        {
            index++;
        }

        return index < value.Length && IsSpreadsheetTrigger(value[index]) ? value[1..] : value;
    }
}
