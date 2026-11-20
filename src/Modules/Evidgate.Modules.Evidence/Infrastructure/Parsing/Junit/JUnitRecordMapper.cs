using System.Text.Json;
using System.Xml;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;

/// <summary>
/// The push mapping of one JUnit report: the child's start / text / end records arrive one at a time and the document
/// is never rebuilt, so the mapper's memory is the cases it emits and the element stack, not the report.
/// <para>
/// The element stack carries what a rebuilt document used to hand an <see cref="XmlReader"/>: the qualified names, a
/// namespace scope per element (so an undeclared or reserved prefix is refused rather than silently mapped), and the
/// status flags of an open <c>testcase</c>. The same reconstruction bound is kept: the bytes the rebuild would have
/// written are counted, and a stream that would have rebuilt past the report cap is refused.
/// </para>
/// </summary>
internal sealed class JUnitRecordMapper : IJUnitRecordMapper
{
    private const string TestCaseElement = "testcase";

    private readonly long maxDocumentBytes;
    private readonly JUnitMapContext context;
    private readonly List<Frame> open = [];
    private readonly List<MappedCase> cases = [];
    private readonly List<string> discrepancies = [];
    private readonly Dictionary<string, int> resolved = new(StringComparer.Ordinal);
    private readonly JUnitNamespaceScope namespaceScope = new();
    private readonly PytestJUnitMapper.CounterReader counters = new();

    private long rebuiltBytes;
    private int roots;
    private int testcases;
    private int testcaseDepth = -1;
    private JUnitParseRefusedException? fault;
    private JUnitParseRefusedException? dialectRefusal;
    private string? rejectedReason;

    /// <summary>Creates one single-use sink.</summary>
    /// <param name="maxDocumentBytes">The most the rebuilt document could have taken (positive).</param>
    /// <param name="context">The synthetic producer identity and the caller's execution-key resolution.</param>
    public JUnitRecordMapper(long maxDocumentBytes, JUnitMapContext context)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDocumentBytes);
        ArgumentNullException.ThrowIfNull(context);

        this.maxDocumentBytes = maxDocumentBytes;
        this.context = context;
    }

    /// <inheritdoc />
    public string? RejectedReason => rejectedReason;

    /// <inheritdoc />
    public void Accept(ReadOnlySpan<byte> utf8Json)
    {
        // The rejection sniff precedes everything else: the child's own verdict must survive whatever this mapper
        // makes of the rest of the stream, and one vectorised search dismisses a record that carries no rejection.
        if (rejectedReason is null && ChildRejectionRecord.ReasonOf(utf8Json) is { } reason)
        {
            rejectedReason = reason;
            return;
        }

        if (fault is not null)
        {
            return;
        }

        try
        {
            Consume(ChildXmlRecords.Decode(utf8Json));
        }
        catch (Exception exception) when (exception is JsonException
            or XmlException
            or InvalidOperationException
            or FormatException
            or ArgumentException
            or OverflowException)
        {
            fault = ChildXmlRecords.Crashed();
        }
    }

    /// <inheritdoc />
    public JUnitMapResult Finish()
    {
        if (fault is not null)
        {
            throw fault;
        }

        if (open.Count != 0 || roots != 1)
        {
            throw ChildXmlRecords.Crashed();
        }

        // A root the dialect does not map is refused last: every structural fault above is a crashed child, which
        // outranks a verdict about the report's content.
        if (dialectRefusal is not null)
        {
            throw dialectRefusal;
        }

        var declaredCounters = counters.Result();
        if (declaredCounters.Tests is { } claimed && claimed != testcases)
        {
            discrepancies.Add(JUnitDiscrepancies.CountMismatch);
        }

        return new JUnitMapResult(cases, declaredCounters, discrepancies);
    }

    private void Consume(in XmlRecord record)
    {
        switch (record.Event)
        {
            case XmlRecordEvent.Start:
                Start(record);
                break;
            case XmlRecordEvent.End:
                End(record);
                break;
            default:
                Text(record);
                break;
        }
    }

    private void Start(in XmlRecord record)
    {
        var name = record.Name ?? throw new InvalidOperationException("a start record has no name");
        var (prefix, local) = JUnitNamespaceScope.SplitName(name);

        if (open.Count == 0 && ++roots > 1)
        {
            throw new InvalidOperationException("the child streamed more than one root element");
        }

        if (open.Count >= ChildXmlRecords.MaxRebuiltDepth)
        {
            throw new InvalidOperationException("the child streamed elements nested deeper than the profile admits");
        }

        namespaceScope.Push(record.Attributes);
        namespaceScope.VerifyElementPrefix(prefix);
        namespaceScope.VerifyAttributes(record.Attributes);
        ChargeRebuild(ChildXmlRecords.StartTagBytes(record));

        var depth = open.Count;
        if (depth == 0)
        {
            if (PytestJUnitMapper.IsKnownRoot(local))
            {
                counters.ReadRoot(local, PytestJUnitMapper.SuiteAttributes.Of(record.Attributes));
            }
            else
            {
                // The frame is still pushed, and charged: a foreign root that is unbalanced, deeper than the
                // profile or over the byte budget is a crashed child, so the dialect's refusal must not short-circuit
                // the structural checks the way an early return did.
                dialectRefusal = PytestJUnitMapper.RootRefusal();
            }
        }
        else if (dialectRefusal is null && testcaseDepth < 0 && depth == 1)
        {
            counters.ReadSuite(local, PytestJUnitMapper.SuiteAttributes.Of(record.Attributes));
        }

        var sequence = 0;
        if (dialectRefusal is null && testcaseDepth < 0 && string.Equals(local, TestCaseElement, StringComparison.Ordinal))
        {
            sequence = ++testcases;
            testcaseDepth = depth;
        }
        else if (dialectRefusal is null && testcaseDepth >= 0 && depth == testcaseDepth + 1)
        {
            // Only a testcase's direct children are outcomes: a word inside a stack trace is not a status.
            MarkOutcome(local);
        }

        // Only a testcase carries attributes that are ever read back, and a foreign root maps no cases at all.
        open.Add(new Frame(
            name,
            sequence == 0 ? default : PytestJUnitMapper.TestCaseAttributes.Of(record.Attributes),
            sequence));
    }

    private void End(in XmlRecord record)
    {
        if (open.Count == 0)
        {
            throw new InvalidOperationException("the child streamed an end record with no open element");
        }

        var index = open.Count - 1;
        var frame = open[index];
        if (record.Name is { } name && !string.Equals(name, frame.QualifiedName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("an end record does not close the element it names");
        }

        open.RemoveAt(index);
        namespaceScope.Pop();
        ChargeRebuild(ChildXmlRecords.EndTagBytes(frame.QualifiedName));

        if (frame.Sequence == 0)
        {
            return;
        }

        testcaseDepth = -1;
        PytestJUnitMapper.MapTestCase(
            frame.TestCase,
            PytestStatus.Select(frame.SawFailure, frame.SawError, frame.SawSkipped),
            frame.Sequence,
            context,
            resolved,
            cases,
            discrepancies);
    }

    private static void Text(in XmlRecord record)
    {
        if (!record.HasStringValue)
        {
            throw new InvalidOperationException("the child streamed a text record without its string value");
        }

        // The value token was skipped in place: no mapper reads element text, so it never becomes a string.
    }

    /// <summary>
    /// Charges one tag against the rebuild's budget before it is counted, the way the rebuild refused a record that
    /// would have taken the document past its cap: the total it produced never exceeded the cap.
    /// </summary>
    /// <param name="bytes">The UTF-8 length the rebuild wrote for this tag.</param>
    private void ChargeRebuild(long bytes)
    {
        if (rebuiltBytes + bytes > maxDocumentBytes)
        {
            throw new InvalidOperationException("the record stream would have rebuilt a document past the cap");
        }

        rebuiltBytes += bytes;
    }

    private void MarkOutcome(string localName)
    {
        var frame = open[testcaseDepth];
        switch (localName)
        {
            case PytestStatus.Failure:
                frame.SawFailure = true;
                break;
            case PytestStatus.Error:
                frame.SawError = true;
                break;
            case PytestStatus.Skipped:
                frame.SawSkipped = true;
                break;
            default:
                return;
        }

        open[testcaseDepth] = frame;
    }

    /// <summary>One open element: the name to close, the testcase it may hold, and the outcomes seen under it.</summary>
    private struct Frame
    {
        public Frame(string qualifiedName, PytestJUnitMapper.TestCaseAttributes testCase, int sequence)
        {
            QualifiedName = qualifiedName;
            TestCase = testCase;
            Sequence = sequence;
            SawFailure = false;
            SawError = false;
            SawSkipped = false;
        }

        public string QualifiedName { get; }

        /// <summary>What a testcase carried when it opened; empty for any other element.</summary>
        public PytestJUnitMapper.TestCaseAttributes TestCase { get; }

        /// <summary>The testcase's document position, or 0 when this element is not a testcase.</summary>
        public int Sequence { get; }

        public bool SawFailure { get; set; }

        public bool SawError { get; set; }

        public bool SawSkipped { get; set; }
    }
}
