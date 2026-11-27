using System.Text;

namespace Evidgate.Modules.Authoring.Application.Import.Formats;

/// <summary>
/// Reads a Markdown outline into requirement rows (the import only; nothing exports Markdown). A heading is a user
/// story, a list item or a Gherkin block under it is one acceptance criterion; the text between a heading and its first
/// list item or Gherkin block is the story's body. See <see cref="MarkdownScanner"/> for the exact rules.
/// </summary>
/// <remarks>
/// <b>Line numbers.</b> <see cref="ImportIssue.Index"/> of a Markdown refusal is the 1-based source line of the construct
/// that was refused, not a row ordinal: a file has no rows until it is read. A refused file lists at most 100 lines and
/// never echoes any of its text. Setext headings (an underline of <c>=</c> or <c>-</c>), HTML and links are not
/// interpreted; text after a story's first list or Gherkin block that belongs to no item is not imported.
/// </remarks>
internal static class MarkdownFormat
{
    /// <summary>The strict UTF-8 decoder: bytes that are not UTF-8 are a refusal, never a replacement character.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Reads one Markdown body into requirement rows, or refuses it.</summary>
    /// <param name="body">The raw request body (a UTF-8 BOM is tolerated).</param>
    /// <returns>The parse: rows, issues, or the over-cap marker that the route answers with 413.</returns>
    internal static ImportParse Read(ReadOnlySpan<byte> body)
    {
        if (body.Length > ImportLimits.MaxBodyBytes)
        {
            return ImportParse.Large();
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(ImportText.WithoutPreamble(body));
        }
        catch (DecoderFallbackException)
        {
            var issues = new List<ImportIssue>();
            ImportSchema.Add(issues, 0, "/row", "invalid_text");

            return ImportParse.Invalid(issues);
        }

        var scanner = new MarkdownScanner();
        var number = 0;
        foreach (var line in Lines(text))
        {
            number++;
            scanner.Feed(line, number);
            if (scanner.TooLarge)
            {
                return ImportParse.Large();
            }
        }

        return scanner.Finish();
    }

    /// <summary>The lines of <paramref name="text"/>, split on CRLF, LF or a lone CR; a final terminator adds no empty line.</summary>
    private static IEnumerable<string> Lines(string text)
    {
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\n' or '\r'))
            {
                continue;
            }

            yield return text[start..index];
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }

            start = index + 1;
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }
}
