using System.Text.Json;
using System.Xml;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Evidence.Contracts;

/// <summary>
/// The Evidence module's untrusted-parser profile (00 §5.7 S5, 06.3.1 §5): the one entry point every
/// server-side parse of artifact bytes goes through. XML, JSON and (from 06.3.1-T2) archives are read
/// through it, so external entities and DTDs are impossible, the depth and size caps are hard limits, and
/// the sniffed signature is checked against the declared media type. A hostile artifact costs bounded CPU
/// and memory and ends <c>rejected</c>, never <c>ready</c>.
/// </summary>
/// <remarks>
/// Every failure is a <see cref="Result"/> value carrying <see cref="ParsingErrorCode.ArtifactInvalid"/>
/// (422) or <see cref="ParsingErrorCode.PayloadTooLarge"/> (413) plus a generic reason from
/// <see cref="ParsingFailureReason"/> — never a parser exception, never an artifact byte, name or path.
/// HTML is never parsed on the server: a declared <c>text/html</c> kind is validated by size and
/// <see cref="SniffMediaType"/> only.
/// <para>
/// <see cref="ParseXmlReport"/> and <see cref="ParseJson"/> are synchronous, and the 30 s cap
/// (<c>ParsingLimits.ParseTimeout</c>) is cooperative: it is checked between reads and per node, so it
/// cannot interrupt a blocking <see cref="Stream.Read"/> or one <c>JsonDocument.Parse</c> call. A caller
/// MUST therefore pass an already-buffered, bounded stream; hard isolation from a runaway parse is 06.3.6.
/// </para>
/// </remarks>
public interface IParsing
{
    /// <summary>
    /// Validates one XML report (a JUnit <c>testsuite</c> document, a DOM snapshot, a UI hierarchy dump)
    /// against the profile and hands back a handle that streams it (06.3.1 AC1, AC2). Synchronous: it
    /// returns only after the whole document has been validated under the depth and size caps.
    /// </summary>
    /// <param name="content">
    /// The untrusted, already-bounded bytes; the caller keeps ownership of the stream. The profile's 30 s
    /// timeout is cooperative and cannot interrupt a blocking read, so a caller passes a buffered stream.
    /// </param>
    /// <param name="maxBytes">The per-kind cap from <c>artifact_kind_policy.max_bytes</c>.</param>
    /// <param name="cancellationToken">Cancels the parse; a timeout of its own is added by the profile.</param>
    /// <returns>The validated report, or the profile failure (<c>artifact_invalid</c>/<c>payload_too_large</c>).</returns>
    Result<ParsedXmlReport> ParseXmlReport(Stream content, long maxBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Parses one JSON payload (a serialised DOM snapshot, an accessibility snapshot, an adapter body)
    /// with a depth cap, a byte cap and no comments or trailing commas (06.3.1 AC2). Synchronous: it
    /// returns only after the whole payload has been validated under the depth and size caps.
    /// </summary>
    /// <param name="content">
    /// The untrusted, already-bounded bytes; the caller keeps ownership of the stream. The profile's 30 s
    /// timeout is cooperative and cannot interrupt one <c>JsonDocument.Parse</c> call, so a caller passes a
    /// buffered stream.
    /// </param>
    /// <param name="maxBytes">The per-kind cap from <c>artifact_kind_policy.max_bytes</c>.</param>
    /// <param name="cancellationToken">Cancels the parse; a timeout of its own is added by the profile.</param>
    /// <returns>The parsed document (the caller disposes it), or the profile failure.</returns>
    Result<JsonDocument> ParseJson(Stream content, long maxBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Enumerates the entries of one zip archive (a Playwright trace, a report bundle) against the profile
    /// (06.3.1 AC3, AC4). Synchronous: it returns only after every entry has been walked, so the caps are
    /// enforced by the time it returns. The entry names are normalized (forward separators, no leading
    /// separator, no <c>.</c>/<c>..</c> segments) and an entry whose name is unsafe — a traversal, an absolute
    /// path, a drive letter, a backslash or control character, a symbolic link — is refused, as is a duplicate
    /// normalized name; a failure never carries an entry name (00 §5.7 S3).
    /// </summary>
    /// <param name="content">
    /// The untrusted, already-bounded archive bytes; the caller keeps ownership of the stream. The profile's
    /// timeout is cooperative and cannot interrupt a blocking read, so a caller passes a buffered stream.
    /// </param>
    /// <param name="maxBytes">The per-kind cap for the archive object itself from <c>artifact_kind_policy.max_bytes</c>.</param>
    /// <param name="cancellationToken">Cancels the walk; a timeout of its own is added by the profile.</param>
    /// <returns>The entry metadata (no entry content), or the profile failure.</returns>
    Result<IReadOnlyList<ArchiveEntry>> EnumerateArchive(Stream content, long maxBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Validates one zip archive exactly as <see cref="EnumerateArchive"/> does and hands back, together with the
    /// entry metadata, the content of the entries the caller selects (13.1.1-T5.2, 13.1.1 AC16). It exists so a
    /// caller that must parse parts <em>inside</em> a zip — the authoring XLSX reader, whose workbook, sheet and
    /// shared-string parts are separate entries — never opens the archive itself: the profile still checks the
    /// declared entry count, every normalized name, duplicate names and symbolic links, the cumulative expanded
    /// size and each entry's compression ratio, and only then reads the selected content.
    /// </summary>
    /// <remarks>
    /// The caps are the caller's, not the profile's defaults: <paramref name="caps"/> states the entry count, the
    /// expanded size and the compression ratio the caller's file format allows (XLSX passes 1 000 entries,
    /// 64 MiB and 20:1 for 13.1.1 AC16), while the nesting depth and the parse timeout stay the profile's own
    /// (<c>ParsingLimits</c>), so no caller can widen those two guards. A caller that wants today's behaviour
    /// keeps calling <see cref="EnumerateArchive"/>, which is unchanged.
    /// <para>
    /// <paramref name="selectEntry"/> receives each entry's <em>normalized</em> name (the same spelling
    /// <see cref="ArchiveContents.Entries"/> reports) and must be a pure predicate: it decides which content is
    /// worth buffering, nothing else. Only the selected, non-directory entries have their bytes read into memory,
    /// each bounded by <paramref name="maxEntryBytes"/> <em>before</em> the allocation and then again by a
    /// counting read that never trusts a declared length, so a hostile header cannot make one entry expand into
    /// an unbounded buffer. Total selected bytes are bounded by the cumulative expanded cap as well.
    /// </para>
    /// <para>
    /// A failure carries a generic reason only and no entry content at all — never a name, a path or an
    /// artifact byte (00 §5.7 S3): the archive is validated first and read second, so a refused archive hands
    /// back nothing it had already buffered. The returned content is immutable to the caller (read-only memory
    /// over a buffer the profile owns and never hands out as an array).
    /// </para>
    /// </remarks>
    /// <param name="content">
    /// The untrusted, already-bounded archive bytes; the caller keeps ownership of the stream. The profile's
    /// timeout is cooperative and cannot interrupt a blocking read, so a caller passes a buffered stream.
    /// </param>
    /// <param name="maxBytes">The per-kind cap for the archive object itself from <c>artifact_kind_policy.max_bytes</c>.</param>
    /// <param name="caps">The entry count, expanded size and compression ratio this caller's format allows.</param>
    /// <param name="selectEntry">Decides, from an entry's normalized name, whether its content is kept.</param>
    /// <param name="maxEntryBytes">The most bytes one selected entry may expand to; over it is <c>size_exceeded</c>.</param>
    /// <param name="cancellationToken">Cancels the read; a timeout of its own is added by the profile.</param>
    /// <returns>The entry metadata plus the selected entries' content, or the profile failure.</returns>
    Result<ArchiveContents> ReadArchiveEntries(
        Stream content,
        long maxBytes,
        ArchiveCaps caps,
        Func<string, bool> selectEntry,
        long maxEntryBytes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Checks the leading bytes of one object against its declared media type (06.3.1 AC5). The declared
    /// type's parameters are stripped and compared case-insensitively; a type the profile has no signature
    /// for answers <see cref="MediaSniffVerdict.Mismatch"/> (fail closed).
    /// </summary>
    /// <param name="declaredMediaType">The artifact's declared <c>media_type</c>.</param>
    /// <param name="prefix">The object's leading bytes (at most <c>ParsingLimits.SniffPrefixBytes</c>).</param>
    /// <returns>The sniff verdict.</returns>
    MediaSniffVerdict SniffMediaType(string declaredMediaType, ReadOnlySpan<byte> prefix);
}

/// <summary>
/// An XML report the profile accepted (06.3.1 AC1, AC2). It carries the closed safe reader factory the
/// profile built — <c>DtdProcessing.Prohibit</c>, <c>XmlResolver = null</c>, entity/depth/size caps — so a
/// reader from <see cref="OpenReader"/> can never resolve an external entity, open a DTD or fetch a URL
/// (00 §5.7 S4). The report never exposes the settings themselves, so no caller can turn a guard off.
/// <para>
/// The document is streamed, never materialised: <see cref="OpenReader"/> hands out one reader at a time and
/// a consumer emits events as it walks (04.4.1's mapper never builds the full <c>testsuite</c> DOM), so a
/// parse of the largest allowed report allocates only the bounded input plus reader buffers (06.3.1 AC6).
/// </para>
/// </summary>
public sealed class ParsedXmlReport
{
    private readonly Func<XmlReader> openReader;

    /// <summary>
    /// Builds a report over the profile's closed reader factory. Internal by design: only the parser
    /// profile constructs these (the factory must be the profile's own — a reader that cannot resolve an
    /// entity or a DTD), and the Evidence module sees this constructor through <c>InternalsVisibleTo</c>.
    /// </summary>
    /// <param name="openReader">The safe reader factory.</param>
    internal ParsedXmlReport(Func<XmlReader> openReader)
    {
        ArgumentNullException.ThrowIfNull(openReader);

        this.openReader = openReader;
    }

    /// <summary>
    /// Opens a fresh streaming reader over the validated document. The caller owns and disposes the reader;
    /// it may be opened more than once.
    /// </summary>
    /// <returns>A reader over the profile-validated XML.</returns>
    public XmlReader OpenReader() => openReader();
}

/// <summary>
/// The metadata of one zip entry the profile accepted (06.3.1 AC3, AC4), returned by
/// <see cref="IParsing.EnumerateArchive"/>. It carries no entry content: <see cref="Name"/> is the entry's
/// normalized relative path (forward separators, no leading separator, no <c>.</c>/<c>..</c> segment) and the
/// two sizes are what the walk measured from the bytes it actually read, so a caller that serves a trace
/// resource can match a requested path without re-opening the archive. The profile never writes a name to a
/// log, a message or a metric (00 §5.7 S3).
/// </summary>
/// <param name="Name">The normalized relative entry path (forward separators).</param>
/// <param name="CompressedBytes">The compressed bytes the walk consumed for this entry.</param>
/// <param name="ExpandedBytes">The expanded bytes the walk actually counted for this entry.</param>
/// <param name="IsDirectory">True for a directory entry (a name trailing <c>/</c> in the archive).</param>
public sealed record ArchiveEntry(string Name, long CompressedBytes, long ExpandedBytes, bool IsDirectory);

/// <summary>
/// The three caps a caller states for its own file format when it reads an archive through
/// <see cref="IParsing.ReadArchiveEntries"/> (13.1.1-T5.2; 13.1.1 AC16 passes 1 000 entries, 64 MiB and 20:1 for
/// XLSX). They are the caller's numbers and not profile defaults: <see cref="IParsing.EnumerateArchive"/> keeps
/// enforcing <c>ParsingLimits</c>, and the two caps that are not here — the zip-in-zip nesting depth and the parse timeout —
/// always stay the profile's own, so a caller can tighten or loosen the three size caps it names but never widen
/// those two guards.
/// </summary>
/// <param name="MaxEntries">The most entries the archive may hold; more is <c>entry_count_exceeded</c>.</param>
/// <param name="MaxExpandedBytes">The most expanded bytes counted across the archive; more is <c>size_exceeded</c>.</param>
/// <param name="MaxCompressionRatio">The most one entry may expand against the compressed bytes it consumed; more is <c>compression_ratio_exceeded</c>.</param>
public sealed record ArchiveCaps(int MaxEntries, long MaxExpandedBytes, int MaxCompressionRatio);

/// <summary>
/// The outcome of <see cref="IParsing.ReadArchiveEntries"/>: the metadata of every entry the profile accepted —
/// the same shape <see cref="IParsing.EnumerateArchive"/> returns — plus the content of the entries the caller
/// selected, looked up by their normalized name (00 §5.7 S3/S5, 13.1.1 AC16).
/// <para>
/// The content is read-only: each entry's bytes live in a buffer this object owns and is never handed out as an
/// array, so a consumer of an untrusted archive cannot mutate what its neighbours read. The lookup is
/// case-insensitive because the profile's duplicate rule is — a normalized name is unique in one archive however
/// it is spelled, so one archive can never hold two entries a lookup could confuse (06.3.1 AC4).
/// </para>
/// <para>
/// An entry that was not selected, a directory entry and a name the archive does not hold all answer
/// <see langword="false"/> from <see cref="TryGetEntry"/>, never an exception: a caller that asks for an optional
/// part decides what its absence means.
/// </para>
/// </summary>
public sealed class ArchiveContents
{
    private readonly Dictionary<string, ReadOnlyMemory<byte>> selected;

    /// <summary>
    /// Builds the result over the walk's entry metadata and the content of the entries it kept. Internal by
    /// design: only the parser profile produces these, and the Evidence module sees this constructor through
    /// <c>InternalsVisibleTo</c>.
    /// </summary>
    /// <param name="entries">The metadata of every accepted entry, in archive order.</param>
    /// <param name="selectedEntries">The content of the selected entries, keyed by normalized name.</param>
    internal ArchiveContents(
        IReadOnlyList<ArchiveEntry> entries,
        Dictionary<string, ReadOnlyMemory<byte>> selectedEntries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(selectedEntries);

        Entries = entries;
        selected = new Dictionary<string, ReadOnlyMemory<byte>>(selectedEntries, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The metadata of every entry the profile accepted, in archive order.</summary>
    public IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>
    /// Reads the content of one entry the caller selected, by its normalized name.
    /// </summary>
    /// <param name="normalizedName">
    /// The entry's normalized relative path as <see cref="Entries"/> reports it (forward separators, no leading
    /// separator); matching is case-insensitive.
    /// </param>
    /// <param name="bytes">The entry's content; empty when this method returns <see langword="false"/>.</param>
    /// <returns>True when the archive held the entry and the caller selected it.</returns>
    public bool TryGetEntry(string normalizedName, out ReadOnlyMemory<byte> bytes)
    {
        bytes = ReadOnlyMemory<byte>.Empty;

        return !string.IsNullOrEmpty(normalizedName) && selected.TryGetValue(normalizedName, out bytes);
    }
}

/// <summary>The media-sniff outcome of <see cref="IParsing.SniffMediaType"/> (06.3.1 AC5).</summary>
public enum MediaSniffVerdict
{
    /// <summary>The leading bytes match the declared media type.</summary>
    Match,

    /// <summary>The leading bytes contradict the declared media type, or the profile has no signature for it (fail closed).</summary>
    Mismatch,
}

/// <summary>
/// The codes of a parser-profile failure (00 §9.2, 06.3.1 AC1/AC2). Both are the registered
/// <see cref="ApiErrorCode"/> constants, so the profile never invents a code: a parse failure maps to
/// <see cref="ApiErrorCode.ArtifactInvalid"/> (422, registered in <c>contracts/openapi.yaml</c>) and the
/// size cap maps to the existing 413 <see cref="ApiErrorCode.PayloadTooLarge"/> (06.1.2-T1). Both are
/// returned by in-process parser callers only — no HTTP route parses an artifact at T1.
/// </summary>
public static class ParsingErrorCode
{
    /// <summary>422: the bytes violate the profile — a DTD/entity, depth, timeout or malformed content.</summary>
    public const string ArtifactInvalid = ApiErrorCode.ArtifactInvalid;

    /// <summary>413 <c>payload_too_large</c> (06.1.2-T1): the bytes exceed the artifact kind's byte cap.</summary>
    public const string PayloadTooLarge = ApiErrorCode.PayloadTooLarge;
}

/// <summary>
/// The closed set of generic reasons a profile failure carries in its <c>details</c> (06.3.1 §2, 06.3.2's
/// corpus asserts them). Every reason is a fixed word: a failure never echoes an artifact byte, element name
/// or path (00 §5.7 S3).
/// </summary>
public static class ParsingFailureReason
{
    /// <summary>The document declares a DTD (<c>&lt;!DOCTYPE</c>/<c>&lt;!ENTITY</c>), which the profile prohibits.</summary>
    public const string DtdProhibited = "dtd_prohibited";

    /// <summary>The document nests deeper than <c>ParsingLimits.MaxDepth</c>.</summary>
    public const string DepthExceeded = "depth_exceeded";

    /// <summary>The content is larger than the kind's <c>max_bytes</c>.</summary>
    public const string SizeExceeded = "size_exceeded";

    /// <summary>The parse did not finish inside <c>ParsingLimits.ParseTimeout</c>.</summary>
    public const string Timeout = "timeout";

    /// <summary>The content is not well-formed (XML/JSON syntax, or no document element).</summary>
    public const string Malformed = "malformed";

    /// <summary>A zip entry name is unsafe: a traversal, an absolute path, a drive letter, a backslash or control character, or a symbolic link (06.3.1 AC4).</summary>
    public const string UnsafeEntryName = "unsafe_entry_name";

    /// <summary>Two zip entries normalize to the same path, so one could shadow the other (06.3.1 AC4).</summary>
    public const string DuplicateEntry = "duplicate_entry";

    /// <summary>The archive holds more entries than <c>ParsingLimits.MaxZipEntries</c> (06.3.1 AC3).</summary>
    public const string EntryCountExceeded = "entry_count_exceeded";

    /// <summary>A zip entry expands by more than <c>ParsingLimits.MaxCompressionRatio</c> against its compressed bytes (06.3.1 AC3).</summary>
    public const string CompressionRatioExceeded = "compression_ratio_exceeded";

    /// <summary>A zip nests deeper than <c>ParsingLimits.MaxNesting</c> (06.3.1 AC3).</summary>
    public const string NestingExceeded = "nesting_exceeded";
}
