namespace Architecture.Tests.Rules;

/// <summary>
/// 06.3.1-T1.1 / plan §11: every parse of untrusted artifact bytes goes through the one enforced profile, so the
/// construction of an XML or archive reader is a build-time fact rather than a review habit. The rule reads
/// source text (the same way the secret-literal rule does), because a reader is constructed in a method body,
/// where reflection cannot see it.
/// <para>
/// Two scopes, because the two families have different reach: the untrusted XML and archive readers
/// (<c>XmlReader</c>, <c>XmlDictionaryReader</c>, <c>XDocument</c>, <c>XElement</c>, <c>XPathDocument</c>,
/// <c>XmlSerializer</c>, <c>ZipArchive</c>, <c>TarReader</c>, and the profile's own report handle) parse
/// nothing in this repository today, so the rule forbids them everywhere outside the profile;
/// <c>JsonDocument.Parse</c> (and the other <c>JsonDocument</c>/<c>JsonNode</c>/<c>JsonSerializer</c>/
/// <c>Utf8JsonReader</c> entry points) is also the normal way to read a request body or a stored row
/// (Policy, Orchestrator, Catalog, Contracts.Validation all do it), so the rule scopes it to the module
/// that owns artifact bytes.
/// </para>
/// </summary>
public static class ParserConstructionRules
{
    /// <summary>The one directory allowed to construct a reader for untrusted artifact bytes.</summary>
    public const string ProfileDirectory = "src/Modules/Evidgate.Modules.Evidence/Infrastructure/Parsing/";

    /// <summary>The module that owns artifacts: inside it, the JSON reader is the profile's too.</summary>
    public const string ArtifactModuleDirectory = "src/Modules/Evidgate.Modules.Evidence/";

    /// <summary>Constructs forbidden everywhere outside the profile: the untrusted XML and archive readers.</summary>
    public static readonly IReadOnlyList<string> RepositoryWideConstructs =
    [
        "XmlReader.Create(",
        "XmlReaderSettings",
        "XmlDictionaryReader.Create(",
        "XDocument.Load(",
        "XDocument.Parse(",
        "XElement.Load(",
        "XElement.Parse(",
        "XPathDocument",
        "XmlSerializer",
        "new XmlDocument(",
        "new XmlTextReader(",
        "new ZipArchive(",
        "ZipFile.Open(",
        "ZipFile.OpenRead(",
        "ZipFile.ExtractToDirectory(",
        "TarReader",
        "new ParsedXmlReport(",
    ];

    /// <summary>Constructs forbidden outside the profile inside the artifact module: the JSON reader.</summary>
    public static readonly IReadOnlyList<string> ArtifactModuleConstructs =
    [
        "JsonDocument.Parse(",
        "JsonDocument.ParseAsync(",
        "JsonDocument.TryParseValue(",
        "JsonNode.Parse(",
        "JsonObject.Parse(",
        "JsonArray.Parse(",
        "JsonSerializer.Deserialize",
        "new Utf8JsonReader(",
    ];

    /// <summary>
    /// The write direction of an archive is not a parse: <c>new ZipArchive(output, ZipArchiveMode.Create)</c> builds an
    /// export (13.1.1-T5 XLSX writer) and reads no untrusted bytes, so a construction whose <em>mode argument</em> is
    /// exactly <c>ZipArchiveMode.Create</c> is not a violation. Every other construct, and every read/update-mode archive,
    /// still is.
    /// <para>
    /// The exemption reads the constructor call's own balanced argument list, not the text up to the next semicolon: a
    /// comment, a string literal, a ternary or a second constructor in the same statement is not the argument list of
    /// this call, and none of them can make a reading archive look like a writer. The mode is argument two of
    /// <c>ZipArchive(Stream, ZipArchiveMode[, bool][, Encoding])</c>, matched as the exact token, so a variable or a
    /// conditional expression that happens to evaluate to <c>Create</c> is still flagged.
    /// </para>
    /// </summary>
    private static bool ContainsParse(string text, string construct)
    {
        if (!text.Contains(construct, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(construct, "new ZipArchive(", StringComparison.Ordinal))
        {
            return true;
        }

        // Trivia is blanked once per file and only when the construct is mentioned at all (the fast path above): a
        // mention inside a comment or a string literal is not a construction.
        var stripped = StripTrivia(text);
        var from = 0;
        while (true)
        {
            var at = stripped.IndexOf(construct, from, StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }

            if (!IsWriteMode(stripped, at + construct.Length - 1))
            {
                return true;
            }

            from = at + construct.Length;
        }
    }

    /// <summary>
    /// True when the call whose argument list opens at <paramref name="open"/> (the index of its <c>(</c>) is the write
    /// direction: its second argument is exactly <c>ZipArchiveMode.Create</c>.
    /// </summary>
    /// <param name="stripped">The source with every comment and literal blanked.</param>
    /// <param name="open">The index of the argument list's opening parenthesis.</param>
    private static bool IsWriteMode(string stripped, int open)
    {
        var arguments = Arguments(stripped, open);

        // ZipArchive(Stream, ZipArchiveMode[, bool][, Encoding]): the mode is argument two. A three-argument writer
        // (`…, ZipArchiveMode.Create, leaveOpen: true`) is the one the XLSX writer uses, so the check is on the mode's
        // position and not on the last argument.
        return arguments.Count >= 2
            && string.Equals(arguments[1], "ZipArchiveMode.Create", StringComparison.Ordinal);
    }

    /// <summary>
    /// The arguments of the call opening at <paramref name="open"/>, split at the commas outside any nested
    /// <c>()</c>/<c>[]</c>/<c>{}</c> of the blanked copy and trimmed, so a nested call or an object initialiser stays one
    /// argument.
    /// </summary>
    /// <param name="stripped">The source with every comment and literal blanked.</param>
    /// <param name="open">The index of the argument list's opening parenthesis.</param>
    private static List<string> Arguments(string stripped, int open)
    {
        var arguments = new List<string>();
        var depth = 0;
        var from = open + 1;

        for (var at = open + 1; at < stripped.Length; at++)
        {
            switch (stripped[at])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;

                case ')' when depth == 0:
                    arguments.Add(stripped[from..at].Trim());

                    return arguments;

                case ')' or ']' or '}':
                    depth--;
                    break;

                case ',' when depth == 0:
                    arguments.Add(stripped[from..at].Trim());
                    from = at + 1;
                    break;

                default:
                    break;
            }
        }

        // An argument list that never closes is not a construction this rule can reason about; no argument was added
        // past the last complete one, so the mode (if any) is what the text between the last comma and the end holds.
        if (from < stripped.Length)
        {
            arguments.Add(stripped[from..].Trim());
        }

        return arguments;
    }

    /// <summary>
    /// The same text with every comment, character literal and string literal replaced by spaces (the copy keeps every
    /// index, so a scan of it can slice the original's positions). It is what lets the exemption above see a
    /// constructor's arguments rather than a neighbour's: a <c>// ZipArchiveMode.Create</c> or a
    /// <c>"ZipArchiveMode.Create"</c> is trivia, not an argument.
    /// </summary>
    /// <param name="text">The source text.</param>
    private static string StripTrivia(string text)
    {
        var stripped = text.ToCharArray();
        var index = 0;

        while (index < text.Length)
        {
            if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] != '\n')
                {
                    Blank(stripped, index++);
                }

                continue;
            }

            if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                Blank(stripped, index++);
                Blank(stripped, index++);

                while (index + 1 < text.Length && !(text[index] == '*' && text[index + 1] == '/'))
                {
                    Blank(stripped, index++);
                }

                // The closing `*/` (a comment that never closes blanks to the end of the file).
                Blank(stripped, index);
                Blank(stripped, index + 1);
                index = Math.Min(index + 2, text.Length);

                continue;
            }

            if (text[index] == '\'')
            {
                index = CloseCharLiteral(text, stripped, index);

                continue;
            }

            if (TryOpenLiteral(text, index, out var verbatim, out var quoteAt, out var quotes))
            {
                // The prefix characters ($, @) are blanked with the literal: they are part of it, not of the code.
                index = quotes >= 3
                    ? CloseRawLiteral(text, stripped, index, quoteAt, quotes)
                    : CloseQuotedLiteral(text, stripped, index, quoteAt, verbatim);

                continue;
            }

            index++;
        }

        return new string(stripped);
    }

    /// <summary>Blanks one <c>'x'</c> literal, escapes included.</summary>
    /// <param name="text">The source.</param>
    /// <param name="stripped">The blanked copy.</param>
    /// <param name="start">The index of the opening quote.</param>
    /// <returns>The index just past the closing quote.</returns>
    private static int CloseCharLiteral(string text, char[] stripped, int start)
    {
        var at = start + 1;
        while (at < text.Length && text[at] != '\'')
        {
            if (text[at] == '\\' && at + 1 < text.Length)
            {
                at++;
            }

            at++;
        }

        var end = Math.Min(at + 1, text.Length);
        Blank(stripped, start, end - 1);

        return end;
    }

    /// <summary>
    /// True when a <c>$</c>/<c>@</c> prefix run at <paramref name="index"/> opens a string literal, answering where its
    /// opening run of quotes starts and how long that run is (three or more quotes is a raw literal).
    /// </summary>
    /// <param name="text">The source.</param>
    /// <param name="index">The index to test.</param>
    /// <param name="verbatim">True for an <c>@"…"</c> literal.</param>
    /// <param name="quoteAt">The index of the first of the opening quotes.</param>
    /// <param name="quotes">The number of quotes in the opening run.</param>
    private static bool TryOpenLiteral(string text, int index, out bool verbatim, out int quoteAt, out int quotes)
    {
        verbatim = false;
        quoteAt = index;
        quotes = 0;

        var at = index;
        while (at < text.Length && (text[at] == '$' || text[at] == '@'))
        {
            verbatim |= text[at] == '@';
            at++;
        }

        // Only a prefix that is immediately followed by the quote opens a literal; `@this` and a bare `$` are code.
        if (at >= text.Length || text[at] != '"')
        {
            return false;
        }

        quoteAt = at;

        while (quoteAt + quotes < text.Length && text[quoteAt + quotes] == '"')
        {
            quotes++;
        }

        return true;
    }

    /// <summary>Blanks one quoted literal (normal or verbatim), escapes and doubled quotes included.</summary>
    /// <param name="text">The source.</param>
    /// <param name="stripped">The blanked copy.</param>
    /// <param name="start">The index of the literal's first character (its prefix, if any).</param>
    /// <param name="quoteAt">The index of the opening quote.</param>
    /// <param name="verbatim">True for a verbatim literal (<c>""</c> escapes a quote, a backslash is literal).</param>
    /// <returns>The index just past the closing quote.</returns>
    private static int CloseQuotedLiteral(string text, char[] stripped, int start, int quoteAt, bool verbatim)
    {
        var at = quoteAt + 1;

        while (at < text.Length)
        {
            if (verbatim && text[at] == '"')
            {
                if (at + 1 < text.Length && text[at + 1] == '"')
                {
                    at += 2;

                    continue;
                }

                Blank(stripped, start, at);

                return at + 1;
            }

            if (!verbatim)
            {
                if (text[at] == '\\')
                {
                    at += 2;

                    continue;
                }

                if (text[at] == '"')
                {
                    Blank(stripped, start, at);

                    return at + 1;
                }
            }

            at++;
        }

        Blank(stripped, start, text.Length - 1);

        return text.Length;
    }

    /// <summary>Blanks one raw string literal (<c>"""…"""</c>, three or more quotes on each side).</summary>
    /// <param name="text">The source.</param>
    /// <param name="stripped">The blanked copy.</param>
    /// <param name="start">The index of the literal's first character (its prefix, if any).</param>
    /// <param name="quoteAt">The index of the opening run of quotes.</param>
    /// <param name="run">The number of quotes in the opening run.</param>
    /// <returns>The index just past the closing run.</returns>
    private static int CloseRawLiteral(string text, char[] stripped, int start, int quoteAt, int run)
    {
        var at = quoteAt + run;

        while (at + run <= text.Length)
        {
            var closes = true;
            for (var offset = 0; offset < run; offset++)
            {
                if (text[at + offset] != '"')
                {
                    closes = false;

                    break;
                }
            }

            if (closes)
            {
                Blank(stripped, start, at + run - 1);

                return at + run;
            }

            at++;
        }

        Blank(stripped, start, text.Length - 1);

        return text.Length;
    }

    /// <summary>Replaces one character of the copy with a space (never a newline, so line positions stay readable).</summary>
    /// <param name="stripped">The blanked copy.</param>
    /// <param name="index">The index to blank.</param>
    private static void Blank(char[] stripped, int index)
    {
        if (index >= 0 && index < stripped.Length && stripped[index] != '\n')
        {
            stripped[index] = ' ';
        }
    }

    /// <summary>Blanks an inclusive range of the copy.</summary>
    /// <param name="stripped">The blanked copy.</param>
    /// <param name="from">The first index.</param>
    /// <param name="to">The last index.</param>
    private static void Blank(char[] stripped, int from, int to)
    {
        for (var index = Math.Max(from, 0); index <= to && index < stripped.Length; index++)
        {
            Blank(stripped, index);
        }
    }

    /// <summary>
    /// One violation per construct found outside the profile, naming the file and the construct. Paths are
    /// repository-relative with '/'.
    /// </summary>
    /// <param name="files">The source files to scan (path + text).</param>
    /// <param name="profileDirectory">The directory allowed to construct a reader.</param>
    /// <param name="artifactModuleDirectory">The directory whose JSON reads must use the profile.</param>
    /// <returns>The violations, ordered by path.</returns>
    public static IReadOnlyList<string> FindViolations(
        IEnumerable<(string Path, string Text)> files,
        string profileDirectory,
        string artifactModuleDirectory)
    {
        var violations = new List<string>();

        foreach (var (path, text) in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            var inProfile = path.StartsWith(profileDirectory, StringComparison.Ordinal);
            if (inProfile)
            {
                continue;
            }

            foreach (var construct in RepositoryWideConstructs)
            {
                if (ContainsParse(text, construct))
                {
                    violations.Add(
                        $"{path} constructs {construct}: the untrusted-parser profile in {profileDirectory} is the "
                        + "only place an XML or archive reader may be constructed (06.3.1 §11, 00 §5.7 S4/S5)");
                }
            }

            if (!path.StartsWith(artifactModuleDirectory, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var construct in ArtifactModuleConstructs)
            {
                if (text.Contains(construct, StringComparison.Ordinal))
                {
                    violations.Add(
                        $"{path} constructs {construct}: inside the artifact module, JSON bytes are parsed by the "
                        + $"profile in {profileDirectory} (06.3.1 §11, 00 §5.7 S5)");
                }
            }
        }

        return violations;
    }
}
