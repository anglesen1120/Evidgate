using System.Globalization;
using System.Text.RegularExpressions;

namespace Evidgate.Modules.Catalog.Domain;

/// <summary>What <see cref="AnnotationParser"/> found in one file.</summary>
internal enum AnnotationOutcome
{
    /// <summary>No <c>evidgate.id</c> annotation.</summary>
    None,

    /// <summary>Exactly one annotation holding a UUID.</summary>
    Valid,

    /// <summary>Exactly one annotation whose value is not a UUID.</summary>
    Invalid,

    /// <summary>Two or more annotations (03.1.1 §5: a second one is a conflict, never a silent pick).</summary>
    Multiple,
}

/// <summary>The parse of one file.</summary>
/// <param name="Outcome">What was found.</param>
/// <param name="Id">The id of a <see cref="AnnotationOutcome.Valid"/> annotation.</param>
/// <param name="Raw">The annotation text as written (the first one), for the identity trail.</param>
internal readonly record struct AnnotationResult(AnnotationOutcome Outcome, Guid? Id, string? Raw);

/// <summary>
/// Extracts the <c>evidgate.id</c> annotation from test source (03.1.1-T1.2, 00 §7). Three spellings are
/// recognised, all of them <em>annotations</em> and never plain strings of test code:
/// <list type="bullet">
/// <item>a line or block comment: <c>// evidgate.id: &lt;uuid&gt;</c>, <c># evidgate.id: …</c>, <c>/* evidgate.id: … */</c>,
/// <c>* @evidgate.id …</c> (JSDoc), <c>-- evidgate.id: …</c>;</item>
/// <item>a decorator or attribute on its own line: <c>@evidgate.id('&lt;uuid&gt;')</c>;</item>
/// <item>Playwright's annotation object: <c>{ type: 'evidgate.id', description: '&lt;uuid&gt;' }</c>.</item>
/// </list>
/// Every pattern has a match timeout and the input is capped, so a hostile file cannot stall a sync.
/// </summary>
internal static partial class AnnotationParser
{
    /// <summary>Largest file scanned; a bigger one is reported by the caller, not read.</summary>
    internal const int MaxContentChars = 2 * 1024 * 1024;

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Scans <paramref name="content"/>.</summary>
    /// <param name="content">The source text.</param>
    public static AnnotationResult Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Length == 0 || content.Length > MaxContentChars)
        {
            return new AnnotationResult(AnnotationOutcome.None, null, null);
        }

        // Every spelling contains the literal; almost every file does not.
        if (content.AsSpan().IndexOf("evidgate.id", StringComparison.Ordinal) < 0)
        {
            return new AnnotationResult(AnnotationOutcome.None, null, null);
        }

        var values = new List<string>();

        try
        {
            foreach (Match match in Comment().Matches(content))
            {
                values.Add(match.Groups["v"].Value);
                if (values.Count >= 16)
                {
                    break;
                }
            }

            foreach (Match match in Decorator().Matches(content))
            {
                values.Add(match.Groups["v"].Value);
                if (values.Count >= 16)
                {
                    break;
                }
            }

            foreach (Match match in PlaywrightObject().Matches(content))
            {
                values.Add(match.Groups["v"].Value);
                if (values.Count >= 16)
                {
                    break;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological file is treated as unreadable rather than guessed at.
            return new AnnotationResult(AnnotationOutcome.Invalid, null, "unreadable");
        }

        if (values.Count == 0)
        {
            return new AnnotationResult(AnnotationOutcome.None, null, null);
        }

        if (values.Count > 1)
        {
            return new AnnotationResult(AnnotationOutcome.Multiple, null, string.Join(',', values.Take(8)));
        }

        return IsCanonicalUuid(values[0], out var id)
            ? new AnnotationResult(AnnotationOutcome.Valid, id, values[0])
            : new AnnotationResult(AnnotationOutcome.Invalid, null, values[0]);
    }

    /// <summary>True for the 36-character hyphenated form only: braces, no hyphens or a different case are other spellings of the same id and would split an identity.</summary>
    internal static bool IsCanonicalUuid(string value, out Guid id) =>
        Guid.TryParseExact(value, "D", out id)
        && string.Equals(id.ToString("D", CultureInfo.InvariantCulture), value, StringComparison.OrdinalIgnoreCase)
        && id != Guid.Empty;

    [GeneratedRegex(
        @"(?<![\w'""`])(?://|#|/\*|\*|--)[ \t]*@?evidgate\.id[ \t]*[:=]?[ \t]*\(?[ \t]*['""]?(?<v>[^\s'"")*/]{1,64})",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex Comment();

    [GeneratedRegex(
        @"^[ \t]*@evidgate\.id[ \t]*\([ \t]*['""]?(?<v>[^\s'"")]{1,64})",
        RegexOptions.CultureInvariant | RegexOptions.Multiline,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex Decorator();

    [GeneratedRegex(
        @"type[ \t]*:[ \t]*['""]evidgate\.id['""][ \t]*,[ \t\r\n]*description[ \t]*:[ \t]*['""](?<v>[^'""\r\n]{1,64})['""]",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex PlaywrightObject();
}
