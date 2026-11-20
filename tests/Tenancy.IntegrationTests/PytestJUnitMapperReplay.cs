using System.Text;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Replays one inline report through the real pytest mapper and the real parser profile, so the mapper tests
/// never build a reader of their own: the closed profile (DTD, depth, bytes, timeout) is the one production uses.
/// </summary>
internal static class PytestJUnitMapperReplay
{
    /// <summary>The kind cap these tests parse against (a JUnit report is the largest XML artifact kind).</summary>
    public const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>The entry cap these tests parse against.</summary>
    public const long MaxCases = 10_000;

    /// <summary>A fixed report identity, so a failure message names the same synthetic producer twice.</summary>
    public static readonly Guid ReportId = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

    /// <summary>Maps one document with a resolver that answers every testcase (the default under test).</summary>
    /// <param name="xml">The document text.</param>
    /// <param name="resolve">The execution-key resolution; the default answers <c>classname::name</c>.</param>
    /// <param name="maxBytes">The byte cap for this parse.</param>
    /// <param name="maxCases">The entry cap for this parse.</param>
    /// <returns>What the mapper produced.</returns>
    public static JUnitMapResult Map(
        string xml,
        Func<string, string, string?>? resolve = null,
        long maxBytes = MaxBytes,
        long maxCases = MaxCases) =>
        new PytestJUnitMapper(new ParsingService(maxCases), maxBytes)
            .Map(new MemoryStream(Encoding.UTF8.GetBytes(xml)), new JUnitMapContext(ReportId, resolve ?? NodeId));

    /// <summary>
    /// Maps the child's records through the push mapper the way a runner feeds it: one line at a time, then the
    /// verdict. A child rejection is the run's own verdict on the report, so it is raised as the refusal a caller of
    /// the byte path would catch instead of being mapped.
    /// </summary>
    /// <param name="mapper">The dialect mapper.</param>
    /// <param name="records">The child's records in stream order, its marker excluded.</param>
    /// <param name="context">The synthetic producer identity and the caller's execution-key resolution.</param>
    /// <returns>What the push mapping produced.</returns>
    public static JUnitMapResult MapRecords(
        PytestJUnitMapper mapper,
        IReadOnlyList<ParseRecord> records,
        JUnitMapContext context)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(records);

        var sink = mapper.OpenRecordMapper(context);
        foreach (var record in records)
        {
            sink.Accept(record.Utf8Json.Span);
        }

        if (sink.RejectedReason is { } reason)
        {
            throw new JUnitParseRefusedException(reason, "the parse child refused the report");
        }

        return sink.Finish();
    }

    /// <summary>The identity hint rule this harness uses: the raw pair, joined.</summary>
    public static string? NodeId(string classname, string name) => classname + "::" + name;

    /// <summary>The raw bytes of one document, for a test that needs its own cap arithmetic.</summary>
    public static byte[] Bytes(string xml) => Encoding.UTF8.GetBytes(xml);
}
