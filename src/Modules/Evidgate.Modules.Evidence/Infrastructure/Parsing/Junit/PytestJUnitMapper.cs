using System.Globalization;
using System.Xml;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Evidgate.Modules.Evidence.Contracts;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;

/// <summary>
/// The pytest <c>junitxml</c> dialect: one <c>testcase</c> becomes one mapped case whose identity is the run's
/// own <c>execution_key</c>, resolved by the caller from the raw <c>classname</c>/<c>name</c> pair. A testcase the
/// caller cannot resolve emits no case at all — an execution key is never guessed, because an event under an
/// invented key would claim evidence for a case nobody planned.
/// <para>
/// The document is walked once, streaming, through the parser profile: the whole report is never materialised as
/// a DOM, and every guard (DTD, depth, bytes, entry count, timeout) is the profile's, not a second copy.
/// Sequences are 1-based document positions, so the same bytes always produce the same identities and order.
/// </para>
/// </summary>
internal sealed class PytestJUnitMapper : IJUnitMapper
{
    /// <summary>The <c>report_parser.parser_id</c> of the pytest dialect.</summary>
    public const string PytestParserId = "junit-pytest";

    /// <summary>The dialect version this mapper implements; a behaviour change is a new version, never a rewrite.</summary>
    public const string PytestVersion = "1.0.0";

    /// <summary>
    /// The most characters of a testcase's own <c>classname</c>/<c>name</c> this mapper echoes into a case. The
    /// attribute is the producer's text and can be any length the report allows, so the echo — which travels into
    /// the event's metadata — is bounded; the report artifact keeps the raw document, and resolution still reads the
    /// attribute as written. The cap sits below the resolver's own identity bound so a long but resolvable identity is
    /// echoed bounded rather than refused.
    /// </summary>
    public const int MaxEchoedIdentityLength = 256;

    /// <summary>What replaces the tail of an echoed identity string longer than <see cref="MaxEchoedIdentityLength"/>.</summary>
    public const string TruncationMarker = "[truncated]";

    /// <summary>
    /// The reason a document whose root is neither a <c>testsuite</c> nor a <c>testsuites</c> is refused with. It
    /// reuses the profile's own word for content it cannot read as a document, so the refusal coins no new code
    /// word and the stored reason keeps the column's shape.
    /// </summary>
    public const string RootRefusalReason = ParsingFailureReason.Malformed;

    private const string TestCaseElement = "testcase";
    private const string TestSuiteElement = "testsuite";
    private const string TestSuitesElement = "testsuites";
    private const string ClassnameAttribute = "classname";
    private const string NameAttribute = "name";
    private const string TimeAttribute = "time";

    /// <summary>Room over the report cap for the names and quoting a rebuilt document spells differently from the source.</summary>
    private const long DocumentSlackBytes = 1024 * 1024;

    private readonly IParsing parsing;
    private readonly long maxReportBytes;

    /// <summary>The mapper over the profile's hard report ceiling (the production registration).</summary>
    /// <param name="parsing">The module's parser profile.</param>
    public PytestJUnitMapper(IParsing parsing)
        : this(parsing, ParsingLimits.MaxReportBytes)
    {
    }

    /// <summary>
    /// Builds the production mapper over the profile. It exists so the composition root instantiates the mapper
    /// in this assembly — the DI registration itself lives in the module, not here.
    /// </summary>
    /// <param name="parsing">The module's parser profile.</param>
    /// <returns>The mapper with the profile's default report ceiling.</returns>
    public static PytestJUnitMapper Create(IParsing parsing) => new(parsing);

    /// <summary>The mapper with an explicit byte cap (a caller that already holds the kind policy's number).</summary>
    /// <param name="parsing">The module's parser profile.</param>
    /// <param name="maxReportBytes">The most bytes one report may carry (positive).</param>
    public PytestJUnitMapper(IParsing parsing, long maxReportBytes)
    {
        ArgumentNullException.ThrowIfNull(parsing);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxReportBytes);

        this.parsing = parsing;
        this.maxReportBytes = maxReportBytes;
    }

    /// <inheritdoc />
    public string ParserId => PytestParserId;

    /// <inheritdoc />
    public string Version => PytestVersion;

    /// <inheritdoc />
    public JUnitMapResult Map(Stream xml, JUnitMapContext context)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(context);

        // The contract of this seam carries no cancellation token, so a caller cancellation cannot reach the
        // profile; the profile's own wall-clock timeout is what bounds one walk.
        var parsed = parsing.ParseXmlReport(xml, maxReportBytes, CancellationToken.None);
        if (parsed.Error is { } failure)
        {
            throw JUnitParseRefusedException.From(failure);
        }

        using var reader = parsed.Value.OpenReader();
        return Walk(reader, context);
    }

    /// <inheritdoc />
    public JUnitMapResult Map(IReadOnlyList<ParseRecord> records, JUnitMapContext context)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(context);

        using var reader = ChildXmlRecords.OpenReader(records, maxReportBytes + DocumentSlackBytes);

        try
        {
            return Walk(reader, context);
        }
        catch (XmlException)
        {
            // The reader is lazy: a stream that rebuilt into a document the reader refuses fails here, and it is the
            // child's protocol that broke, not a property of the report.
            throw ChildXmlRecords.Crashed();
        }
    }

    private static JUnitMapResult Walk(XmlReader reader, JUnitMapContext context)
    {
        var cases = new List<MappedCase>();
        var discrepancies = new List<string>();
        var resolved = new Dictionary<string, int>(StringComparer.Ordinal);
        var counters = new CounterReader();
        var testcases = 0;

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.Depth == 0)
            {
                EnsureKnownRoot(reader);
                counters.ReadRoot(reader);
            }
            else
            {
                counters.ReadSuite(reader);
            }

            if (!string.Equals(reader.LocalName, TestCaseElement, StringComparison.Ordinal))
            {
                continue;
            }

            testcases++;
            MapTestCase(reader, testcases, context, resolved, cases, discrepancies);
        }

        var declared = counters.Result();
        if (declared.Tests is { } claimed && claimed != testcases)
        {
            discrepancies.Add(JUnitDiscrepancies.CountMismatch);
        }

        return new JUnitMapResult(cases, declared, discrepancies);
    }

    /// <summary>
    /// Refuses a document whose root element is neither a <c>testsuite</c> nor a <c>testsuites</c>: this dialect was
    /// only agreed to read that shape, and mapping the testcases of any other root would claim evidence the producer
    /// never declared. Only depth 0 decides, so a suite nested under a foreign root cannot rescue the document.
    /// </summary>
    /// <param name="reader">The reader, positioned on the document element.</param>
    /// <exception cref="JUnitParseRefusedException">The root's local name is neither of the two accepted ones.</exception>
    private static void EnsureKnownRoot(XmlReader reader)
    {
        var localName = reader.LocalName;
        if (string.Equals(localName, TestSuiteElement, StringComparison.Ordinal)
            || string.Equals(localName, TestSuitesElement, StringComparison.Ordinal))
        {
            return;
        }

        // The message stays generic: the root's name is producer text, and a refusal never echoes the report back.
        throw new JUnitParseRefusedException(
            RootRefusalReason,
            "the report root is not a testsuite or testsuites element");
    }

    /// <summary>
    /// Maps one <c>testcase</c> and consumes its subtree, so an outcome element's text is never mistaken for a
    /// sibling testcase. A testcase that cannot resolve emits nothing and records why. A testcase that repeats an
    /// already-mapped <c>execution_key</c> records <c>duplicate_case</c> and emits no second event — one execution
    /// owns one event — but a more severe repeat still decides the result: the kept case keeps its position, its
    /// document sequence and its identity echo, and takes the repeat's raw and normalised status.
    /// </summary>
    private static void MapTestCase(
        XmlReader reader,
        int sequence,
        JUnitMapContext context,
        Dictionary<string, int> resolved,
        List<MappedCase> cases,
        List<string> discrepancies)
    {
        var classname = reader.GetAttribute(ClassnameAttribute) ?? string.Empty;
        var name = reader.GetAttribute(NameAttribute) ?? string.Empty;
        var duration = JUnitDuration.ToMilliseconds(reader.GetAttribute(TimeAttribute));
        var (rawStatus, normalized) = ReadStatus(reader);

        var executionKey = context.ResolveExecutionKey(classname, name);
        if (string.IsNullOrWhiteSpace(executionKey))
        {
            discrepancies.Add(JUnitDiscrepancies.UnmappedCase);
            return;
        }

        if (!resolved.TryGetValue(executionKey, out var kept))
        {
            // The echo is bounded here, after resolution: the resolver judged the attribute as written.
            resolved.Add(executionKey, cases.Count);
            cases.Add(new MappedCase(
                executionKey,
                rawStatus,
                normalized,
                duration,
                BoundedEcho(classname),
                BoundedEcho(name),
                sequence));
            return;
        }

        discrepancies.Add(JUnitDiscrepancies.DuplicateCase);

        if (PytestStatus.IsMoreSevere(normalized, cases[kept].Normalized))
        {
            cases[kept] = cases[kept] with { RawStatus = rawStatus, Normalized = normalized };
        }
    }

    /// <summary>
    /// Bounds an identity string for the event's metadata: the leading characters plus
    /// <see cref="TruncationMarker"/> when it is over <see cref="MaxEchoedIdentityLength"/>, never split mid-pair.
    /// </summary>
    /// <param name="value">The raw <c>classname</c> or <c>name</c> attribute.</param>
    /// <returns>The value, or its bounded prefix with the truncation marker.</returns>
    internal static string BoundedEcho(string value)
    {
        if (value.Length <= MaxEchoedIdentityLength)
        {
            return value;
        }

        var length = MaxEchoedIdentityLength - TruncationMarker.Length;
        if (char.IsHighSurrogate(value[length - 1]))
        {
            // A cut between the halves of a surrogate pair would leave text no encoder accepts.
            length--;
        }

        return string.Concat(value.AsSpan(0, length), TruncationMarker.AsSpan());
    }

    /// <summary>
    /// Reads one testcase's outcome elements and leaves the reader on its end tag. Only direct children count:
    /// an outcome word that appears inside a failure's stack trace text is not a status.
    /// </summary>
    private static (string Raw, string Normalized) ReadStatus(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            return PytestStatus.Select(false, false, false);
        }

        var depth = reader.Depth;
        var sawFailure = false;
        var sawError = false;
        var sawSkipped = false;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element || reader.Depth != depth + 1)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case PytestStatus.Failure:
                    sawFailure = true;
                    break;
                case PytestStatus.Error:
                    sawError = true;
                    break;
                case PytestStatus.Skipped:
                    sawSkipped = true;
                    break;
            }
        }

        return PytestStatus.Select(sawFailure, sawError, sawSkipped);
    }

    /// <summary>
    /// Collects the declared counters: the root element's own attributes, or — for a <c>testsuites</c> root that
    /// declares none — the sum of its direct <c>testsuite</c> children, which is how pytest shards a run.
    /// </summary>
    private sealed class CounterReader
    {
        private SuiteCounters? root;
        private SuiteCounters? summed;
        private bool rootIsSuites;

        public void ReadRoot(XmlReader reader)
        {
            rootIsSuites = string.Equals(reader.LocalName, TestSuitesElement, StringComparison.Ordinal);
            root = SuiteCounters.Read(reader);
        }

        public void ReadSuite(XmlReader reader)
        {
            if (!rootIsSuites
                || reader.Depth != 1
                || !string.Equals(reader.LocalName, TestSuiteElement, StringComparison.Ordinal))
            {
                return;
            }

            summed = SuiteCounters.Add(summed, SuiteCounters.Read(reader));
        }

        public JUnitDeclaredCounters Result() =>
            new(
                root?.Tests ?? summed?.Tests,
                root?.Failures ?? summed?.Failures,
                root?.Errors ?? summed?.Errors,
                root?.Skipped ?? summed?.Skipped);
    }

    /// <summary>One element's four declared counters; a missing or unusable attribute stays null.</summary>
    private readonly record struct SuiteCounters(int? Tests, int? Failures, int? Errors, int? Skipped)
    {
        public static SuiteCounters Read(XmlReader reader) =>
            new(
                Counter(reader, "tests"),
                Counter(reader, "failures"),
                Counter(reader, "errors"),
                Counter(reader, "skipped"));

        public static SuiteCounters Add(SuiteCounters? left, SuiteCounters right) =>
            left is null
                ? right
                : new(
                    Sum(left.Value.Tests, right.Tests),
                    Sum(left.Value.Failures, right.Failures),
                    Sum(left.Value.Errors, right.Errors),
                    Sum(left.Value.Skipped, right.Skipped));

        private static int? Sum(int? left, int? right) =>
            left is null
                ? right
                : right is null
                    ? left
                    : (int)Math.Min(int.MaxValue, (long)left.Value + right.Value);

        private static int? Counter(XmlReader reader, string attribute) =>
            int.TryParse(reader.GetAttribute(attribute), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value >= 0
                ? value
                : null;
    }
}
