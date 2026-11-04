using System.Runtime.InteropServices;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The scan of one stored object, in the two shapes a stored object has: bytes that are the document, and bytes that
/// are an archive of documents.
/// <para>
/// <b>A deflated entry hides its content from a byte scan.</b> The compressed stream holds no readable prefix of an
/// entry, so scanning a trace or report bundle in place would always answer clean. An object whose declared type is
/// <c>application/zip</c> — the type the completion sniffed against these very bytes — is therefore read through the
/// parser profile: every non-directory entry is decompressed under the profile's own caps and scanned, and each class
/// keeps the sum of its entry counts.
/// </para>
/// <para>
/// <b>The expanded budget is the object's own cap.</b> The entry count and the compression ratio are the profile's
/// constants, and the cumulative expanded bytes may not exceed the cap the object was admitted under, so one artifact
/// can never expand past the size its kind allows it to weigh.
/// </para>
/// <para>
/// <b>A bundle that cannot be read is not a clean bundle.</b> An archive the profile refuses, an expanded set over
/// the budget, and an entry whose content is itself a zip are thrown rather than answered: the caller's task queue
/// retries and finally dead-letters while the artifact stays <c>pending</c>, which is the only fail-closed answer for
/// bytes nothing has classified. The nested case needs the refusal because the profile returns a nested archive's own
/// compressed bytes and not its children, so those children's secrets are the one part of the object this pass cannot
/// see; the entry is recognized by its zip magic, never by its name.
/// </para>
/// </summary>
internal static class ArtifactSecretScan
{
    /// <summary>The one archive media type the profile holds a signature for.</summary>
    private const string ZipMediaType = "application/zip";

    /// <summary>
    /// Counts the classes of one stored object: in place when the object is a document, per entry when its declared
    /// type says it is an archive.
    /// </summary>
    /// <param name="parsing">The parser profile, the only arm that decompresses an untrusted archive.</param>
    /// <param name="mediaType">The artifact's declared media type.</param>
    /// <param name="content">The already-bounded stored bytes.</param>
    /// <param name="maxBytes">The kind's cap — the bound of the object and of the archive's expanded bytes.</param>
    /// <param name="cancellationToken">Cancels the pass; a cancelled attempt rethrows for the caller to retry.</param>
    /// <returns>One entry per class that matched anywhere in the object, in <see cref="SecretPatternClass.All"/> order.</returns>
    /// <exception cref="InvalidOperationException">The object is an archive the profile could not read to the end,
    /// an archive whose expanded set exceeds the budget, or an archive holding a nested archive whose entries this
    /// pass does not read.</exception>
    public static IReadOnlyList<SecretPatternHit> Scan(
        IParsing parsing,
        string mediaType,
        ReadOnlyMemory<byte> content,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parsing);

        return IsArchive(mediaType)
            ? ScanArchive(parsing, content, maxBytes, cancellationToken)
            : SecretPatternScanner.Scan(content.Span, cancellationToken);
    }

    /// <summary>
    /// True when <paramref name="mediaType"/> is the archive type, ignoring its parameters and letter case — the same
    /// comparison the completion applies to a declared type.
    /// </summary>
    /// <param name="mediaType">The declared media type, or null when the row carries none.</param>
    /// <returns>True for <c>application/zip</c> in any casing and with any parameters.</returns>
    public static bool IsArchive(string? mediaType) =>
        mediaType is not null
        && string.Equals(mediaType.Split(';', 2)[0], ZipMediaType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decompresses every entry of one archive under the profile's caps and sums the classes across them.
    /// </summary>
    /// <param name="parsing">The parser profile.</param>
    /// <param name="content">The archive's bytes.</param>
    /// <param name="maxBytes">The kind's cap, applied to the object and to the expanded total.</param>
    /// <param name="cancellationToken">Cancels the read and the scan.</param>
    /// <returns>The summed hits, in <see cref="SecretPatternClass.All"/> order; empty when no entry matched.</returns>
    private static List<SecretPatternHit> ScanArchive(
        IParsing parsing,
        ReadOnlyMemory<byte> content,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var caps = new ArchiveCaps(ParsingLimits.MaxZipEntries, maxBytes, ParsingLimits.MaxCompressionRatio);

        // The stream reads the buffer the object store already handed over: no copy of the archive, and the profile
        // never takes ownership of it.
        using var stream = OpenRead(content);
        var read = parsing.ReadArchiveEntries(
            stream,
            maxBytes,
            caps,
            selectEntry: _ => true,
            maxEntryBytes: maxBytes,
            cancellationToken);

        if (!read.IsSuccess)
        {
            throw new InvalidOperationException(
                $"the stored object is an archive the secret scan could not read ({ReasonOf(read.Error)}), so its bytes were not classified");
        }

        var totals = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var entry in read.Value.Entries)
        {
            if (entry.IsDirectory || !read.Value.TryGetEntry(entry.Name, out var entryContent))
            {
                continue;
            }

            // A bundle inside the bundle is where this pass would otherwise answer clean about bytes it never read:
            // the profile validates a nested archive's entries against the caps but hands back only the nested file's
            // own compressed bytes, and a deflated secret leaves no readable prefix in them. The entry is refused
            // instead, so the caller retries and finally dead-letters rather than storing a verdict nothing supports.
            if (SafeZipArchive.IsZipMagic(entryContent.Span))
            {
                throw new InvalidOperationException(
                    "the stored object holds a nested archive, whose entries this scan does not read, so its bytes were not classified");
            }

            foreach (var hit in SecretPatternScanner.Scan(entryContent.Span, cancellationToken))
            {
                totals[hit.PatternClass] = totals.GetValueOrDefault(hit.PatternClass) + hit.HitCount;
            }
        }

        if (totals.Count == 0)
        {
            return [];
        }

        var hits = new List<SecretPatternHit>(totals.Count);

        foreach (var patternClass in SecretPatternClass.All)
        {
            if (totals.TryGetValue(patternClass, out var count))
            {
                hits.Add(new SecretPatternHit(patternClass, count));
            }
        }

        return hits;
    }

    /// <summary>
    /// A read-only stream over exactly <paramref name="content"/>, so the archive reader reads the stored buffer
    /// instead of a copy of it. Storage answers an array; the copy is the fallback for any other memory a caller passes.
    /// </summary>
    /// <param name="content">The stored bytes.</param>
    /// <returns>A stream over those bytes only.</returns>
    private static MemoryStream OpenRead(ReadOnlyMemory<byte> content) =>
        MemoryMarshal.TryGetArray(content, out var segment) && segment.Array is { } array
            ? new MemoryStream(array, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(content.ToArray(), writable: false);

    /// <summary>
    /// The generic reason word of a profile failure, for an operator log and a dead-letter row: the profile's reasons
    /// are a closed set and carry no entry name, path or byte.
    /// </summary>
    /// <param name="error">The failure, or null.</param>
    /// <returns>The first detail reason, or the error code, or a fixed word.</returns>
    private static string ReasonOf(Error? error)
    {
        if (error is null)
        {
            return "unknown";
        }

        return error.Details is { Count: > 0 } details ? details[0].Reason : error.Code;
    }
}
