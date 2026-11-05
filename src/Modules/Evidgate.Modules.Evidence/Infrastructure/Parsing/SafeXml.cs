using System.Buffers;
using System.Xml;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Contracts;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing;

/// <summary>
/// The XML arm of the parser profile (06.3.1 AC1, AC2; 00 §5.7 S4/S5). A caller hands in an untrusted
/// stream and the kind's byte cap; the profile reads the stream itself under that cap, refuses a document
/// that declares a DTD, then walks the whole document once with a closed <see cref="XmlReader"/> profile
/// (<c>DtdProcessing.Prohibit</c>, <c>XmlResolver = null</c>, <c>MaxCharactersFromEntities = 1</c>) counting
/// element depth against <see cref="ParsingLimits.MaxDepth"/> and the wall clock against
/// <see cref="ParsingLimits.ParseTimeout"/>.
/// <para>
/// The DTD check is a byte-level scan of the prologue rather than a message match on the reader's exception:
/// it is deterministic (no localized text), it never sees a byte of the body (a <c>&lt;!DOCTYPE</c> inside a
/// CDATA section or a text node is not a DTD), and it names the reason <c>dtd_prohibited</c> for 06.3.2's
/// corpus. The reader is the enforcement anyway — the scan only makes the reason exact.
/// </para>
/// <para>
/// The whole input is bounded into memory first (never more than <paramref name="maxBytes"/> bytes, even when
/// the stream lies about <see cref="Stream.Length"/>), so the returned report can hand out readers that walk
/// the same bytes again without a second read of the caller's stream (06.3.1 AC6: one bounded copy, not a DOM).
/// </para>
/// </summary>
internal static class SafeXml
{
    /// <summary>How many ASCII letters of a prologue <c>!NAME</c> are compared (<c>DOCTYPE</c> is the longest).</summary>
    private const int NameBufferBytes = 8;

    /// <summary>The element a JUnit report carries once per test entry.</summary>
    private const string ReportCaseElement = "testcase";

    /// <summary>
    /// Validates one XML document against the profile and returns the report handle on success.
    /// </summary>
    /// <param name="content">The untrusted bytes; the caller keeps ownership of the stream.</param>
    /// <param name="maxBytes">The per-kind byte cap.</param>
    /// <param name="cancellationToken">The caller's cancellation; a caller cancellation is never swallowed.</param>
    /// <returns>The validated report, or the profile failure.</returns>
    public static Result<ParsedXmlReport> Parse(Stream content, long maxBytes, CancellationToken cancellationToken) =>
        Parse(content, maxBytes, long.MaxValue, ParsingLimits.ParseTimeout, cancellationToken);

    /// <summary>
    /// Validates one XML document against the profile, refusing a report that carries more than
    /// <paramref name="maxCases"/> test entries.
    /// </summary>
    /// <param name="content">The untrusted bytes; the caller keeps ownership of the stream.</param>
    /// <param name="maxBytes">The per-kind byte cap.</param>
    /// <param name="maxCases">The most <c>testcase</c> elements the document may carry (positive).</param>
    /// <param name="cancellationToken">The caller's cancellation; a caller cancellation is never swallowed.</param>
    /// <returns>The validated report, or the profile failure.</returns>
    public static Result<ParsedXmlReport> Parse(
        Stream content,
        long maxBytes,
        long maxCases,
        CancellationToken cancellationToken) =>
        Parse(content, maxBytes, maxCases, ParsingLimits.ParseTimeout, cancellationToken);

    /// <summary>
    /// The profile with an explicit timeout, so a test can prove the wall-clock guard without waiting 30 s.
    /// </summary>
    /// <param name="content">The untrusted bytes; the caller keeps ownership of the stream.</param>
    /// <param name="maxBytes">The per-kind byte cap.</param>
    /// <param name="timeout">The wall-clock cap for this parse.</param>
    /// <param name="cancellationToken">The caller's cancellation; a caller cancellation is never swallowed.</param>
    /// <returns>The validated report, or the profile failure.</returns>
    internal static Result<ParsedXmlReport> Parse(
        Stream content,
        long maxBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        Parse(content, maxBytes, long.MaxValue, timeout, cancellationToken);

    /// <summary>
    /// The full profile: one bounded read, the DTD scan, then one walk that enforces depth, entry count and the
    /// wall clock.
    /// </summary>
    /// <param name="content">The untrusted bytes; the caller keeps ownership of the stream.</param>
    /// <param name="maxBytes">The per-kind byte cap.</param>
    /// <param name="maxCases">The most <c>testcase</c> elements the document may carry, or <see cref="long.MaxValue"/> for none.</param>
    /// <param name="timeout">The wall-clock cap for this parse.</param>
    /// <param name="cancellationToken">The caller's cancellation; a caller cancellation is never swallowed.</param>
    /// <returns>The validated report, or the profile failure.</returns>
    internal static Result<ParsedXmlReport> Parse(
        Stream content,
        long maxBytes,
        long maxCases,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCases);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var token = timeoutSource.Token;

        try
        {
            var chunks = BoundedContent.Read(content, maxBytes, token);

            token.ThrowIfCancellationRequested();

            if (PrologueDeclaresDtd(chunks))
            {
                return Result.Failure<ParsedXmlReport>(ParsingFailure.For(ParsingFailureReason.DtdProhibited));
            }

            var failure = Validate(chunks, maxBytes, maxCases, token);
            if (failure is not null)
            {
                return Result.Failure<ParsedXmlReport>(failure);
            }

            return Result.Success(new ParsedXmlReport(() => OpenReader(chunks, maxBytes)));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller's own cancellation propagates; only the profile's timeout becomes a failure value.
            return Result.Failure<ParsedXmlReport>(ParsingFailure.For(ParsingFailureReason.Timeout));
        }
        catch (ContentTooLongException)
        {
            // The bounded read refused the content: the stream is longer than the kind's cap.
            return Result.Failure<ParsedXmlReport>(ParsingFailure.For(ParsingFailureReason.SizeExceeded));
        }
        catch (InvalidDataException)
        {
            // A caller stream that fails with InvalidDataException (a corrupt GZipStream, say) is malformed
            // content, not a breached cap: only the read's own ContentTooLongException means size_exceeded.
            return Result.Failure<ParsedXmlReport>(ParsingFailure.For(ParsingFailureReason.Malformed));
        }
        catch (XmlException)
        {
            return Result.Failure<ParsedXmlReport>(ParsingFailure.For(ParsingFailureReason.Malformed));
        }
        catch (ArgumentOutOfRangeException)
        {
            // Found by the 06.3.2 fuzz harness: a non-ASCII byte inside the XML declaration makes
            // XmlTextReaderImpl.SwitchEncoding throw ArgumentOutOfRangeException from a negative BlockCopy count
            // instead of XmlException. It is malformed content, never an unhandled crash.
            return Result.Failure<ParsedXmlReport>(ParsingFailure.For(ParsingFailureReason.Malformed));
        }
    }

    /// <summary>
    /// Walks the document once with the closed profile, enforcing depth, the entry count and the wall clock. Returns the
    /// failure, or null when the document is well-formed and inside every cap.
    /// </summary>
    /// <remarks>
    /// The entry count is exact rather than an early stop at <c>maxCases + 1</c>: the walk happens anyway for the
    /// depth cap, and it reads bytes that are already bounded in memory, so the only thing a full count buys is an
    /// honest <c>actual</c> in the refusal.
    /// </remarks>
    private static Error? Validate(IReadOnlyList<byte[]> chunks, long maxBytes, long maxCases, CancellationToken token)
    {
        using var reader = OpenReader(chunks, maxBytes);
        var cases = 0L;

        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();

            // Only element nodes count. XmlReader.Depth is the element nesting depth (0 at the root), so the
            // 64th element level has Depth 63 and is allowed, while the 65th has Depth 64 and is refused. A text,
            // whitespace or comment node inside the 64th element has Depth 64 too, and must not be mistaken for a
            // 65th element — counting every node's depth would reject a legal document that carries a text leaf.
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Depth >= ParsingLimits.MaxDepth)
                {
                    return ParsingFailure.For(ParsingFailureReason.DepthExceeded);
                }

                // A JUnit report carries one <testcase> per entry; the prefix is irrelevant, the local name is not.
                if (string.Equals(reader.LocalName, ReportCaseElement, StringComparison.Ordinal))
                {
                    cases++;
                }
            }
        }

        return cases > maxCases ? ParsingFailure.CasesExceeded(maxCases, cases) : null;
    }

    /// <summary>Opens a reader over the profile's closed settings; every setting is a guard, none is optional.</summary>
    private static XmlReader OpenReader(IReadOnlyList<byte[]> chunks, long maxBytes) =>
        XmlReader.Create(BoundedContent.ToStream(chunks), CreateSettings(maxBytes));

    /// <summary>
    /// The one XML reader profile (00 §5.7 S4): DTDs prohibited, no resolver, a positive entity cap, a document
    /// character cap, and the input stream left open so the report's readers own nothing but the bytes.
    /// <para>
    /// <c>DtdProcessing.Prohibit</c> is the real guard: with DTDs prohibited no general entity can be declared or
    /// expanded. <c>MaxCharactersFromEntities</c> is 1 as a positive cap, but it counts only characters produced
    /// by general-entity expansion — predefined entities and numeric character references are not counted — so a
    /// legitimate document is unaffected (a value of 0 would mean no limit at all, i.e. no guard).
    /// </para>
    /// <para>
    /// Internal rather than private so the AC1 test can assert the four guards on the exact settings object the
    /// profile uses. Handing these settings to another reader only ever produces the same closed profile, so this
    /// is not a way to loosen a guard.
    /// </para>
    /// </summary>
    internal static XmlReaderSettings CreateSettings(long maxBytes) => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 1,
        MaxCharactersInDocument = maxBytes,
        CloseInput = false,
        Async = false,
    };

    /// <summary>
    /// True when the document's prologue declares a DTD (<c>&lt;!DOCTYPE</c>, or a top-level
    /// <c>&lt;!ENTITY</c>). The scan understands the XML declaration, comments and processing instructions,
    /// and stops at the first element start — after which a DTD is impossible — so a document that merely
    /// contains the text <c>&lt;!DOCTYPE</c> inside a comment, a CDATA section or an element is not flagged.
    /// </summary>
    private static bool PrologueDeclaresDtd(IReadOnlyList<byte[]> chunks)
    {
        var state = PrologueState.Start;
        var name = new byte[NameBufferBytes];
        var nameLength = 0;

        foreach (var chunk in chunks)
        {
            for (var index = 0; index < chunk.Length; index++)
            {
                var value = chunk[index];

                switch (state)
                {
                    case PrologueState.Start:
                        if (value == 0xEF)
                        {
                            state = PrologueState.BomSecond;
                            break;
                        }

                        goto case PrologueState.Between;

                    case PrologueState.BomSecond:
                        state = value == 0xBB ? PrologueState.BomThird : PrologueState.Between;
                        break;

                    case PrologueState.BomThird:
                        state = PrologueState.Between;
                        break;

                    case PrologueState.Between:
                        if (IsAsciiWhitespace(value))
                        {
                            break;
                        }

                        if (value == (byte)'<')
                        {
                            state = PrologueState.AfterLessThan;
                            break;
                        }

                        return false;

                    case PrologueState.AfterLessThan:
                        if (value == (byte)'?')
                        {
                            state = PrologueState.InProcessingInstruction;
                            break;
                        }

                        if (value == (byte)'!')
                        {
                            state = PrologueState.AfterBang;
                            break;
                        }

                        // The root element has started: no DTD can follow it.
                        return false;

                    case PrologueState.AfterBang:
                        if (value == (byte)'-')
                        {
                            state = PrologueState.AfterDash;
                            break;
                        }

                        if (IsAsciiLetter(value))
                        {
                            nameLength = 0;
                            name[nameLength++] = value;
                            state = PrologueState.InName;
                            break;
                        }

                        return false;

                    case PrologueState.AfterDash:
                        if (value == (byte)'-')
                        {
                            state = PrologueState.InComment;
                            break;
                        }

                        return false;

                    case PrologueState.InComment:
                        if (value == (byte)'-')
                        {
                            state = PrologueState.InCommentDash;
                        }

                        break;

                    case PrologueState.InCommentDash:
                        if (value == (byte)'-')
                        {
                            break;
                        }

                        state = value == (byte)'>' ? PrologueState.Between : PrologueState.InComment;
                        break;

                    case PrologueState.InProcessingInstruction:
                        if (value == (byte)'?')
                        {
                            state = PrologueState.InProcessingInstructionQuestion;
                        }

                        break;

                    case PrologueState.InProcessingInstructionQuestion:
                        state = value == (byte)'>' ? PrologueState.Between : PrologueState.InProcessingInstruction;
                        break;

                    case PrologueState.InName:
                        if (IsAsciiLetter(value))
                        {
                            if (nameLength >= NameBufferBytes)
                            {
                                return false;
                            }

                            name[nameLength++] = value;
                            break;
                        }

                        return IsDtdName(name.AsSpan(0, nameLength));

                    default:
                        return false;
                }
            }
        }

        return false;
    }

    /// <summary>True when the prologue <c>!NAME</c> is a DTD declaration (<c>DOCTYPE</c> or <c>ENTITY</c>).</summary>
    private static bool IsDtdName(ReadOnlySpan<byte> name) =>
        MatchesAsciiIgnoreCase(name, "DOCTYPE"u8) || MatchesAsciiIgnoreCase(name, "ENTITY"u8);

    /// <summary>Case-insensitive ASCII comparison of a prologue name with a keyword.</summary>
    private static bool MatchesAsciiIgnoreCase(ReadOnlySpan<byte> name, ReadOnlySpan<byte> expected)
    {
        if (name.Length != expected.Length)
        {
            return false;
        }

        for (var index = 0; index < name.Length; index++)
        {
            if (ToAsciiUpper(name[index]) != expected[index])
            {
                return false;
            }
        }

        return true;
    }

    private static byte ToAsciiUpper(byte value) =>
        value is >= (byte)'a' and <= (byte)'z' ? (byte)(value - 32) : value;

    private static bool IsAsciiLetter(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';

    private static bool IsAsciiWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    /// <summary>The prologue scanner's states: only a DTD declaration ends it with <c>true</c>.</summary>
    private enum PrologueState
    {
        Start,
        BomSecond,
        BomThird,
        Between,
        AfterLessThan,
        AfterBang,
        AfterDash,
        InComment,
        InCommentDash,
        InProcessingInstruction,
        InProcessingInstructionQuestion,
        InName,
    }
}

/// <summary>
/// The bounded read's own over-cap signal (06.3.1 AC2). It is thrown only by <see cref="BoundedContent"/> when
/// the bytes read pass <c>maxBytes</c>, so <c>SafeXml</c> and <c>SafeJson</c> can catch exactly this and map it
/// to <c>size_exceeded</c>. A plain <see cref="InvalidDataException"/> raised by the caller's own stream (a
/// corrupt <c>GZipStream</c>, say) is deliberately not this type: that is malformed content, not a breached cap,
/// and must not be misreported as <c>size_exceeded</c>.
/// </summary>
internal sealed class ContentTooLongException : Exception
{
    /// <summary>Creates the over-cap failure with the profile's fixed, secret-free message.</summary>
    /// <param name="message">The fixed message <see cref="BoundedContent"/> supplies.</param>
    public ContentTooLongException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The bounded input of one parse (06.3.1 AC2, AC6): the stream is read in fixed chunks until EOF, and the
/// moment the total passes <c>maxBytes</c> the read fails with <see cref="ContentTooLongException"/>. The cap is
/// therefore enforced against the bytes actually read, never against a <see cref="Stream.Length"/> the caller
/// controls. The chunks are the profile's one copy of the input and are never mutated, so a
/// <see cref="ParsedXmlReport"/> can hand out more than one reader over them.
/// </summary>
internal static class BoundedContent
{
    private const int ReadChunkBytes = 64 * 1024;

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> bytes into immutable chunks, honouring the profile's token.
    /// The scratch buffer is filled to its full length before a chunk is allocated, so a stream that hands out
    /// one byte per <see cref="Stream.Read(byte[],int,int)"/> still allocates one array per chunk, never one per
    /// byte.
    /// </summary>
    /// <param name="content">The untrusted stream.</param>
    /// <param name="maxBytes">The byte cap; exceeding it throws <see cref="ContentTooLongException"/>.</param>
    /// <param name="cancellationToken">The profile's token (caller cancellation linked with the timeout).</param>
    /// <returns>The chunks, in order; an empty list for an empty stream.</returns>
    /// <exception cref="ContentTooLongException">The stream is longer than <paramref name="maxBytes"/>.</exception>
    public static IReadOnlyList<byte[]> Read(Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        var chunks = new List<byte[]>();
        var scratch = ArrayPool<byte>.Shared.Rent(ReadChunkBytes);
        long total = 0;

        try
        {
            while (true)
            {
                var filled = 0;

                // Coalesce: keep reading until the scratch buffer is full or the stream ends, so a byte-at-a-time
                // stream produces one chunk per buffer instead of one array per byte. The token is observed before
                // every read, so the wall-clock guard still fires mid-buffer.
                while (filled < scratch.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var read = content.Read(scratch, filled, scratch.Length - filled);
                    if (read == 0)
                    {
                        break;
                    }

                    filled += read;
                    total += read;
                    if (total > maxBytes)
                    {
                        throw TooLong();
                    }
                }

                if (filled == 0)
                {
                    break;
                }

                var chunk = new byte[filled];
                scratch.AsSpan(0, filled).CopyTo(chunk);
                chunks.Add(chunk);

                if (filled < scratch.Length)
                {
                    // The stream ended inside the buffer, so the next read would only see EOF.
                    break;
                }
            }
        }
        finally
        {
            // Clear on return: the profile's buffer must not hand artifact bytes to the pool's next renter.
            ArrayPool<byte>.Shared.Return(scratch, clearArray: true);
        }

        return chunks;
    }

    /// <summary>A forward-only stream over the chunks; <see cref="Stream.Length"/> is the true content length.</summary>
    public static Stream ToStream(IReadOnlyList<byte[]> chunks) => new ChunkedReadStream(chunks);

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> bytes into ONE contiguous buffer, honouring the profile's
    /// token. A consumer that must hand a single span to a parser (<see cref="System.Text.Json.JsonDocument"/>)
    /// gets it without a second copy: the cap is enforced against the bytes actually read, never against a
    /// <see cref="Stream.Length"/> the caller controls, and that length only ever sizes the first allocation.
    /// </summary>
    /// <param name="content">The untrusted stream.</param>
    /// <param name="maxBytes">The byte cap; exceeding it throws <see cref="ContentTooLongException"/>.</param>
    /// <param name="cancellationToken">The profile's token (caller cancellation linked with the timeout).</param>
    /// <returns>The buffer (longer than the content) and the number of bytes read.</returns>
    /// <exception cref="ContentTooLongException">The stream is longer than <paramref name="maxBytes"/>.</exception>
    public static (byte[] Buffer, int Length) ReadContiguous(
        Stream content,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        // Clamp first: a cap above Array.MaxLength must never reach new byte[...] or Array.Resize. A stream longer
        // than the clamp is refused as size-exceeded, exactly as it would be for the caller's own smaller cap.
        var cap = EffectiveCap(maxBytes);

        // Room for the one byte that proves the content is longer than the cap.
        var ceiling = (int)(cap + 1);
        var buffer = new byte[InitialCapacity(content, ceiling)];
        var length = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (length == buffer.Length)
            {
                // The buffer is full: one byte tells EOF from "the stream has more content" without
                // reallocating on the exact-fit path.
                var probe = content.ReadByte();
                if (probe < 0)
                {
                    break;
                }

                if (length >= cap)
                {
                    throw TooLong();
                }

                Array.Resize(ref buffer, (int)Math.Min(ceiling, Math.Max((long)buffer.Length * 2, 1)));
                buffer[length++] = (byte)probe;
                continue;
            }

            var read = content.Read(buffer, length, buffer.Length - length);
            if (read == 0)
            {
                break;
            }

            length += read;
            if (length > cap)
            {
                throw TooLong();
            }
        }

        return (buffer, length);
    }

    /// <summary>
    /// The effective contiguous-read cap: never above <c>Array.MaxLength - 1</c>, so the one buffer can always
    /// grow to the extra byte that proves the stream is longer than the cap without overflowing a
    /// <see cref="byte"/>[] (<c>Array.MaxLength</c> is the largest length an array may have, so the probe byte
    /// needs the cap to stay one below it). A stream longer than the clamp is refused with
    /// <see cref="ContentTooLongException"/>.
    /// </summary>
    /// <param name="maxBytes">The caller's per-kind cap.</param>
    /// <returns>The cap the contiguous read enforces.</returns>
    internal static long EffectiveCap(long maxBytes) => Math.Min(maxBytes, Array.MaxLength - 1);

    /// <summary>
    /// The first allocation: exactly the remaining bytes when a seekable stream reports them truthfully
    /// (the common object-store path, one allocation and no growth), otherwise one read-sized chunk that
    /// doubles only if the stream turns out longer than it claimed.
    /// </summary>
    private static int InitialCapacity(Stream content, int ceiling)
    {
        if (content.CanSeek)
        {
            try
            {
                var remaining = content.Length - content.Position;
                if (remaining >= 0 && remaining <= ceiling)
                {
                    return (int)remaining;
                }
            }
            catch (Exception exception) when (exception is NotSupportedException or IOException or ObjectDisposedException)
            {
                // A stream that cannot report its length is read into a growing buffer instead.
            }
        }

        return Math.Min(ceiling, ReadChunkBytes);
    }

    private static ContentTooLongException TooLong() =>
        new("the content is longer than the artifact kind's byte limit");
}

/// <summary>
/// A read-only stream over the chunks of one bounded parse, so the XML reader can walk the same validated
/// bytes again (the report hands out a fresh reader per call). It reports the true content length and supports
/// seeking over the chunks, so the reader sizes itself from the profile's bytes and never from a
/// caller-supplied <see cref="Stream.Length"/>.
/// </summary>
internal sealed class ChunkedReadStream : Stream
{
    private readonly IReadOnlyList<byte[]> chunks;
    private int chunkIndex;
    private int chunkOffset;
    private long position;

    /// <summary>Wraps the chunks; the list is read-only and the arrays are never mutated by this stream.</summary>
    /// <param name="chunks">The chunks, in order.</param>
    public ChunkedReadStream(IReadOnlyList<byte[]> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        this.chunks = chunks;
        foreach (var chunk in chunks)
        {
            Length += chunk.Length;
        }
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => true;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length { get; }

    /// <inheritdoc />
    public override long Position
    {
        get => position;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        var written = 0;

        while (written < buffer.Length && chunkIndex < chunks.Count)
        {
            var chunk = chunks[chunkIndex];
            var take = Math.Min(chunk.Length - chunkOffset, buffer.Length - written);

            chunk.AsSpan(chunkOffset, take).CopyTo(buffer[written..]);

            chunkOffset += take;
            written += take;
            position += take;

            if (chunkOffset == chunk.Length)
            {
                chunkIndex++;
                chunkOffset = 0;
            }
        }

        return written;
    }

    /// <inheritdoc />
    public override void Flush()
    {
        // Read-only stream: nothing to flush.
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (target < 0)
        {
            throw new IOException("An attempt was made to move the position before the beginning of the stream.");
        }

        position = target;
        chunkIndex = 0;
        chunkOffset = 0;

        var remaining = target;
        while (chunkIndex < chunks.Count && remaining >= chunks[chunkIndex].Length)
        {
            remaining -= chunks[chunkIndex].Length;
            chunkIndex++;
        }

        chunkOffset = (int)remaining;

        return position;
    }

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
