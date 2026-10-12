using System.Text.Json;
using System.Xml;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Evidence.Contracts;

/// <summary>
/// The Evidence module's untrusted-parser profile (00 §5.7 S5, 06.3.1 §5): the one entry point every
/// server-side parse of artifact bytes goes through. XML, JSON and (from 06.3.1-T2) archives are read
/// through it, so external entities and DTDs are impossible, depth/size/time caps are hard limits and the
/// sniffed signature is checked against the declared media type. A hostile artifact costs bounded CPU and
/// memory and ends <c>rejected</c>, never <c>ready</c>.
/// </summary>
/// <remarks>
/// Every failure is a <see cref="Result"/> value carrying <c>artifact_invalid</c> (422) or <c>too_large</c>
/// (413) plus a generic reason from <see cref="ParsingFailureReason"/> — never a parser exception, never an
/// artifact byte, name or path. HTML is never parsed on the server: a declared <c>text/html</c> kind is
/// validated by size and <see cref="SniffMediaType"/> only.
/// </remarks>
public interface IParsing
{
    /// <summary>
    /// Validates one XML report (a JUnit <c>testsuite</c> document, a DOM snapshot, a UI hierarchy dump)
    /// against the profile and hands back a handle that streams it (06.3.1 AC1, AC2).
    /// </summary>
    /// <param name="content">The untrusted bytes; the caller keeps ownership of the stream.</param>
    /// <param name="maxBytes">The per-kind cap from <c>artifact_kind_policy.max_bytes</c>.</param>
    /// <param name="cancellationToken">Cancels the parse; a timeout of its own is added by the profile.</param>
    /// <returns>The validated report, or the profile failure (<c>artifact_invalid</c>/<c>too_large</c>).</returns>
    Result<ParsedXmlReport> ParseXmlReport(Stream content, long maxBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Parses one JSON payload (a serialised DOM snapshot, an accessibility snapshot, an adapter body)
    /// with a depth cap, a byte cap and no comments or trailing commas (06.3.1 AC2).
    /// </summary>
    /// <param name="content">The untrusted bytes; the caller keeps ownership of the stream.</param>
    /// <param name="maxBytes">The per-kind cap from <c>artifact_kind_policy.max_bytes</c>.</param>
    /// <param name="cancellationToken">Cancels the parse; a timeout of its own is added by the profile.</param>
    /// <returns>The parsed document (the caller disposes it), or the profile failure.</returns>
    Result<JsonDocument> ParseJson(Stream content, long maxBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Checks the leading bytes of one object against its declared media type (06.3.1 AC5). The declared
    /// type's parameters are stripped and compared case-insensitively; a type the profile does not sniff
    /// yet (every type 06.3.1-T2.2 has not added) answers <see cref="MediaSniffVerdict.NotSniffed"/>.
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
    /// Builds a report over the profile's closed reader factory. Only the parser profile constructs these;
    /// the factory must be the profile's own (a reader that cannot resolve an entity or a DTD).
    /// </summary>
    /// <param name="openReader">The safe reader factory.</param>
    public ParsedXmlReport(Func<XmlReader> openReader)
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

/// <summary>The media-sniff outcome of <see cref="IParsing.SniffMediaType"/> (06.3.1 AC5).</summary>
public enum MediaSniffVerdict
{
    /// <summary>The leading bytes match the declared media type.</summary>
    Match,

    /// <summary>The leading bytes contradict the declared media type.</summary>
    Mismatch,

    /// <summary>
    /// The profile does not sniff this declared type yet; 06.3.1-T2.2 makes this fail closed at
    /// <c>complete</c>. At T1 the <c>complete</c> route treats it as "no sniff" and keeps the artifact.
    /// </summary>
    NotSniffed,
}

/// <summary>
/// The two codes of a parser-profile failure (00 §9.2, 06.3.1 AC1/AC2). <c>artifact_invalid</c> is
/// <see cref="Evidgate.BuildingBlocks.SharedKernel.Api.ApiErrorCode.ArtifactInvalid"/>; <c>too_large</c> is the
/// 413 the size cap maps to. Both are registered in <c>contracts/openapi.yaml</c> (the only error-code source,
/// T52) and are returned by in-process parser callers only — no HTTP route parses an artifact at T1.
/// </summary>
public static class ParsingErrorCode
{
    /// <summary>422: the bytes violate the profile — a DTD/entity, depth, timeout or malformed content.</summary>
    public const string ArtifactInvalid = "artifact_invalid";

    /// <summary>413: the bytes exceed the artifact kind's byte cap.</summary>
    public const string TooLarge = "too_large";
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
}
