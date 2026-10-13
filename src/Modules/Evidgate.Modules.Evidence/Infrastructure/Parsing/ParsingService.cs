using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Contracts;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing;

/// <summary>
/// The module's implementation of <see cref="IParsing"/> (06.3.1 §5, 00 §5.5 C8): a thin, stateless adapter
/// over the profile's four arms — <see cref="SafeXml"/>, <see cref="SafeJson"/>, <see cref="SafeZipArchive"/>
/// and <see cref="MediaSniffer"/>.
/// It holds no state and opens no resource, so it is registered as a singleton and every caller (the
/// <c>complete</c> verifier, 04.4.1's report mapper, 06.2.3's scanner) shares one instance.
/// </summary>
internal sealed class ParsingService : IParsing
{
    /// <inheritdoc />
    public Result<ParsedXmlReport> ParseXmlReport(Stream content, long maxBytes, CancellationToken cancellationToken) =>
        SafeXml.Parse(content, maxBytes, cancellationToken);

    /// <inheritdoc />
    public Result<JsonDocument> ParseJson(Stream content, long maxBytes, CancellationToken cancellationToken) =>
        SafeJson.Parse(content, maxBytes, cancellationToken);

    /// <inheritdoc />
    public Result<IReadOnlyList<ArchiveEntry>> EnumerateArchive(Stream content, long maxBytes, CancellationToken cancellationToken) =>
        SafeZipArchive.Enumerate(content, maxBytes, cancellationToken);

    /// <inheritdoc />
    public MediaSniffVerdict SniffMediaType(string declaredMediaType, ReadOnlySpan<byte> prefix) =>
        MediaSniffer.Check(declaredMediaType, prefix) == SniffResult.Match ? MediaSniffVerdict.Match : MediaSniffVerdict.Mismatch;
}
