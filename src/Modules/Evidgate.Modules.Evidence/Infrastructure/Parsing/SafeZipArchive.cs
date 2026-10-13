using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Contracts;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing;

/// <summary>
/// The archive arm of the parser profile (06.3.1 AC3, AC4; 00 §5.7 S5, S3/P5). A caller hands in an untrusted
/// archive stream and the kind's byte cap; the profile bounds the object into one buffer under that cap, then
/// walks every entry of every nested archive against the caps in <see cref="ParsingLimits"/> — an entry count,
/// a cumulative expanded size measured from the bytes actually read, a per-entry compression ratio, and a
/// zip-in-zip nesting depth — and normalizes and validates every entry name so no name could resolve outside
/// the extraction root. A hostile archive costs bounded CPU and memory and ends a <c>Result</c> failure, never a
/// <c>ready</c> artifact.
/// <para>
/// Everything derivable from the central directory — the declared entry count, each name, duplicate normalized
/// names, a symbolic-link entry — is checked before a single entry byte is written. The caps that need the
/// content (expanded size, ratio, nesting) abort at the first breach, and when extraction is offered they are
/// proven by a non-extracting pass over the same bounded bytes before the extraction pass runs, so a
/// content-derived breach never leaves even a partial file: the root is created by the extraction pass alone.
/// Every file is then written under that unique temporary root, which this class owns and deletes on dispose or
/// failure, each destination path is re-checked against the root after normalization, and no existing file is
/// overwritten (<see cref="FileMode.CreateNew"/>) or followed.
/// </para>
/// <para>
/// A nested archive is recognized by the leading PK magic of the entry's content alone, never by its name: a
/// <c>.zip</c> entry whose content is not a zip is not recursed into, and an entry that is one is recursed
/// whatever it is called. Because the nested archive is bounded and re-validated here, a consumer that later
/// opens an extracted file must run it through this profile again rather than trusting the extraction
/// (hand-over to 04.4.1's trace mapper and 06.3.4's report bundle reader).
/// </para>
/// <para>
/// The walk is synchronous and its <c>ParsingLimits.ParseTimeout</c> is cooperative — checked between entries and
/// between reads, so a caller MUST pass an already-buffered, bounded stream; hard isolation from a runaway walk
/// is 06.3.6. The walk's own footprint is the bounding buffer (the kind's byte cap) plus the expanded budget,
/// with at most one nested archive buffered at a time; a process-wide memory or concurrency bound, and spooling a
/// large object to a temp file instead of holding it, belong to 06.3.6 rather than to this profile (recorded as
/// the 06.3.1-T2 perf hand-over). No failure message, reason or metric ever carries an entry name or an artifact
/// byte (00 §5.7 S3).
/// </para>
/// </summary>
internal static class SafeZipArchive
{
    private const int ReadChunkBytes = 64 * 1024;
    private const int ZipMagicBytes = 4;

    /// <summary>The Unix mode bits of an entry, held in the upper 16 bits of <c>ExternalAttributes</c>.</summary>
    private const int UnixFileTypeMask = 0xF000;

    /// <summary>The Unix <c>S_IFLNK</c> file type: a symbolic link, which the profile never follows.</summary>
    private const int UnixSymlinkType = 0xA000;

    /// <summary>The classic end-of-central-directory signature (<c>PK\x05\x06</c>).</summary>
    private const uint EndOfCentralDirectorySignature = 0x0605_4B50;

    /// <summary>The classic end-of-central-directory record's fixed length, before its comment.</summary>
    private const int EndOfCentralDirectoryLength = 22;

    /// <summary>Where the classic record's total entry count sits, relative to its signature.</summary>
    private const int EndOfCentralDirectoryTotalEntriesOffset = 10;

    /// <summary>Where the classic record's comment length sits, relative to its signature.</summary>
    private const int EndOfCentralDirectoryCommentLengthOffset = 20;

    /// <summary>The largest comment a classic record can declare (a <c>ushort</c>).</summary>
    private const int MaxEndOfCentralDirectoryComment = 0xFFFF;

    /// <summary>The ZIP64 end-of-central-directory locator's signature (<c>PK\x06\x07</c>).</summary>
    private const uint Zip64LocatorSignature = 0x0706_4B50;

    /// <summary>The locator's fixed length; its ZIP64 record offset sits at <c>+8</c>.</summary>
    private const int Zip64LocatorLength = 20;

    /// <summary>The ZIP64 end-of-central-directory record's signature (<c>PK\x06\x06</c>).</summary>
    private const uint Zip64RecordSignature = 0x0606_4B50;

    /// <summary>The ZIP64 record's fixed length, which holds its total entry count at <c>+32</c>.</summary>
    private const int Zip64RecordLength = 40;

    /// <summary>Where the ZIP64 record's total entry count sits, relative to its signature.</summary>
    private const int Zip64RecordTotalEntriesOffset = 32;

    /// <summary>
    /// Enumerates one archive against the profile and returns its entry metadata. No entry content is returned
    /// and nothing is written to disk.
    /// </summary>
    /// <param name="content">The untrusted archive bytes; the caller keeps ownership of the stream.</param>
    /// <param name="maxBytes">The per-kind byte cap for the archive object itself.</param>
    /// <param name="cancellationToken">The caller's cancellation; a caller cancellation is never swallowed.</param>
    /// <returns>The entries of the top-level archive, or the profile failure.</returns>
    public static Result<IReadOnlyList<ArchiveEntry>> Enumerate(
        Stream content,
        long maxBytes,
        CancellationToken cancellationToken) =>
        Enumerate(content, maxBytes, ZipArchiveOptions.Default, cancellationToken);

    /// <summary>
    /// The profile with explicit caps, so a test can prove every guard with tiny generated archives.
    /// </summary>
    /// <param name="content">The untrusted archive bytes; the caller keeps ownership of the stream.</param>
    /// <param name="maxBytes">The per-kind byte cap for the archive object itself.</param>
    /// <param name="options">The caps for this walk.</param>
    /// <param name="cancellationToken">The caller's cancellation; a caller cancellation is never swallowed.</param>
    /// <returns>The entries of the top-level archive, or the profile failure.</returns>
    internal static Result<IReadOnlyList<ArchiveEntry>> Enumerate(
        Stream content,
        long maxBytes,
        ZipArchiveOptions options,
        CancellationToken cancellationToken)
    {
        var walk = Walk(content, maxBytes, options, extract: false, cancellationToken);

        return walk.IsSuccess
            ? Result.Success(walk.Value.Entries)
            : Result.Failure<IReadOnlyList<ArchiveEntry>>(walk.Error!);
    }

    /// <summary>
    /// Validates one archive exactly as <see cref="Enumerate"/> does — central directory and every content-derived
    /// cap — and only once that pass has accepted the whole object does it extract every top-level entry under a
    /// unique temporary root the returned handle owns, so a rejected archive never materializes a root or a
    /// partial file. A nested archive's children are validated against the caps but never written, so a nested
    /// name cannot collide with a top-level destination. The handle deletes the tree on
    /// <see cref="ArchiveExtraction.Dispose"/>, so a caller never leaks extracted artifact bytes.
    /// </summary>
    /// <param name="content">The untrusted archive bytes; the caller keeps ownership of the stream.</param>
    /// <param name="maxBytes">The per-kind byte cap for the archive object itself.</param>
    /// <param name="options">The caps for this walk.</param>
    /// <param name="cancellationToken">The caller's cancellation; a caller cancellation is never swallowed.</param>
    /// <returns>The extraction handle (dispose it to delete the tree), or the profile failure.</returns>
    /// <remarks>
    /// No production path extracts an archive yet — <see cref="Enumerate"/> is what the <c>complete</c> verifier
    /// calls, and extraction is the arm the trace and report-bundle consumers (04.4.1, 06.3.4) will call. A
    /// consumer that feeds an extracted file to a parser MUST run it back through this profile: the file is
    /// validated only for what this walk read of it.
    /// </remarks>
    internal static Result<ArchiveExtraction> Extract(
        Stream content,
        long maxBytes,
        ZipArchiveOptions options,
        CancellationToken cancellationToken)
    {
        var walk = Walk(content, maxBytes, options, extract: true, cancellationToken);

        return walk.IsSuccess
            ? Result.Success(new ArchiveExtraction(walk.Value.Root!, walk.Value.Entries))
            : Result.Failure<ArchiveExtraction>(walk.Error!);
    }

    /// <summary>
    /// Bounds the object into one buffer under the kind's cap (never trusting a <see cref="Stream.Length"/> the
    /// caller controls), then walks it under the profile's cooperative timeout. When <paramref name="extract"/> is
    /// set, a non-extracting pass runs first — the same pass production uses through <see cref="Enumerate"/> — so
    /// every cap that needs the content is refused before the extraction pass writes a single byte. The
    /// extraction pass then re-checks the same caps as it reads, as a defence against content that differs from
    /// what was probed. Both passes share the operation's one timeout budget, so an archive can never buy more
    /// than <c>ParsingLimits.ParseTimeout</c> of work: a probe that runs out of time fails the whole extraction
    /// with <c>timeout</c> instead of over-running the cap.
    /// </summary>
    private static Result<ArchiveWalk> Walk(
        Stream content,
        long maxBytes,
        ZipArchiveOptions options,
        bool extract,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(options.Timeout);
        var token = timeoutSource.Token;

        var state = new WalkState(options.MaxExpandedBytes);
        var handedOver = false;

        try
        {
            var (buffer, length) = BoundedContent.ReadContiguous(content, maxBytes, token);
            token.ThrowIfCancellationRequested();

            if (extract)
            {
                // Its own budget, so a probed byte never eats into the extraction pass's allowance.
                var probeState = new WalkState(options.MaxExpandedBytes);

                using var probe = new MemoryStream(buffer, 0, length, writable: false);
                using var probeSource = new CountingStream(probe);

                _ = WalkArchive(
                    probeSource, buffer.AsSpan(0, length), options, probeState, extract: false, depth: 1, token);
            }

            using var memory = new MemoryStream(buffer, 0, length, writable: false);
            using var source = new CountingStream(memory);

            var entries = WalkArchive(
                source, buffer.AsSpan(0, length), options, state, extract, depth: 1, token);
            var walk = Result.Success(new ArchiveWalk(entries, state.Root));
            handedOver = true;

            return walk;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller's own cancellation propagates; only the profile's timeout becomes a failure value.
            return Result.Failure<ArchiveWalk>(ParsingFailure.For(ParsingFailureReason.Timeout));
        }
        catch (ContentTooLongException)
        {
            // The bounded read refused the object: it is longer than the kind's cap.
            return Result.Failure<ArchiveWalk>(ParsingFailure.For(ParsingFailureReason.SizeExceeded));
        }
        catch (ArchiveLimitException limit)
        {
            return Result.Failure<ArchiveWalk>(ParsingFailure.For(limit.Reason));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            // ZipArchive reports a truncated or non-zip object, and the file system a destination it cannot
            // materialize, as ordinary exceptions; an entry shape the profile did not anticipate can surface as
            // another. None of them may escape as a 500 (00 §5.7 S5), so they all end a failure value — timeout
            // when the profile's own cap fired, malformed otherwise. An out-of-memory condition is left to the
            // host: it says nothing about one artifact.
            var reason = token.IsCancellationRequested
                ? ParsingFailureReason.Timeout
                : ParsingFailureReason.Malformed;

            return Result.Failure<ArchiveWalk>(ParsingFailure.For(reason));
        }
        finally
        {
            if (!handedOver)
            {
                // One cleanup point: a rejected object, a caller cancellation and an abandoned run all leave no
                // partial tree, and a successful extraction passes its root to the handle that owns it.
                DeleteTree(state.Root);
            }
        }
    }

    /// <summary>
    /// Walks one archive (a top-level object or a nested zip already buffered): refuses a declared entry count
    /// over the cap before <see cref="ZipArchive"/> materializes the directory, then validates the whole central
    /// directory, then reads every entry under the caps. Returns this level's entry metadata; a nested archive's
    /// entries are validated but not returned.
    /// </summary>
    private static List<ArchiveEntry> WalkArchive(
        CountingStream source,
        ReadOnlySpan<byte> bytes,
        ZipArchiveOptions options,
        WalkState state,
        bool extract,
        int depth,
        CancellationToken token)
    {
        CheckDeclaredEntryCount(bytes, options);

        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);

        var validated = ValidateDirectory(archive, options, token);

        if (extract && state.Root is null)
        {
            // Created only after this level's central directory is accepted: a rejection derivable from the
            // directory (a count, a name, a duplicate, a link) writes nothing at all.
            state.Root = CreateRoot();
            state.RootPrefix = Path.TrimEndingDirectorySeparator(state.Root) + Path.DirectorySeparatorChar;
        }

        var entries = new List<ArchiveEntry>(validated.Count);

        foreach (var candidate in validated)
        {
            if (candidate.IsDirectory)
            {
                if (extract)
                {
                    EnsureDirectory(state, candidate);
                }

                entries.Add(new ArchiveEntry(candidate.Normalized, 0, 0, IsDirectory: true));
                continue;
            }

            entries.Add(WalkEntry(source, candidate, options, state, extract, depth, token));
        }

        return entries;
    }

    /// <summary>
    /// Reads one entry's bytes under the caps: charges the cumulative expanded budget and the per-entry ratio
    /// from the bytes actually read, sniffs the PK magic of the content to recurse into a nested archive, and —
    /// when extracting — writes the bytes to a destination inside the owned root. Returns the entry metadata.
    /// </summary>
    private static ArchiveEntry WalkEntry(
        CountingStream source,
        Candidate candidate,
        ZipArchiveOptions options,
        WalkState state,
        bool extract,
        int depth,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var entry = candidate.Entry;

        using var stream = entry.Open();

        // Counts what the archive really consumed for this entry — the local header it reads through the source
        // plus every compressed byte the entry stream pulls — never a declared length.
        var compressedBefore = source.BytesRead;

        using var destination = extract ? OpenDestination(state, candidate) : null;
        MemoryStream? nested = null;
        var scratch = ArrayPool<byte>.Shared.Rent(ReadChunkBytes);

        try
        {
            Span<byte> prefix = stackalloc byte[ZipMagicBytes];
            var prefixLength = ReadUpTo(stream, prefix, token);
            var nestedArchive = IsZipMagic(prefix[..prefixLength]);

            if (nestedArchive && depth + 1 > options.MaxNesting)
            {
                throw new ArchiveLimitException(ParsingFailureReason.NestingExceeded);
            }

            if (nestedArchive)
            {
                nested = new MemoryStream();
            }

            long expanded = 0;

            expanded += prefixLength;
            state.Charge(prefixLength);
            destination?.Write(prefix[..prefixLength]);
            nested?.Write(prefix[..prefixLength]);

            while (true)
            {
                token.ThrowIfCancellationRequested();

                var read = stream.Read(scratch, 0, scratch.Length);
                if (read == 0)
                {
                    break;
                }

                expanded += read;
                state.Charge(read);

                // Both sides of the ratio are measured from what actually happened by now: the bytes read out of
                // the entry against the bytes consumed for it. A bomb therefore hits the cap while it is still
                // being read, before it is buffered whole, and a lying declared length cannot widen the allowance.
                CheckRatio(expanded, source.BytesRead - compressedBefore, options);

                destination?.Write(scratch, 0, read);
                nested?.Write(scratch, 0, read);
            }

            var compressedRead = source.BytesRead - compressedBefore;

            if (nested is not null)
            {
                nested.Position = 0;

                // A nested archive is walked for its caps and its names only: its children are not written, so
                // a nested path can never collide with a top-level destination inside the same root.
                var nestedBytes = nested.GetBuffer().AsSpan(0, (int)nested.Length);

                using var nestedSource = new CountingStream(nested);
                _ = WalkArchive(nestedSource, nestedBytes, options, state, extract: false, depth + 1, token);
            }

            return new ArchiveEntry(candidate.Normalized, compressedRead, expanded, IsDirectory: false);
        }
        finally
        {
            // Cleared on return: the scratch held untrusted artifact bytes and the pool is shared process-wide.
            ArrayPool<byte>.Shared.Return(scratch, clearArray: true);
            nested?.Dispose();
        }
    }

    /// <summary>
    /// The central-directory pass: the entry count, then each entry's name (normalized and validated), its
    /// duplicate check and its symbolic-link flag. The ancestor folders of every name are collected in the same
    /// pass, so the one file-shadows-a-folder check at the end is a set lookup. Throws before any byte is written.
    /// </summary>
    private static List<Candidate> ValidateDirectory(
        ZipArchive archive,
        ZipArchiveOptions options,
        CancellationToken token)
    {
        if (archive.Entries.Count > options.MaxEntries)
        {
            throw new ArchiveLimitException(ParsingFailureReason.EntryCountExceeded);
        }

        var validated = new List<Candidate>(archive.Entries.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // An alternate lookup so the ancestor walk compares spans and allocates no substring for a name.
        var folderLookup = folders.GetAlternateLookup<ReadOnlySpan<char>>();

        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();

            var isDirectory = entry.FullName.EndsWith('/');

            if (IsSymbolicLink(entry) || !TryNormalize(entry.FullName, out var normalized))
            {
                throw new ArchiveLimitException(ParsingFailureReason.UnsafeEntryName);
            }

            if (!seen.Add(normalized))
            {
                throw new ArchiveLimitException(ParsingFailureReason.DuplicateEntry);
            }

            var name = normalized.AsSpan();

            if (isDirectory)
            {
                folderLookup.Add(name);
            }

            // Every ancestor folder of this name, so one pass over the names yields the whole set of paths that
            // have to be folders.
            var offset = 0;
            while (true)
            {
                var relative = name[offset..].IndexOf('/');
                if (relative < 0)
                {
                    break;
                }

                offset += relative;
                folderLookup.Add(name[..offset]);
                offset++;
            }

            validated.Add(new Candidate(entry, normalized, isDirectory));
        }

        // A file entry that is also an ancestor folder of another entry (a and a/b) cannot be materialized
        // without one shadowing the other, so it is refused here, from the central directory alone.
        foreach (var candidate in validated)
        {
            if (!candidate.IsDirectory && folderLookup.Contains(candidate.Normalized.AsSpan()))
            {
                throw new ArchiveLimitException(ParsingFailureReason.DuplicateEntry);
            }
        }

        return validated;
    }

    /// <summary>
    /// True when the entry is a Unix symbolic link: the upper 16 bits of <c>ExternalAttributes</c> are the file
    /// mode, and <c>S_IFLNK</c> (<c>0xA000</c>) marks a link the profile must never follow.
    /// </summary>
    private static bool IsSymbolicLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & UnixFileTypeMask) == UnixSymlinkType;

    /// <summary>
    /// Normalizes one archive entry name: rejects a backslash separator, a drive letter or <c>:</c>, an absolute
    /// (leading <c>/</c>) name, a name longer than <see cref="ParsingLimits.MaxEntryNameLength"/> or deeper than
    /// <see cref="ParsingLimits.MaxEntryNameSegments"/>, a character that does not render as itself (a control,
    /// format, line/paragraph separator or private-use character, or an unpaired surrogate), and a segment ending
    /// in <c>.</c> or a space; collapses <c>//</c> and <c>.</c> segments so <c>a//b</c> and <c>A/./b</c>
    /// normalize to the same path (and so collide as a duplicate); and rejects any <c>..</c> segment, so no
    /// normalized name can leave its root. Returns false for an unsafe name.
    /// <para>
    /// The two bounds are checked before <see cref="string.Split(char)"/> so a hostile name cannot ask for a
    /// large allocation, and everything else stays linear in the name's own length.
    /// </para>
    /// <para>
    /// Internal rather than private so the AC4 normalization tests can assert the guard directly, including the
    /// names a <see cref="ZipArchive"/> writer cannot represent (an empty name); the walk still applies it to
    /// every entry. Exposing it loosens nothing — the same rule runs on every archive.
    /// </para>
    /// </summary>
    internal static bool TryNormalize(string raw, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrEmpty(raw) || raw.Length > ParsingLimits.MaxEntryNameLength)
        {
            return false;
        }

        if (raw.StartsWith('/') || raw.Contains('\\') || raw.Contains(':'))
        {
            return false;
        }

        var segments = raw.Split('/');

        if (segments.Length > ParsingLimits.MaxEntryNameSegments)
        {
            return false;
        }

        var kept = new List<string>(segments.Length);

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == ".")
            {
                // A trailing '/' (a directory entry), '//' and '.' collapse to the same path.
                continue;
            }

            if (segment == ".." || !IsRenderable(segment) || segment[^1] is '.' or ' ')
            {
                return false;
            }

            kept.Add(segment);
        }

        if (kept.Count == 0)
        {
            // '', '/', '.' and './' all normalize to nothing: there is no entry to name.
            return false;
        }

        normalized = string.Join('/', kept);
        return true;
    }

    /// <summary>
    /// True when every character of one segment renders as itself: a control character, a format character (a
    /// bidi override such as U+202E, a zero-width space such as U+200B), a line or paragraph separator
    /// (U+2028/U+2029), a private-use character or an unpaired surrogate is refused, because a name containing
    /// one reads as something other than the path it resolves to.
    /// </summary>
    private static bool IsRenderable(string segment)
    {
        for (var index = 0; index < segment.Length; index++)
        {
            var character = segment[index];

            if (char.IsControl(character) || HasUnsafeCategory(character))
            {
                return false;
            }

            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= segment.Length || !char.IsLowSurrogate(segment[index + 1]))
                {
                    return false;
                }

                // A valid pair is one supplementary code point, so its low half is consumed here.
                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The Unicode categories the profile refuses outright in a name.</summary>
    private static bool HasUnsafeCategory(char character) =>
        char.GetUnicodeCategory(character) is
            UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.PrivateUse;

    /// <summary>True when the leading bytes are a zip signature (local header, or an empty/spanned archive).</summary>
    private static bool IsZipMagic(ReadOnlySpan<byte> prefix) =>
        prefix.Length >= ZipMagicBytes
        && prefix[0] == (byte)'P'
        && prefix[1] == (byte)'K'
        && ((prefix[2] == 3 && prefix[3] == 4)
            || (prefix[2] == 5 && prefix[3] == 6)
            || (prefix[2] == 7 && prefix[3] == 8));

    /// <summary>
    /// Refuses an archive whose end-of-central-directory record declares more entries than the profile allows,
    /// before <see cref="ZipArchive"/> materializes the directory: the declared count is the whole-directory cost
    /// a hostile object can announce, and a few hundred bytes can announce millions. A record that is missing,
    /// truncated or inconsistent is left to the normal <c>malformed</c> path.
    /// </summary>
    private static void CheckDeclaredEntryCount(ReadOnlySpan<byte> bytes, ZipArchiveOptions options)
    {
        if (!TryFindEndOfCentralDirectory(bytes, out var endOfCentralDirectory))
        {
            return;
        }

        // A ZIP64 archive points at its own record from a locator directly before the classic one.
        var locator = endOfCentralDirectory - Zip64LocatorLength;
        var zip64Window = bytes.Length - Zip64RecordLength;

        if (locator >= 0 && zip64Window >= 0 && ReadUInt32(bytes, locator) == Zip64LocatorSignature)
        {
            var record = ReadUInt64(bytes, locator + 8);

            if (record <= (ulong)zip64Window && ReadUInt32(bytes, (int)record) == Zip64RecordSignature)
            {
                if (ReadUInt64(bytes, (int)record + Zip64RecordTotalEntriesOffset) > (ulong)options.MaxEntries)
                {
                    throw new ArchiveLimitException(ParsingFailureReason.EntryCountExceeded);
                }

                return;
            }
        }

        // The classic count. A ZIP64 archive's classic record carries the 0xFFFF marker instead of a real count,
        // and that is over any cap the profile sets, so a ZIP64 record the locator did not point at still fails
        // closed rather than passing.
        if (ReadUInt16(bytes, endOfCentralDirectory + EndOfCentralDirectoryTotalEntriesOffset) > options.MaxEntries)
        {
            throw new ArchiveLimitException(ParsingFailureReason.EntryCountExceeded);
        }
    }

    /// <summary>
    /// Locates the classic end-of-central-directory record: the last occurrence whose declared comment length
    /// reaches exactly to the end of the object, which is what tells the real record apart from the same four
    /// bytes appearing inside a comment. Returns false when the object has no such record.
    /// </summary>
    private static bool TryFindEndOfCentralDirectory(ReadOnlySpan<byte> bytes, out int index)
    {
        index = -1;

        var earliest = Math.Max(0, bytes.Length - EndOfCentralDirectoryLength - MaxEndOfCentralDirectoryComment);

        for (var candidate = bytes.Length - EndOfCentralDirectoryLength; candidate >= earliest; candidate--)
        {
            if (ReadUInt32(bytes, candidate) != EndOfCentralDirectorySignature)
            {
                continue;
            }

            if (candidate + EndOfCentralDirectoryLength
                + ReadUInt16(bytes, candidate + EndOfCentralDirectoryCommentLengthOffset) == bytes.Length)
            {
                index = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>A little-endian 16-bit read at an offset the caller has already bounded.</summary>
    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);

    /// <summary>A little-endian 32-bit read at an offset the caller has already bounded.</summary>
    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    /// <summary>A little-endian 64-bit read at an offset the caller has already bounded.</summary>
    private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);

    /// <summary>Refuses an entry whose expanded bytes exceed the profile ratio times its compressed bytes.</summary>
    private static void CheckRatio(long expanded, long compressed, ZipArchiveOptions options)
    {
        if (!RatioAccepted(expanded, compressed, options))
        {
            throw new ArchiveLimitException(ParsingFailureReason.CompressionRatioExceeded);
        }
    }

    /// <summary>
    /// True when <paramref name="expanded"/> bytes are inside the per-entry ratio cap for
    /// <paramref name="compressed"/> bytes. Internal rather than private so the AC3 boundary — exactly the
    /// profile ratio accepted, one byte more refused — is asserted on the guard itself: a real deflate stream
    /// cannot be built to a byte-exact ratio.
    /// <para>
    /// A declared compressed length high enough to overflow the product is not a bomb, so clamping the ceiling to
    /// <c>long.MaxValue</c> keeps the guard from refusing a legitimate large entry.
    /// </para>
    /// </summary>
    internal static bool RatioAccepted(long expanded, long compressed, ZipArchiveOptions options)
    {
        var ceiling = compressed > long.MaxValue / options.MaxCompressionRatio
            ? long.MaxValue
            : compressed * options.MaxCompressionRatio;

        return expanded <= ceiling;
    }

    /// <summary>Reads up to <paramref name="buffer"/>.Length bytes, observing the profile's token between reads.</summary>
    private static int ReadUpTo(Stream stream, Span<byte> buffer, CancellationToken token)
    {
        var filled = 0;

        while (filled < buffer.Length)
        {
            token.ThrowIfCancellationRequested();

            var read = stream.Read(buffer[filled..]);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        return filled;
    }

    /// <summary>
    /// The unique temporary root this class creates and owns, under the process temp directory. On a Unix-like
    /// system it is created with owner-only permissions (0700): everything that will live under it is untrusted
    /// artifact content, and the process temp directory itself is often shared.
    /// </summary>
    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "evidgate-parse-" + Guid.NewGuid().ToString("N"));

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(root);
        }
        else
        {
            Directory.CreateDirectory(
                root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return root;
    }

    /// <summary>Creates a directory entry's folder, re-checking the resolved path stays inside the root.</summary>
    private static void EnsureDirectory(WalkState state, Candidate candidate) =>
        Directory.CreateDirectory(ResolveWithinRoot(state, candidate));

    /// <summary>
    /// Opens one entry's destination file inside the root. The resolved path is checked against the root prefix
    /// after normalization (defense in depth: normalization already refuses <c>..</c> and absolute names) and
    /// opened with <see cref="FileMode.CreateNew"/>, so an existing file is never overwritten or followed; on a
    /// Unix-like system the file is created owner-only (0600), like the root it lives in.
    /// </summary>
    private static FileStream OpenDestination(WalkState state, Candidate candidate)
    {
        var destination = ResolveWithinRoot(state, candidate);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(destination, options);
    }

    /// <summary>
    /// The absolute path of an entry under the extraction root, refused unless it resolves inside the root.
    /// </summary>
    private static string ResolveWithinRoot(WalkState state, Candidate candidate)
    {
        var relative = candidate.Normalized.Replace('/', Path.DirectorySeparatorChar);
        var resolved = Path.GetFullPath(Path.Combine(state.Root!, relative));

        if (!resolved.StartsWith(state.RootPrefix!, StringComparison.Ordinal))
        {
            throw new ArchiveLimitException(ParsingFailureReason.UnsafeEntryName);
        }

        return resolved;
    }

    /// <summary>Deletes an extraction root, tolerating an already-deleted tree or a locked file.</summary>
    private static void DeleteTree(string? root)
    {
        if (root is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A best-effort cleanup: the tree lives under the process temp directory and the OS reclaims it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    /// <summary>One validated entry: its archive object, its normalized name and whether it is a directory.</summary>
    private readonly record struct Candidate(ZipArchiveEntry Entry, string Normalized, bool IsDirectory);

    /// <summary>The result of one walk: the top-level entry metadata and, when extracting, the owned root.</summary>
    private readonly record struct ArchiveWalk(IReadOnlyList<ArchiveEntry> Entries, string? Root);

    /// <summary>
    /// The mutable walk state: the cumulative expanded budget (shared by nested archives, so a bomb split across
    /// levels is still capped) and the extraction root once it is created.
    /// </summary>
    private sealed class WalkState(long maxExpandedBytes)
    {
        private readonly long maxExpandedBytes = maxExpandedBytes;
        private long expanded;

        /// <summary>The extraction root, created only after the top-level central directory is accepted.</summary>
        public string? Root { get; set; }

        /// <summary>The root's path with a trailing separator, the prefix every destination must match.</summary>
        public string? RootPrefix { get; set; }

        /// <summary>Charges <paramref name="bytes"/> of expanded content; over the budget is <c>size_exceeded</c>.</summary>
        public void Charge(long bytes)
        {
            expanded += bytes;
            if (expanded > maxExpandedBytes)
            {
                throw new ArchiveLimitException(ParsingFailureReason.SizeExceeded);
            }
        }
    }

    /// <summary>
    /// The walk's own cap/guard signal, mapped at the boundary to a <c>Result</c> failure. It carries only a
    /// generic <see cref="ParsingFailureReason"/> — never an entry name or an artifact byte (00 §5.7 S3).
    /// </summary>
    private sealed class ArchiveLimitException(string reason) : Exception(reason)
    {
        /// <summary>One of <see cref="ParsingFailureReason"/>.</summary>
        public string Reason { get; } = reason;
    }

    /// <summary>
    /// A seekable stream that counts the bytes read through it, so a per-entry compression ratio is measured from
    /// what the archive really consumed — the local header it reads plus every compressed byte the entry stream
    /// pulls — never from a declared length. Seeks pass through unchanged (they move no content), and the wrapped
    /// stream is left open on dispose, because the caller owns it.
    /// </summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        /// <summary>The bytes read through this stream so far.</summary>
        public long BytesRead { get; private set; }

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;

            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            BytesRead += read;

            return read;
        }

        public override void Flush()
        {
            // Read-only.
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// The caps of one archive walk (06.3.1 AC3). <see cref="Default"/> is <see cref="ParsingLimits"/>, so the
/// profile enforces the plan's limits in production; tests pass scaled-down caps to prove every guard quickly.
/// </summary>
internal sealed record ZipArchiveOptions(
    int MaxEntries,
    long MaxExpandedBytes,
    int MaxCompressionRatio,
    int MaxNesting,
    TimeSpan Timeout)
{
    /// <summary>The production caps (06.3.1 §4).</summary>
    public static ZipArchiveOptions Default { get; } = new(
        ParsingLimits.MaxZipEntries,
        ParsingLimits.MaxExpandedBytes,
        ParsingLimits.MaxCompressionRatio,
        ParsingLimits.MaxNesting,
        ParsingLimits.ParseTimeout);
}

/// <summary>
/// One validated archive extracted under a temporary root this handle owns (06.3.1 AC4). The root is created
/// by the profile, every file inside it was written with <see cref="FileMode.CreateNew"/>, and
/// <see cref="Dispose"/> deletes the whole tree, so extracted artifact bytes never survive a caller.
/// <para>
/// The handle is the profile's only extraction surface and today only the tests take one; a consumer (04.4.1's
/// trace mapper, 06.3.4's report-bundle reader) owns the handle for as long as it reads the tree. A file inside
/// the root was validated as archive content, not as a document: a consumer that parses one runs it through the
/// profile again.
/// </para>
/// </summary>
internal sealed class ArchiveExtraction : IDisposable
{
    private readonly string root;
    private bool disposed;

    internal ArchiveExtraction(string root, IReadOnlyList<ArchiveEntry> entries)
    {
        this.root = root;
        Entries = entries;
    }

    /// <summary>The temporary directory that holds the extracted files; deleted on <see cref="Dispose"/>.</summary>
    public string Root => root;

    /// <summary>The metadata of every entry that was written under <see cref="Root"/>.</summary>
    public IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>Deletes the extraction tree; safe to call more than once.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort, as in SafeZipArchive.DeleteTree.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
