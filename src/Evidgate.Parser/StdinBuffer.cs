using System.Buffers;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;

namespace Evidgate.Parser;

/// <summary>
/// The child's bounded stdin read (06.3.6 §7): the artifact's bytes arrive on stdin, are consumed into chunks of
/// at most <see cref="ChunkBytes"/>, and never exceed <c>--max-bytes</c>. One byte is read past the cap, so an
/// object that is one byte too large is a rejection rather than a truncated parse — stdin carries no length the
/// child could trust, and a producer that lies about its size is exactly the input this process exists for.
/// <para>
/// The bytes are kept so the run has two passes over one bounded copy: the media sniff reads the leading
/// <see cref="ParsingLimits.SniffPrefixBytes"/> bytes, and the profile reads the whole object again through
/// <see cref="OpenStream"/>. The profile bounds its own copy a second time, so a stream that over-reports its
/// length cannot widen either cap. Nothing is spooled to a file (06.3.6 AC1: the child touches no file), which
/// means one run holds the content twice — the input copy here plus the profile's own chunks; the launcher's
/// memory limit is what bounds that, and the output cap is a later task's concern (06.3.6-T1.4).
/// </para>
/// </summary>
internal sealed class StdinBuffer
{
    private const int ChunkBytes = 64 * 1024;

    private readonly List<byte[]> chunks = [];
    private readonly byte[] prefix = new byte[ParsingLimits.SniffPrefixBytes];

    private StdinBuffer()
    {
    }

    /// <summary>How many bytes the object held up to the cap.</summary>
    public long Length { get; private set; }

    /// <summary>True when the object is longer than the cap; then <see cref="Length"/> is the cap itself.</summary>
    public bool Overflowed { get; private set; }

    /// <summary>The leading bytes the media sniff reads (at most <see cref="ParsingLimits.SniffPrefixBytes"/>).</summary>
    public ReadOnlySpan<byte> Prefix =>
        prefix.AsSpan(0, (int)Math.Min(Length, ParsingLimits.SniffPrefixBytes));

    /// <summary>
    /// Reads stdin under a hard cap. Blocking reads are not interrupted: the profile's own 30 s cap is
    /// cooperative, so a producer that stalls mid-object is the launcher's wall-clock limit to enforce
    /// (06.3.6 AC2, T1.3), not something this reader can decide.
    /// </summary>
    /// <param name="stdin">The standard input stream; the caller keeps ownership.</param>
    /// <param name="maxBytes">The per-kind cap; the read stops at it and one byte past it.</param>
    /// <returns>The bounded content and whether the object was longer than the cap.</returns>
    public static StdinBuffer Read(Stream stdin, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var buffer = new StdinBuffer();
        var scratch = ArrayPool<byte>.Shared.Rent(ChunkBytes);

        try
        {
            while (buffer.Length < maxBytes)
            {
                var want = (int)Math.Min(scratch.Length, maxBytes - buffer.Length);
                var read = stdin.Read(scratch, 0, want);
                if (read <= 0)
                {
                    break;
                }

                buffer.Append(scratch.AsSpan(0, read));
            }

            if (buffer.Length == maxBytes && stdin.Read(scratch, 0, 1) > 0)
            {
                buffer.Overflowed = true;
            }
        }
        finally
        {
            // Clear on return: the buffer must not hand artifact bytes to the pool's next renter.
            ArrayPool<byte>.Shared.Return(scratch, clearArray: true);
        }

        return buffer;
    }

    /// <summary>A fresh forward-only read stream over the bounded content, for one profile pass.</summary>
    /// <returns>The content stream; the caller keeps ownership.</returns>
    public Stream OpenStream() => BoundedContent.ToStream(chunks);

    private void Append(ReadOnlySpan<byte> bytes)
    {
        chunks.Add(bytes.ToArray());

        if (Length < ParsingLimits.SniffPrefixBytes)
        {
            var take = (int)Math.Min(bytes.Length, ParsingLimits.SniffPrefixBytes - Length);
            bytes[..take].CopyTo(prefix.AsSpan((int)Length));
        }

        Length += bytes.Length;
    }
}
