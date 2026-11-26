namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// What an untrusted segment is. A closed set: the kind is written into the delimiter line, so a free-text kind
/// would be a second channel into the prompt.
/// </summary>
internal enum PromptSegmentKind
{
    /// <summary>A DOM or accessibility snapshot excerpt.</summary>
    Dom,

    /// <summary>Test, console or network log text.</summary>
    Log,

    /// <summary>A retrieved record (an earlier incident, a knowledge entry, requirement text).</summary>
    Record,

    /// <summary>The error text of the failing attempt.</summary>
    Error,
}

/// <summary>
/// One piece of untrusted content and the id of the record it came from. The content is accepted already masked at
/// capture: the builder has no secret source to put a value back from, so a masked value stays masked.
/// </summary>
/// <param name="Kind">What the content is.</param>
/// <param name="SourceId">
/// The id of the source record or artifact (1–128 characters of <c>A-Z a-z 0-9 . _ : -</c>), recorded in the delimiter
/// line and in the context hash. Anything else is refused, so an id can never carry delimiter text.
/// </param>
/// <param name="Content">The text, treated as opaque data.</param>
internal sealed record PromptSegment(PromptSegmentKind Kind, string SourceId, string Content)
{
    /// <summary>The content is reported as a length: a segment can reach a log line through <see cref="object.ToString"/>.</summary>
    public override string ToString() =>
        $"PromptSegment {{ Kind = {Kind}, SourceId = {SourceId}, Content = {Content?.Length ?? 0} chars }}";
}

/// <summary>
/// The finished, provider-neutral prompt: a fixed instruction preamble followed by delimited data blocks. It is plain
/// text only; nothing in it names a tool, a command, a path to fetch or a merge.
/// </summary>
/// <param name="Preamble">The constant system preamble, byte-identical in every context.</param>
/// <param name="Body">The wrapped data blocks, in the order the segments were given.</param>
/// <param name="ContextHash">
/// Lowercase hex SHA-256 over the format version, every source id and every wrapped block, so a reviewer can
/// reconstruct exactly which data reached the model.
/// </param>
/// <param name="SourceIds">The source ids that were wrapped, in order.</param>
internal sealed record PromptContext(string Preamble, string Body, string ContextHash, IReadOnlyList<string> SourceIds)
{
    /// <summary>The text handed to <see cref="Evidgate.Modules.Healing.Contracts.HealingLlmRequest.Payload"/>.</summary>
    public string Payload => string.Concat(Preamble, "\n\n", Body);

    /// <summary>The body is untrusted and may be sensitive: only its length is printed.</summary>
    public override string ToString() =>
        $"PromptContext {{ ContextHash = {ContextHash}, Sources = {SourceIds.Count}, Body = {Body.Length} chars }}";
}

/// <summary>Builds a <see cref="PromptContext"/> from untrusted segments.</summary>
internal interface IPromptContextBuilder
{
    /// <summary>Wraps <paramref name="segments"/> as delimited data.</summary>
    /// <param name="segments">The untrusted content, in prompt order.</param>
    /// <returns>The prompt context.</returns>
    /// <exception cref="ArgumentException">A source id is malformed, or the input exceeds the segment or size limit.</exception>
    PromptContext Build(IReadOnlyList<PromptSegment> segments);
}
