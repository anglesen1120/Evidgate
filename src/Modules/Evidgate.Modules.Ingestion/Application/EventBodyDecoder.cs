using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

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
/// <item>The compressed bytes are served to the inflater one byte at a time
/// (<see cref="SingleByteStream"/>). That is the only way the managed API exposes the exact end of the
/// first gzip member: with a whole-buffer feed the inflater reads ahead, and .NET's <c>GZipStream</c>
/// then either transparently concatenates the next member or silently ignores trailing bytes. With a
/// one-byte feed the inflater consumes exactly the member, so "fewer bytes served than the body carries"
/// is proof of a second member or trailing data — both refused (<c>gzip_invalid</c>), never ignored.</item>
/// </list>
/// <para>
/// Truncation is the one case zlib does not report (its strict-validation switch is off by default), so
/// the decoded body is checked against the gzip trailer: a complete member ends exactly at the body's
/// last eight bytes, and the trailer carries the CRC-32 and the ISIZE of what was decoded.
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

        var maxExpanded = limits.MaxExpandedBytes;
        var ratioCap = (long)limits.MaxExpansionRatio * compressed.Length;

        var chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
        byte[]? output = null;
        var expanded = 0;

        try
        {
            using (var source = new SingleByteStream(compressed))
            using (var gzip = new GZipStream(source, CompressionMode.Decompress))
            {
                while (true)
                {
                    var read = gzip.Read(chunk, 0, chunk.Length);
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

                // One byte per read means the inflater consumed exactly the first member. A body that
                // served fewer bytes than it carries holds a second member or trailing data.
                if (source.Served != compressed.Length)
                {
                    Return(ref output);
                    return DecodedBody.Refused(IngestBatchRejection.GzipInvalid, expanded);
                }
            }

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
            // zlib refused the container: bad header, corrupt deflate data, wrong CRC/ISIZE.
            Return(ref output);
            return DecodedBody.Refused(IngestBatchRejection.GzipInvalid, expanded);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
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
            ArrayPool<byte>.Shared.Return(buffer);
        }

        buffer = grown;
    }

    /// <summary>Returns a rented buffer to the pool and clears the reference.</summary>
    private static void Return(ref byte[]? buffer)
    {
        if (buffer is null)
        {
            return;
        }

        ArrayPool<byte>.Shared.Return(buffer);
        buffer = null;
    }

    /// <summary>
    /// True when the body's last eight bytes are the gzip trailer of what was decoded: a complete member
    /// ends there (zlib already verified the CRC/ISIZE), while a truncated member ends mid-stream and its
    /// tail is deflate data, not a trailer.
    /// </summary>
    private static bool TrailerMatches(ReadOnlySpan<byte> compressed, byte[]? output, int expanded)
    {
        var trailer = compressed[^TrailerLength..];
        var crc = BinaryPrimitives.ReadUInt32BigEndian(trailer);
        var isize = BinaryPrimitives.ReadUInt32BigEndian(trailer[4..]);

        return isize == (uint)expanded && crc == Crc32(output, expanded);
    }

    /// <summary>The reflected IEEE CRC-32 (RFC 1952 §8) of the first <paramref name="length"/> bytes.</summary>
    private static uint Crc32(byte[]? data, int length)
    {
        var crc = uint.MaxValue;
        if (data is not null)
        {
            for (var i = 0; i < length; i++)
            {
                crc = (crc >> 8) ^ CrcTable[(crc ^ data[i]) & 0xFF];
            }
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
    /// first gzip member (<see cref="Served"/> then equals that member's exact byte length). Not seekable
    /// on purpose: a seekable base stream would let <c>DeflateStream</c> rewind an ambiguous trailing
    /// <c>0x1F</c> probe, which is exactly the trailing-data case this class must see.
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
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
