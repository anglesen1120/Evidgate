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

    /// <summary>
    /// Reads the caller-selected entries of one archive (13.1.1-T5.2): the walk is the same one
    /// <see cref="EnumerateArchive"/> runs, with the caller's three size caps in place of
    /// <see cref="ParsingLimits"/>' archive numbers. The two caps a caller may not state for itself — the
    /// zip-in-zip nesting depth and the parse timeout — stay the profile's constants, so a format that states
    /// 1 000 entries / 64 MiB / 20:1 (13.1.1 AC16) changes no other guard.
    /// </summary>
    /// <param name="content">The untrusted archive bytes; the caller keeps ownership of the stream.</param>
    /// <param name="maxBytes">The per-kind byte cap for the archive object itself.</param>
    /// <param name="caps">The entry count, expanded size and compression ratio the caller's format allows.</param>
    /// <param name="selectEntry">Decides, from an entry's normalized name, whether its content is kept.</param>
    /// <param name="maxEntryBytes">The most bytes one selected entry may expand to.</param>
    /// <param name="cancellationToken">The caller's cancellation; a caller cancellation is never swallowed.</param>
    /// <returns>The entry metadata plus the selected entries' content, or the profile failure.</returns>
    public Result<ArchiveContents> ReadArchiveEntries(
        Stream content,
        long maxBytes,
        ArchiveCaps caps,
        Func<string, bool> selectEntry,
        long maxEntryBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caps);

        var options = new ZipArchiveOptions(
            caps.MaxEntries,
            caps.MaxExpandedBytes,
            caps.MaxCompressionRatio,
            ParsingLimits.MaxNesting,
            ParsingLimits.ParseTimeout);

        return SafeZipArchive.ReadEntries(content, maxBytes, options, selectEntry, maxEntryBytes, cancellationToken);
    }

    /// <inheritdoc />
    public MediaSniffVerdict SniffMediaType(string declaredMediaType, ReadOnlySpan<byte> prefix) =>
        MediaSniffer.Check(declaredMediaType, prefix) == SniffResult.Match ? MediaSniffVerdict.Match : MediaSniffVerdict.Mismatch;
}
