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
/// nothing in this repository today, so the rule forbids them everywhere outside the profile and the parser child's
/// entry point (<c>src/Evidgate.Parser/Program.cs</c>, 06.3.6 AC1: the console process that runs the profile on
/// untrusted bytes, which links the profile sources instead of copying them) — the entry point and not the whole
/// directory, so a second file added to that project cannot become a second parse path unannounced;
/// <c>JsonDocument.Parse</c> (and the other <c>JsonDocument</c>/<c>JsonNode</c>/<c>JsonSerializer</c>/
/// <c>Utf8JsonReader</c> entry points) is also the normal way to read a request body or a stored row
/// (Policy, Orchestrator, Catalog, Contracts.Validation all do it), so the rule scopes it to the module
/// that owns artifact bytes.
/// </para>
/// </summary>
public static class ParserConstructionRules
{
    /// <summary>The profile directory allowed to construct a reader for untrusted artifact bytes (06.3.1).</summary>
    public const string ProfileDirectory = "src/Modules/Evidgate.Modules.Evidence/Infrastructure/Parsing/";

    /// <summary>
    /// The second allowed place (06.3.6 AC1): the console child's entry point, the one file that runs the profile on
    /// untrusted bytes. It links the profile sources rather than copying them, so a reader constructed there is still
    /// the profile's reader. Deliberately one named file and not <c>src/Evidgate.Parser/</c>: the rest of that project
    /// only chooses the arm, caps the stdin read and streams the result, so a second file there that constructs a
    /// reader is a second parse path and must fail the rule.
    /// </summary>
    public const string ParserChildEntryPoint = "src/Evidgate.Parser/Program.cs";

    /// <summary>
    /// Constructs that are forbidden even in that entry point: the profile's report handle (<c>ParsedXmlReport</c>) is
    /// built by the profile's own closed reader factory and handed to the caller (06.3.1-T1), so the child receives
    /// it and never constructs one — a <c>new ParsedXmlReport(</c> there would be a second factory outside the profile,
    /// which is exactly what the internal constructor exists to prevent.
    /// </summary>
    public static readonly IReadOnlyList<string> ParserChildEntryPointConstructs =
    [
        "new ParsedXmlReport(",
    ];

    /// <summary>The module that owns artifacts: inside it, the JSON reader is the profile's too.</summary>
    public const string ArtifactModuleDirectory = "src/Modules/Evidgate.Modules.Evidence/";

    /// <summary>
    /// The only tree that is scanned: a test may build its own reader to prove the profile's behaviour, and no test
    /// is shipped, so a path outside <c>src/</c> is not a production parse path at all.
    /// </summary>
    public const string SourceRoot = "src/";

    /// <summary>Constructs forbidden everywhere outside the profile and the child's entry point: the untrusted XML and archive readers.</summary>
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
    /// <para>
    /// An interpolation hole is code, not text. In <c>$"{File.ReadAllText(path)}"</c> the hole
    /// <c>{File.ReadAllText(path)}</c> stays in the copy, because blanking it would let a real file read — or a real
    /// <c>XmlReader.Create</c>, or a bare <c>HttpClient</c> — pass as a string. Only the literal segments are blanked:
    /// the prefix and the quotes, the text between the holes, an escaped <c>{{</c>/<c>}}</c>, and, inside a hole, the
    /// strings, character literals and comments the expression itself contains, which the same scan finds one level
    /// down (<c>$"{(flag ? "x" : "y")}"</c> keeps the ternary and blanks its two strings). A raw literal
    /// (<c>$"""…"""</c>) works the same way. A hole's own braces are text like the text around them, while the braces
    /// the expression itself contains stay and are balanced — the scan stops on the <c>}</c> at depth zero — so
    /// <see cref="Arguments"/> still splits an argument list at the right commas.
    /// </para>
    /// </summary>
    /// <param name="text">The source text.</param>
    internal static string StripTrivia(string text)
    {
        var stripped = text.ToCharArray();
        var index = 0;

        ScanCode(text, stripped, ref index, inHole: false);

        return new string(stripped);
    }

    /// <summary>
    /// Blanks the trivia of the code starting at <paramref name="index"/>: comments, character literals and string
    /// literals. Outside a hole the scan runs to the end of the file; inside one it returns with
    /// <paramref name="index"/> on the <c>}</c> that closes the hole (the brace at depth zero), so the literal scan
    /// that called it blanks that brace and carries on with the literal text.
    /// </summary>
    /// <param name="text">The source.</param>
    /// <param name="stripped">The blanked copy.</param>
    /// <param name="index">The index to start at; on return, where the scan stopped.</param>
    /// <param name="inHole">True when scanning the expression inside an interpolation hole.</param>
    private static void ScanCode(string text, char[] stripped, ref int index, bool inHole)
    {
        var depth = 0;

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

            if (TryOpenLiteral(text, index, out var literal))
            {
                index = CloseLiteral(text, stripped, index, literal);

                continue;
            }

            if (inHole)
            {
                if (text[index] == '{')
                {
                    depth++;
                }
                else if (text[index] == '}')
                {
                    if (depth == 0)
                    {
                        return;
                    }

                    depth--;
                }
            }

            index++;
        }
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
    /// One string literal's shape: where it starts, how many quotes open it, and whether it interpolates.
    /// </summary>
    /// <param name="Start">The index of the literal's first character (its prefix, if any).</param>
    /// <param name="QuoteAt">The index of the first of the opening quotes.</param>
    /// <param name="Quotes">The number of quotes in the opening run (three or more is a raw literal).</param>
    /// <param name="Dollars">The number of <c>$</c> prefixes; a hole opens with exactly this many braces.</param>
    /// <param name="Verbatim">True for an <c>@"…"</c> literal.</param>
    private readonly record struct Literal(int Start, int QuoteAt, int Quotes, int Dollars, bool Verbatim)
    {
        /// <summary>True when the literal has holes, whose expressions are code and not text.</summary>
        public bool Interpolated => Dollars > 0;
    }

    /// <summary>
    /// True when a <c>$</c>/<c>@</c> prefix run at <paramref name="index"/> opens a string literal, answering where it
    /// starts and what shape it has: three or more quotes is a raw literal, and one or more <c>$</c> makes it
    /// interpolated.
    /// </summary>
    /// <param name="text">The source.</param>
    /// <param name="index">The index to test.</param>
    /// <param name="literal">The literal's shape when this returns true.</param>
    private static bool TryOpenLiteral(string text, int index, out Literal literal)
    {
        var verbatim = false;
        var dollars = 0;

        var at = index;
        while (at < text.Length && (text[at] == '$' || text[at] == '@'))
        {
            verbatim |= text[at] == '@';

            if (text[at] == '$')
            {
                dollars++;
            }

            at++;
        }

        // Only a prefix that is immediately followed by the quote opens a literal; `@this` and a bare `$` are code.
        if (at >= text.Length || text[at] != '"')
        {
            literal = default;

            return false;
        }

        var quotes = 0;
        while (at + quotes < text.Length && text[at + quotes] == '"')
        {
            quotes++;
        }

        literal = new Literal(index, at, quotes, dollars, verbatim);

        return true;
    }

    /// <summary>Blanks one string literal, dispatching on the quote run: three or more quotes is a raw literal.</summary>
    /// <param name="text">The source.</param>
    /// <param name="stripped">The blanked copy.</param>
    /// <param name="start">The index of the literal's first character (its prefix, if any).</param>
    /// <param name="literal">The literal's shape.</param>
    /// <returns>The index just past the literal.</returns>
    private static int CloseLiteral(string text, char[] stripped, int start, Literal literal) =>
        literal.Quotes >= 3
            ? CloseRawLiteral(text, stripped, start, literal)
            : CloseQuotedLiteral(text, stripped, start, literal);

    /// <summary>
    /// Blanks one quoted literal (normal or verbatim), escapes and doubled quotes included, and every literal segment
    /// of an interpolated one; the code inside its holes is left as it is.
    /// </summary>
    /// <param name="text">The source.</param>
    /// <param name="stripped">The blanked copy.</param>
    /// <param name="start">The index of the literal's first character (its prefix, if any).</param>
    /// <param name="literal">The literal's shape.</param>
    /// <returns>The index just past the closing quote.</returns>
    private static int CloseQuotedLiteral(string text, char[] stripped, int start, Literal literal)
    {
        // The prefix characters ($, @) and the opening quote are part of the literal, not of the code.
        Blank(stripped, start, literal.QuoteAt);

        var at = literal.QuoteAt + 1;

        while (at < text.Length)
        {
            var character = text[at];

            if (literal.Verbatim && character == '"')
            {
                if (at + 1 < text.Length && text[at + 1] == '"')
                {
                    Blank(stripped, at);
                    Blank(stripped, at + 1);
                    at += 2;

                    continue;
                }

                Blank(stripped, at);

                return at + 1;
            }

            if (!literal.Verbatim && character == '\\')
            {
                Blank(stripped, at);
                Blank(stripped, at + 1);
                at += 2;

                continue;
            }

            if (!literal.Verbatim && character == '"')
            {
                Blank(stripped, at);

                return at + 1;
            }

            if (literal.Interpolated && (character == '{' || character == '}'))
            {
                at = SkipBraces(text, stripped, at, literal);

                continue;
            }

            Blank(stripped, at);
            at++;
        }

        return text.Length;
    }

    /// <summary>
    /// Blanks one raw string literal (<c>"""…"""</c>, three or more quotes on each side) and every literal segment of
    /// an interpolated one; the code inside its holes is left as it is.
    /// </summary>
    /// <param name="text">The source.</param>
    /// <param name="stripped">The blanked copy.</param>
    /// <param name="start">The index of the literal's first character (its prefix, if any).</param>
    /// <param name="literal">The literal's shape.</param>
    /// <returns>The index just past the closing run.</returns>
    private static int CloseRawLiteral(string text, char[] stripped, int start, Literal literal)
    {
        Blank(stripped, start, literal.QuoteAt + literal.Quotes - 1);

        var at = literal.QuoteAt + literal.Quotes;

        while (at + literal.Quotes <= text.Length)
        {
            if (StartsQuoteRun(text, at, literal.Quotes))
            {
                Blank(stripped, at, at + literal.Quotes - 1);

                return at + literal.Quotes;
            }

            if (literal.Interpolated && (text[at] == '{' || text[at] == '}'))
            {
                at = SkipBraces(text, stripped, at, literal);

                continue;
            }

            Blank(stripped, at);
            at++;
        }

        Blank(stripped, at, text.Length - 1);

        return text.Length;
    }

    /// <summary>
    /// Handles one brace of an interpolated literal: a run of exactly <see cref="Literal.Dollars"/> <c>{</c> opens a
    /// hole, whose expression is scanned as code (and whose closing brace run — <c>N</c> of them — is text again);
    /// every other brace is literal text, which is what an escaped <c>{{</c>/<c>}}</c> is and what a shorter or longer
    /// run means in the language too.
    /// </summary>
    /// <param name="text">The source.</param>
    /// <param name="stripped">The blanked copy.</param>
    /// <param name="at">The index of the brace.</param>
    /// <param name="literal">The literal's shape.</param>
    /// <returns>The index just past the brace run (and past the hole, when the run opened one).</returns>
    private static int SkipBraces(string text, char[] stripped, int at, Literal literal)
    {
        var brace = text[at];
        var run = BraceRun(text, at, brace);

        if (brace != '{' || run != literal.Dollars)
        {
            Blank(stripped, at, at + run - 1);

            return at + run;
        }

        Blank(stripped, at, at + run - 1);
        at += run;
        ScanCode(text, stripped, ref at, inHole: true);

        if (at < text.Length)
        {
            // The hole's closing braces are literal text again. A hole that never closes takes the rest of the file.
            var close = BraceRun(text, at, '}');

            Blank(stripped, at, at + close - 1);
            at += close;
        }

        return at;
    }

    /// <summary>The number of consecutive <paramref name="brace"/> characters starting at <paramref name="at"/>.</summary>
    /// <param name="text">The source.</param>
    /// <param name="at">The first index.</param>
    /// <param name="brace">The brace character.</param>
    private static int BraceRun(string text, int at, char brace)
    {
        var run = 0;

        while (at + run < text.Length && text[at + run] == brace)
        {
            run++;
        }

        return run;
    }

    /// <summary>True when <paramref name="run"/> quotation marks start at <paramref name="at"/>.</summary>
    /// <param name="text">The source.</param>
    /// <param name="at">The first index.</param>
    /// <param name="run">How many quotation marks the run must hold.</param>
    private static bool StartsQuoteRun(string text, int at, int run)
    {
        for (var offset = 0; offset < run; offset++)
        {
            if (at + offset >= text.Length || text[at + offset] != '"')
            {
                return false;
            }
        }

        return true;
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
    /// One violation per construct found outside the profile and the parser child's entry point, naming the file and
    /// the construct. Paths are repository-relative with '/'; anything outside <see cref="SourceRoot"/> is not scanned.
    /// </summary>
    /// <param name="files">The source files to scan (path + text).</param>
    /// <param name="profileDirectory">The directory allowed to construct a reader.</param>
    /// <param name="artifactModuleDirectory">The directory whose JSON reads must use the profile.</param>
    /// <returns>The violations, ordered by path.</returns>
    public static IReadOnlyList<string> FindViolations(
        IEnumerable<(string Path, string Text)> files,
        string profileDirectory,
        string artifactModuleDirectory) =>
        FindViolations(files, profileDirectory, artifactModuleDirectory, ParserChildEntryPoint);

    /// <summary>
    /// One violation per construct found outside <paramref name="profileDirectory"/> and
    /// <paramref name="parserChildEntryPoint"/>, naming the file and the construct. Paths are repository-relative with
    /// '/'; anything outside <see cref="SourceRoot"/> is not scanned. The two allowed places are parameters so the
    /// synthetic cases can prove an exemption without the real tree, and an empty child entry point means "no child".
    /// </summary>
    /// <param name="files">The source files to scan (path + text).</param>
    /// <param name="profileDirectory">The profile directory allowed to construct a reader.</param>
    /// <param name="artifactModuleDirectory">The directory whose JSON reads must use the profile.</param>
    /// <param name="parserChildEntryPoint">
    /// The child's one allowed file (<c>src/Evidgate.Parser/Program.cs</c>), matched exactly: a second file in that
    /// project is not exempt.
    /// </param>
    /// <returns>The violations, ordered by path.</returns>
    public static IReadOnlyList<string> FindViolations(
        IEnumerable<(string Path, string Text)> files,
        string profileDirectory,
        string artifactModuleDirectory,
        string parserChildEntryPoint)
    {
        var violations = new List<string>();
        var child = parserChildEntryPoint;

        foreach (var (path, text) in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            // Only shipped sources are scanned: a test project may build its own reader (06.3.6 §9 scans
            // src/**/*.cs only), so a path outside src/ is not a production parse path.
            if (!path.StartsWith(SourceRoot, StringComparison.Ordinal))
            {
                continue;
            }

            var inProfile = path.StartsWith(profileDirectory, StringComparison.Ordinal);
            if (inProfile)
            {
                continue;
            }

            // 06.3.6 AC1: the child's entry point is the profile's other hand — it links the profile sources and must
            // construct the reader to feed them, so it is the second named allowed place. Only that one file is: the
            // child's other files choose the arm and stream the result, so a reader built in one of them is a second
            // parse path. The report handle is constructed by the profile's factory and never here, not even in the
            // entry point (06.3.1-T1).
            if (!string.IsNullOrEmpty(child) && string.Equals(path, child, StringComparison.Ordinal))
            {
                var entry = StripTrivia(text);

                foreach (var construct in ParserChildEntryPointConstructs)
                {
                    if (entry.Contains(construct, StringComparison.Ordinal))
                    {
                        violations.Add(
                            $"{path} constructs {construct}: the profile in {profileDirectory} builds the report "
                            + "handle and hands it to its caller, so the parser child never constructs one "
                            + "(06.3.1 §11, 06.3.6 AC1)");
                    }
                }

                continue;
            }

            foreach (var construct in RepositoryWideConstructs)
            {
                if (ContainsParse(text, construct))
                {
                    violations.Add(
                        $"{path} constructs {construct}: the untrusted-parser profile in {profileDirectory} and the "
                        + $"parser child's entry point {child} are the only places an XML or archive reader may be "
                        + "constructed (06.3.1 §11, 06.3.6 AC1, 00 §5.7 S4/S5)");
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
