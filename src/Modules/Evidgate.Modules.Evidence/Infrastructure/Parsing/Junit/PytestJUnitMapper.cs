using System.Globalization;
using System.Xml;
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
    private const string TestsAttribute = "tests";
    private const string FailuresAttribute = "failures";
    private const string ErrorsAttribute = "errors";
    private const string SkippedAttribute = "skipped";

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
    public IJUnitRecordMapper OpenRecordMapper(JUnitMapContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new JUnitRecordMapper(maxReportBytes + ChildXmlRecords.DocumentSlackBytes, context);
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

            var localName = reader.LocalName;
            if (reader.Depth == 0)
            {
                EnsureKnownRoot(localName);
                counters.ReadRoot(localName, SuiteAttributes.Read(reader));
            }
            else if (reader.Depth == 1)
            {
                counters.ReadSuite(localName, SuiteAttributes.Read(reader));
            }

            if (!string.Equals(localName, TestCaseElement, StringComparison.Ordinal))
            {
                continue;
            }

            testcases++;
            MapTestCase(
                TestCaseAttributes.Read(reader),
                ReadStatus(reader),
                testcases,
                context,
                resolved,
                cases,
                discrepancies);
        }

        var declared = counters.Result();
        if (declared.Tests is { } claimed && claimed != testcases)
        {
            discrepancies.Add(JUnitDiscrepancies.CountMismatch);
        }

        return new JUnitMapResult(cases, declared, discrepancies);
    }

    /// <summary>True when the root element's local name is one this dialect reads.</summary>
    /// <param name="localName">The root element's local name, as a name-aware reader reported it.</param>
    /// <returns>True for <c>testsuite</c> and <c>testsuites</c>.</returns>
    internal static bool IsKnownRoot(string localName) =>
        string.Equals(localName, TestSuiteElement, StringComparison.Ordinal)
        || string.Equals(localName, TestSuitesElement, StringComparison.Ordinal);

    /// <summary>
    /// The refusal a document whose root element is neither a <c>testsuite</c> nor a <c>testsuites</c> earns: this
    /// dialect was only agreed to read that shape, and mapping the testcases of any other root would claim evidence
    /// the producer never declared. Only depth 0 decides, so a suite under a foreign root cannot rescue the document.
    /// </summary>
    /// <returns>The refusal, with a message that never echoes the report back.</returns>
    internal static JUnitParseRefusedException RootRefusal() =>
        new(RootRefusalReason, "the report root is not a testsuite or testsuites element");

    /// <summary>
    /// Refuses a document whose root element is neither a <c>testsuite</c> nor a <c>testsuites</c>.
    /// </summary>
    /// <param name="localName">The document element's local name.</param>
    /// <exception cref="JUnitParseRefusedException">The root's local name is neither of the two accepted ones.</exception>
    private static void EnsureKnownRoot(string localName)
    {
        if (!IsKnownRoot(localName))
        {
            throw RootRefusal();
        }
    }

    /// <summary>
    /// Maps one <c>testcase</c> whose identity, duration and status are already known, so the streaming walk and the
    /// record sink share one copy of the rule. A testcase that cannot resolve emits nothing and records why. A
    /// testcase that repeats an already-mapped <c>execution_key</c> records <c>duplicate_case</c> and emits no second
    /// event — one execution owns one event — but a more severe repeat still decides the result: the kept case keeps
    /// its position, its document sequence and its identity echo, and takes the repeat's raw and normalised status.
    /// </summary>
    /// <param name="attributes">The testcase's own attributes, as the producer wrote them.</param>
    /// <param name="status">The raw and normalised status the testcase's outcome elements decided.</param>
    /// <param name="sequence">The testcase's 1-based document position.</param>
    /// <param name="context">The caller's identity resolution.</param>
    /// <param name="resolved">The execution keys mapped so far, with their index in <paramref name="cases"/>.</param>
    /// <param name="cases">The mapped cases, in document order.</param>
    /// <param name="discrepancies">The discrepancies, in document order.</param>
    internal static void MapTestCase(
        TestCaseAttributes attributes,
        (string Raw, string Normalized) status,
        int sequence,
        JUnitMapContext context,
        Dictionary<string, int> resolved,
        List<MappedCase> cases,
        List<string> discrepancies)
    {
        var duration = JUnitDuration.ToMilliseconds(attributes.Time);
        var executionKey = context.ResolveExecutionKey(attributes.Classname, attributes.Name);
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
                status.Raw,
                status.Normalized,
                duration,
                BoundedEcho(attributes.Classname),
                BoundedEcho(attributes.Name),
                sequence));
            return;
        }

        discrepancies.Add(JUnitDiscrepancies.DuplicateCase);

        if (PytestStatus.IsMoreSevere(status.Normalized, cases[kept].Normalized))
        {
            cases[kept] = cases[kept] with { RawStatus = status.Raw, Normalized = status.Normalized };
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
    /// Finds one attribute by the exact qualified name the producer wrote, which is what
    /// <see cref="XmlReader.GetAttribute(string)"/> matched: <c>name</c> never matched <c>p:name</c>.
    /// </summary>
    /// <param name="attributes">A start record's attribute pairs, or <see langword="null"/>.</param>
    /// <param name="name">The exact qualified attribute name.</param>
    /// <returns>The value, or <see langword="null"/> when the element carries no such attribute.</returns>
    internal static string? Attribute(List<(string Key, string Value)>? attributes, string name)
    {
        if (attributes is null)
        {
            return null;
        }

        foreach (var (key, value) in attributes)
        {
            if (string.Equals(key, name, StringComparison.Ordinal))
            {
                return value;
            }
        }

        return null;
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

    /// <summary>A <c>testcase</c>'s own attributes, read from either a reader or a record's pairs.</summary>
    /// <param name="Classname">The raw <c>classname</c>, empty when absent.</param>
    /// <param name="Name">The raw <c>name</c>, empty when absent.</param>
    /// <param name="Time">The raw <c>time</c>, or <see langword="null"/> when absent.</param>
    internal readonly record struct TestCaseAttributes(string Classname, string Name, string? Time)
    {
        public static TestCaseAttributes Read(XmlReader reader) =>
            new(
                reader.GetAttribute(ClassnameAttribute) ?? string.Empty,
                reader.GetAttribute(NameAttribute) ?? string.Empty,
                reader.GetAttribute(TimeAttribute));

        public static TestCaseAttributes Of(List<(string Key, string Value)>? attributes) =>
            new(
                Attribute(attributes, ClassnameAttribute) ?? string.Empty,
                Attribute(attributes, NameAttribute) ?? string.Empty,
                Attribute(attributes, TimeAttribute));
    }

    /// <summary>One element's four declared counter attributes, raw as the producer wrote them.</summary>
    /// <param name="Tests">The raw <c>tests</c>, or <see langword="null"/> when absent.</param>
    /// <param name="Failures">The raw <c>failures</c>, or <see langword="null"/> when absent.</param>
    /// <param name="Errors">The raw <c>errors</c>, or <see langword="null"/> when absent.</param>
    /// <param name="Skipped">The raw <c>skipped</c>, or <see langword="null"/> when absent.</param>
    internal readonly record struct SuiteAttributes(string? Tests, string? Failures, string? Errors, string? Skipped)
    {
        public static SuiteAttributes Read(XmlReader reader) =>
            new(
                reader.GetAttribute(TestsAttribute),
                reader.GetAttribute(FailuresAttribute),
                reader.GetAttribute(ErrorsAttribute),
                reader.GetAttribute(SkippedAttribute));

        public static SuiteAttributes Of(List<(string Key, string Value)>? attributes) =>
            new(
                Attribute(attributes, TestsAttribute),
                Attribute(attributes, FailuresAttribute),
                Attribute(attributes, ErrorsAttribute),
                Attribute(attributes, SkippedAttribute));
    }

    /// <summary>
    /// Collects the declared counters: the root element's own attributes, or — for a <c>testsuites</c> root that
    /// declares none — the sum of its direct <c>testsuite</c> children, which is how pytest shards a run.
    /// </summary>
    internal sealed class CounterReader
    {
        private SuiteCounters? root;
        private SuiteCounters? summed;
        private bool rootIsSuites;

        public void ReadRoot(string localName, SuiteAttributes attributes)
        {
            rootIsSuites = string.Equals(localName, TestSuitesElement, StringComparison.Ordinal);
            root = SuiteCounters.Read(attributes);
        }

        /// <summary>Adds one direct child of a <c>testsuites</c> root; any other element contributes nothing.</summary>
        /// <param name="localName">The child element's local name.</param>
        /// <param name="attributes">The child element's counter attributes.</param>
        public void ReadSuite(string localName, SuiteAttributes attributes)
        {
            if (!rootIsSuites || !string.Equals(localName, TestSuiteElement, StringComparison.Ordinal))
            {
                return;
            }

            summed = SuiteCounters.Add(summed, SuiteCounters.Read(attributes));
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
        public static SuiteCounters Read(SuiteAttributes attributes) =>
            new(
                Counter(attributes.Tests),
                Counter(attributes.Failures),
                Counter(attributes.Errors),
                Counter(attributes.Skipped));

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

        private static int? Counter(string? text) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
                ? value
                : null;
    }
}
