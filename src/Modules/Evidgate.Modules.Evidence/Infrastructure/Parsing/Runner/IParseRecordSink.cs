namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;

/// <summary>
/// Where the stdout reader hands each accepted record line: one call per line, from the stdout pump, in stream
/// order, with the marker excluded.
/// <para>
/// The sink runs synchronously inside the pump because the record bytes are only valid for the duration of the
/// call — the reader reuses its line buffer. An implementation that needs the bytes beyond the call copies them;
/// one that maps them on the spot (the JUnit mapper) does not. A sink must never throw for content: a bad record is
/// the sink's own refusal to defer until the run's verdict is known, so a throw would replace the runner's verdict
/// with an unhandled exception from a background pump.
/// </para>
/// </summary>
internal interface IParseRecordSink
{
    /// <summary>Consumes one accepted record line.</summary>
    /// <param name="utf8Json">The record's JSON object as the child wrote it, without the terminating newline.</param>
    void Accept(ReadOnlySpan<byte> utf8Json);
}

/// <summary>
/// The sink that keeps every record as a copied byte array, for the profiles whose consumer needs the whole stream
/// after the run (a JSON document or an archive walk). It is the default when a request names no sink.
/// </summary>
internal sealed class BufferingParseRecordSink : IParseRecordSink
{
    private readonly List<ParseRecord> records = [];

    /// <summary>The records accepted so far, in stream order.</summary>
    public IReadOnlyList<ParseRecord> Records => records;

    /// <inheritdoc />
    public void Accept(ReadOnlySpan<byte> utf8Json) => records.Add(new ParseRecord(utf8Json.ToArray()));
}
