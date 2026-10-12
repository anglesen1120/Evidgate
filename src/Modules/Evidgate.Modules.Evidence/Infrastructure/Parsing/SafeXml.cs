using System.Buffers;
using System.Xml;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Contracts;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing;

/// <summary>
/// The XML arm of the parser profile (06.3.1 AC1, AC2; 00 §5.7 S4/S5). A caller hands in an untrusted
/// stream and the kind's byte cap; the profile reads the stream itself under that cap, refuses a document
/// that declares a DTD, then walks the whole document once with a closed <see cref="XmlReader"/> profile
/// (<c>DtdProcessing.Prohibit</c>, <c>XmlResolver = null</c>, <c>MaxCharactersFromEntities = 0</c>) counting
/// depth against <see cref="ParsingLimits.MaxDepth"/> and the wall clock against
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

    /// <summary>
    /// Validates one XML document against the profile and returns the report handle on success.
    /// </summary>
    /// <param name="content">The untrusted bytes; the caller keeps ownership of the stream.</param>
    /// <param name="maxBytes">The per-kind byte cap.</param>
    /// <param name="cancellationToken">The caller's cancellation; a caller cancellation is never swallowed.</param>
    /// <returns>The validated report, or the profile failure.</returns>
    public static Result<ParsedXmlReport> Parse(Stream content, long maxBytes, CancellationToken cancellationToken) =>
        Parse(content, maxBytes, ParsingLimits.ParseTimeout, cancellationToken);

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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

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

            var failure = Validate(chunks, maxBytes, token);
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
        catch (InvalidDataException)
        {
            // The bounded read refused the content: the stream is longer than the kind's cap.
            return Result.Failure<ParsedXmlReport>(ParsingFailure.For(ParsingFailureReason.SizeExceeded));
        }
        catch (XmlException)
        {
            return Result.Failure<ParsedXmlReport>(ParsingFailure.For(ParsingFailureReason.Malformed));
        }
    }

    /// <summary>
    /// Walks the document once with the closed profile, enforcing depth and the wall clock. Returns the
    /// failure, or null when the document is well-formed and inside every cap.
    /// </summary>
    private static Error? Validate(IReadOnlyList<byte[]> chunks, long maxBytes, CancellationToken token)
    {
        using var reader = OpenReader(chunks, maxBytes);

        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();

            // XmlReader.Depth is 0 at the root element, so the 64th level of nesting has Depth 63.
            if (reader.Depth >= ParsingLimits.MaxDepth)
            {
                return ParsingFailure.For(ParsingFailureReason.DepthExceeded);
            }
        }

        return null;
    }

    /// <summary>Opens a reader over the profile's closed settings; every setting is a guard, none is optional.</summary>
    private static XmlReader OpenReader(IReadOnlyList<byte[]> chunks, long maxBytes) =>
        XmlReader.Create(BoundedContent.ToStream(chunks), CreateSettings(maxBytes));

    /// <summary>
    /// The one XML reader profile (00 §5.7 S4): DTDs prohibited, no resolver, no entity expansion, a document
    /// character cap, and the input stream left open so the report's readers own nothing but the bytes.
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
        MaxCharactersFromEntities = 0,
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
/// The bounded input of one parse (06.3.1 AC2, AC6): the stream is read in fixed chunks until EOF, and the
/// moment the total passes <c>maxBytes</c> the read fails with <see cref="InvalidDataException"/>. The cap is
/// therefore enforced against the bytes actually read, never against a <see cref="Stream.Length"/> the caller
/// controls. The chunks are the profile's one copy of the input and are never mutated, so a
/// <see cref="ParsedXmlReport"/> can hand out more than one reader over them.
/// </summary>
internal static class BoundedContent
{
    private const int ReadChunkBytes = 64 * 1024;

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> bytes into immutable chunks, honouring the profile's token.
    /// </summary>
    /// <param name="content">The untrusted stream.</param>
    /// <param name="maxBytes">The byte cap; exceeding it throws <see cref="InvalidDataException"/>.</param>
    /// <param name="cancellationToken">The profile's token (caller cancellation linked with the timeout).</param>
    /// <returns>The chunks, in order; an empty list for an empty stream.</returns>
    /// <exception cref="InvalidDataException">The stream is longer than <paramref name="maxBytes"/>.</exception>
    public static IReadOnlyList<byte[]> Read(Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        var chunks = new List<byte[]>();
        var scratch = ArrayPool<byte>.Shared.Rent(ReadChunkBytes);
        long total = 0;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var read = content.Read(scratch, 0, scratch.Length);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > maxBytes)
                {
                    throw TooLong();
                }

                var chunk = new byte[read];
                scratch.AsSpan(0, read).CopyTo(chunk);
                chunks.Add(chunk);
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
    /// <param name="maxBytes">The byte cap; exceeding it throws <see cref="InvalidDataException"/>.</param>
    /// <param name="cancellationToken">The profile's token (caller cancellation linked with the timeout).</param>
    /// <returns>The buffer (longer than the content) and the number of bytes read.</returns>
    /// <exception cref="InvalidDataException">The stream is longer than <paramref name="maxBytes"/>.</exception>
    public static (byte[] Buffer, int Length) ReadContiguous(
        Stream content,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        // Room for the one byte that proves the content is longer than the cap.
        var ceiling = (int)Math.Min(maxBytes + 1, int.MaxValue);
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

                if (length >= maxBytes)
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
            if (length > maxBytes)
            {
                throw TooLong();
            }
        }

        return (buffer, length);
    }

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

    private static InvalidDataException TooLong() =>
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
