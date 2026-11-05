using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using Evidgate.Modules.Ingestion.Contracts;

namespace Evidgate.Modules.Ingestion.Application;

/// <summary>
/// The gzip half of the 04.1.1-T3 caps (00 §9.3/§9.4, DECISIONS §8.1 D-A8): it turns a
/// <c>Content-Encoding: gzip</c> body into the decoded bytes the parser reads, and refuses the body the
/// moment it would pass either cap — <see cref="IngestionLimits.MaxExpandedBytes"/> or
/// <see cref="IngestionLimits.MaxExpansionRatio"/> times the compressed size.
/// <para>
/// Three properties make the guard hold under hostile input:
/// </para>
/// <list type="number">
/// <item>The compressed body is already bounded (1 MiB, the transport cap) and fully buffered, so the
/// ratio denominator is known before a single byte is inflated.</item>
/// <item>The output is read in bounded chunks and the caps are checked before a chunk is kept, so the
/// decoded buffer never holds more than <see cref="IngestionLimits.MaxExpandedBytes"/> bytes and the
/// decode aborts within one chunk past the cap (the 50 MiB-from-20 KiB bomb).</item>
/// <item>The member is framed here rather than by .NET's <c>GZipStream</c>: the header is skipped by hand,
/// the raw deflate stream is inflated one byte at a time (<see cref="SingleByteStream"/>), and the bytes
/// the inflater consumed pin the deflate end exactly. A single member must then be exactly
/// header + deflate + trailer; any other length is a second member, a repeated trailer or trailing data,
/// all refused (<c>gzip_invalid</c>), never ignored. <c>GZipStream</c> cannot make that check: after a
/// member it probes for a following one and consumes any tail of eight bytes or fewer, so
/// <c>member || its own 8-byte trailer</c> would look like a clean member and its last eight bytes would
/// still validate as the trailer.</item>
/// </list>
/// <para>
/// Truncation is the one case zlib does not report (its strict-validation switch is off by default), so
/// the decoded body is checked against the gzip trailer at the exact member end: the trailer carries the
/// CRC-32 and the ISIZE of what was decoded, and both must match.
/// </para>
/// </summary>
internal static class EventBodyDecoder
{
    /// <summary>The decoded-chunk size: the most a cap may be overshot by before the abort.</summary>
    internal const int ChunkSize = 64 * 1024;

    /// <summary>The gzip trailer length (CRC-32 + ISIZE, RFC 1952 §2.3.1).</summary>
    private const int TrailerLength = 8;

    /// <summary>The CRC-32 table of RFC 1952's ISIZE companion (the reflected IEEE polynomial).</summary>
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>
    /// Decodes one <c>Content-Encoding: gzip</c> body under <paramref name="limits"/>. The caller owns the
    /// result and must dispose it; a refusal carries the reason and the expanded count at the abort.
    /// </summary>
    /// <param name="compressed">The body as sent, already capped at 1 MiB by the endpoint.</param>
    /// <param name="limits">The boot-bound expanded-size and ratio caps.</param>
    internal static DecodedBody DecodeGzip(ReadOnlyMemory<byte> compressed, IngestionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        // A gzip member is at least a header plus a trailer: a shorter body cannot be one.
        if (compressed.Length <= TrailerLength)
        {
            return DecodedBody.Refused(IngestBatchRejection.GzipInvalid, 0);
        }

        // The deflate stream starts after the member's header; a body whose header is absent or malformed
        // cannot be a member.
        if (!TrySkipGzipHeader(compressed.Span, out var headerLength))
        {
            return DecodedBody.Refused(IngestBatchRejection.GzipInvalid, 0);
        }

        var maxExpanded = limits.MaxExpandedBytes;
        var ratioCap = (long)limits.MaxExpansionRatio * compressed.Length;

        var chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
        byte[]? output = null;
        var expanded = 0;
        var deflateBytes = 0;

        try
        {
            // Only the deflate stream is inflated, so the inflater stops at the exact deflate end instead of
            // probing for another member the way GZipStream does (that probe is what let a repeated trailer
            // pass). One byte per read makes Served the exact byte length the deflate stream consumed.
            using (var source = new SingleByteStream(compressed[headerLength..]))
            using (var inflater = new DeflateStream(source, CompressionMode.Decompress))
            {
                while (true)
                {
                    var read = inflater.Read(chunk, 0, chunk.Length);
                    if (read == 0)
                    {
                        break;
                    }

                    var next = expanded + read;
                    if (next > maxExpanded)
                    {
                        Return(ref output);
                        return DecodedBody.Refused(IngestBatchRejection.ExpandedTooLarge, next);
                    }

                    if (next > ratioCap)
                    {
                        Return(ref output);
                        return DecodedBody.Refused(IngestBatchRejection.RatioExceeded, next);
                    }

                    EnsureCapacity(ref output, next, expanded, maxExpanded);
                    Array.Copy(chunk, 0, output!, expanded, read);
                    expanded = next;
                }

                // One byte per read means the inflater consumed exactly the deflate stream: headerLength plus
                // Served is the exact deflate end. A single member is exactly header + deflate + trailer, so
                // anything else — a second member, a repeated trailer, any trailing byte — is refused, never
                // ignored: the framing of the body has to be exact.
                deflateBytes = source.Served;
            }

            // A deflate stream is never empty (the final block alone is at least one byte), so a member of just
            // header + trailer is malformed rather than an empty body.
            if (deflateBytes == 0 || headerLength + deflateBytes + TrailerLength != compressed.Length)
            {
                Return(ref output);
                return DecodedBody.Refused(IngestBatchRejection.GzipInvalid, expanded);
            }

            // The position check above pinned the trailer to the body's last eight bytes: it is this
            // member's own trailer, so its CRC-32 and ISIZE must match what was decoded.
            if (!TrailerMatches(compressed.Span, output, expanded))
            {
                Return(ref output);
                return DecodedBody.Refused(IngestBatchRejection.GzipInvalid, expanded);
            }

            var body = DecodedBody.Accepted(output, expanded);
            output = null;
            return body;
        }
        catch (InvalidDataException)
        {
            // zlib refused the deflate stream: corrupt data or an incomplete stream.
            Return(ref output);
            return DecodedBody.Refused(IngestBatchRejection.GzipInvalid, expanded);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk, clearArray: true);
        }
    }

    /// <summary>
    /// Locates the first byte of a gzip member's deflate stream: the 10-byte fixed header (RFC 1952 §2.3.1)
    /// plus the optional FEXTRA/FNAME/FCOMMENT/FHCRC fields its flags announce. False when the bytes are not
    /// a gzip header, so a non-member is refused before anything is inflated.
    /// </summary>
    private static bool TrySkipGzipHeader(ReadOnlySpan<byte> data, out int headerLength)
    {
        headerLength = 0;

        // ID1, ID2 (magic), CM (deflate) and the reserved FLG bits must all match (RFC 1952 §2.3.1).
        if (data.Length < 10
            || data[0] != 0x1F
            || data[1] != 0x8B
            || data[2] != 0x08
            || (data[3] & 0xE0) != 0)
        {
            return false;
        }

        var flags = data[3];
        var index = 10;

        if ((flags & 0x04) != 0) // FEXTRA: a 2-byte little-endian length and that many bytes.
        {
            if (index + 2 > data.Length)
            {
                return false;
            }

            index += 2 + (data[index] | (data[index + 1] << 8));
            if (index > data.Length)
            {
                return false;
            }
        }

        if ((flags & 0x08) != 0) // FNAME: a NUL-terminated name.
        {
            index = SkipZeroTerminated(data, index);
            if (index < 0)
            {
                return false;
            }
        }

        if ((flags & 0x10) != 0) // FCOMMENT: a NUL-terminated comment.
        {
            index = SkipZeroTerminated(data, index);
            if (index < 0)
            {
                return false;
            }
        }

        if ((flags & 0x02) != 0) // FHCRC: the low 16 bits of the header's CRC-32, little-endian.
        {
            if (index + 2 > data.Length)
            {
                return false;
            }

            var stored = (ushort)(data[index] | (data[index + 1] << 8));
            if (stored != (ushort)Crc32(data[..index]))
            {
                return false;
            }

            index += 2;
        }

        headerLength = index;
        return true;
    }

    /// <summary>The index just past the next NUL byte, or -1 when the span holds none.</summary>
    private static int SkipZeroTerminated(ReadOnlySpan<byte> data, int start)
    {
        for (var i = start; i < data.Length; i++)
        {
            if (data[i] == 0)
            {
                return i + 1;
            }
        }

        return -1;
    }

    /// <summary>Grows the decoded buffer geometrically, never past <paramref name="maxExpanded"/> bytes.</summary>
    private static void EnsureCapacity(ref byte[]? buffer, int required, int used, int maxExpanded)
    {
        if (buffer is not null && buffer.Length >= required)
        {
            return;
        }

        var target = Math.Min(maxExpanded, Math.Max(required, (buffer?.Length ?? 0) * 2));
        var grown = ArrayPool<byte>.Shared.Rent(target);
        if (buffer is not null)
        {
            Array.Copy(buffer, grown, used);
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        buffer = grown;
    }

    /// <summary>
    /// Returns a rented buffer to the pool and clears the reference. The decoded bytes are tenant data, so
    /// the buffer is cleared before it is pooled.
    /// </summary>
    private static void Return(ref byte[]? buffer)
    {
        if (buffer is null)
        {
            return;
        }

        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        buffer = null;
    }

    /// <summary>
    /// True when the body's last eight bytes are the gzip trailer of what was decoded. The caller has
    /// already framed the member, so those bytes are this member's own trailer and both fields must match
    /// what was decoded; RFC 1952 §2.3.1 stores them little-endian.
    /// </summary>
    private static bool TrailerMatches(ReadOnlySpan<byte> compressed, byte[]? output, int expanded)
    {
        var trailer = compressed[^TrailerLength..];
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
        var isize = BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]);

        return isize == (uint)expanded && crc == Crc32(output.AsSpan(0, expanded));
    }

    /// <summary>The reflected IEEE CRC-32 (RFC 1952 §8) of <paramref name="data"/>.</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc = (crc >> 8) ^ CrcTable[(crc ^ value) & 0xFF];
        }

        return ~crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var i = 0u; i < 256u; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    /// <summary>
    /// Serves the compressed bytes one at a time, so the inflater can never read past the end of the
    /// deflate stream (<see cref="Served"/> then equals that stream's exact byte length). Not seekable on
    /// purpose: a seekable base stream would let the inflater rewind to the exact end of the compressed
    /// data, which would hide the trailing bytes this class must count.
    /// </summary>
    private sealed class SingleByteStream(ReadOnlyMemory<byte> data) : Stream
    {
        private readonly ReadOnlyMemory<byte> _data = data;
        private int _served;

        /// <summary>The bytes the inflater has taken so far.</summary>
        internal int Served => _served;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _data.Length;

        public override long Position
        {
            get => _served;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);

            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || _served >= _data.Length)
            {
                return 0;
            }

            buffer[0] = _data.Span[_served];
            _served++;
            return 1;
        }

        public override void Flush()
        {
            // Read-only stream: nothing to flush.
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// The outcome of one gzip decode (04.1.1-T3): the decoded bytes, or the reason the body was refused and
/// the expanded count reached when the decision was taken. Owns the pooled buffer it hands out, so the
/// caller disposes it.
/// </summary>
internal sealed class DecodedBody : IDisposable
{
    private byte[]? _buffer;

    private DecodedBody(byte[]? buffer, int expandedBytes, string? reason)
    {
        _buffer = buffer;
        ExpandedBytes = expandedBytes;
        Reason = reason;
    }

    /// <summary>The decoded bytes; empty when <see cref="Reason"/> is not null.</summary>
    internal ReadOnlyMemory<byte> Bytes => _buffer is null ? default : _buffer.AsMemory(0, ExpandedBytes);

    /// <summary>The decoded bytes counted — the count at the abort when <see cref="Reason"/> is not null.</summary>
    internal int ExpandedBytes { get; }

    /// <summary>The refusal reason (an <see cref="IngestBatchRejection"/> constant), or null when the body decoded.</summary>
    internal string? Reason { get; }

    /// <summary>An accepted body over <paramref name="buffer"/> (may be null for an empty member).</summary>
    internal static DecodedBody Accepted(byte[]? buffer, int expandedBytes) => new(buffer, expandedBytes, null);

    /// <summary>A refused body: no bytes, the reason and the expanded count at the abort.</summary>
    internal static DecodedBody Refused(string reason, int expandedBytes) => new(null, expandedBytes, reason);

    /// <inheritdoc />
    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = null;
        if (buffer is not null)
        {
            // The decoded bytes are the tenant's request body, so the buffer is cleared before it is pooled.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
